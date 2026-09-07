namespace Heartbeat.Desktop.Windows.Services;

/// <summary>Actual input observation availability, separate from the saved capability switches.</summary>
public sealed class InputObservationStatus
{
    public bool IsAvailable { get; private set; } = true;
    public event Action? Changed;
    internal void SetAvailable(bool value)
    {
        if (IsAvailable == value) return;
        IsAvailable = value;
        Changed?.Invoke();
    }
}
