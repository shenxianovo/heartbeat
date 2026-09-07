using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Desktop.Mac.Native;

namespace Heartbeat.Desktop.Mac;

public sealed class MacDesktopState : IDesktopState, IDisposable
{
    private readonly MacConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IDesktopLoginStart _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;
    private readonly IMacAccessibilityEvents _accessibility;
    private readonly IMacInputMonitoringEvents _inputMonitoring;
    private readonly IMacApplicationLocator _applicationLocator;

    public MacDesktopState(
        MacConfigManager config,
        ICollectionStatus collection,
        IDesktopLoginStart loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads,
        IMacAccessibilityEvents accessibility,
        IMacInputMonitoringEvents inputMonitoring,
        IMacApplicationLocator applicationLocator)
    {
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;
        _accessibility = accessibility;
        _inputMonitoring = inputMonitoring;
        _applicationLocator = applicationLocator;
        _config.ConfigChanged += OnConfigChanged;
        _collection.CurrentActivityChanged += OnCurrentActivityChanged;
        _compatibility.Changed += OnCompatibilityChanged;
        _uploads.Changed += Publish;
        _accessibility.CapabilityChanged += OnAccessibilityCapabilityChanged;
        _inputMonitoring.CapabilityChanged += OnInputMonitoringCapabilityChanged;
    }

    public DesktopStateSnapshot Current => BuildSnapshot();
    public event Action<DesktopStateSnapshot>? Changed;

    public void SaveSettings(DesktopSettingsInput settings) =>
        _config.Update(config =>
        {
            config.ApiKey = settings.ApiKey;
            config.DeviceName = settings.DeviceName;
            config.UploadIntervalMinutes = settings.UploadIntervalMinutes;
        });

    public void SetLoginStartEnabled(bool enabled)
    {
        if (enabled && Environment.ProcessPath is { Length: > 0 } executable)
            _loginStart.Enable(executable);
        else if (!enabled)
            _loginStart.Disable();
        Publish();
    }

    public void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled)
    {
        switch (capability)
        {
            case SystemCapability.WindowActivity:
                _accessibility.SetEnabledFromUser(enabled);
                break;
            case SystemCapability.InteractionSignal:
                _inputMonitoring.SetInteractionSignalEnabledFromUser(enabled);
                break;
            case SystemCapability.InputEventRecording:
                _inputMonitoring.SetInputEventRecordingEnabledFromUser(enabled);
                break;
        }
    }

    public void RecoverSystemCapability(SystemCapability capability)
    {
        if (capability is SystemCapability.WindowActivity
            or SystemCapability.InteractionSignal
            or SystemCapability.InputEventRecording)
        {
            // Prepare the exact running app/binary in Finder before System Settings
            // becomes frontmost, so the user can add or drag it into the privacy list.
            _applicationLocator.RevealFromUser();
        }

        if (capability == SystemCapability.WindowActivity)
            _accessibility.OpenPermissionSettingsFromUser();
        else if (capability is SystemCapability.InteractionSignal or SystemCapability.InputEventRecording)
            _inputMonitoring.OpenPermissionSettingsFromUser();
    }

    public void RevealSystemCapabilityApplication(SystemCapability capability)
    {
        if (capability != SystemCapability.ForegroundApp)
            _applicationLocator.RevealFromUser();
    }

    public void SetThemeMode(DesktopThemeMode mode) =>
        _config.Update(config => config.ThemeMode = mode.ToString());

    private DesktopStateSnapshot BuildSnapshot()
    {
        var config = _config.Current;
        return new DesktopStateSnapshot(
            _collection.CurrentActivity,
            new DesktopSettingsSnapshot(
                config.ApiKey,
                config.DeviceName,
                config.UploadIntervalMinutes,
                ParseThemeMode(config.ThemeMode)),
            _loginStart.IsEnabled,
            _compatibility.Current,
            _uploads.Snapshot,
            BuildCapabilities(config)) { LoginStartSupported = _loginStart.IsSupported };
    }

    private DesktopCapabilitySnapshot BuildCapabilities(MacAgentConfig config) => new(
        new Dictionary<SystemCapability, SystemCapabilityState>
        {
            [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
            [SystemCapability.WindowActivity] = WindowActivityState(config.WindowTitleObservationEnabled),
            [SystemCapability.InteractionSignal] = InteractionSignalState(config),
            [SystemCapability.InputEventRecording] = InputEventRecordingState(config.InputEventRecordingEnabled),
        });

    private SystemCapabilityState WindowActivityState(bool requested) => new(
        requested,
        !requested ? CapabilityAvailability.Available : _accessibility.CapabilityState switch
        {
            MacAccessibilityCapabilityState.Available => CapabilityAvailability.Available,
            MacAccessibilityCapabilityState.PermissionRequired => CapabilityAvailability.PermissionRequired,
            _ => CapabilityAvailability.Unavailable,
        },
        requested && _accessibility.CapabilityState == MacAccessibilityCapabilityState.PermissionRequired,
        requested && _accessibility.CapabilityState == MacAccessibilityCapabilityState.PermissionRequired);

    private SystemCapabilityState InteractionSignalState(MacAgentConfig config) => new(
        config.InteractionSignalEnabled,
        !config.InteractionSignalEnabled
            ? CapabilityAvailability.Available
            : !config.WindowTitleObservationEnabled
                ? CapabilityAvailability.Paused
                : InputMonitoringAvailability(),
        config.InteractionSignalEnabled
            && config.WindowTitleObservationEnabled
            && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired,
        config.InteractionSignalEnabled
            && config.WindowTitleObservationEnabled
            && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired);

    private SystemCapabilityState InputEventRecordingState(bool requested) => new(
        requested,
        !requested ? CapabilityAvailability.Available : InputMonitoringAvailability(),
        requested && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired,
        requested && _inputMonitoring.CapabilityState == MacInputMonitoringCapabilityState.PermissionRequired);

    private CapabilityAvailability InputMonitoringAvailability() => _inputMonitoring.CapabilityState switch
    {
        MacInputMonitoringCapabilityState.Available => CapabilityAvailability.Available,
        MacInputMonitoringCapabilityState.PermissionRequired => CapabilityAvailability.PermissionRequired,
        _ => CapabilityAvailability.Unavailable,
    };

    private static DesktopThemeMode ParseThemeMode(string? value) =>
        Enum.TryParse<DesktopThemeMode>(value, true, out var mode)
            ? mode
            : DesktopThemeMode.System;

    private void OnConfigChanged(MacAgentConfig _) => Publish();
    private void OnCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void OnCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void OnAccessibilityCapabilityChanged(MacAccessibilityCapabilityState _) => Publish();
    private void OnInputMonitoringCapabilityChanged(MacInputMonitoringCapabilityState _) => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= OnConfigChanged;
        _collection.CurrentActivityChanged -= OnCurrentActivityChanged;
        _compatibility.Changed -= OnCompatibilityChanged;
        _uploads.Changed -= Publish;
        _accessibility.CapabilityChanged -= OnAccessibilityCapabilityChanged;
        _inputMonitoring.CapabilityChanged -= OnInputMonitoringCapabilityChanged;
        GC.SuppressFinalize(this);
    }
}
