using System.Collections.Concurrent;
using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.UI.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.UI.Tests.Hosting;

public sealed class DesktopApplicationLifetimeTests
{
    [Theory]
    [InlineData(DesktopExitReason.Quit)]
    [InlineData(DesktopExitReason.UpdateRestart)]
    public void Exit_DisposesAsynchronouslyBeforeStoppingUiLoop(DesktopExitReason reason)
    {
        using var ui = new UiContext();
        var resource = new YieldingResource();
        var host = new TestHost(resource);
        using var lifetime = new DesktopApplicationLifetime(host);
        var desktop = new DesktopParticipant();
        lifetime.Attach(desktop);
        desktop.OnExit = exitReason =>
        {
            Assert.True(resource.Disposed, "Host resources must be disposed before the UI loop exits.");
            Assert.True(desktop.Disposed);
            Assert.True(lifetime.IsShutdownPrepared);
            Assert.Equal(reason, exitReason);
            Assert.Equal(ui.ThreadId, Environment.CurrentManagedThreadId);
            ui.Stop();
        };

        var exiting = lifetime.RequestExitAsync(reason);
        ui.RunUntil(exiting);
        Assert.True(exiting.IsCompletedSuccessfully, exiting.Exception?.ToString());
        Assert.Equal(1, host.StopCalls);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(1, host.Updates.FinalCalls);
        Assert.Equal(reason, host.Updates.Reason);
        Assert.Equal(1, desktop.ExitCalls);
    }

    [Fact]
    public void ConcurrentRequests_JoinOneTransaction_AndCannotExitWhileDisposalIsPending()
    {
        using var ui = new UiContext();
        var release = new TaskCompletionSource();
        var resource = new YieldingResource { BeforeDispose = release.Task };
        using var lifetime = new DesktopApplicationLifetime(new TestHost(resource));
        var desktop = new DesktopParticipant { OnExit = _ => ui.Stop() };
        lifetime.Attach(desktop);

        var first = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        var second = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        Assert.Same(first, second);
        Assert.Throws<InvalidOperationException>(() => { _ = lifetime.RequestExitAsync(DesktopExitReason.UpdateRestart); });
        Assert.Equal(0, desktop.ExitCalls);
        Assert.False(lifetime.IsShutdownPrepared);
        Assert.Equal(0, desktop.ExitCalls);
        release.SetResult();
        ui.RunUntil(first);
        Assert.True(first.IsCompletedSuccessfully, first.Exception?.ToString());
        Assert.True(lifetime.IsShutdownPrepared);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(1, desktop.ExitCalls);
    }

