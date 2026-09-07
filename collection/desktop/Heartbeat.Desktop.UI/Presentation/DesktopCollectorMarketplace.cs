using Heartbeat.Collection.Hub.Collectors.Packages;

namespace Heartbeat.Desktop.UI.Presentation;

/// <summary>Presentation mapping over Host-owned Collector management; no runtime ownership.</summary>
public sealed class DesktopCollectorMarketplace(ICollectorMarketplace marketplace) : IDesktopCollectorMarketplace
{
    private readonly ICollectorMarketplace _marketplace = marketplace;

    public IReadOnlyList<CollectorMarketplaceOperationSnapshot> OperationsSnapshot() =>
        _marketplace.OperationsSnapshot().Select(ToPresentation).ToArray();

    public async ValueTask<CollectorMarketplaceOperationSnapshot> WaitForOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ToPresentation(await _marketplace.WaitForOperationAsync(operationId, cancellationToken));

    public async ValueTask<IReadOnlyList<CollectorMarketplaceSnapshot>> BrowseAsync(
        CancellationToken cancellationToken = default) =>
        (await _marketplace.BrowseAsync(cancellationToken)).Select(ToPresentation).ToArray();

    public async ValueTask InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        await _marketplace.WaitForSuccessAsync(_marketplace.Install(packageId), cancellationToken);

    public async ValueTask RetryAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        await _marketplace.WaitForSuccessAsync(_marketplace.Retry(packageId), cancellationToken);

    public async ValueTask UninstallAsync(
        string packageId,
        CancellationToken cancellationToken = default) =>
        await _marketplace.WaitForSuccessAsync(_marketplace.Uninstall(packageId), cancellationToken);

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

    private static CollectorMarketplaceOperationSnapshot ToPresentation(HostManagementOperation operation) => new(
        operation.OperationId,
        operation.PackageId,
        operation.Kind switch
        {
            HostManagementOperationKind.Install => CollectorMarketplaceOperationKind.Install,
            HostManagementOperationKind.Retry => CollectorMarketplaceOperationKind.Retry,
            HostManagementOperationKind.Uninstall => CollectorMarketplaceOperationKind.Uninstall,
            _ => CollectorMarketplaceOperationKind.SubmitAuthorization
        },
        operation.Phase switch
        {
            HostManagementOperationPhase.Pending => CollectorMarketplaceOperationPhase.Pending,
            HostManagementOperationPhase.Running => CollectorMarketplaceOperationPhase.Running,
            HostManagementOperationPhase.Committing => CollectorMarketplaceOperationPhase.Committing,
            HostManagementOperationPhase.Succeeded => CollectorMarketplaceOperationPhase.Succeeded,
            HostManagementOperationPhase.Cancelled => CollectorMarketplaceOperationPhase.Cancelled,
            _ => CollectorMarketplaceOperationPhase.Failed
        },
        operation.Failure);

}
