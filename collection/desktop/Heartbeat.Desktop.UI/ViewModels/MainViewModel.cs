using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Core;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using System.Collections.ObjectModel;
using Serilog.Events;

namespace Heartbeat.Desktop.UI.ViewModels;

public enum MainPage
{
    Overview,
    Collectors,
    Settings
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopState _desktopState;
    private readonly IUpdateController _updates;
    private readonly IWindowController _window;
    private readonly IPresentationScheduler _scheduler;
    private readonly ILogFeed _logs;
    private readonly IDesktopCollectorMarketplace _collectorMarketplace;
    private readonly object _marketplaceOperationGate = new();
    private readonly HashSet<Guid> _trackedMarketplaceOperations = [];
    private readonly CancellationTokenSource _marketplaceTracking = new();
    private int _disposed;
    private DesktopSettingsSnapshot? _lastSettings;
    private bool? _lastLoginStart;
    private bool _suppressLoginStart;
    private bool _suppressSettings;
    private long _marketplaceRefreshVersion;
    private readonly List<LogEntry> _allLogs = [];

    [ObservableProperty]
    private string _currentApp = "(未检测)";

    [ObservableProperty]
    private MainPage _selectedPage = MainPage.Overview;

    [ObservableProperty]
    private bool _isDiagnosticsExpanded;

    [ObservableProperty]
    private bool _isApiKeyVisible;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    private int _selectedThemeIndex;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private UpdateState _updateState;

    [ObservableProperty]
    private string? _updateVersion;

    [ObservableProperty]
    private int? _updateDownloadProgress;

    [ObservableProperty]
    private string? _updateError;

    [ObservableProperty]
    private UpdateCheckResult? _lastUpdateCheckResult;

    [ObservableProperty]
    private string _updateCheckMessage = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _uploadIntervalMinutes = "1";

    [ObservableProperty]
    private bool _loginStartEnabled;

    [ObservableProperty]
    private string _saveStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _saveStatusIsError;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private LogEventLevel _selectedLogLevel = LogEventLevel.Information;

    [ObservableProperty]
    private string? _collectorMarketplaceError;

    public ObservableCollection<CollectorItemViewModel> Collectors { get; } = [];
    public ObservableCollection<MarketplaceCollectorItemViewModel> MarketplaceCollectors { get; } = [];
    public ObservableCollection<OperationalNoticeViewModel> OperationalNotices { get; } = [];
    public bool UpdatesSupported { get; }
    public bool IsOverviewSelected => SelectedPage == MainPage.Overview;
    public bool IsCollectorsSelected => SelectedPage == MainPage.Collectors;
    public bool IsSettingsSelected => SelectedPage == MainPage.Settings;
    public int SelectedPageIndex
    {
        get => (int)SelectedPage;
        set
        {
            if (Enum.IsDefined((MainPage)value))
                SelectedPage = (MainPage)value;
        }
    }
    public double SidebarLabelOpacity => IsSidebarCollapsed ? 0 : 1;
    public int SidebarIconColumnSpan => IsSidebarCollapsed ? 2 : 1;
    public bool IsApiKeyHidden => !IsApiKeyVisible;
    public bool IsDiagnosticsCollapsed => !IsDiagnosticsExpanded;
    public bool HasOperationalNotices => OperationalNotices.Count > 0;
    public bool HasSaveStatusMessage => !string.IsNullOrWhiteSpace(SaveStatusMessage);
    public bool ShowSaveSuccess => HasSaveStatusMessage && !SaveStatusIsError;
    public bool ShowSaveError => HasSaveStatusMessage && SaveStatusIsError;
    public bool ShowSaveBar => HasUnsavedChanges || HasSaveStatusMessage;
    public bool HasUpdateError => !string.IsNullOrWhiteSpace(UpdateError);
    public bool HasUpdateCheckMessage => !string.IsNullOrWhiteSpace(UpdateCheckMessage);
    public bool IsUpdateReady => UpdateState == UpdateState.ReadyToApply;
    public bool HasAvailableUpdate => UpdatesSupported && UpdateState != UpdateState.Idle;
    public string CurrentActivityDetail => CurrentApp == "(未检测)"
        ? "等待系统采集器报告前台应用"
        : CurrentApp == "(离开)"
            ? "设备当前处于离开状态"
            : "正在记录此应用的活动";
    public string UpdateStateText => UpdateState switch
    {
        UpdateState.UpdateAvailable => "发现新版本",
        UpdateState.Downloading => UpdateDownloadProgress is { } progress
            ? $"正在下载 · {progress}%"
            : "正在下载",
        UpdateState.ReadyToApply => "更新已就绪",
        _ => "自动更新已启用"
    };
    public string UpdateStateDetail => UpdateState switch
    {
        UpdateState.UpdateAvailable => UpdateVersion is { Length: > 0 }
            ? $"Heartbeat {UpdateVersion} 可用"
            : "正在准备下载",
        UpdateState.Downloading => "下载完成后即可应用并重启",
        UpdateState.ReadyToApply => UpdateVersion is { Length: > 0 }
            ? $"Heartbeat {UpdateVersion} 已下载"
            : "新版本已下载",
        _ => "在后台检查并安全下载新版本"
    };

