using Heartbeat.Collection.Hub.Hosting;

namespace Heartbeat.Desktop.UI.Presentation;

/// <summary>One admission policy for both platform adapters. The platform state is borrowed.</summary>
public sealed class DesktopStateCommands : IDesktopState, IDisposable
{
    private readonly IDesktopState _state;
    private readonly HostOperationAdmission _admission;

    public DesktopStateCommands(IDesktopState state, HostOperationAdmission admission)
    {
        _state = state;
        _admission = admission;
        state.Changed += OnChanged;
        admission.Closed += OnClosed;
    }

    public DesktopStateSnapshot Current => _state.Current with { ChangesAccepted = _admission.IsOpen };
    public event Action<DesktopStateSnapshot>? Changed;
    private void OnChanged(DesktopStateSnapshot snapshot) =>
        Changed?.Invoke(snapshot with { ChangesAccepted = _admission.IsOpen });
    private void OnClosed() => OnChanged(_state.Current);
    public void SaveSettings(DesktopSettingsInput settings) => _admission.Run(() => _state.SaveSettings(settings));
    public void SetLoginStartEnabled(bool enabled) => _admission.Run(() => _state.SetLoginStartEnabled(enabled));
    public void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled) =>
        _admission.Run(() => _state.SetSystemCapabilityEnabled(capability, enabled));
    public void RecoverSystemCapability(SystemCapability capability) =>
        _admission.Run(() => _state.RecoverSystemCapability(capability));
    public void RevealSystemCapabilityApplication(SystemCapability capability) =>
        _admission.Run(() => _state.RevealSystemCapabilityApplication(capability));
    public void SetThemeMode(DesktopThemeMode mode) => _admission.Run(() => _state.SetThemeMode(mode));

    public void Dispose()
    {
        _state.Changed -= OnChanged;
        _admission.Closed -= OnClosed;
    }
}
