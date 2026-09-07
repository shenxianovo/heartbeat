using Heartbeat.Collector.System.Input;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests.Input;

public sealed class MacInputEventCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-mac-input-{Guid.NewGuid()}");

    [Fact]
    public async Task DisablingCapability_ReturnsWhileNativeCleanupIsBlocked()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeNative
        {
            IsAuthorized = true,
            BeforeStop = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        };
        using var collector = new MacInputEventCollector(config, native, new FakeCommandRunner(),
            new FakeSignal(), new InputEventBuffer(new FixedClock()));
        await collector.StartAsync(CancellationToken.None);
        var submitting = Task.Run(() => collector.SetInputEventRecordingEnabledFromUser(false));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { await submitting.WaitAsync(TimeSpan.FromMilliseconds(300)); }
        finally { release.Set(); await submitting; await collector.StopAsync(CancellationToken.None); }
        Assert.False(native.Running);
    }

    [Fact]
    public async Task NativeFailure_IsReportedWithoutOverwritingTheEnabledIntent()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        var native = new FakeNative { IsAuthorized = true };
        using var collector = new MacInputEventCollector(config, native, new FakeCommandRunner(),
            new FakeSignal(), new InputEventBuffer(new FixedClock()));
        await collector.StartAsync(CancellationToken.None);
        native.RaiseFailure(new IOException("event tap failed"));
        await collector.RefreshPermission();
        Assert.Equal(MacInputMonitoringCapabilityState.Unavailable, collector.CapabilityState);
        Assert.True(config.Current.InputEventRecordingEnabled);
        Assert.False(native.Running);
        collector.SetInputEventRecordingEnabledFromUser(true);
        await collector.RefreshPermission();
        Assert.Equal(MacInputMonitoringCapabilityState.Available, collector.CapabilityState);
        Assert.True(native.Running);
    }

    [Fact]
    public async Task EnablingInteraction_RequestsPermissionWithoutStartingAnUnauthorizedHook()
    {
        var config = NewConfig();
        config.Update(value => value.WindowTitleObservationEnabled = true);
        var native = new FakeNative { IsAuthorized = false };
        using var collector = new MacInputEventCollector(
            config,
            native,
            new FakeCommandRunner(),
            new FakeSignal(),
            new InputEventBuffer(new FixedClock()));
        await collector.StartAsync(CancellationToken.None);

        collector.SetInteractionSignalEnabledFromUser(true);
        await collector.RefreshPermission();

        Assert.True(config.Current.InteractionSignalEnabled);
        Assert.Equal(1, native.RequestCount);
        Assert.Equal(MacInputMonitoringCapabilityState.PermissionRequired, collector.CapabilityState);
        Assert.Equal(0, native.StartCount);
    }

    [Fact]
    public async Task EnablingAnAlreadyAuthorizedCapability_DoesNotRequestPermissionAgain()
    {
        var config = NewConfig();
        config.Update(value => value.WindowTitleObservationEnabled = true);
        var native = new FakeNative { IsAuthorized = true };
        using var collector = new MacInputEventCollector(
            config,
            native,
            new FakeCommandRunner(),
            new FakeSignal(),
            new InputEventBuffer(new FixedClock()));
        await collector.StartAsync(CancellationToken.None);

        collector.SetInteractionSignalEnabledFromUser(true);
        await collector.RefreshPermission();

        Assert.Equal(0, native.RequestCount);
        Assert.Equal(1, native.StartCount);
    }

    [Fact]
    public async Task RecordingDisabled_KeepsClickFallbackButProducesNoDurableEvents()
    {
        var config = NewConfig();
        config.Update(value =>
        {
            value.WindowTitleObservationEnabled = true;
            value.InteractionSignalEnabled = true;
            value.InputEventRecordingEnabled = false;
        });
        var native = new FakeNative { IsAuthorized = true };
        var signal = new FakeSignal();
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new MacInputEventCollector(
            config,
            native,
            new FakeCommandRunner(),
            signal,
            buffer);
        await collector.StartAsync(CancellationToken.None);

        native.Raise(new MacInputObservation(MacInputObservationKind.MouseButton, 1));
        native.Raise(new MacInputObservation(MacInputObservationKind.KeyDown, 0x00));
        native.Raise(new MacInputObservation(MacInputObservationKind.Scroll, 120));

        Assert.Equal(1, native.StartCount);
        Assert.Equal(1, signal.Clicks);
        Assert.Empty(buffer.ReadAll());
    }

    [Fact]
    public async Task RecordingEnabled_MapsPhysicalKeysAndPreservesSharedMouseSemantics()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        var native = new FakeNative { IsAuthorized = true };
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new MacInputEventCollector(
            config,
            native,
            new FakeCommandRunner(),
            new FakeSignal(),
            buffer);
        await collector.StartAsync(CancellationToken.None);

        native.Raise(new MacInputObservation(MacInputObservationKind.KeyDown, 0x00));
        native.Raise(new MacInputObservation(MacInputObservationKind.KeyDown, 0x00));
        native.Raise(new MacInputObservation(MacInputObservationKind.KeyUp, 0x00));
        native.Raise(new MacInputObservation(MacInputObservationKind.KeyDown, 0x00));
        native.Raise(new MacInputObservation(MacInputObservationKind.MouseButton, 2));
        native.Raise(new MacInputObservation(MacInputObservationKind.Scroll, 240));

        var events = buffer.ReadAll();
        Assert.Equal(5, events.Count);
        Assert.Equal(2, events.Count(item =>
            item.EventType == InputEventType.KeyDown &&
            item.Code == (short)InputKeyPosition.KeyA));
        Assert.Contains(events, item =>
            item.EventType == InputEventType.MouseButton && item.Code == 2);
        Assert.Equal(2, events.Count(item =>
            item.EventType == InputEventType.MouseScroll && item.Code == 1));
        Assert.All(events, item =>
            Assert.Equal(InputCodeSets.HeartbeatKeyPositionV1, item.CodeSet));
    }

    [Fact]
    public async Task PermissionRevocation_AutomaticallyStopsHookAndPreservesRequestedIntent()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        var native = new FakeNative { IsAuthorized = true };
        using var collector = new MacInputEventCollector(
            config,
            native,
            new FakeCommandRunner(),
            new FakeSignal(),
            new InputEventBuffer(new FixedClock()));
        await collector.StartAsync(CancellationToken.None);
        native.IsAuthorized = false;

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (native.StopCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(1, native.StopCount);
        Assert.True(config.Current.InputEventRecordingEnabled);
        Assert.Equal(MacInputMonitoringCapabilityState.PermissionRequired, collector.CapabilityState);
    }

    [Fact]
    public async Task StopDuringStart_JoinsTheTransitionAndLeavesNoRunningHook()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeNative
        {
            IsAuthorized = true,
            BeforeStart = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        };
        using var collector = new MacInputEventCollector(config, native, new FakeCommandRunner(),
            new FakeSignal(), new InputEventBuffer(new FixedClock()));
        var starting = Task.Run(() => collector.StartAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopping = collector.StopAsync(CancellationToken.None);
        try { Assert.False(stopping.IsCompleted); }
        finally { release.Set(); await starting; await stopping; }
        Assert.False(native.Running);
        Assert.Equal(1, native.StartCount);
        Assert.Equal(1, native.StopCount);
    }

    [Fact]
    public async Task RapidChangesDuringStart_ConvergeWithoutRestartingForPendingIntermediateIntent()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeNative
        {
            IsAuthorized = true,
            BeforeStart = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
        };
        using var collector = new MacInputEventCollector(config, native, new FakeCommandRunner(),
            new FakeSignal(), new InputEventBuffer(new FixedClock()));
        var starting = Task.Run(() => collector.StartAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            config.Update(value => value.InputEventRecordingEnabled = false);
            config.Update(value => value.InputEventRecordingEnabled = true);
        }
        finally { release.Set(); await starting; }
        Assert.True(native.Running);
        Assert.Equal(1, native.StartCount);
        Assert.Equal(0, native.StopCount);
    }

    [Fact]
    public async Task PermissionRevoked_RejectsObservationsWhileNativeStopIsStillPending()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeNative
        {
            IsAuthorized = true,
            BeforeStop = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
        };
        var buffer = new InputEventBuffer(new FixedClock());
        using var collector = new MacInputEventCollector(config, native, new FakeCommandRunner(), new FakeSignal(), buffer);
        await collector.StartAsync(CancellationToken.None);
        native.IsAuthorized = false;
        var refreshing = Task.Run(collector.RefreshPermission);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            native.Raise(new MacInputObservation(MacInputObservationKind.KeyDown, 0x00));
            Assert.Empty(buffer.ReadAll());
        }
        finally { release.Set(); await refreshing; }
    }

    [Fact]
    public async Task StopCompletion_DoesNotRequireCallerContextWhileNativeStartIsPending()
    {
        var config = NewConfig();
        config.Update(value => value.InputEventRecordingEnabled = true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeNative
        {
            IsAuthorized = true,
            BeforeStart = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
        };
        using var collector = new MacInputEventCollector(config, native, new FakeCommandRunner(),
            new FakeSignal(), new InputEventBuffer(new FixedClock()));
        var starting = Task.Run(() => collector.StartAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task stopping;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            stopping = collector.StopAsync(CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); release.Set(); }
        await starting;
        try { await stopping.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally
        {
            // If the regression returns, release its captured continuations before disposing the fixture.
            for (var i = 0; !stopping.IsCompleted && i < 100; i++)
            {
                context.Drain();
                await Task.Delay(10);
            }
        }
        collector.Dispose();
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue(() => callback(state));
        public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
    }

    private MacConfigManager NewConfig() => new(new MacAgentPaths(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeNative : IMacInputMonitoringNative
    {
        public event Action<MacInputObservation>? Observation;
        public event Action<Exception>? Failed;
        public void RaiseFailure(Exception error) => Failed?.Invoke(error);
        public bool IsAvailable { get; set; } = true;
        public bool IsAuthorized { get; set; }
        public int RequestCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public void RequestAuthorization() => RequestCount++;
        public Action? BeforeStart { get; init; }
        public Action? BeforeStop { get; init; }
        public bool Running { get; private set; }
        public void StartListening() { BeforeStart?.Invoke(); StartCount++; Running = true; }
        public void StopListening() { BeforeStop?.Invoke(); StopCount++; Running = false; }
        public void Raise(MacInputObservation observation) => Observation?.Invoke(observation);
    }

    private sealed class FakeCommandRunner : IMacCommandRunner
    {
        public MacCommandResult Run(string executable, IReadOnlyList<string> arguments) =>
            new(0, string.Empty, string.Empty);
    }

    private sealed class FakeSignal : IInputActivitySignal
    {
        public int Clicks { get; private set; }
        public void MarkClick() => Clicks++;
        public bool ClickedWithin(TimeSpan window) => Clicks > 0;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-08-15T00:00:00Z");
    }
}
