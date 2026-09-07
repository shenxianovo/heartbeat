using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Models;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopState : IDesktopState, IDisposable
{
    private readonly ConfigManager _config;
    private readonly ICollectionStatus _collection;
    private readonly IAutoStartService _loginStart;
    private readonly IClientCompatibilityStatus _compatibility;
    private readonly IUploadStatus _uploads;
    private readonly InputObservationStatus _input;

    public WindowsDesktopState(
        ConfigManager config,
        ICollectionStatus collection,
        IAutoStartService loginStart,
        IClientCompatibilityStatus compatibility,
        IUploadStatus uploads, InputObservationStatus? input = null)
    {
        _input = input ?? new InputObservationStatus();
        _input.Changed += Publish;
        _config = config;
        _collection = collection;
        _loginStart = loginStart;
        _compatibility = compatibility;
        _uploads = uploads;

        ReconcileLoginStartRegistration();
        _config.ConfigChanged += HandleConfigChanged;
        _collection.CurrentActivityChanged += HandleCurrentActivityChanged;
        _compatibility.Changed += HandleCompatibilityChanged;
        _uploads.Changed += HandleUploadChanged;
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
        if (enabled)
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
                _loginStart.Enable(executablePath);
        }
        else
        {
            _loginStart.Disable();
        }
        Publish();
    }

    public void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled) =>
        _config.Update(config =>
        {
            switch (capability)
            {
                case SystemCapability.WindowActivity:
                    config.WindowActivityCollectionEnabled = enabled;
                    break;
                case SystemCapability.InteractionSignal:
                    config.InteractionSignalEnabled = enabled;
                    break;
                case SystemCapability.InputEventRecording:
                    config.InputEventRecordingEnabled = enabled;
                    break;
            }
        });

    public void RecoverSystemCapability(SystemCapability capability) { }

    public void RevealSystemCapabilityApplication(SystemCapability capability) { }

    public void SetThemeMode(DesktopThemeMode mode) =>
        _config.Update(config => config.ThemeMode = mode.ToString());

    private void ReconcileLoginStartRegistration()
    {
        if (!_loginStart.IsEnabled)
            return;

        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
            _loginStart.Enable(executablePath);
    }

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
            new DesktopCapabilitySnapshot(new Dictionary<SystemCapability, SystemCapabilityState>
            {
                [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
                [SystemCapability.WindowActivity] = new(
                    config.WindowActivityCollectionEnabled,
                    CapabilityAvailability.Available),
                [SystemCapability.InteractionSignal] = new(
                    config.InteractionSignalEnabled,
                    !config.WindowActivityCollectionEnabled ? CapabilityAvailability.Paused
                        : _input.IsAvailable ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
                [SystemCapability.InputEventRecording] = new(
                    config.InputEventRecordingEnabled,
                    _input.IsAvailable ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            }));
    }

    private static DesktopThemeMode ParseThemeMode(string? value) =>
        Enum.TryParse<DesktopThemeMode>(value, true, out var mode)
            ? mode
            : DesktopThemeMode.System;

    private void HandleConfigChanged(AgentConfig _) => Publish();
    private void HandleCurrentActivityChanged(CurrentActivity? _) => Publish();
    private void HandleCompatibilityChanged(ClientCompatibilitySnapshot _) => Publish();
    private void HandleUploadChanged() => Publish();
    private void Publish() => Changed?.Invoke(BuildSnapshot());

    public void Dispose()
    {
        _config.ConfigChanged -= HandleConfigChanged;
        _collection.CurrentActivityChanged -= HandleCurrentActivityChanged;
        _compatibility.Changed -= HandleCompatibilityChanged;
        _uploads.Changed -= HandleUploadChanged;
        _input.Changed -= Publish;
    }
}
