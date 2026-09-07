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

    private sealed class Management : ICollectorMarketplace
    {
        public IReadOnlyList<CollectorMarketplaceRuntimeItem> InstalledSnapshot() => [];
        public ValueTask<IReadOnlyList<CollectorMarketplaceRuntimeItem>> BrowseAsync(CancellationToken token = default) =>
            ValueTask.FromResult<IReadOnlyList<CollectorMarketplaceRuntimeItem>>([
                new("reference", "Reference", "Example", "1.0.0", "1.0.0", Guid.NewGuid(),
                    CollectorMarketplaceRuntimePhase.WaitingForExternalHost, ConnectedExternalHosts: 2)
            ]);
        public ValueTask<CollectorMarketplaceRuntimeItem> InstallAsync(string packageId, CancellationToken token = default) =>
            throw new NotSupportedException();
        public ValueTask<CollectorMarketplaceRuntimeItem> RetryAsync(string packageId, CancellationToken token = default) =>
            throw new NotSupportedException();
        public ValueTask UninstallAsync(string packageId, CancellationToken token = default) =>
            throw new NotSupportedException();
        public ValueTask SubmitAuthorizationAsync(Guid instanceId, Guid interactionId,
            IReadOnlyDictionary<string, string> values, CancellationToken token = default) =>
            throw new NotSupportedException();
    }
}
