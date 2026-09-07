using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Packages;

public sealed class CollectorInstallationLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"heartbeat-installation-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task Install_CreatesOneDefaultInstanceFromThePackageBlueprint()
    {
        var packageSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceCollectorPackage");
        var package = LocalCollectorPackage.Load(packageSource);
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new NullSegmentSink());
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
        using var lifecycle = new CollectorInstallationLifecycle(
            new LocalPackageSource(installations, packageSource),
            installations,
            runtime,
            kind => kind == SubjectKind.Machine
                ? subject
                : throw new InvalidOperationException("Unexpected SubjectKind."));

        var first = await lifecycle.InstallAsync(package.Manifest.PackageId);
        var second = await lifecycle.InstallAsync(package.Manifest.PackageId);

        Assert.Equal(first.Instance.CollectorInstanceId, second.Instance.CollectorInstanceId);
        Assert.Equal(CollectorRuntime.DefaultInstanceKey, first.Instance.InstanceKey);
        Assert.Equal(subject, first.Instance.Subject);
        Assert.Equal(package.Manifest.DefaultInstance!.ConfigVersion, first.Instance.Spec.ConfigVersion);
        Assert.Equal("{}", first.Instance.Spec.Config.GetRawText());
        Assert.Single(lifecycle.ListInstalled());
        Assert.Single(runtime.ListInstances());
        Assert.Single(installations.List());
    }

    [Fact]
    public async Task Uninstall_RemovesTheDefaultInstanceAndItsExactInstallation()
    {
        var packageSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceCollectorPackage");
        var package = LocalCollectorPackage.Load(packageSource);
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "uninstall-packages"));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "uninstall-runtime.json"),
            new NullSegmentSink());
        using var lifecycle = new CollectorInstallationLifecycle(
            new LocalPackageSource(installations, packageSource),
            installations,
            runtime,
            kind => new SubjectReference(Guid.CreateVersion7(), kind));
        await lifecycle.InstallAsync(package.Manifest.PackageId);

        await lifecycle.UninstallAsync(package.Manifest.PackageId);

        Assert.Empty(lifecycle.ListInstalled());
        Assert.Empty(runtime.ListInstances());
        Assert.Empty(installations.List());
    }

    [Fact]
    public async Task InspectInstalled_IsolatesACorruptInstallationWithoutHidingItsInstance()
    {
        var packageSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceCollectorPackage");
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "corrupt-packages"));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "corrupt-runtime.json"),
            new NullSegmentSink());
        using var lifecycle = new CollectorInstallationLifecycle(
            new LocalPackageSource(installations, packageSource),
            installations,
            runtime,
            kind => new SubjectReference(Guid.CreateVersion7(), kind));
        var installed = await lifecycle.InstallAsync("heartbeat.collector.reference");
        File.Delete(Path.Combine(installed.Installation.Directory, "collector-manifest.json"));

        var inspection = Assert.Single(lifecycle.InspectInstalled());

        Assert.False(inspection.IsValid);
        Assert.Equal(installed.Instance.CollectorInstanceId, inspection.Instance.CollectorInstanceId);
        Assert.NotNull(inspection.Failure);
    }

    [Fact]
    public async Task Uninstall_RuntimeCommitFailureLeavesAVisibleRetryableInstance()
    {
        var packageSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReferenceCollectorPackage");
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "retry-packages"));
        var failRuntimeCommit = false;
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "retry-runtime.json"),
            new NullSegmentSink(),
            new CollectorRuntimeOptions
            {
                BeforeStatePrepare = () =>
                {
                    if (failRuntimeCommit)
                        throw new IOException("injected runtime commit failure");
                }
            });
        using var lifecycle = new CollectorInstallationLifecycle(
            new LocalPackageSource(installations, packageSource),
            installations,
            runtime,
            kind => new SubjectReference(Guid.CreateVersion7(), kind));
        await lifecycle.InstallAsync("heartbeat.collector.reference");
        failRuntimeCommit = true;

        await Assert.ThrowsAsync<IOException>(async () =>
            await lifecycle.UninstallAsync("heartbeat.collector.reference"));

        var failed = Assert.Single(lifecycle.InspectInstalled());
        Assert.False(failed.IsValid);
        Assert.Empty(installations.List());

        failRuntimeCommit = false;
        await lifecycle.UninstallAsync("heartbeat.collector.reference");
        Assert.Empty(lifecycle.InspectInstalled());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class LocalPackageSource(
        CollectorPackageInstallations installations,
        string packageSource) : ICollectorPackageMarketplace
    {
        private readonly LocalCollectorPackage _package = LocalCollectorPackage.Load(packageSource);

        public ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CollectorCatalogItem>>([
                new(
                    _package.Manifest.PackageId,
                    _package.Manifest.Presentation!.DisplayName,
                    _package.Manifest.Presentation.Summary,
                    _package.Manifest.Version,
                    new CollectorMarketplaceTarget("test", "test"))
            ]);

        public ValueTask<CollectorPackageInstallation> InstallLatestAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(installations.Install(packageSource));
    }

    private sealed class NullSegmentSink : ISegmentSink
    {
        public void Push(List<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem> snapshots) { }
    }
}
