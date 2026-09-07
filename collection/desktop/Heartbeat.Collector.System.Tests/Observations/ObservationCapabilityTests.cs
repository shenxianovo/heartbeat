using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Collector.System.Tests.Observations;

public sealed class ObservationCapabilityTests
{
    [Fact]
    public async Task UnchangedRefresh_PreservesLiveObservationAdmissionWhileCheckingPermissions()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var checking = false;
        var capability = new ObservationCapability<bool, bool>(true, false, () =>
        {
            if (checking) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            return new(true, true);
        }, _ => { }, () => { });
        await capability.Start();
        checking = true;
        var refreshing = capability.Refresh();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.True(capability.AcceptsObservations); }
        finally { checking = false; release.Set(); await refreshing; await capability.Stop(); }
    }

    [Fact]
    public async Task PendingTargets_AreCoalescedAndOnlySettledStateIsPublished()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var enabled = true;
        var starts = 0;
        var stops = 0;
        var capability = new ObservationCapability<string, int>("disabled", "failed",
            () => new(enabled ? "available" : "disabled", enabled ? 1 : null),
            _ => { Interlocked.Increment(ref starts); entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); },
            () => Interlocked.Increment(ref stops));
        var states = new List<string>();
        capability.Changed += state => states.Add(state);
        var starting = capability.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            enabled = false;
            var disabling = capability.Refresh();
            enabled = true;
            Assert.Same(disabling, capability.Refresh());
            Assert.False(capability.AcceptsObservations);
        }
        finally { release.Set(); await starting; }
        Assert.Equal(["available"], states);
        Assert.True(capability.AcceptsObservations);
        Assert.Equal(1, starts);
        Assert.Equal(0, stops);
        await capability.Stop();
    }

    [Fact]
    public async Task FailureAndRecovery_HaveOneOrderedStatePublisher()
    {
        using var notifying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var capability = new ObservationCapability<bool, bool>(true, false,
            () => new(true, true), _ => { }, () => { });
        var states = new List<bool>();
        var stateDuringNotification = true;
        capability.Changed += state =>
        {
            states.Add(state);
            if (state) return;
            notifying.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            stateDuringNotification = capability.State;
        };
        await capability.Start();
        capability.ReportFailure(new IOException("old session failed"));
        Assert.True(notifying.Wait(TimeSpan.FromSeconds(5)));
        var recovering = capability.Refresh(retry: true);
        try { Assert.False(recovering.IsCompleted); }
        finally { release.Set(); await recovering; }
        Assert.Equal([false, true], states);
        Assert.False(stateDuringNotification);
        Assert.True(capability.State);
        Assert.True(capability.AcceptsObservations);
        await capability.Stop();
    }

    [Fact]
    public async Task AvailableNotification_CanReadReadyObservations()
    {
        var capability = new ObservationCapability<bool, bool>(false, false,
            () => new(true, true), _ => { }, () => { });
        var acceptedAtNotification = false;
        capability.Changed += _ => acceptedAtNotification = capability.AcceptsObservations;
        await capability.Start();
        Assert.True(acceptedAtNotification);
        await capability.Stop();
    }

    [Fact]
    public async Task DisablingFailedCapability_ReportsDisabledAndPreservesExplicitRecovery()
    {
        var enabled = true;
        var capability = new ObservationCapability<string, int>("available", "failed",
            () => new(enabled ? "available" : "disabled", enabled ? 1 : null), _ => { }, () => { });
        await capability.Start();
        capability.ReportFailure(new IOException("native failure"));
        await capability.Refresh();
        Assert.Equal("failed", capability.State);
        enabled = false;
        await capability.Refresh();
        Assert.Equal("disabled", capability.State);
        enabled = true;
        await capability.Refresh();
        Assert.Equal("failed", capability.State);
        await capability.Refresh(retry: true);
        Assert.Equal("available", capability.State);
        await capability.Stop();
    }

    [Fact]
    public async Task FailedStop_IsRetainedAndCannotBeHiddenByDuplicateStopOrRestart()
    {
        var stops = 0;
        var capability = new ObservationCapability<bool, bool>(true, false,
            () => new(true, true), _ => { }, () => { stops++; throw new TimeoutException("native thread still running"); });
        await capability.Start();
        var stopping = capability.Stop();
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);
        Assert.Same(stopping, capability.Stop());
        await Assert.ThrowsAsync<InvalidOperationException>(() => capability.Start());
        Assert.Equal(1, stops);
        Assert.False(capability.AcceptsObservations);
    }
}
