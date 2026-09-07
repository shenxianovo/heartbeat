using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Collector.System.Tests.Observations;

public sealed class ObservationThreadTests
{
    [Fact]
    public async Task FailureNotification_MustFinishBeforeSessionCanBeReplaced()
    {
        using var notifying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("Native failure");
        var session = new ObservationThread("failed observation", _ => throw failure, _ =>
        {
            notifying.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        });
        session.Start();
        Assert.True(notifying.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.False(session.Completion.IsCompleted); }
        finally { release.Set(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        session.Stop(TimeSpan.FromSeconds(5));
        Assert.Same(failure, session.Failure);
    }

    [Fact]
    public async Task StopBeforeNativeInitialization_RetainsSessionUntilItsCleanupFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var startedNative = false;
        var releasedNative = false;
        var session = new ObservationThread("test observation", stop =>
        {
            entered.Set();
            try
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                stop.ThrowIfCancellationRequested();
                startedNative = true;
            }
            finally { releasedNative = true; }
        });
        session.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Throws<TimeoutException>(() => session.Stop(TimeSpan.Zero));
            Assert.False(session.Completion.IsCompleted);
        }
        finally { release.Set(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        session.Stop(TimeSpan.FromSeconds(5));
        Assert.False(startedNative);
        Assert.True(releasedNative);
        Assert.Null(session.Failure);
    }
}