    [Fact]
    public void Exit_ClosesAdmissionBeforeAwaitingHostStop()
    {
        using var ui = new UiContext();
        var release = new TaskCompletionSource();
        var host = new TestHost(new YieldingResource { BeforeDispose = release.Task });
        using var lifetime = new DesktopApplicationLifetime(host);
        lifetime.Attach(new DesktopParticipant { OnExit = _ => ui.Stop() });
        var admission = host.Services.GetRequiredService<HostOperationAdmission>();
        admission.Run(() => { });
        var exiting = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        try { Assert.Throws<InvalidOperationException>(() => admission.Run(() => { })); }
        finally { release.SetResult(); ui.RunUntil(exiting); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void UnconfirmedData_BlocksFinalActionEvenWhenStopSucceeds(int? unconfirmed)
    {
        using var ui = new UiContext();
        var host = new TestHost(new YieldingResource()) { Remainder = new(0, unconfirmed) };
        var lifetime = new DesktopApplicationLifetime(host);
        var desktop = new DesktopParticipant();
        lifetime.Attach(desktop);
        var exiting = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        ui.RunUntil(exiting);
        Assert.True(exiting.IsFaulted);
        Assert.Equal(0, desktop.ExitCalls);
        Assert.False(lifetime.IsShutdownPrepared);
    }

    [Fact]
    public void NativeQuit_WithUnknownDataDoesNotPropagateAnUnhandledUiException()
    {
        using var ui = new UiContext();
        var host = new TestHost(new YieldingResource()) { Remainder = DeliveryRemainder.Unknown };
        using var lifetime = new DesktopApplicationLifetime(host);
        var desktop = new DesktopParticipant();
        lifetime.Attach(desktop);
        var quit = lifetime.QuitAsync();
        ui.RunUntil(quit);
        Assert.True(quit.IsCompletedSuccessfully);
        Assert.False(lifetime.Result!.IsDataSafe);
        Assert.False(desktop.Disposed);
        Assert.Equal(0, desktop.ExitCalls);
    }

    [Fact]
    public void DurableData_DisposalFailureIsReported_AndStillExits()
    {
        using var ui = new UiContext();
        var error = new IOException("disposal failed");
        var resource = new YieldingResource { Failure = error };
        var lifetime = new DesktopApplicationLifetime(new TestHost(resource));
        var desktop = new DesktopParticipant();
        lifetime.Attach(desktop);
        var exiting = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        ui.RunUntil(exiting);
        Assert.True(exiting.IsCompletedSuccessfully);
        Assert.Same(exiting, lifetime.RequestExitAsync(DesktopExitReason.Quit));
        Assert.Contains(error, lifetime.Result!.CleanupErrors);
        Assert.True(lifetime.IsShutdownPrepared);
        Assert.Equal(1, desktop.ExitCalls);
        lifetime.Dispose();
        Assert.Equal(1, resource.DisposeCalls);
    }

    [Fact]
    public void FinalActionFailure_StillExitsWithDurableData()
    {
        using var ui = new UiContext();
        var error = new IOException("collector stop failed");
        var resource = new YieldingResource();
        var host = new TestHost(resource);
        var lifetime = new DesktopApplicationLifetime(host);
        host.Updates.Failure = error;
        var desktop = new DesktopParticipant();
        lifetime.Attach(desktop);

        var exiting = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        ui.RunUntil(exiting);

        Assert.True(exiting.IsCompletedSuccessfully);
        Assert.True(desktop.Disposed);
        Assert.True(resource.Disposed);
        Assert.Equal(1, host.StopCalls);
        Assert.Same(error, lifetime.Result!.FinalActionError);
        Assert.Equal(1, desktop.ExitCalls);
        Assert.True(lifetime.IsShutdownPrepared);
        lifetime.Dispose();
    }

    [Fact]
    public void DurableData_HostStopFailureIsSeparateFromDataSafety()
    {
        using var ui = new UiContext();
        var failure = new IOException("host stop failed");
        var resource = new YieldingResource();
        var host = new TestHost(resource) { StopFailure = failure };
        var lifetime = new DesktopApplicationLifetime(host);
        var desktop = new DesktopParticipant();
        lifetime.Attach(desktop);

        var exiting = lifetime.RequestExitAsync(DesktopExitReason.Quit);
        ui.RunUntil(exiting);

        Assert.True(exiting.IsCompletedSuccessfully);
        Assert.Equal(1, host.Updates.FinalCalls);
        Assert.Contains(failure, lifetime.Result!.CleanupErrors);
        Assert.Equal(1, desktop.ExitCalls);
        Assert.True(desktop.Disposed);
        Assert.True(resource.Disposed);
        lifetime.Dispose();
    }

    [Fact]
    public void UnexpectedUiReturn_DoesNotBlockOnCleanupBoundToTheUiThread()
    {
        using var ui = new UiContext();
        var release = new TaskCompletionSource();
        var resource = new YieldingResource { BeforeDispose = release.Task };
        using var lifetime = new DesktopApplicationLifetime(new TestHost(resource));
        lifetime.Attach(new DesktopParticipant { OnExit = _ => ui.Stop() });
        var exiting = lifetime.RequestExitAsync(DesktopExitReason.Quit);

        var error = Assert.Throws<InvalidOperationException>(lifetime.Dispose);
        Assert.Contains("UI loop ended", error.Message);
        Assert.False(resource.Disposed);

        // Let the simulated UI continue so the deliberately interrupted test leaves no work behind.
        release.SetResult();
        ui.RunUntil(exiting);
        Assert.True(exiting.IsCompletedSuccessfully);
    }

    [Fact]
    public void StartupFailure_DisposesHostWithoutUsingAnInactiveSynchronizationContext()
    {
        using var ui = new UiContext();
        var resource = new YieldingResource();
        var lifetime = new DesktopApplicationLifetime(new TestHost(resource));
        lifetime.Dispose();
        lifetime.Dispose();
        Assert.True(resource.Disposed);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Same(ui, SynchronizationContext.Current);
    }

    private sealed class YieldingResource : IAsyncDisposable
    {
        public Task BeforeDispose { get; init; } = Task.CompletedTask;
        public Exception? Failure { get; init; }
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await BeforeDispose;
            // Intentionally capture the caller's context: the lifetime must support ordinary async
            // dependencies without requiring ConfigureAwait(false) throughout the dependency tree.
            await Task.Yield();
            if (Failure is not null) throw Failure;
            Disposed = true;
        }
    }

    private sealed class TestHost : IHost, IAsyncDisposable, IHostedService, IHostShutdownEvidence
    {
        private readonly ServiceProvider _services;
        public TestUpdates Updates { get; } = new();
        public DeliveryRemainder Remainder { get; init; } = new(0, 0);
        public DeliveryRemainder ShutdownRemainder => StopCalls > 0 ? Remainder : DeliveryRemainder.Unknown;
        public int StopCalls { get; private set; }
        public Exception? StopFailure { get; init; }
        public TestHost(YieldingResource resource)
        {
            _services = new ServiceCollection().AddSingleton<HostOperationAdmission>().AddSingleton(new DesktopInstallation(DesktopInstallation.Detached.LoginStart, _ => Updates)).AddSingleton<IHostedService>(this).AddSingleton(_ => resource).BuildServiceProvider();
            _ = _services.GetRequiredService<YieldingResource>();
        }
        public IServiceProvider Services => _services;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure);
        }
        public ValueTask DisposeAsync() => _services.DisposeAsync();
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class DesktopParticipant : IDesktopApplicationParticipant
    {
        public Action<DesktopExitReason>? OnExit { get; set; }
        public bool Disposed { get; private set; }
        public int ExitCalls { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task ExitAsync(DesktopExitReason reason)
        {
            ExitCalls++;
            OnExit?.Invoke(reason);
            return Task.CompletedTask;
        }
    }

    private sealed class TestUpdates : IDesktopUpdates
    {
        public bool IsSupported => true;
        public Exception? Failure { get; set; }
        public int FinalCalls { get; private set; }
        public DesktopExitReason? Reason { get; private set; }
        public UpdateSnapshot Current => UpdateSnapshot.Idle;
        public event Action<UpdateSnapshot>? Changed { add { } remove { } }
        public Task<UpdateCheckResult> CheckAsync() => Task.FromResult(UpdateCheckResult.Skipped);
        public Task<bool> ApplyAsync() => Task.FromResult(false);
        public void Start() { }
        public void Dispose() { }
        public Action PrepareExit(DesktopExitReason reason) => () =>
        {
            FinalCalls++;
            Reason = reason;
            if (Failure is not null) throw Failure;
        };
    }

    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly BlockingCollection<Action> _queue = new();
        private bool _stopped;
        public int ThreadId { get; } = Environment.CurrentManagedThreadId;
        public UiContext() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback callback, object? state) =>
            _queue.Add(() => callback(state));
        public void Stop() => _stopped = true;
        public void RunUntil(Task completion)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!completion.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < deadline, "Desktop shutdown did not finish.");
                if (!_stopped && _queue.TryTake(out var action, 10)) action();
                else Thread.Yield();
            }
        }
        public void Dispose()
        {
            SetSynchronizationContext(_previous);
            while (_queue.TryTake(out var action)) ThreadPool.QueueUserWorkItem(_ => action());
        }
    }
}
