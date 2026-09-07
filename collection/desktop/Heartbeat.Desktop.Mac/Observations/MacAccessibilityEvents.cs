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
public sealed class MacAccessibilityEvents : IMacAccessibilityEvents, IDisposable, IAsyncDisposable
{
    private readonly MacConfigManager _config;
    private readonly IMacAccessibilityNative _native;
    private readonly IMacCommandRunner _commandRunner;
    private readonly ObservationCapability<MacAccessibilityCapabilityState, int> _capability;
    public MacAccessibilityEvents(MacConfigManager config, IMacAccessibilityNative native,
        IMacCommandRunner commandRunner)
    {
        _config = config;
        _native = native;
        _commandRunner = commandRunner;
        _capability = new(ComputeState(), MacAccessibilityCapabilityState.Unavailable, ReadDesired,
            _native.ObserveApplication, _native.StopObserving, PermissionPollInterval);
    }
    private static readonly TimeSpan PermissionPollInterval = TimeSpan.FromSeconds(1);
    private const string AccessibilitySettingsUrl =
        "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    private readonly object _gate = new();
    private int _currentProcessIdentifier;
    private bool _started;

    public event Action<MacAccessibilityObservation>? Observation;
    public event Action<MacAccessibilityCapabilityState>? CapabilityChanged
    {
        add => _capability.Changed += value;
        remove => _capability.Changed -= value;
    }

    public bool Enabled => _config.Current.WindowTitleObservationEnabled;
    public MacAccessibilityCapabilityState CapabilityState => _capability.State;

    public string? CurrentTitle
    {
        get
        {
            int processIdentifier;
            lock (_gate)
            {
                // This is a direct AX query for the current PID, independent of notification setup.
                if (!Enabled || CapabilityState != MacAccessibilityCapabilityState.Available)
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
            _started = true;
            _native.Observation += OnNativeObservation;
            _native.Failed += _capability.ReportFailure;
            _config.ConfigChanged += OnConfigChanged;
            _capability.Start();
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _started = false;
            _config.ConfigChanged -= OnConfigChanged;
            _native.Observation -= OnNativeObservation;
            _native.Failed -= _capability.ReportFailure;
            return _capability.Stop();
        }
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    public void SetCurrentApplication(int processIdentifier)
    {
        lock (_gate)
        {
            if (_currentProcessIdentifier == processIdentifier) return;
            _currentProcessIdentifier = processIdentifier;
            _capability.Refresh(retry: true);
        }
    }

    private void OnConfigChanged(MacAgentConfig _) => _capability.Refresh();

    public void SetEnabledFromUser(bool enabled)
    {
        _config.Update(value => value.WindowTitleObservationEnabled = enabled);
        if (enabled)
            _native.RequestProcessTrust();
        _capability.Refresh(retry: true);
    }

    public void OpenPermissionSettingsFromUser()
    {
        if (!Enabled) return;
        var result = _commandRunner.Run("/usr/bin/open", [AccessibilitySettingsUrl]);
        if (result.ExitCode != 0)
            Log.Warning("打开 macOS Accessibility 设置失败: {Error}", result.StandardError);
        _capability.Refresh();
    }

    public Task RefreshPermission() => _capability.Refresh();

    private ObservationTarget<MacAccessibilityCapabilityState, int> ReadDesired()
    {
        var state = ComputeState();
        int processIdentifier;
        lock (_gate) processIdentifier = _currentProcessIdentifier;
        return new(state, state == MacAccessibilityCapabilityState.Available && processIdentifier > 0 ? processIdentifier : null);
    }

    private MacAccessibilityCapabilityState ComputeState()
    {
        if (!Enabled) return MacAccessibilityCapabilityState.Disabled;
        if (!_native.IsAvailable) return MacAccessibilityCapabilityState.Unavailable;
        return _native.IsProcessTrusted
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

    private void OnNativeObservation(MacAccessibilityObservation observation)
    {
        int currentProcessIdentifier;
        lock (_gate)
        {
            if (!Enabled || !_capability.AcceptsObservations)
                return;
            currentProcessIdentifier = _currentProcessIdentifier;
        }
        if (observation.ProcessIdentifier > 0
            && observation.ProcessIdentifier != currentProcessIdentifier)
            return;
        Observation?.Invoke(observation);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
