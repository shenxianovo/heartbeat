using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Collector.System.Observations;

namespace Heartbeat.Desktop.Mac.Tests.Observations;

public sealed class MacDesktopObservationSourceTests
{
    [Fact]
    public async Task SwitchingApplication_PreservesItsStaticTitleWhileObserverChangesInBackground()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-title-{Guid.NewGuid()}");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var config = new MacConfigManager(new MacAgentPaths(root));
            config.Update(value => value.WindowTitleObservationEnabled = true);
            using var accessibility = new MacAccessibilityEvents(config, new NativeTitles(entered, release), new NoCommands());
            var desktop = new FakeEvents { FrontmostApplication = new("com.apple.Safari", null, "Safari", 99) };
            var source = new MacDesktopObservationSource(desktop, accessibility);
            var received = new List<DesktopObservation>();
            source.Observation += received.Add;
            source.Start();
            await accessibility.RefreshPermission();
            desktop.FrontmostApplication = new("com.apple.Terminal", null, "Terminal", 100);
            desktop.Activate();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                var activation = Assert.Single(received, value => value.Kind == DesktopObservationKind.AppActivated);
                Assert.Equal("Static terminal title", activation.Activity.Title);
            }
            finally { release.Set(); await accessibility.RefreshPermission(); source.Stop(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class NativeTitles(ManualResetEventSlim entered, ManualResetEventSlim release) : IMacAccessibilityNative
    {
        public event Action<Exception>? Failed { add { } remove { } }
        public event Action<MacAccessibilityObservation>? Observation { add { } remove { } }
        public bool IsAvailable => true;
        public bool IsProcessTrusted => true;
        public void RequestProcessTrust() { }
        public string? ReadFocusedWindowTitle(int processIdentifier) => processIdentifier == 100 ? "Static terminal title" : "Safari";
        public void ObserveApplication(int processIdentifier)
        {
            if (processIdentifier != 100) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
        public void StopObserving() { }
    }

    private sealed class NoCommands : IMacCommandRunner
    {
        public MacCommandResult Run(string fileName, IReadOnlyList<string> arguments) => throw new InvalidOperationException("No permission prompt expected");
    }

    private sealed class FakeAccessibility : IMacAccessibilityEvents
    {
        public event Action<MacAccessibilityObservation>? Observation;
        public event Action<MacAccessibilityCapabilityState>? CapabilityChanged;
        public bool Enabled { get; set; }
        public MacAccessibilityCapabilityState CapabilityState { get; set; } =
            MacAccessibilityCapabilityState.Disabled;
        public string? CurrentTitle { get; set; }
        public int CurrentProcessIdentifier { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void SetCurrentApplication(int processIdentifier) =>
            CurrentProcessIdentifier = processIdentifier;
        public void SetEnabledFromUser(bool enabled) => Enabled = enabled;
        public void OpenPermissionSettingsFromUser() { }
        public void Raise(MacAccessibilityObservation observation) => Observation?.Invoke(observation);
        public void PublishCapability(MacAccessibilityCapabilityState state)
        {
            CapabilityState = state;
            CapabilityChanged?.Invoke(state);
        }
    }

    private sealed class FakeEvents : IMacDesktopEvents
    {
        public event Action? ApplicationActivated;
        public event Action<MacAwayReason>? AwayEntered;
        public event Action<MacAwayReason>? AwayExited;

        public MacApplication? FrontmostApplication { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Activate() => ApplicationActivated?.Invoke();
        public void Enter(MacAwayReason reason) => AwayEntered?.Invoke(reason);
        public void Exit(MacAwayReason reason) => AwayExited?.Invoke(reason);
    }

    [Fact]
    public void ApplicationActivation_BecomesAppOnlySemanticObservation()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication(
                "com.microsoft.VSCode", null, "Visual Studio Code")
        };
        var source = new MacDesktopObservationSource(native, new FakeAccessibility());
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;

        source.Start();
        native.Activate();
        source.Stop();

        var observation = Assert.Single(received);
        Assert.Equal(DesktopObservationKind.AppActivated, observation.Kind);
        Assert.Equal("mac:com.microsoft.vscode", observation.Activity.AppIdentityKey);
        Assert.Null(observation.Activity.Title);
        Assert.Equal(1, native.StartCount);
        Assert.Equal(1, native.StopCount);
    }

    [Theory]
    [InlineData(MacAwayReason.ScreenLocked)]
    [InlineData(MacAwayReason.SessionInactive)]
    [InlineData(MacAwayReason.DisplaySleep)]
    [InlineData(MacAwayReason.SystemSleep)]
    public void HardAwayReason_BecomesAwayAndResumesWithFreshFrontmostApp(MacAwayReason reason)
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari")
        };
        var source = new MacDesktopObservationSource(native, new FakeAccessibility());
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Enter(reason);
        native.FrontmostApplication = new MacApplication("com.apple.Terminal", null, "Terminal");
        native.Exit(reason);

        Assert.Collection(
            received,
            entered => Assert.Equal(DesktopObservationKind.EnteredAway, entered.Kind),
            exited =>
            {
                Assert.Equal(DesktopObservationKind.ExitedAway, exited.Kind);
                Assert.Equal("mac:com.apple.terminal", exited.Activity.AppIdentityKey);
            });
    }

    [Fact]
    public void OverlappingHardAwayReasons_ProduceOneAwaySpan()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari")
        };
        var source = new MacDesktopObservationSource(native, new FakeAccessibility());
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Enter(MacAwayReason.SessionInactive);
        native.Enter(MacAwayReason.SystemSleep);
        native.Exit(MacAwayReason.SystemSleep);
        native.Exit(MacAwayReason.SessionInactive);

        Assert.Equal(
            [DesktopObservationKind.EnteredAway, DesktopObservationKind.ExitedAway],
            received.Select(observation => observation.Kind));
    }

    [Fact]
    public void WakeWithoutMatchingAway_DoesNotInventAnAwayExit()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari")
        };
        var source = new MacDesktopObservationSource(native, new FakeAccessibility());
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Exit(MacAwayReason.DisplaySleep);

        Assert.Empty(received);
    }

    [Fact]
    public void AccessibilityNotifications_PreserveFocusAndTitleSemanticKinds()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication(
                "com.microsoft.VSCode", null, "Visual Studio Code", 42)
        };
        var accessibility = new FakeAccessibility
        {
            Enabled = true,
            CapabilityState = MacAccessibilityCapabilityState.Available,
            CurrentTitle = "README.md"
        };
        var source = new MacDesktopObservationSource(native, accessibility);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        accessibility.Raise(new MacAccessibilityObservation(
            MacAccessibilityObservationKind.FocusedWindowChanged,
            "Program.cs"));
        accessibility.Raise(new MacAccessibilityObservation(
            MacAccessibilityObservationKind.TitleChanged,
            "Program.cs — saved"));

        Assert.Collection(
            received,
            focused =>
            {
                Assert.Equal(DesktopObservationKind.FocusedWindowChanged, focused.Kind);
                Assert.Equal("Program.cs", focused.Activity.Title);
                Assert.Equal("mac:com.microsoft.vscode", focused.Activity.AppIdentityKey);
            },
            title =>
            {
                Assert.Equal(DesktopObservationKind.TitleChanged, title.Kind);
                Assert.Equal("Program.cs — saved", title.Activity.Title);
            });
        Assert.Equal(42, accessibility.CurrentProcessIdentifier);
    }

    [Fact]
    public void PermissionUnavailable_StillForwardsAppOnlyActivation()
    {
        var native = new FakeEvents
        {
            FrontmostApplication = new MacApplication("com.apple.Safari", null, "Safari", 7)
        };
        var accessibility = new FakeAccessibility
        {
            Enabled = true,
            CapabilityState = MacAccessibilityCapabilityState.PermissionRequired
        };
        var source = new MacDesktopObservationSource(native, accessibility);
        var received = new List<DesktopObservation>();
        source.Observation += received.Add;
        source.Start();

        native.Activate();

        var observation = Assert.Single(received);
        Assert.Equal(DesktopObservationKind.AppActivated, observation.Kind);
        Assert.Equal("mac:com.apple.safari", observation.Activity.AppIdentityKey);
        Assert.Null(observation.Activity.Title);
    }
}
