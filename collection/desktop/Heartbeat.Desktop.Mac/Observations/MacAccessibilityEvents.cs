using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Desktop.Mac.Native;
using Serilog;

namespace Heartbeat.Desktop.Mac.Observations;

public enum MacAccessibilityCapabilityState
{
    Disabled,
    PermissionRequired,
    Available,
    Unavailable,
}

public enum MacAccessibilityObservationKind
{
    FocusedWindowChanged,
    TitleChanged,
}

public readonly record struct MacAccessibilityObservation(
    MacAccessibilityObservationKind Kind,
    string? Title,
    int ProcessIdentifier = 0);

public interface IMacAccessibilityNative
{
    event Action<Exception>? Failed;
    event Action<MacAccessibilityObservation>? Observation;

    bool IsAvailable { get; }
    bool IsProcessTrusted { get; }

    void RequestProcessTrust();
    string? ReadFocusedWindowTitle(int processIdentifier);
    void ObserveApplication(int processIdentifier);
    void StopObserving();
}

public interface IMacAccessibilityEvents
{
    event Action<MacAccessibilityObservation>? Observation;
    event Action<MacAccessibilityCapabilityState>? CapabilityChanged;

    bool Enabled { get; }
    MacAccessibilityCapabilityState CapabilityState { get; }
    string? CurrentTitle { get; }

    void Start();
    void Stop();
    void SetCurrentApplication(int processIdentifier);
    void SetEnabledFromUser(bool enabled);
    void OpenPermissionSettingsFromUser();
}

/// <summary>
/// Accessibility 能力的生命周期边界。配置启用与 TCC 授权分开表示；启动和普通配置更新
/// 只检查权限，只有显式用户动作才请求权限或打开系统设置。
/// </summary>
public sealed class MacAccessibilityEvents : IMacAccessibilityEvents, IDisposable
{
    private readonly MacConfigManager _config;
    private readonly IMacAccessibilityNative _native;
    private readonly IMacCommandRunner _commandRunner;
    private readonly ObservationReconciler _transitions;
    public MacAccessibilityEvents(MacConfigManager config, IMacAccessibilityNative native,
        IMacCommandRunner commandRunner)
    {
        _config = config;
        _native = native;
        _commandRunner = commandRunner;
        _capabilityState = ComputeInitialState(config, native);
        _transitions = new ObservationReconciler(ReconcileObservation);
    }
    private static readonly TimeSpan PermissionPollInterval = TimeSpan.FromSeconds(1);
    private const string AccessibilitySettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private int _currentProcessIdentifier;
    private bool _started;
    private bool _nativeFailed;
    private Task? _stopTask;
    private MacAccessibilityCapabilityState _capabilityState;

    public event Action<MacAccessibilityObservation>? Observation;
    public event Action<MacAccessibilityCapabilityState>? CapabilityChanged;

    public bool Enabled => _config.Current.WindowTitleObservationEnabled;

    public MacAccessibilityCapabilityState CapabilityState
    {
        get
        {
            lock (_gate)
                return _capabilityState;
        }
    }

    public string? CurrentTitle
    {
        get
        {
            int processIdentifier;
            lock (_gate)
            {
                if (_capabilityState != MacAccessibilityCapabilityState.Available)
                    return null;
                processIdentifier = _currentProcessIdentifier;
            }

            return processIdentifier <= 0 ? null : ReadTitle(processIdentifier);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            if (_stopTask is { IsCompleted: false })
                throw new InvalidOperationException("Accessibility observation is still stopping.");
            _stopTask?.GetAwaiter().GetResult();
            _stopTask = null;
            _started = true;
            _native.Observation += OnNativeObservation;
            _native.Failed += OnNativeFailure;
            _config.ConfigChanged += OnConfigChanged;
            var polling = new CancellationTokenSource();
            _pollCts = polling;
            _pollTask = Task.Run(() => PollPermissionAsync(polling.Token), CancellationToken.None);
        }
        RefreshCapability();
    }

