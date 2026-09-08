using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac.Tests;

public sealed class MacDesktopStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-state-{Guid.NewGuid()}");

    [Fact]
    public void Snapshot_OffersOptionalTitleDepthWithoutClaimingUnavailablePermission()
    {
        var login = new FakeLoginStart();
        var accessibility = new FakeAccessibility();
        using var state = Build(login, accessibility);

        Assert.Equal(
            CapabilityAvailability.Available,
            state.Current.Capabilities.Get(SystemCapability.WindowActivity).Availability);
        Assert.False(state.Current.Capabilities.Get(SystemCapability.WindowActivity).RequestedEnabled);
        Assert.False(state.Current.Capabilities.Get(SystemCapability.InputEventRecording).RequestedEnabled);

        state.SetSystemCapabilityEnabled(SystemCapability.WindowActivity, true);
        state.SetSystemCapabilityEnabled(SystemCapability.InputEventRecording, true);

        Assert.True(accessibility.Enabled);
        Assert.True(state.Current.Capabilities.Get(SystemCapability.InputEventRecording).RequestedEnabled);
    }

    [Fact]
    public void PermissionRequiredSnapshot_OffersARecoveryPathAndKeepsAppObservationAvailable()
    {
        var accessibility = new FakeAccessibility
        {
            Enabled = true,
            CapabilityState = MacAccessibilityCapabilityState.PermissionRequired
        };
        var applicationLocator = new FakeApplicationLocator();
        using var state = Build(
            new FakeLoginStart(),
            accessibility,
            applicationLocator: applicationLocator);

        Assert.Equal(
            CapabilityAvailability.Available,
            state.Current.Capabilities.Get(SystemCapability.ForegroundApp).Availability);
        Assert.Equal(
            CapabilityAvailability.PermissionRequired,
            state.Current.Capabilities.Get(SystemCapability.WindowActivity).Availability);
        Assert.True(state.Current.Capabilities.Get(SystemCapability.WindowActivity).RecoveryActionAvailable);
        Assert.True(state.Current.Capabilities.Get(SystemCapability.WindowActivity).ApplicationLocationActionAvailable);

        state.RecoverSystemCapability(SystemCapability.WindowActivity);

        Assert.Equal(1, accessibility.OpenSettingsCount);
        Assert.Equal(1, applicationLocator.RevealCount);

        state.RevealSystemCapabilityApplication(SystemCapability.WindowActivity);

        Assert.Equal(2, applicationLocator.RevealCount);
    }

    [Fact]
    public void InteractionIntent_RemainsEnabledWhenInputMonitoringPermissionIsMissing()
    {
        var accessibility = new FakeAccessibility
        {
            Enabled = true,
            CapabilityState = MacAccessibilityCapabilityState.Available
        };
        var inputMonitoring = new FakeInputMonitoring
        {
            CapabilityState = MacInputMonitoringCapabilityState.PermissionRequired
        };
        using var state = Build(new FakeLoginStart(), accessibility, inputMonitoring);

        state.SetSystemCapabilityEnabled(SystemCapability.InteractionSignal, true);

        var capability = state.Current.Capabilities.Get(SystemCapability.InteractionSignal);
        Assert.True(capability.RequestedEnabled);
        Assert.Equal(CapabilityAvailability.PermissionRequired, capability.Availability);
        Assert.True(capability.RecoveryActionAvailable);

        state.RecoverSystemCapability(SystemCapability.InteractionSignal);

        Assert.Equal(1, inputMonitoring.OpenSettingsCount);
    }

    [Fact]
    public void SettingsCollectorsAndLoginStart_UsePlatformHeadSeams()
    {
        var login = new FakeLoginStart();
        using var state = Build(login);

        state.SaveSettings(new DesktopSettingsInput(" key ", "Studio", 5));
        state.SetLoginStartEnabled(true);
        state.SetThemeMode(DesktopThemeMode.Dark);

        Assert.Equal(" key ", state.Current.Settings.ApiKey);
        Assert.Equal("Studio", state.Current.Settings.DeviceName);
        Assert.Equal(5, state.Current.Settings.UploadIntervalMinutes);
        Assert.Equal(DesktopThemeMode.Dark, state.Current.Settings.ThemeMode);
        Assert.True(state.Current.LoginStartEnabled);
        Assert.Equal(Environment.ProcessPath, login.EnabledExecutable);
    }

    [Fact]
    public void ReadingState_DoesNotRewriteInstallation()
    {
        var login = new FakeLoginStart(isEnabled: true);

        using var state = Build(login);

        Assert.Null(login.EnabledExecutable);
        Assert.Equal(0, login.EnableCount);
    }

    [Fact]
    public void ClosedAdmission_RejectsAllDesktopChangesAndPreservesReadableState()
    {
        var login = new FakeLoginStart();
        var accessibility = new FakeAccessibility();
        var locator = new FakeApplicationLocator();
        using var platform = Build(login, accessibility, applicationLocator: locator);
        var admission = new HostOperationAdmission();
        using var state = new DesktopStateCommands(platform, admission);
        state.SaveSettings(new("key", "Studio", 5));
        var settings = state.Current.Settings;
        DesktopStateSnapshot? observed = null;
        state.Changed += snapshot => observed = snapshot;
        admission.Close();
        Assert.False(observed!.ChangesAccepted);
        Assert.Throws<InvalidOperationException>(() => state.SaveSettings(new("new", "changed", 2)));
        Assert.Throws<InvalidOperationException>(() => state.SetLoginStartEnabled(true));
        Assert.Throws<InvalidOperationException>(() => state.SetThemeMode(DesktopThemeMode.Dark));
        Assert.Throws<InvalidOperationException>(() => state.SetSystemCapabilityEnabled(SystemCapability.WindowActivity, true));
        Assert.Throws<InvalidOperationException>(() => state.RecoverSystemCapability(SystemCapability.WindowActivity));
        Assert.Throws<InvalidOperationException>(() => state.RevealSystemCapabilityApplication(SystemCapability.WindowActivity));
        Assert.Equal(settings, state.Current.Settings);
        Assert.False(login.IsEnabled);
        Assert.False(accessibility.Enabled);
        Assert.Equal(0, locator.RevealCount);
    }

    private MacDesktopState Build(
        FakeLoginStart login,
        FakeAccessibility? accessibility = null,
        FakeInputMonitoring? inputMonitoring = null,
        FakeApplicationLocator? applicationLocator = null)
    {
        var config = new MacConfigManager(new MacAgentPaths(_root));
        accessibility ??= new FakeAccessibility();
        accessibility.SetEnabledAction = enabled =>
            config.Update(value => value.WindowTitleObservationEnabled = enabled);
        inputMonitoring ??= new FakeInputMonitoring();
        inputMonitoring.SetInteractionSignalEnabledAction = enabled =>
            config.Update(value => value.InteractionSignalEnabled = enabled);
        inputMonitoring.SetInputEventRecordingEnabledAction = enabled =>
            config.Update(value => value.InputEventRecordingEnabled = enabled);
        applicationLocator ??= new FakeApplicationLocator();
        if (accessibility.Enabled)
            config.Update(value => value.WindowTitleObservationEnabled = true);
        return new MacDesktopState(
            config,
            new FakeCollectionStatus(),
            login,
            new ClientCompatibilityStatus(),
            new UploadStatusRegistry(),
            accessibility,
            inputMonitoring,
            applicationLocator);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeLoginStart : IDesktopLoginStart
    {
        public FakeLoginStart(bool isEnabled = false) => IsEnabled = isEnabled;

        public bool IsEnabled { get; private set; }
        public string? EnabledExecutable { get; private set; }
        public int EnableCount { get; private set; }
        public void Enable(string executablePath)
        {
            IsEnabled = true;
            EnabledExecutable = executablePath;
            EnableCount++;
        }
        public void Disable() => IsEnabled = false;
    }

    private sealed class FakeCollectionStatus : ICollectionStatus
    {
        public CurrentActivity? CurrentActivity => null;
        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen { get; } =
            new Dictionary<string, DateTimeOffset>();
        public event Action<CurrentActivity?>? CurrentActivityChanged { add { } remove { } }
    }

    private sealed class FakeAccessibility : IMacAccessibilityEvents
    {
        public event Action<MacAccessibilityObservation>? Observation { add { } remove { } }
        public event Action<MacAccessibilityCapabilityState>? CapabilityChanged;
        public bool Enabled { get; set; }
        public MacAccessibilityCapabilityState CapabilityState { get; set; } =
            MacAccessibilityCapabilityState.Disabled;
        public string? CurrentTitle => null;
        public int OpenSettingsCount { get; private set; }
        public Action<bool>? SetEnabledAction { get; set; }
        public void Start() { }
        public void Stop() { }
        public void SetCurrentApplication(int processIdentifier) { }
        public void SetEnabledFromUser(bool enabled)
        {
            SetEnabledAction?.Invoke(enabled);
            Enabled = enabled;
            CapabilityState = enabled
                ? MacAccessibilityCapabilityState.PermissionRequired
                : MacAccessibilityCapabilityState.Disabled;
            CapabilityChanged?.Invoke(CapabilityState);
        }
        public void OpenPermissionSettingsFromUser() => OpenSettingsCount++;
    }

    private sealed class FakeInputMonitoring : IMacInputMonitoringEvents
    {
        public event Action<MacInputMonitoringCapabilityState>? CapabilityChanged { add { } remove { } }
        public MacInputMonitoringCapabilityState CapabilityState { get; set; } =
            MacInputMonitoringCapabilityState.Disabled;
        public int OpenSettingsCount { get; private set; }
        public Action<bool>? SetInteractionSignalEnabledAction { get; set; }
        public Action<bool>? SetInputEventRecordingEnabledAction { get; set; }
        public void SetInteractionSignalEnabledFromUser(bool enabled) =>
            SetInteractionSignalEnabledAction?.Invoke(enabled);
        public void SetInputEventRecordingEnabledFromUser(bool enabled) =>
            SetInputEventRecordingEnabledAction?.Invoke(enabled);
        public void OpenPermissionSettingsFromUser() => OpenSettingsCount++;
    }

    private sealed class FakeApplicationLocator : IMacApplicationLocator
    {
        public int RevealCount { get; private set; }
        public void RevealFromUser() => RevealCount++;
    }
}
