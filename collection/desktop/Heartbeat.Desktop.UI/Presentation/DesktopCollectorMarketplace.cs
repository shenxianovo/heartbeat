using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Desktop.UI.Presentation;

/// <summary>Presentation mapping over Host-owned Collector management; no runtime ownership.</summary>
public sealed class DesktopCollectorMarketplace(ICollectorMarketplace marketplace) : IDesktopCollectorMarketplace
{
    private readonly ICollectorMarketplace _marketplace = marketplace;

    public async ValueTask<IReadOnlyList<CollectorMarketplaceSnapshot>> BrowseAsync(
        CancellationToken cancellationToken = default) =>
        (await _marketplace.BrowseAsync(cancellationToken)).Select(ToPresentation).ToArray();

    public async ValueTask InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        _ = await _marketplace.InstallAsync(packageId, cancellationToken);

    public async ValueTask RetryAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        _ = await _marketplace.RetryAsync(packageId, cancellationToken);

    public ValueTask UninstallAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        _marketplace.UninstallAsync(packageId, cancellationToken);

    private static CollectorMarketplaceSnapshot ToPresentation(CollectorMarketplaceRuntimeItem item) => new(
        item.PackageId,
        item.DisplayName,
        item.Summary,
        item.LatestVersion,
        item.InstalledVersion,
        item.Phase switch
        {
            CollectorMarketplaceRuntimePhase.NotInstalled => CollectorMarketplacePhase.NotInstalled,
            CollectorMarketplaceRuntimePhase.WaitingForExternalHost => CollectorMarketplacePhase.Waiting,
            CollectorMarketplaceRuntimePhase.Starting => CollectorMarketplacePhase.Starting,
            CollectorMarketplaceRuntimePhase.WaitingForAuthorization => CollectorMarketplacePhase.WaitingForAuthorization,
            CollectorMarketplaceRuntimePhase.Running => CollectorMarketplacePhase.Running,
            CollectorMarketplaceRuntimePhase.ConnectionConflict => CollectorMarketplacePhase.ConnectionConflict,
            _ => CollectorMarketplacePhase.Failed
        },
        item.ConnectedExternalHosts,
        item.Failure?.Message,
        item.CatalogFailure is not null);

}
