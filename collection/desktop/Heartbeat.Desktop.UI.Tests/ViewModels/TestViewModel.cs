using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.ViewModels;

namespace Heartbeat.Desktop.UI.Tests.ViewModels;

internal static class TestViewModel
{
    public static MainViewModel Create(
        IDesktopState? state = null,
        IUpdateController? updates = null,
        IWindowController? window = null,
        IPresentationScheduler? scheduler = null,
        ILogFeed? logs = null,
        IDesktopCollectorMarketplace? marketplace = null) =>
        new(
            state ?? new FakeDesktopState(),
            updates ?? new FakeUpdateController(),
            window ?? new FakeWindowController(),
            scheduler ?? new ManualPresentationScheduler(),
            logs ?? new FakeLogFeed(),
            marketplace ?? new FakeCollectorMarketplace());
}

internal sealed class FakeCollectorMarketplace(
    IReadOnlyList<CollectorMarketplaceSnapshot>? snapshots = null) : IDesktopCollectorMarketplace
{
    public IReadOnlyList<CollectorMarketplaceSnapshot> Snapshots { get; set; } = snapshots ?? [];
    public string? InstalledPackageId { get; private set; }
    public string? RetriedPackageId { get; private set; }
    public string? UninstalledPackageId { get; private set; }
    public Exception? InstallException { get; set; }

    public ValueTask<IReadOnlyList<CollectorMarketplaceSnapshot>> BrowseAsync(
        CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshots);

    public ValueTask InstallAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (InstallException is not null)
            return ValueTask.FromException(InstallException);
        InstalledPackageId = packageId;
        return ValueTask.CompletedTask;
    }

    public ValueTask RetryAsync(string packageId, CancellationToken cancellationToken = default)
    {
        RetriedPackageId = packageId;
        return ValueTask.CompletedTask;
    }

    public ValueTask UninstallAsync(string packageId, CancellationToken cancellationToken = default)
    {
        UninstalledPackageId = packageId;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeDesktopState : IDesktopState
{
    public DesktopStateSnapshot Current { get; set; } = DesktopStateSnapshot.Empty;
    public (SystemCapability Capability, bool Enabled)? LastSystemCapabilityValue { get; private set; }
    public SystemCapability? LastRecoveredSystemCapability { get; private set; }
    public SystemCapability? LastRevealedSystemCapability { get; private set; }
    public DesktopSettingsInput? LastSettings { get; private set; }
    public bool? LastLoginStartValue { get; private set; }
    public DesktopThemeMode? LastThemeMode { get; private set; }
    public event Action<DesktopStateSnapshot>? Changed;

    public void SaveSettings(DesktopSettingsInput settings) => LastSettings = settings;
    public void SetSystemCapabilityEnabled(SystemCapability capability, bool enabled) =>
        LastSystemCapabilityValue = (capability, enabled);
    public void RecoverSystemCapability(SystemCapability capability) =>
        LastRecoveredSystemCapability = capability;
    public void RevealSystemCapabilityApplication(SystemCapability capability) =>
        LastRevealedSystemCapability = capability;
    public void SetLoginStartEnabled(bool enabled) => LastLoginStartValue = enabled;
    public void SetThemeMode(DesktopThemeMode mode) => LastThemeMode = mode;

    public void Publish(DesktopStateSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }
}

internal sealed class FakeUpdateController : IUpdateController
{
    public bool IsSupported { get; set; } = true;
    public UpdateSnapshot Current { get; set; } = UpdateSnapshot.Idle;
    public int ApplyCount { get; private set; }
    public UpdateCheckResult CheckResult { get; set; } = UpdateCheckResult.UpToDate;
    public event Action<UpdateSnapshot>? Changed;
    public Task<UpdateCheckResult> CheckAsync() => Task.FromResult(CheckResult);
    public Task<bool> ApplyAsync()
    {
        ApplyCount++;
        return Task.FromResult(Current.State == UpdateState.ReadyToApply);
    }
    public void Publish(UpdateSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }
}

internal sealed class FakeWindowController : IWindowController
{
    public int HideCount { get; private set; }
    public string? ClipboardText { get; private set; }
    public void HideSettings() => HideCount++;
    public void CopyTextToClipboard(string text) => ClipboardText = text;
}

internal sealed class ManualPresentationScheduler : IPresentationScheduler
{
    public void Post(Action action) => action();
}

internal sealed class FakeLogFeed : ILogFeed
{
    private readonly IReadOnlyList<LogEntry> _entries;

    public FakeLogFeed(IReadOnlyList<LogEntry>? entries = null)
    {
        _entries = entries ?? [];
    }

    public int Capacity => 200;
    public event Action<IReadOnlyList<LogEntry>>? Changed;
    public IReadOnlyList<LogEntry> GetAll() => _entries;
    public void Publish(IReadOnlyList<LogEntry> entries) => Changed?.Invoke(entries);
}
