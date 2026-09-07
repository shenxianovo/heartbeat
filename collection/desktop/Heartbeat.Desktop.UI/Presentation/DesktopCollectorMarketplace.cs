using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using System.Runtime.InteropServices;

namespace Heartbeat.Desktop.UI.Presentation;

/// <summary>
/// Thin native-presentation adapter over the shared Collector Marketplace Runtime. It selects the
/// Desktop target and Machine Subject but does not interpret Package, Driver, Activation or status.
/// </summary>
public sealed class DesktopCollectorMarketplace : IDesktopCollectorMarketplace, IAsyncDisposable
{
    public static readonly Uri OfficialRegistryRoot =
        new("https://heartbeat.shenxianovo.com/collector-registry/v1/");

    private readonly HttpClient _http;
    private readonly CollectorMarketplaceRuntime _marketplace;

    private DesktopCollectorMarketplace(HttpClient http, CollectorMarketplaceRuntime marketplace)
    {
        _http = http;
        _marketplace = marketplace;
    }

    public static DesktopCollectorMarketplace Open(
        string operatingSystem,
        string machineIdentity,
        CollectorPackageInstallations installations,
        CollectorRuntime runtime,
        Uri? registryRoot = null)
    {
        var target = new CollectorMarketplaceTarget(operatingSystem, CurrentArchitecture());
        var http = new HttpClient();
        try
        {
            var packages = new CollectorPackageMarketplace(
                http,
                registryRoot ?? OfficialRegistryRoot,
                target,
                installations);
            var marketplace = new CollectorMarketplaceRuntime(
                packages,
                installations,
                runtime,
                target,
                new DesktopHostAdapter(machineIdentity));
            return new DesktopCollectorMarketplace(http, marketplace);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public void StartInstalledCollectors() => _marketplace.Start();

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

    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _marketplace.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _marketplace.DisposeAsync();
        _http.Dispose();
    }

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

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            "Desktop Collector Marketplace supports x64 and arm64.")
    };

    private sealed class DesktopHostAdapter(string machineIdentity) : ICollectorMarketplaceHostAdapter
    {
        public SubjectReference CreateSubject(SubjectKind kind)
        {
            if (kind != SubjectKind.Machine)
                throw new PackageValidationException(
                    "Desktop Marketplace default Instances must use the Machine Subject.");
            if (!Guid.TryParse(machineIdentity, out var machineId) || machineId == Guid.Empty)
                throw new InvalidOperationException(
                    "Desktop machine identity must be a non-empty UUID before installing a Collector.");
            return new SubjectReference(machineId, SubjectKind.Machine);
        }

        public ValueTask AttachAsync(
            CollectorInstance instance,
            CollectorPackagePresentation presentation,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DetachAsync(
            CollectorInstance instance,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
