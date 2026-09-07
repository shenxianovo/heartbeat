namespace Heartbeat.Desktop.UI.Presentation;

public enum CollectorMarketplacePhase
{
    NotInstalled,
    Waiting,
    Starting,
    WaitingForAuthorization,
    Running,
    ConnectionConflict,
    Failed
}

public sealed record CollectorMarketplaceSnapshot(
    string PackageId,
    string DisplayName,
    string Summary,
    string? LatestVersion,
    string? InstalledVersion,
    CollectorMarketplacePhase Phase,
    int? ConnectedHosts = null,
    string? Detail = null,
    bool CatalogUnavailable = false);

/// <summary>
/// Desktop presentation port for the generic Collector Marketplace. Product-specific Package
/// names and behavior stay behind the Catalog and Package manifests.
/// </summary>
public interface IDesktopCollectorMarketplace
{
    ValueTask<IReadOnlyList<CollectorMarketplaceSnapshot>> BrowseAsync(
        CancellationToken cancellationToken = default);

    ValueTask InstallAsync(string packageId, CancellationToken cancellationToken = default);

    ValueTask RetryAsync(string packageId, CancellationToken cancellationToken = default);

    ValueTask UninstallAsync(string packageId, CancellationToken cancellationToken = default);
}
