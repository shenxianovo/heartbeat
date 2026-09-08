using Heartbeat.Desktop.Updater.Velopack;
using Heartbeat.Desktop.UI.Presentation;
using System.Net.Http;
using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Upload;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Desktop.Updater.Velopack.Tests;

public sealed class VelopackUpdateControllerTests
{
    [Fact]
    public async Task CheckWhenCurrent_DoesNotPrepareAgentForRestart()
    {
        var client = new FakeReleaseUpdateClient();
        var preparedForRestart = false;
        using var updateController = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            []);
        IUpdateController controller = updateController;

        var result = await controller.CheckAsync();

        Assert.Equal(UpdateCheckResult.UpToDate, result);
        Assert.Equal(UpdateSnapshot.Idle, controller.Current);
        Assert.False(preparedForRestart);
    }

    [Fact]
    public async Task FoundUpdate_DownloadsToReadyWithoutPreparingAgentForRestart()
    {
        var release = new FakeReleaseUpdate("2.0.0");
        var client = new FakeReleaseUpdateClient { Update = release };
        var preparedForRestart = false;
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            []);
        var ready = WaitForState(controller, UpdateState.ReadyToApply);

        var result = await controller.CheckAsync();
        await client.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(UpdateCheckResult.UpdateFound, result);
        Assert.Equal(UpdateState.Downloading, controller.Current.State);
        Assert.False(preparedForRestart);

        client.AllowDownloadToComplete.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new UpdateSnapshot(UpdateState.ReadyToApply, "2.0.0", 100), controller.Current);
        Assert.False(preparedForRestart);
    }

    [Fact]
    public async Task Apply_PreparesAgentOnlyAfterUpdateIsReady()
    {
        var release = new FakeReleaseUpdate("2.0.0");
        var client = new FakeReleaseUpdateClient { Update = release };
        var lifecycle = new List<string>();
        client.Applied = _ => lifecycle.Add("schedule");
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                lifecycle.Add("prepare");
                return Task.CompletedTask;
            },
            []);

        Assert.False(await controller.ApplyAsync());
        Assert.Empty(lifecycle);

        var ready = WaitForState(controller, UpdateState.ReadyToApply);
        await controller.CheckAsync();
        client.AllowDownloadToComplete.SetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await controller.ApplyAsync());
        Assert.Equal(["prepare"], lifecycle);
    }

    [Theory]
    [InlineData(DesktopExitReason.Quit, false, true)]
    [InlineData(DesktopExitReason.UpdateRestart, false, true)]
    [InlineData(DesktopExitReason.UpdateRestart, true, true)]
    [InlineData(DesktopExitReason.UpdateRestart, false, false)]
    public async Task Application_ConsumesCustodyBeforeOneInstallerAction(
        DesktopExitReason reason, bool schedulingFails, bool durable)
    {
        var client = new FakeReleaseUpdateClient
        {
            Update = new FakeReleaseUpdate("2.0.0"), AutoCompleteDownload = true,
            ScheduleException = schedulingFails ? new IOException("updater unavailable") : null
        };
        var subtree = new StoppingSubtree(durable);
        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<HostOperationAdmission>();
            services.AddSingleton(new DesktopInstallation(DesktopInstallation.Detached.LoginStart,
                restart => new VelopackUpdateController(client, restart, [])));
            services.AddSingleton<IHostedService>(subtree);
        }).Build();
        var application = new DesktopApplicationLifetime(host);
        var desktop = new DesktopParticipant();
        try
        {
            await host.StartAsync();
            application.Attach(desktop);
            var updates = (VelopackUpdateController)application.Updates;
            Assert.Equal(UpdateState.ReadyToApply, updates.Current.State);
            client.Applied = _ => Assert.True(desktop.Disposed);
            var exiting = application.RequestExitAsync(reason);
            await subtree.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Same(exiting, application.RequestExitAsync(reason));
            Assert.Equal(0, client.ScheduleAttempts);
            Assert.Equal(0, desktop.Exits);
            var duplicateApply = reason == DesktopExitReason.UpdateRestart ? updates.ApplyAsync() : null;
            subtree.Release.SetResult();
            if (durable)
            {
                await exiting;
                if (duplicateApply is not null) Assert.True(await duplicateApply);
                Assert.Equal(1, client.ScheduleAttempts);
                Assert.Equal(reason == DesktopExitReason.UpdateRestart, client.Restart);
                Assert.Equal(1, desktop.Exits);
                Assert.Equal(schedulingFails, application.Result!.FinalActionError is not null);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => exiting);
                if (duplicateApply is not null)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => duplicateApply);
                Assert.Equal(0, client.ScheduleAttempts);
                Assert.Equal(0, desktop.Exits);
                Assert.False(desktop.Disposed);
            }
        }
        finally
        {
            subtree.Release.TrySetResult();
            application.Dispose();
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    private sealed class StoppingSubtree(bool durable) : IHostedService, IHostShutdownEvidence
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _stopped;
        public DeliveryRemainder ShutdownRemainder => _stopped && durable ? new(7, 0) : DeliveryRemainder.Unknown;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task;
            _stopped = true;
        }
    }

    private sealed class DesktopParticipant : IDesktopApplicationParticipant
    {
        public bool Disposed { get; private set; }
        public int Exits { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task ExitAsync(DesktopExitReason reason) { Exits++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task FailedCheck_IsDistinctFromBeingUpToDate()
    {
        var client = new FakeReleaseUpdateClient
        {
            CheckException = new HttpRequestException("offline"),
        };
        using var controller = new VelopackUpdateController(client, () => Task.CompletedTask, []);

        var result = await controller.CheckAsync();

        Assert.Equal(UpdateCheckResult.CheckFailed, result);
        Assert.Equal(UpdateState.Idle, controller.Current.State);
        Assert.Equal("检查更新失败，请检查网络后重试。", controller.Current.Error);
    }

    [Fact]
    public async Task TransientDownloadFailure_RetriesWithoutPreparingAgentForRestart()
    {
        var client = new FakeReleaseUpdateClient
        {
            Update = new FakeReleaseUpdate("2.0.0"),
            AutoCompleteDownload = true,
        };
        client.DownloadFailures.Enqueue(new HttpRequestException("temporary"));
        var preparedForRestart = false;
        using var controller = new VelopackUpdateController(
            client,
            () =>
            {
                preparedForRestart = true;
                return Task.CompletedTask;
            },
            [TimeSpan.Zero]);
        var ready = WaitForState(controller, UpdateState.ReadyToApply);

        await controller.CheckAsync();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, client.DownloadAttempts);
        Assert.Equal(UpdateState.ReadyToApply, controller.Current.State);
        Assert.False(preparedForRestart);
    }

    private static Task WaitForState(VelopackUpdateController controller, UpdateState state)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += snapshot =>
        {
            if (snapshot.State == state) completion.TrySetResult();
        };
        return completion.Task;
    }

    private sealed class FakeReleaseUpdateClient : IReleaseUpdateClient
    {
        public bool IsInstalled { get; set; } = true;
        public IReleaseUpdate? Update { get; set; }
        public Exception? CheckException { get; set; }
        public Exception? ScheduleException { get; set; }
        public int ScheduleAttempts { get; private set; }
        public bool Restart { get; private set; }
        public Queue<Exception> DownloadFailures { get; } = new();
        public bool AutoCompleteDownload { get; set; }
        public int DownloadAttempts { get; private set; }
        public TaskCompletionSource DownloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDownloadToComplete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<IReleaseUpdate>? Applied { get; set; }

        public Task<IReleaseUpdate?> CheckForUpdatesAsync() => CheckException == null
            ? Task.FromResult(Update)
            : Task.FromException<IReleaseUpdate?>(CheckException);

        public async Task DownloadUpdatesAsync(IReleaseUpdate release, Action<int> progress)
        {
            DownloadAttempts++;
            if (DownloadFailures.TryDequeue(out var failure)) throw failure;
            DownloadStarted.TrySetResult();
            if (!AutoCompleteDownload) await AllowDownloadToComplete.Task;
        }
        public void ScheduleUpdateAndRestart(IReleaseUpdate release)
        {
            ScheduleAttempts++;
            Restart = true;
            if (ScheduleException != null) throw ScheduleException;
            Applied?.Invoke(release);
        }
        public void ScheduleUpdateAndExit(IReleaseUpdate release)
        {
            ScheduleAttempts++;
            Restart = false;
            if (ScheduleException != null) throw ScheduleException;
            Applied?.Invoke(release);
        }
    }

    private sealed record FakeReleaseUpdate(string Version) : IReleaseUpdate;
}
