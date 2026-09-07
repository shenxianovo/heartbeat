using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.Tests.Presentation;

public sealed class DesktopCollectorMarketplaceTests
{
    [Fact]
    public async Task Browse_MapsHostReadModelWithoutOwningTheRuntime()
    {
        var marketplace = new DesktopCollectorMarketplace(new Management());

        var snapshot = Assert.Single(await marketplace.BrowseAsync());

        Assert.Equal("reference", snapshot.PackageId);
        Assert.Equal(CollectorMarketplacePhase.Waiting, snapshot.Phase);
        Assert.Equal(2, snapshot.ConnectedHosts);
        Assert.False((object)marketplace is IAsyncDisposable);
        Assert.False((object)marketplace is IDisposable);
    }

    [Fact]
    public void OperationsSnapshot_MapsTheHostOwnedResultWithoutExposingExecutionOwnership()
    {
        var operationId = Guid.CreateVersion7();
        var marketplace = new DesktopCollectorMarketplace(new Management
        {
            Operations =
            [
                new HostManagementOperation(
                    operationId,
                    HostManagementOperationKind.Uninstall,
                    "reference",
                    HostManagementOperationPhase.Committing)
            ]
        });

        var operation = Assert.Single(marketplace.OperationsSnapshot());

        Assert.Equal(operationId, operation.OperationId);
        Assert.Equal(CollectorMarketplaceOperationKind.Uninstall, operation.Kind);
        Assert.Equal(CollectorMarketplaceOperationPhase.Committing, operation.Phase);
    }

    private sealed class Management : ICollectorMarketplace
    {
        public IReadOnlyList<HostManagementOperation> Operations { get; init; } = [];
        public IReadOnlyList<CollectorMarketplaceRuntimeItem> InstalledSnapshot() => [];
        public ValueTask<IReadOnlyList<CollectorMarketplaceRuntimeItem>> BrowseAsync(CancellationToken token = default) =>
            ValueTask.FromResult<IReadOnlyList<CollectorMarketplaceRuntimeItem>>([
                new("reference", "Reference", "Example", "1.0.0", "1.0.0", Guid.NewGuid(),
                    CollectorMarketplaceRuntimePhase.WaitingForExternalHost, ConnectedExternalHosts: 2)
            ]);
        public HostManagementOperation Install(string packageId) => throw new NotSupportedException();
        public HostManagementOperation Retry(string packageId) => throw new NotSupportedException();
        public HostManagementOperation Uninstall(string packageId) => throw new NotSupportedException();
        public HostManagementOperation SubmitAuthorization(Guid instanceId, Guid interactionId,
            IReadOnlyDictionary<string, string> values) => throw new NotSupportedException();
        public IReadOnlyList<HostManagementOperation> OperationsSnapshot() => Operations;
        public HostManagementOperation GetOperation(Guid operationId) => throw new NotSupportedException();
        public ValueTask<HostManagementOperation> WaitForOperationAsync(
            Guid operationId,
            CancellationToken token = default) => throw new NotSupportedException();
        public HostManagementOperationCancellation CancelOperation(Guid operationId) =>
            HostManagementOperationCancellation.NotFound;
    }
}
