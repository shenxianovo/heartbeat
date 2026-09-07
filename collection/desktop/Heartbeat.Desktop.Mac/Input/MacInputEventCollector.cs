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
    private MacInputMonitoringCapabilityState _capabilityState;
    private bool _started;
    private bool _hookRunning;
    private bool _nativeFailed;
    private readonly ObservationReconciler _transitions;
    private Task? _stopTask;
    private bool _lastRecordingEnabled;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private event Action<bool>? RecordingChanged;

    public MacInputEventCollector(
        MacConfigManager config,
        IMacInputMonitoringNative native,
        IMacCommandRunner commandRunner,
        IInputActivitySignal inputActivity,
        InputEventBuffer buffer)
    {
        _transitions = new ObservationReconciler(ReconcileHook);
        _config = config;
        _native = native;
        _commandRunner = commandRunner;
        _inputActivity = inputActivity;
        _buffer = buffer;
        _capabilityState = ComputeState();
        _lastRecordingEnabled = config.Current.InputEventRecordingEnabled;
    }

    public event Action<MacInputMonitoringCapabilityState>? CapabilityChanged;

    public MacInputMonitoringCapabilityState CapabilityState
    {
        get
        {
            lock (_gate)
                return _capabilityState;
        }
    }

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
            if (_stopTask is not null) throw new InvalidOperationException("Input collection is stopping.");
            if (_started) return Task.CompletedTask;
            _started = true;
            _config.ConfigChanged += OnConfigChanged;
            _native.Observation += OnNativeObservation;
            _native.Failed += OnNativeFailure;
            var polling = new CancellationTokenSource();
            _pollCts = polling;
            _pollTask = Task.Run(() => PollPermissionAsync(polling.Token), CancellationToken.None);
        }
        RefreshCapability();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask.WaitAsync(cancellationToken);
            _started = false;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            _config.ConfigChanged -= OnConfigChanged;
            _native.Observation -= OnNativeObservation;
            _native.Failed -= OnNativeFailure;
        }
        _ = CompleteStopAsync(completion);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            _pollCts?.Cancel();
            try { await _transitions.Reconcile().ConfigureAwait(false); }
            finally
            {
                try { if (_pollTask is not null) await _pollTask.ConfigureAwait(false); }
                finally { _pollCts?.Dispose(); }
            }
            completion.SetResult();
        }
        catch (Exception exception) { completion.SetException(exception); }
    }

    public void SetInteractionSignalEnabledFromUser(bool enabled)
    {
        lock (_gate) _nativeFailed = false;
        _config.Update(value => value.InteractionSignalEnabled = enabled);
        if (enabled && !_native.IsAuthorized)
            _native.RequestAuthorization();
        RefreshCapability();
    }

    public void SetInputEventRecordingEnabledFromUser(bool enabled)
    {
        lock (_gate) _nativeFailed = false;
        _config.Update(value => value.InputEventRecordingEnabled = enabled);
        if (enabled && !_native.IsAuthorized)
            _native.RequestAuthorization();
        RefreshCapability();
    }

    public void OpenPermissionSettingsFromUser()
    {
        var result = _commandRunner.Run("/usr/bin/open", [InputMonitoringSettingsUrl]);
        if (result.ExitCode != 0)
            Log.Warning("打开 macOS Input Monitoring 设置失败: {Error}", result.StandardError);
        RefreshCapability();
    }

    public void RefreshPermission() => RefreshCapability();

    private async Task PollPermissionAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PermissionPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                RefreshCapability();
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void OnConfigChanged(MacAgentConfig value)
    {
        if (_lastRecordingEnabled != value.InputEventRecordingEnabled)
        {
            _lastRecordingEnabled = value.InputEventRecordingEnabled;
            if (!value.InputEventRecordingEnabled)
                _buffer.ResetTransientState();
            RecordingChanged?.Invoke(value.InputEventRecordingEnabled);
        }
        RefreshCapability();
    }

    private void RefreshCapability()
    {
        var current = ComputeState();
        MacInputMonitoringCapabilityState previous;
        lock (_gate)
        {
            previous = _capabilityState;
            _capabilityState = current;
        }

        _ = ObserveTransitionAsync(_transitions.Reconcile());
        // A synchronous transition may have reported a native failure. Publish its resulting state.
        current = CapabilityState;
        if (previous != current)
            CapabilityChanged?.Invoke(current);
    }

    private MacInputMonitoringCapabilityState ComputeState()
    {
        var config = _config.Current;
        if (!config.InteractionSignalEnabled && !config.InputEventRecordingEnabled)
            return MacInputMonitoringCapabilityState.Disabled;
        if (_nativeFailed || !_native.IsAvailable)
            return MacInputMonitoringCapabilityState.Unavailable;
        return _native.IsAuthorized
            ? MacInputMonitoringCapabilityState.Available
            : MacInputMonitoringCapabilityState.PermissionRequired;
    }

    private void ReconcileHook()
    {
        try { ApplyHookState(); }
        catch
        {
            lock (_gate)
            {
                _nativeFailed = true;
                _capabilityState = MacInputMonitoringCapabilityState.Unavailable;
            }
            CapabilityChanged?.Invoke(MacInputMonitoringCapabilityState.Unavailable);
            throw;
        }
    }

    private void ApplyHookState()
    {
        var config = _config.Current;
        bool started;
        lock (_gate) started = _started;
        var shouldRun = started && ComputeState() == MacInputMonitoringCapabilityState.Available
            && (config.InputEventRecordingEnabled
                || (config.InteractionSignalEnabled && config.WindowTitleObservationEnabled));
        if (shouldRun && !_hookRunning)
        {
            _native.StartListening();
            _hookRunning = true;
        }
        else if (!shouldRun && _hookRunning)
        {
            _native.StopListening();
            _hookRunning = false;
        }
    }

    private async Task ObserveTransitionAsync(Task transition)
    {
        try { await transition; }
        catch (Exception exception)
        {
            Log.Warning(exception, "macOS input capability transition failed");
        }
    }

    private void OnNativeFailure(Exception exception)
    {
        lock (_gate)
        {
            if (!_started) return;
            _nativeFailed = true;
            _capabilityState = MacInputMonitoringCapabilityState.Unavailable;
        }
        Log.Warning(exception, "macOS input observation failed");
        _ = ObserveTransitionAsync(_transitions.Reconcile());
        CapabilityChanged?.Invoke(MacInputMonitoringCapabilityState.Unavailable);
    }

    private void OnNativeObservation(MacInputObservation observation)
    {
        lock (_gate) { if (!_started || _capabilityState != MacInputMonitoringCapabilityState.Available) return; }
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
