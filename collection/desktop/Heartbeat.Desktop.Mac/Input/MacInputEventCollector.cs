using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Native;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Mac.Input;

public sealed class MacInputEventCollector :
    IHostedService,
    IMacInputMonitoringEvents,
    IInputEventRecordingPolicy,
    IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan PermissionPollInterval = TimeSpan.FromSeconds(1);
    private const string InputMonitoringSettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent";

    private readonly MacConfigManager _config;
    private readonly IMacInputMonitoringNative _native;
    private readonly IMacCommandRunner _commandRunner;
    private readonly IInputActivitySignal _inputActivity;
    private readonly InputEventBuffer _buffer;
    private readonly object _gate = new();
    private bool _started;
    private readonly ObservationCapability<MacInputMonitoringCapabilityState, bool> _capability;
    private bool _lastRecordingEnabled;
    private event Action<bool>? RecordingChanged;

    public MacInputEventCollector(
        MacConfigManager config,
        IMacInputMonitoringNative native,
        IMacCommandRunner commandRunner,
        IInputActivitySignal inputActivity,
        InputEventBuffer buffer)
    {
        _config = config;
        _native = native;
        _commandRunner = commandRunner;
        _inputActivity = inputActivity;
        _buffer = buffer;
        _capability = new(ComputeState(), MacInputMonitoringCapabilityState.Unavailable, ReadDesired,
            _ => _native.StartListening(), _native.StopListening, PermissionPollInterval);
        _lastRecordingEnabled = config.Current.InputEventRecordingEnabled;
    }

    public event Action<MacInputMonitoringCapabilityState>? CapabilityChanged
    {
        add => _capability.Changed += value;
        remove => _capability.Changed -= value;
    }

    public MacInputMonitoringCapabilityState CapabilityState => _capability.State;

    public bool Enabled => _config.Current.InputEventRecordingEnabled;

    event Action<bool>? IInputEventRecordingPolicy.Changed
    {
        add => RecordingChanged += value;
        remove => RecordingChanged -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                _config.ConfigChanged += OnConfigChanged;
                _native.Observation += OnNativeObservation;
                _native.Failed += _capability.ReportFailure;
            }
            return _capability.Start().WaitAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _started = false;
            _config.ConfigChanged -= OnConfigChanged;
            _native.Observation -= OnNativeObservation;
            _native.Failed -= _capability.ReportFailure;
            return _capability.Stop().WaitAsync(cancellationToken);
        }
    }

    public void SetInteractionSignalEnabledFromUser(bool enabled)
    {
        _config.Update(value => value.InteractionSignalEnabled = enabled);
        if (enabled && !_native.IsAuthorized)
            _native.RequestAuthorization();
        _capability.Refresh(retry: true);
    }

    public void SetInputEventRecordingEnabledFromUser(bool enabled)
    {
        _config.Update(value => value.InputEventRecordingEnabled = enabled);
        if (enabled && !_native.IsAuthorized)
            _native.RequestAuthorization();
        _capability.Refresh(retry: true);
    }

    public void OpenPermissionSettingsFromUser()
    {
        var result = _commandRunner.Run("/usr/bin/open", [InputMonitoringSettingsUrl]);
        if (result.ExitCode != 0)
            Log.Warning("打开 macOS Input Monitoring 设置失败: {Error}", result.StandardError);
        _capability.Refresh();
    }

    public Task RefreshPermission() => _capability.Refresh();

    private void OnConfigChanged(MacAgentConfig value)
    {
        if (_lastRecordingEnabled != value.InputEventRecordingEnabled)
        {
            _lastRecordingEnabled = value.InputEventRecordingEnabled;
            if (!value.InputEventRecordingEnabled)
                _buffer.ResetTransientState();
            RecordingChanged?.Invoke(value.InputEventRecordingEnabled);
        }
        _capability.Refresh();
    }

    private ObservationTarget<MacInputMonitoringCapabilityState, bool> ReadDesired()
    {
        var config = _config.Current;
        var state = ComputeState();
        var run = state == MacInputMonitoringCapabilityState.Available
            && (config.InputEventRecordingEnabled || (config.InteractionSignalEnabled && config.WindowTitleObservationEnabled));
        return new(state, run ? true : null);
    }

    private MacInputMonitoringCapabilityState ComputeState()
    {
        var config = _config.Current;
        if (!config.InteractionSignalEnabled && !config.InputEventRecordingEnabled)
            return MacInputMonitoringCapabilityState.Disabled;
        if (!_native.IsAvailable)
            return MacInputMonitoringCapabilityState.Unavailable;
        return _native.IsAuthorized
            ? MacInputMonitoringCapabilityState.Available
            : MacInputMonitoringCapabilityState.PermissionRequired;
    }

    private void OnNativeObservation(MacInputObservation observation)
    {
        if (!_capability.AcceptsObservations) return;
        var config = _config.Current;
        switch (observation.Kind)
        {
            case MacInputObservationKind.KeyDown when config.InputEventRecordingEnabled:
                if (MacKeyPositionMapper.TryMap((ushort)observation.Value, out var downPosition))
                    _buffer.OnKeyDown(downPosition);
                break;
            case MacInputObservationKind.KeyUp:
                if (MacKeyPositionMapper.TryMap((ushort)observation.Value, out var upPosition))
                    _buffer.OnKeyUp(upPosition);
                break;
            case MacInputObservationKind.MouseButton:
                if (config.InteractionSignalEnabled && config.WindowTitleObservationEnabled)
                    _inputActivity.MarkClick();
                if (config.InputEventRecordingEnabled)
                    _buffer.OnMouseButton((short)observation.Value);
                break;
            case MacInputObservationKind.Scroll when config.InputEventRecordingEnabled:
                _buffer.OnScroll(observation.Value);
                break;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
