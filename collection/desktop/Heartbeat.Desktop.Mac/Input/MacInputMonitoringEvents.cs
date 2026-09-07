namespace Heartbeat.Desktop.Mac.Input;

public enum MacInputMonitoringCapabilityState
{
    Disabled,
    PermissionRequired,
    Available,
    Unavailable,
}

public interface IMacInputMonitoringEvents
{
    event Action<MacInputMonitoringCapabilityState>? CapabilityChanged;
    MacInputMonitoringCapabilityState CapabilityState { get; }

    void SetInteractionSignalEnabledFromUser(bool enabled);
    void SetInputEventRecordingEnabledFromUser(bool enabled);
    void OpenPermissionSettingsFromUser();
}

public enum MacInputObservationKind
{
    KeyDown,
    KeyUp,
    MouseButton,
    Scroll,
}

public readonly record struct MacInputObservation(MacInputObservationKind Kind, int Value);

public interface IMacInputMonitoringNative
{
    event Action<Exception>? Failed;
    event Action<MacInputObservation>? Observation;
    bool IsAvailable { get; }
    bool IsAuthorized { get; }
    void RequestAuthorization();
    void StartListening();
    void StopListening();
}
