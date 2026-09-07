using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Desktop.Mac.Observations;

namespace Heartbeat.Desktop.Mac.Tests.Observations;

public sealed class MacAccessibilityEventsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-accessibility-{Guid.NewGuid()}");

    [Fact]
    public async Task FirstLaunchAndUnrelatedSettings_DoNotPromptForAccessibility()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = false };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());

        events.Start();
        await events.RefreshPermission();
        config.Update(value => value.DeviceName = "Studio");

        Assert.False(events.Enabled);
        Assert.Equal(MacAccessibilityCapabilityState.Disabled, events.CapabilityState);
        Assert.Equal(0, native.RequestCount);
        Assert.Equal(0, native.ObserveCount);
    }

    [Fact]
    public async Task EnablingFromUser_PersistsSettingAndExplicitlyRequestsPermission()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = false };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        events.Start();
        await events.RefreshPermission();

        events.SetEnabledFromUser(true);
        await events.RefreshPermission();

        Assert.True(config.Current.WindowTitleObservationEnabled);
        Assert.Equal(1, native.RequestCount);
        Assert.Equal(MacAccessibilityCapabilityState.PermissionRequired, events.CapabilityState);
    }

    [Fact]
    public async Task EnabledSettingAtStartup_ChecksPermissionWithoutPromptingAgain()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        config.Update(value => value.WindowTitleObservationEnabled = true);
        var native = new FakeNative { IsProcessTrusted = false };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());

        events.Start();
        await events.RefreshPermission();

        Assert.Equal(MacAccessibilityCapabilityState.PermissionRequired, events.CapabilityState);
        Assert.Equal(0, native.RequestCount);
    }

    [Fact]
    public async Task AuthorizationStartsCurrentApplicationAndForwardsNativeSemantics()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative
        {
            IsProcessTrusted = true,
            FocusedWindowTitle = "README.md"
        };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var received = new List<MacAccessibilityObservation>();
        events.Observation += received.Add;
        events.Start();
        await events.RefreshPermission();
        events.SetCurrentApplication(99);
        await events.RefreshPermission();

        events.SetEnabledFromUser(true);
        await events.RefreshPermission();
        native.Raise(new MacAccessibilityObservation(
            MacAccessibilityObservationKind.FocusedWindowChanged,
            "Program.cs"));
        native.Raise(new MacAccessibilityObservation(
            MacAccessibilityObservationKind.TitleChanged,
            "Program.cs — saved"));

        Assert.Equal(MacAccessibilityCapabilityState.Available, events.CapabilityState);
        Assert.Equal(99, native.LastObservedProcessIdentifier);
        Assert.Equal("README.md", events.CurrentTitle);
        Assert.Equal(
            [MacAccessibilityObservationKind.FocusedWindowChanged, MacAccessibilityObservationKind.TitleChanged],
            received.Select(item => item.Kind));
    }

    [Fact]
    public async Task RevocationStopsAccessibilityButLeavesCapabilityRecoverable()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = true };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        events.Start();
        await events.RefreshPermission();
        events.SetCurrentApplication(99);
        await events.RefreshPermission();
        events.SetEnabledFromUser(true);
        await events.RefreshPermission();
        var stopCountBeforeRevocation = native.StopCount;

        native.IsProcessTrusted = false;
        await events.RefreshPermission();

        Assert.Equal(MacAccessibilityCapabilityState.PermissionRequired, events.CapabilityState);
        Assert.True(native.StopCount > stopCountBeforeRevocation);
        Assert.True(config.Current.WindowTitleObservationEnabled);
    }

    [Fact]
    public async Task LateNotificationFromPreviousApplication_IsIgnored()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = true };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var received = new List<MacAccessibilityObservation>();
        events.Observation += received.Add;
        events.Start();
        await events.RefreshPermission();
        events.SetCurrentApplication(99);
        await events.RefreshPermission();
        events.SetEnabledFromUser(true);
        await events.RefreshPermission();

        native.Raise(new MacAccessibilityObservation(
            MacAccessibilityObservationKind.TitleChanged,
            "Old App",
            12));

        Assert.Empty(received);
    }

    [Fact]
    public async Task PermissionRecoveryPathOpensSystemSettingsOnlyFromUserAction()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = false };
        var runner = new FakeCommandRunner();
        using var events = new MacAccessibilityEvents(config, native, runner);
        events.Start();
        await events.RefreshPermission();
        events.SetEnabledFromUser(true);
        await events.RefreshPermission();

        events.OpenPermissionSettingsFromUser();
        await events.RefreshPermission();

        var command = Assert.Single(runner.Commands);
        Assert.Equal("/usr/bin/open", command.FileName);
        Assert.Contains("Privacy_Accessibility", Assert.Single(command.Arguments));
    }

    [Fact]
    public async Task FailedEnable_LastCapabilityNotificationMatchesUnavailableState()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = true, StartFailure = new IOException("AX start failed") };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var states = new List<MacAccessibilityCapabilityState>();
        events.CapabilityChanged += states.Add;
        events.Start();
        await events.RefreshPermission();
        events.SetCurrentApplication(99);
        await events.RefreshPermission();

        events.SetEnabledFromUser(true);
        await events.RefreshPermission();

        Assert.Equal(MacAccessibilityCapabilityState.Unavailable, events.CapabilityState);
        Assert.Equal(events.CapabilityState, states.Last());
    }

    [Fact]
    public async Task SwitchingApplicationAfterNativeFailure_ImmediatelyRecoversCapabilityAndObservations()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        config.Update(value => value.WindowTitleObservationEnabled = true);
        var native = new FakeNative { IsProcessTrusted = true, FocusedWindowTitle = "New App" };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var received = new List<MacAccessibilityObservation>();
        events.Observation += received.Add;
        events.Start();
        await events.RefreshPermission();
        events.SetCurrentApplication(99);
        await events.RefreshPermission();
        native.RaiseFailure(new InvalidOperationException("Application observation failed"));
        await events.RefreshPermission();
        Assert.Equal(MacAccessibilityCapabilityState.Unavailable, events.CapabilityState);

        events.SetCurrentApplication(100);
        await events.RefreshPermission();
        native.Raise(new MacAccessibilityObservation(MacAccessibilityObservationKind.TitleChanged, "New App", 100));

        Assert.Equal(MacAccessibilityCapabilityState.Available, events.CapabilityState);
        Assert.Equal("New App", events.CurrentTitle);
        Assert.Equal(100, native.LastObservedProcessIdentifier);
        Assert.Single(received);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeNative : IMacAccessibilityNative
    {
        public event Action<MacAccessibilityObservation>? Observation;
        public event Action<Exception>? Failed;
        public void RaiseFailure(Exception error) => Failed?.Invoke(error);
        public bool IsAvailable { get; set; } = true;
        public bool IsProcessTrusted { get; set; }
        public string? FocusedWindowTitle { get; set; }
        public int RequestCount { get; private set; }
        public int ObserveCount { get; private set; }
        public int StopCount { get; private set; }
        public int LastObservedProcessIdentifier { get; private set; }
        public Exception? StartFailure { get; init; }
        public void RequestProcessTrust() => RequestCount++;
        public string? ReadFocusedWindowTitle(int processIdentifier) => FocusedWindowTitle;
        public void ObserveApplication(int processIdentifier)
        {
            if (StartFailure is { } failure) throw failure;
            ObserveCount++;
            LastObservedProcessIdentifier = processIdentifier;
        }
        public void StopObserving() => StopCount++;
        public void Raise(MacAccessibilityObservation observation) => Observation?.Invoke(observation);
    }

    private sealed class FakeCommandRunner : IMacCommandRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Commands { get; } = [];

        public MacCommandResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            Commands.Add((fileName, arguments));
            return new MacCommandResult(0, string.Empty, string.Empty);
        }
    }
}