    public void Stop()
    {
        TaskCompletionSource? completion = null;
        Task stopping;
        lock (_gate)
        {
            if (_stopTask is null)
            {
                _started = false;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
                _config.ConfigChanged -= OnConfigChanged;
                _native.Observation -= OnNativeObservation;
                _native.Failed -= OnNativeFailure;
            }
            stopping = _stopTask;
        }
        if (completion is not null)
        {
            try
            {
                _pollCts?.Cancel();
                try { _transitions.Reconcile().GetAwaiter().GetResult(); }
                finally
                {
                    try { _pollTask?.GetAwaiter().GetResult(); }
                    finally { _pollCts?.Dispose(); }
                }
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        }
        stopping.GetAwaiter().GetResult();
    }

    public void SetCurrentApplication(int processIdentifier)
    {
        lock (_gate)
        {
            if (_currentProcessIdentifier != processIdentifier) _nativeFailed = false;
            _currentProcessIdentifier = processIdentifier;
        }
        RefreshCapability();
    }

    private void OnConfigChanged(MacAgentConfig _) => RefreshCapability();

    public void SetEnabledFromUser(bool enabled)
    {
        lock (_gate) _nativeFailed = false;
        _config.Update(value => value.WindowTitleObservationEnabled = enabled);
        if (enabled)
            _native.RequestProcessTrust();
        RefreshCapability();
    }

    public void OpenPermissionSettingsFromUser()
    {
        if (!Enabled) return;
        var result = _commandRunner.Run("/usr/bin/open", [AccessibilitySettingsUrl]);
        if (result.ExitCode != 0)
            Log.Warning("打开 macOS Accessibility 设置失败: {Error}", result.StandardError);
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
            // 正常停止
        }
    }

    private void RefreshCapability()
    {
        MacAccessibilityCapabilityState previous;
        MacAccessibilityCapabilityState current;
        lock (_gate)
        {
            previous = _capabilityState;
            current = ComputeState();
            _capabilityState = current;
        }

        _ = ObserveTransitionAsync(_transitions.Reconcile());

        // A synchronous transition may have reported a native failure. Publish its resulting state.
        current = CapabilityState;
        if (previous != current)
        {
            Log.Information("macOS Accessibility 能力状态: {State}", current);
            CapabilityChanged?.Invoke(current);
        }
    }

    private void ReconcileObservation()
    {
        try { ApplyObservationState(); }
        catch
        {
            lock (_gate) { _nativeFailed = true; _capabilityState = MacAccessibilityCapabilityState.Unavailable; }
            CapabilityChanged?.Invoke(MacAccessibilityCapabilityState.Unavailable);
            throw;
        }
    }

    private void ApplyObservationState()
    {
        int processIdentifier;
        bool started;
        lock (_gate) { processIdentifier = _currentProcessIdentifier; started = _started; }
        if (started && ComputeState() == MacAccessibilityCapabilityState.Available && processIdentifier > 0)
            _native.ObserveApplication(processIdentifier);
        else
            _native.StopObserving();
    }

    private async Task ObserveTransitionAsync(Task transition)
    {
        try { await transition; }
        catch (Exception exception)
        {
            Log.Warning(exception, "macOS Accessibility capability transition failed");
        }
    }

    private MacAccessibilityCapabilityState ComputeState()
    {
        if (!Enabled) return MacAccessibilityCapabilityState.Disabled;
        if (_nativeFailed || !_native.IsAvailable) return MacAccessibilityCapabilityState.Unavailable;
        return _native.IsProcessTrusted
            ? MacAccessibilityCapabilityState.Available
            : MacAccessibilityCapabilityState.PermissionRequired;
    }

    private static MacAccessibilityCapabilityState ComputeInitialState(
        MacConfigManager config,
        IMacAccessibilityNative native)
    {
        if (!config.Current.WindowTitleObservationEnabled)
            return MacAccessibilityCapabilityState.Disabled;
        if (!native.IsAvailable)
            return MacAccessibilityCapabilityState.Unavailable;
        return native.IsProcessTrusted
            ? MacAccessibilityCapabilityState.Available
            : MacAccessibilityCapabilityState.PermissionRequired;
    }

    private string? ReadTitle(int processIdentifier)
    {
        try
        {
            return _native.ReadFocusedWindowTitle(processIdentifier);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "读取 macOS focused-window 标题失败");
            return null;
        }
    }

    private void OnNativeFailure(Exception exception)
    {
        lock (_gate)
        {
            if (!_started) return;
            _nativeFailed = true;
            _capabilityState = MacAccessibilityCapabilityState.Unavailable;
        }
        Log.Warning(exception, "macOS Accessibility observation failed");
        _ = ObserveTransitionAsync(_transitions.Reconcile());
        CapabilityChanged?.Invoke(MacAccessibilityCapabilityState.Unavailable);
    }

    private void OnNativeObservation(MacAccessibilityObservation observation)
    {
        int currentProcessIdentifier;
        lock (_gate)
        {
            if (!_started || _capabilityState != MacAccessibilityCapabilityState.Available)
                return;
            currentProcessIdentifier = _currentProcessIdentifier;
        }
        if (observation.ProcessIdentifier > 0
            && observation.ProcessIdentifier != currentProcessIdentifier)
            return;
        Observation?.Invoke(observation);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
