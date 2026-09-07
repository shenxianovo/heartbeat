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
    public void FirstLaunchAndUnrelatedSettings_DoNotPromptForAccessibility()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = false };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());

        events.Start();
        config.Update(value => value.DeviceName = "Studio");

        Assert.False(events.Enabled);
        Assert.Equal(MacAccessibilityCapabilityState.Disabled, events.CapabilityState);
        Assert.Equal(0, native.RequestCount);
        Assert.Equal(0, native.ObserveCount);
    }

    [Fact]
    public void EnablingFromUser_PersistsSettingAndExplicitlyRequestsPermission()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = false };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        events.Start();

        events.SetEnabledFromUser(true);

        Assert.True(config.Current.WindowTitleObservationEnabled);
        Assert.Equal(1, native.RequestCount);
        Assert.Equal(MacAccessibilityCapabilityState.PermissionRequired, events.CapabilityState);
    }

    [Fact]
    public void EnabledSettingAtStartup_ChecksPermissionWithoutPromptingAgain()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        config.Update(value => value.WindowTitleObservationEnabled = true);
        var native = new FakeNative { IsProcessTrusted = false };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());

        events.Start();

        Assert.Equal(MacAccessibilityCapabilityState.PermissionRequired, events.CapabilityState);
        Assert.Equal(0, native.RequestCount);
    }

    [Fact]
    public void AuthorizationStartsCurrentApplicationAndForwardsNativeSemantics()
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
        events.SetCurrentApplication(99);

        events.SetEnabledFromUser(true);
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
    public void RevocationStopsAccessibilityButLeavesCapabilityRecoverable()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = true };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        events.Start();
        events.SetCurrentApplication(99);
        events.SetEnabledFromUser(true);
        var stopCountBeforeRevocation = native.StopCount;

        native.IsProcessTrusted = false;
        events.RefreshPermission();

        Assert.Equal(MacAccessibilityCapabilityState.PermissionRequired, events.CapabilityState);
        Assert.True(native.StopCount > stopCountBeforeRevocation);
        Assert.True(config.Current.WindowTitleObservationEnabled);
    }

    [Fact]
    public void LateNotificationFromPreviousApplication_IsIgnored()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = true };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var received = new List<MacAccessibilityObservation>();
        events.Observation += received.Add;
        events.Start();
        events.SetCurrentApplication(99);
        events.SetEnabledFromUser(true);

        native.Raise(new MacAccessibilityObservation(
            MacAccessibilityObservationKind.TitleChanged,
            "Old App",
            12));

        Assert.Empty(received);
    }

    [Fact]
    public void PermissionRecoveryPathOpensSystemSettingsOnlyFromUserAction()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = false };
        var runner = new FakeCommandRunner();
        using var events = new MacAccessibilityEvents(config, native, runner);
        events.Start();
        events.SetEnabledFromUser(true);

        events.OpenPermissionSettingsFromUser();

        var command = Assert.Single(runner.Commands);
        Assert.Equal("/usr/bin/open", command.FileName);
        Assert.Contains("Privacy_Accessibility", Assert.Single(command.Arguments));
    }

    [Fact]
    public void FailedEnable_LastCapabilityNotificationMatchesUnavailableState()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        var native = new FakeNative { IsProcessTrusted = true, StartFailure = new IOException("AX start failed") };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var states = new List<MacAccessibilityCapabilityState>();
        events.CapabilityChanged += states.Add;
        events.Start();
        events.SetCurrentApplication(99);

        events.SetEnabledFromUser(true);

        Assert.Equal(MacAccessibilityCapabilityState.Unavailable, events.CapabilityState);
        Assert.Equal(events.CapabilityState, states.Last());
    }

    [Fact]
    public void SwitchingApplicationAfterNativeFailure_ImmediatelyRecoversCapabilityAndObservations()
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        config.Update(value => value.WindowTitleObservationEnabled = true);
        var native = new FakeNative { IsProcessTrusted = true, FocusedWindowTitle = "New App" };
        using var events = new MacAccessibilityEvents(config, native, new FakeCommandRunner());
        var received = new List<MacAccessibilityObservation>();
        events.Observation += received.Add;
        events.Start();
        events.SetCurrentApplication(99);
        native.RaiseFailure(new InvalidOperationException("Application observation failed"));
        Assert.Equal(MacAccessibilityCapabilityState.Unavailable, events.CapabilityState);

        events.SetCurrentApplication(100);
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