    public MainViewModel(
        IDesktopState desktopState,
        IUpdateController updates,
        IWindowController window,
        IPresentationScheduler scheduler,
        ILogFeed logs,
        IDesktopCollectorMarketplace collectorMarketplace)
    {
        _desktopState = desktopState;
        _updates = updates;
        _window = window;
        _scheduler = scheduler;
        _logs = logs;
        _collectorMarketplace = collectorMarketplace;
        UpdatesSupported = updates.IsSupported;

        ApplyState(desktopState.Current);
        ApplyUpdateState(updates.Current);
        _desktopState.Changed += HandleStateChanged;
        _updates.Changed += HandleUpdateChanged;
        _logs.Changed += HandleLogsChanged;

        _allLogs.AddRange(_logs.GetAll());
        RebuildLogText();
    }

    private void HandleStateChanged(DesktopStateSnapshot snapshot) =>
        _scheduler.Post(() => ApplyState(snapshot));

    private void HandleUpdateChanged(UpdateSnapshot snapshot) =>
        _scheduler.Post(() => ApplyUpdateState(snapshot));

    private void HandleLogsChanged(IReadOnlyList<LogEntry> entries) =>
        _scheduler.Post(() =>
        {
            _allLogs.AddRange(entries);
            if (_allLogs.Count > _logs.Capacity * 2)
                _allLogs.RemoveRange(0, _allLogs.Count - _logs.Capacity);
            RebuildLogText();
        });

    private void ApplyUpdateState(UpdateSnapshot snapshot)
    {
        UpdateState = snapshot.State;
        UpdateVersion = snapshot.Version;
        UpdateDownloadProgress = snapshot.DownloadProgress;
        UpdateError = snapshot.Error;
        OnPropertyChanged(nameof(UpdateStateText));
        OnPropertyChanged(nameof(UpdateStateDetail));
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(HasAvailableUpdate));
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedLogLevelChanged(LogEventLevel value) => RebuildLogText();

    private void RebuildLogText()
    {
        LogText = string.Join(
            Environment.NewLine,
            _allLogs.Where(entry => entry.Level >= SelectedLogLevel).Select(entry => entry.Message));
    }

    [RelayCommand]
    private void SetLogLevel(string level) =>
        SelectedLogLevel = Enum.Parse<LogEventLevel>(level);

    private void ApplyState(DesktopStateSnapshot snapshot)
    {
        CurrentApp = FormatActivity(snapshot.CurrentActivity) ?? "(未检测)";
        ApplySettings(snapshot);
        RebuildCollectors(snapshot);
        RebuildOperationalNotices(snapshot);
    }

