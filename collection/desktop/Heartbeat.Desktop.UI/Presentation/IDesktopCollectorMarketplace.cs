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

public enum CollectorMarketplaceOperationKind
{
    Install,
    Retry,
    Uninstall,
    SubmitAuthorization
}

public enum CollectorMarketplaceOperationPhase
{
    Pending,
    Running,
    Committing,
    Succeeded,
    Cancelled,
    Failed
}

public sealed record CollectorMarketplaceOperationSnapshot(
    Guid OperationId,
    string PackageId,
    CollectorMarketplaceOperationKind Kind,
    CollectorMarketplaceOperationPhase Phase,
    string? Failure = null)
{
    public bool IsActive => Phase is
        CollectorMarketplaceOperationPhase.Pending or
        CollectorMarketplaceOperationPhase.Running or
        CollectorMarketplaceOperationPhase.Committing;
}

/// <summary>
/// Desktop presentation port for the generic Collector Marketplace. Product-specific Package
/// names and behavior stay behind the Catalog and Package manifests.
/// </summary>
public interface IDesktopCollectorMarketplace
{
    IReadOnlyList<CollectorMarketplaceOperationSnapshot> OperationsSnapshot();

    ValueTask<CollectorMarketplaceOperationSnapshot> WaitForOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CollectorMarketplaceSnapshot>> BrowseAsync(
        CancellationToken cancellationToken = default);

    ValueTask InstallAsync(string packageId, CancellationToken cancellationToken = default);

    ValueTask RetryAsync(string packageId, CancellationToken cancellationToken = default);

    ValueTask UninstallAsync(string packageId, CancellationToken cancellationToken = default);
}
