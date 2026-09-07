using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.ViewModels;

public partial class MarketplaceCollectorItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _install;
    private readonly Func<string, Task> _retry;
    private readonly Func<string, Task> _uninstall;

    public MarketplaceCollectorItemViewModel(
        CollectorMarketplaceSnapshot snapshot,
        Func<string, Task> install,
        Func<string, Task> retry,
        Func<string, Task> uninstall)
    {
        PackageId = snapshot.PackageId;
        _install = install;
        _retry = retry;
        _uninstall = uninstall;
        Apply(snapshot);
    }

    public string PackageId { get; }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private CollectorMarketplacePhase _phase;
    [ObservableProperty] private int? _connectedHosts;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isUninstallConfirmationVisible;

    public bool IsInstalled => InstalledVersion is { Length: > 0 };
    public bool IsNotInstalled => !IsInstalled;
    public bool CanInstall => IsNotInstalled && !CanRetry;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool CanRetry => Phase == CollectorMarketplacePhase.Failed;
    public string StatusText => Phase switch
    {
        CollectorMarketplacePhase.NotInstalled => "未安装",
        CollectorMarketplacePhase.Waiting => "等待连接",
        CollectorMarketplacePhase.Starting => "启动中",
        CollectorMarketplacePhase.WaitingForAuthorization => "等待授权",
        CollectorMarketplacePhase.Running => ConnectedHosts is { } connected
            ? $"运行中 · {connected} 个连接"
            : "运行中",
        CollectorMarketplacePhase.ConnectionConflict => "连接冲突",
        CollectorMarketplacePhase.Failed => IsInstalled ? "运行失败" : "安装失败",
        _ => "未知"
    };

    public string VersionText => InstalledVersion is { Length: > 0 } installed
        ? installed
        : LatestVersion ?? string.Empty;

    public void Apply(CollectorMarketplaceSnapshot snapshot)
    {
        if (snapshot.PackageId != PackageId)
            throw new ArgumentException("Marketplace snapshot PackageId cannot change.", nameof(snapshot));
        DisplayName = snapshot.DisplayName;
        Summary = snapshot.Summary;
        LatestVersion = snapshot.LatestVersion;
        InstalledVersion = snapshot.InstalledVersion;
        Phase = snapshot.Phase;
        ConnectedHosts = snapshot.ConnectedHosts;
        Detail = snapshot.Detail;
        IsUninstallConfirmationVisible = false;
        NotifyDerivedProperties();
    }

    public void ApplyFailure(string detail)
    {
        Phase = CollectorMarketplacePhase.Failed;
        Detail = detail;
        IsExpanded = true;
        NotifyDerivedProperties();
    }

    public void ApplyOperationDetail(string detail)
    {
        Detail = detail;
        IsExpanded = true;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _install(PackageId); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await (IsInstalled ? _retry : _install)(PackageId); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RequestUninstall() => IsUninstallConfirmationVisible = true;

    [RelayCommand]
    private void CancelUninstall() => IsUninstallConfirmationVisible = false;

    [RelayCommand]
    private async Task ConfirmUninstallAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _uninstall(PackageId); }
        finally { IsBusy = false; }
    }

    partial void OnPhaseChanged(CollectorMarketplacePhase value) => NotifyDerivedProperties();
    partial void OnConnectedHostsChanged(int? value) => OnPropertyChanged(nameof(StatusText));
    partial void OnDetailChanged(string? value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnLatestVersionChanged(string? value) => OnPropertyChanged(nameof(VersionText));
    partial void OnInstalledVersionChanged(string? value) => OnPropertyChanged(nameof(VersionText));

    private void NotifyDerivedProperties()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(VersionText));
    }
}