    private void ApplySettings(DesktopStateSnapshot snapshot)
    {
        var textSettingsChanged = _lastSettings == null ||
            _lastSettings.ApiKey != snapshot.Settings.ApiKey ||
            _lastSettings.DeviceName != snapshot.Settings.DeviceName ||
            _lastSettings.UploadIntervalMinutes != snapshot.Settings.UploadIntervalMinutes;

        _suppressSettings = true;
        if (_lastSettings == null || (!HasUnsavedChanges && textSettingsChanged))
        {
            ApiKey = snapshot.Settings.ApiKey;
            DeviceName = snapshot.Settings.DeviceName;
            UploadIntervalMinutes = snapshot.Settings.UploadIntervalMinutes.ToString();
        }
        SelectedThemeIndex = (int)snapshot.Settings.ThemeMode;
        _lastSettings = snapshot.Settings;
        _suppressSettings = false;
        UpdateDirtyState();

        if (_lastLoginStart != snapshot.LoginStartEnabled)
        {
            _suppressLoginStart = true;
            LoginStartEnabled = snapshot.LoginStartEnabled;
            _suppressLoginStart = false;
            _lastLoginStart = snapshot.LoginStartEnabled;
        }
    }

    private void RebuildOperationalNotices(DesktopStateSnapshot snapshot)
    {
        OperationalNotices.Clear();

        if (snapshot.Compatibility.UpdateRequired)
        {
            OperationalNotices.Add(new OperationalNoticeViewModel(
                OperationalNoticeKind.UpdateRequired,
                "需要更新 Heartbeat",
                snapshot.Compatibility.Message ?? "服务器要求更新 Heartbeat 后再继续上传。",
                "检查并应用更新"));
        }
        foreach (var (stream, status) in snapshot.UploadStreams.OrderBy(pair => pair.Key))
        {
            var notice = status.State switch
            {
                UploadStreamState.CacheMigrationFailed => new OperationalNoticeViewModel(
                    OperationalNoticeKind.CacheMigrationFailed,
                    $"{stream}缓存迁移失败",
                    status.Message ?? "缓存迁移失败，上传已暂停。",
                    status.Action,
                    status.RecoveryPath),
                UploadStreamState.CacheWriteFailed => new OperationalNoticeViewModel(
                    OperationalNoticeKind.CacheWriteFailed,
                    $"{stream}缓存写入失败",
                    status.Message ?? "离线数据无法安全写入缓存。",
                    status.Action,
                    status.RecoveryPath),
                UploadStreamState.DeadLetterWriteFailed => new OperationalNoticeViewModel(
                    OperationalNoticeKind.DeadLetterWriteFailed,
                    $"{stream}隔离记录写入失败",
                    status.Message ?? "永久拒绝的记录无法写入 dead letter。",
                    status.Action,
                    status.DeadLetterPath),
                _ => null
            };

            if (notice != null)
                OperationalNotices.Add(notice);

            if (status.DeadLetterCount > 0)
            {
                OperationalNotices.Add(new OperationalNoticeViewModel(
                    OperationalNoticeKind.DeadLettersAvailable,
                    $"{stream}存在已隔离记录",
                    $"有 {status.DeadLetterCount} 条记录被永久拒绝，可检查诊断文件。",
                    "查看诊断文件",
                    status.DeadLetterPath));
            }
        }
        OnPropertyChanged(nameof(HasOperationalNotices));
    }

    private void RebuildCollectors(DesktopStateSnapshot snapshot)
    {
        var system = Collectors.FirstOrDefault();
        if (system is null)
        {
            system = new CollectorItemViewModel(
                ActivitySources.System,
                _desktopState.SetSystemCapabilityEnabled,
                _desktopState.RecoverSystemCapability,
                _desktopState.RevealSystemCapabilityApplication);
            Collectors.Add(system);
        }
        system.SetSystemCapabilities(snapshot.Capabilities);
    }

