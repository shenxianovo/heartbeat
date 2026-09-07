using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Desktop.UI.Presentation;

public enum DesktopThemeMode
{
    System,
    Light,
    Dark
}

public sealed record DesktopSettingsSnapshot(
    string ApiKey,
    string DeviceName,
    int UploadIntervalMinutes,
    DesktopThemeMode ThemeMode = DesktopThemeMode.System)
{
    public static DesktopSettingsSnapshot Default { get; } = new("", "", 1);
}

public sealed record DesktopSettingsInput(
    string ApiKey,
    string DeviceName,
    int UploadIntervalMinutes);

public enum CapabilityAvailability
{
    Available,
    Unavailable,
    PermissionRequired,
    Paused
}

public enum SystemCapability
{
    ForegroundApp,
    WindowActivity,
    InteractionSignal,
    InputEventRecording
}

public sealed record SystemCapabilityState(
    bool? RequestedEnabled,
    CapabilityAvailability Availability,
    bool RecoveryActionAvailable = false,
    bool ApplicationLocationActionAvailable = false);

public sealed record DesktopCapabilitySnapshot(
    IReadOnlyDictionary<SystemCapability, SystemCapabilityState> System)
{
    public SystemCapabilityState Get(SystemCapability capability) => System[capability];

    public static DesktopCapabilitySnapshot WindowsFull { get; } = new(
        new Dictionary<SystemCapability, SystemCapabilityState>
        {
            [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
            [SystemCapability.WindowActivity] = new(true, CapabilityAvailability.Available),
            [SystemCapability.InteractionSignal] = new(true, CapabilityAvailability.Available),
            [SystemCapability.InputEventRecording] = new(true, CapabilityAvailability.Available),
        });

    public static DesktopCapabilitySnapshot MacAppOnly { get; } = new(
        new Dictionary<SystemCapability, SystemCapabilityState>
        {
            [SystemCapability.ForegroundApp] = new(null, CapabilityAvailability.Available),
            [SystemCapability.WindowActivity] = new(false, CapabilityAvailability.Available),
            [SystemCapability.InteractionSignal] = new(false, CapabilityAvailability.Available),
            [SystemCapability.InputEventRecording] = new(false, CapabilityAvailability.Available),
        });
}

public sealed record DesktopStateSnapshot(
    CurrentActivity? CurrentActivity,
    DesktopSettingsSnapshot Settings,
    bool LoginStartEnabled,
    ClientCompatibilitySnapshot Compatibility,
    IReadOnlyDictionary<string, UploadStreamStatus> UploadStreams,
    DesktopCapabilitySnapshot Capabilities)
{
    public bool LoginStartSupported { get; init; } = true;

    public static DesktopStateSnapshot Empty { get; } = new(
        null,
        DesktopSettingsSnapshot.Default,
        false,
        new ClientCompatibilitySnapshot(false),
        new Dictionary<string, UploadStreamStatus>(),
        DesktopCapabilitySnapshot.WindowsFull);
}

/// <summary>
/// Platform-head seam consumed by the shared desktop presentation module.
/// It combines portable hub state with platform configuration without exposing
/// Windows or macOS persistence details to Avalonia ViewModels.
/// </summary>
public interface IDesktopState
{
    DesktopStateSnapshot Current { get; }
    event Action<DesktopStateSnapshot>? Changed;

    void SaveSettings(DesktopSettingsInput settings);
    void SetLoginStartEnabled(bool enabled);
    void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled);
    void RecoverSystemCapability(SystemCapability capability);
    void RevealSystemCapabilityApplication(SystemCapability capability);
    void SetThemeMode(DesktopThemeMode mode);
}