    private static string? FormatActivity(CurrentActivity? activity)
    {
        if (activity == null) return null;
        if (activity.AppIdentityKey == AppIdentityKeys.Away) return "(离开)";
        return string.IsNullOrWhiteSpace(activity.AppDisplayName)
            ? activity.AppIdentityKey
            : activity.AppDisplayName;
    }

    [RelayCommand]
    private void CloseSettings() => _window.HideSettings();

    [RelayCommand]
    private void Navigate(string page)
    {
        if (Enum.TryParse<MainPage>(page, true, out var target))
            SelectedPage = target;
    }

    [RelayCommand]
    private async Task RefreshCollectorMarketplaceAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        var refreshVersion = Interlocked.Increment(ref _marketplaceRefreshVersion);
        try
        {
            var operations = _collectorMarketplace.OperationsSnapshot();
            TrackMarketplaceOperations(operations);
            _scheduler.Post(() =>
            {
                if (refreshVersion == Volatile.Read(ref _marketplaceRefreshVersion) &&
                    Volatile.Read(ref _disposed) == 0)
                    ApplyMarketplaceOperations(operations);
            });
            var snapshots = await _collectorMarketplace.BrowseAsync();
            var completedOperations = _collectorMarketplace.OperationsSnapshot();
            TrackMarketplaceOperations(completedOperations);
            _scheduler.Post(() =>
            {
                if (refreshVersion == Volatile.Read(ref _marketplaceRefreshVersion) &&
                    Volatile.Read(ref _disposed) == 0)
                {
                    ApplyMarketplaceSnapshots(snapshots);
                    ApplyMarketplaceOperations(completedOperations);
                }
            });
        }
        catch (Exception)
        {
            _scheduler.Post(() =>
            {
                if (refreshVersion == Volatile.Read(ref _marketplaceRefreshVersion) &&
                    Volatile.Read(ref _disposed) == 0)
                    CollectorMarketplaceError = "无法加载采集器";
            });
        }
    }

    private void TrackMarketplaceOperations(
        IReadOnlyList<CollectorMarketplaceOperationSnapshot> operations)
    {
        foreach (var operation in operations.Where(operation => operation.IsActive))
        {
            lock (_marketplaceOperationGate)
            {
                if (_disposed != 0)
                    return;
                if (!_trackedMarketplaceOperations.Add(operation.OperationId))
                    continue;
                _ = RefreshWhenMarketplaceOperationCompletesAsync(operation.OperationId);
            }
        }
    }

    private async Task RefreshWhenMarketplaceOperationCompletesAsync(Guid operationId)
    {
        try
        {
            await _collectorMarketplace.WaitForOperationAsync(
                operationId,
                _marketplaceTracking.Token);
            if (Volatile.Read(ref _disposed) != 0)
                return;
            await RefreshCollectorMarketplaceAsync();
        }
        catch (OperationCanceledException) when (_marketplaceTracking.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await RefreshCollectorMarketplaceAsync();
        }
        finally
        {
            lock (_marketplaceOperationGate)
                _trackedMarketplaceOperations.Remove(operationId);
        }
    }

    private void ApplyMarketplaceOperations(
        IReadOnlyList<CollectorMarketplaceOperationSnapshot> operations)
    {
        var items = MarketplaceCollectors.ToDictionary(item => item.PackageId, StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (items.TryGetValue(operation.PackageId, out var item))
                item.ApplyOperation(operation);
        }
    }

    private void ApplyMarketplaceSnapshots(IReadOnlyList<CollectorMarketplaceSnapshot> snapshots)
    {
        CollectorMarketplaceError = snapshots.Any(snapshot => snapshot.CatalogUnavailable)
            ? "无法加载采集器"
            : null;
        var byId = MarketplaceCollectors.ToDictionary(item => item.PackageId, StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (byId.Remove(snapshot.PackageId, out var existing))
                existing.Apply(snapshot);
            else
                MarketplaceCollectors.Add(new MarketplaceCollectorItemViewModel(
                    snapshot,
                    InstallCollectorAsync,
                    RetryCollectorAsync,
                    UninstallCollectorAsync));
        }
        foreach (var removed in byId.Values)
            MarketplaceCollectors.Remove(removed);
    }

    private async Task RetryCollectorAsync(string packageId)
    {
        try
        {
            await _collectorMarketplace.RetryAsync(packageId, _marketplaceTracking.Token);
            await RefreshCollectorMarketplaceAsync();
        }
        catch (OperationCanceledException) when (_marketplaceTracking.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RefreshCollectorMarketplaceAsync();
            _scheduler.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    ApplyMarketplaceOperationError(packageId, "重试失败", exception.Message);
            });
        }
    }

    private async Task InstallCollectorAsync(string packageId)
    {
        try
        {
            await _collectorMarketplace.InstallAsync(packageId, _marketplaceTracking.Token);
            await RefreshCollectorMarketplaceAsync();
        }
        catch (OperationCanceledException) when (_marketplaceTracking.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RefreshCollectorMarketplaceAsync();
            _scheduler.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    ApplyMarketplaceFailure(packageId, "安装失败", exception.Message);
            });
        }
    }

    private async Task UninstallCollectorAsync(string packageId)
    {
        try
        {
            await _collectorMarketplace.UninstallAsync(packageId, _marketplaceTracking.Token);
            await RefreshCollectorMarketplaceAsync();
        }
        catch (OperationCanceledException) when (_marketplaceTracking.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RefreshCollectorMarketplaceAsync();
            _scheduler.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    ApplyMarketplaceOperationError(packageId, "移除失败", exception.Message);
            });
        }
    }

    private void ApplyMarketplaceFailure(string packageId, string title, string detail)
    {
        CollectorMarketplaceError = title;
        MarketplaceCollectors.FirstOrDefault(item => item.PackageId == packageId)?.ApplyFailure(detail);
    }

    private void ApplyMarketplaceOperationError(string packageId, string title, string detail)
    {
        CollectorMarketplaceError = title;
        MarketplaceCollectors.FirstOrDefault(item => item.PackageId == packageId)?.ApplyOperationDetail(detail);
    }

    [RelayCommand]
    private void ToggleDiagnostics() => IsDiagnosticsExpanded = !IsDiagnosticsExpanded;

    [RelayCommand]
    private void ToggleApiKeyVisibility() => IsApiKeyVisible = !IsApiKeyVisible;

    partial void OnSelectedPageChanged(MainPage value)
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsCollectorsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(SelectedPageIndex));
        if (value == MainPage.Collectors)
            _ = RefreshCollectorMarketplaceAsync();
    }

    partial void OnIsApiKeyVisibleChanged(bool value) => OnPropertyChanged(nameof(IsApiKeyHidden));

    partial void OnIsDiagnosticsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsDiagnosticsCollapsed));

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarLabelOpacity));
        OnPropertyChanged(nameof(SidebarIconColumnSpan));
    }

    partial void OnCurrentAppChanged(string value) => OnPropertyChanged(nameof(CurrentActivityDetail));

    partial void OnUpdateErrorChanged(string? value) => OnPropertyChanged(nameof(HasUpdateError));

    partial void OnUpdateCheckMessageChanged(string value) => OnPropertyChanged(nameof(HasUpdateCheckMessage));

    partial void OnSaveStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasSaveStatusMessage));
        OnPropertyChanged(nameof(ShowSaveSuccess));
        OnPropertyChanged(nameof(ShowSaveError));
        OnPropertyChanged(nameof(ShowSaveBar));
    }

    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));

    partial void OnSaveStatusIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveSuccess));
        OnPropertyChanged(nameof(ShowSaveError));
    }

    partial void OnApiKeyChanged(string value) => UpdateDirtyState();
    partial void OnDeviceNameChanged(string value) => UpdateDirtyState();
    partial void OnUploadIntervalMinutesChanged(string value) => UpdateDirtyState();

    partial void OnSelectedThemeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedThemeMode));
        if (!_suppressSettings && Enum.IsDefined((DesktopThemeMode)value))
            _desktopState.SetThemeMode((DesktopThemeMode)value);
    }

    public DesktopThemeMode SelectedThemeMode => Enum.IsDefined((DesktopThemeMode)SelectedThemeIndex)
        ? (DesktopThemeMode)SelectedThemeIndex
        : DesktopThemeMode.System;

    private void UpdateDirtyState()
    {
        if (_suppressSettings || _lastSettings == null) return;
        HasUnsavedChanges = ApiKey != _lastSettings.ApiKey ||
            DeviceName != _lastSettings.DeviceName ||
            UploadIntervalMinutes != _lastSettings.UploadIntervalMinutes.ToString();
        SaveConfigCommand.NotifyCanExecuteChanged();
        if (HasUnsavedChanges && !string.IsNullOrWhiteSpace(SaveStatusMessage))
            SaveStatusMessage = string.Empty;
    }

    partial void OnLoginStartEnabledChanged(bool value)
    {
        if (!_suppressLoginStart)
            _desktopState.SetLoginStartEnabled(value);
    }

    private bool CanSaveConfig() => HasUnsavedChanges;

    [RelayCommand(CanExecute = nameof(CanSaveConfig))]
    private void SaveConfig()
    {
        if (!int.TryParse(UploadIntervalMinutes, out var uploadInterval) || uploadInterval < 1)
        {
            SaveStatusMessage = "上传间隔必须为正整数";
            SaveStatusIsError = true;
            return;
        }

        var normalizedApiKey = ApiKey.Trim();
        var normalizedDeviceName = DeviceName.Trim();
        _desktopState.SaveSettings(new DesktopSettingsInput(normalizedApiKey, normalizedDeviceName, uploadInterval));
        if (_lastSettings != null)
            _lastSettings = _lastSettings with
            {
                ApiKey = normalizedApiKey,
                DeviceName = normalizedDeviceName,
                UploadIntervalMinutes = uploadInterval
            };
        _suppressSettings = true;
        ApiKey = normalizedApiKey;
        DeviceName = normalizedDeviceName;
        UploadIntervalMinutes = uploadInterval.ToString();
        _suppressSettings = false;
        HasUnsavedChanges = false;
        SaveConfigCommand.NotifyCanExecuteChanged();
        SaveStatusMessage = "配置已保存，下次上传周期将使用新配置";
        SaveStatusIsError = false;
    }

    private bool CanApplyUpdate() => UpdateState == UpdateState.ReadyToApply;

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private Task ApplyUpdateAsync() => _updates.ApplyAsync();

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        LastUpdateCheckResult = await _updates.CheckAsync();
        UpdateCheckMessage = LastUpdateCheckResult switch
        {
            UpdateCheckResult.UpToDate => "当前已是最新版本。",
            UpdateCheckResult.UpdateFound => "发现新版本，正在下载。",
            UpdateCheckResult.CheckFailed => "检查更新失败，请检查网络后重试。",
            _ => "更新检查已跳过：下载或待应用的更新优先。"
        };
    }

    public void Dispose()
    {
        lock (_marketplaceOperationGate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            Interlocked.Increment(ref _marketplaceRefreshVersion);
        }
        _marketplaceTracking.Cancel();
        _desktopState.Changed -= HandleStateChanged;
        _updates.Changed -= HandleUpdateChanged;
        _logs.Changed -= HandleLogsChanged;
        _marketplaceTracking.Dispose();
        GC.SuppressFinalize(this);
    }
}
