using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Packaging;

public sealed class CollectorMarketplaceRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-marketplace-runtime-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task AcceptedInstall_OutlivesCancelledWaiterAndPublishesItsResult()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new PausingInstallPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;

        var accepted = marketplace.Install("heartbeat.collector.reference-managed");
        await source.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var abandonedWait = new CancellationTokenSource();
        var waiting = marketplace.WaitForOperationAsync(
            accepted.OperationId,
            abandonedWait.Token).AsTask();
        abandonedWait.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(marketplace.GetOperation(accepted.OperationId).IsTerminal);

        source.AllowInstall.TrySetResult();
        var completed = await marketplace.WaitForOperationAsync(accepted.OperationId);

        Assert.Equal(HostManagementOperationPhase.Succeeded, completed.Phase);
        Assert.Single(runtime.ListInstances());
    }

    [Fact]
    public async Task CancelInstall_BeforeCommitStopsOwnedWorkWithoutPublishingFacts()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new PausingInstallPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;
        var accepted = marketplace.Install("heartbeat.collector.reference-managed");
        await source.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = marketplace.CancelOperation(accepted.OperationId);
        var completed = await marketplace.WaitForOperationAsync(accepted.OperationId);

        Assert.Equal(HostManagementOperationCancellation.Accepted, cancellation);
        Assert.Equal(HostManagementOperationPhase.Cancelled, completed.Phase);
        Assert.Empty(runtime.ListInstances());
        Assert.Empty(installations.List());
    }

    [Fact]
    public async Task CancelInstall_AfterCommitBeginsIsRejectedAndInstallationFinishes()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var host = new PausingAttachHostAdapter();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            new LocalPackageSource(installations, package.Path, CurrentTarget()),
            installations,
            runtime,
            CurrentTarget(),
            host);
        marketplace.Start();
        await marketplace.Initialized;
        var accepted = marketplace.Install("heartbeat.collector.reference-managed");
        await host.AttachStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = marketplace.CancelOperation(accepted.OperationId);

        Assert.Equal(HostManagementOperationCancellation.NotCancellable, cancellation);
        Assert.Equal(
            HostManagementOperationPhase.Committing,
            marketplace.GetOperation(accepted.OperationId).Phase);

        host.AllowAttach.TrySetResult();
        var completed = await marketplace.WaitForOperationAsync(accepted.OperationId);

        Assert.Equal(HostManagementOperationPhase.Succeeded, completed.Phase);
        Assert.Single(runtime.ListInstances());
        Assert.Single(installations.List());
    }

    [Fact]
    public async Task HostStop_CancelsAcceptedInstallWhileItIsStillSafeToExit()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new PausingInstallPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;
        var accepted = marketplace.Install("heartbeat.collector.reference-managed");
        await source.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await marketplace.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await marketplace.WaitForOperationAsync(accepted.OperationId);

        Assert.Equal(HostManagementOperationPhase.Cancelled, completed.Phase);
        Assert.Empty(runtime.ListInstances());
        Assert.Empty(installations.List());
    }

    [Fact]
    public async Task HostStop_WaitsForInstallThatHasStartedCommitting()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var host = new PausingAttachHostAdapter();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            new LocalPackageSource(installations, package.Path, CurrentTarget()),
            installations,
            runtime,
            CurrentTarget(),
            host);
        marketplace.Start();
        await marketplace.Initialized;
        var accepted = marketplace.Install("heartbeat.collector.reference-managed");
        await host.AttachStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopping = marketplace.StopAsync().AsTask();

        Assert.False(stopping.IsCompleted);
        host.AllowAttach.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            HostManagementOperationPhase.Succeeded,
            (await marketplace.WaitForOperationAsync(accepted.OperationId)).Phase);
    }

    [Fact]
    public async Task CancelUninstall_AfterCommitBeginsIsRejected()
    {
        using var package = ExternalHostReferencePackage();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var host = new PausingDetachHostAdapter();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            new LocalPackageSource(installations, package.Path, CurrentTarget()),
            installations,
            runtime,
            CurrentTarget(),
            host);
        marketplace.Start();
        await marketplace.Initialized;
        await CompleteInstallAsync(marketplace, "heartbeat.collector.reference");
        var accepted = marketplace.Uninstall("heartbeat.collector.reference");
        await host.DetachStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            HostManagementOperationCancellation.NotCancellable,
            marketplace.CancelOperation(accepted.OperationId));

        host.AllowDetach.TrySetResult();
        await CompleteOperationAsync(marketplace, accepted);
        Assert.Empty(runtime.ListInstances());
    }

    [Fact]
    public async Task CancelRetry_BeforeCommitKeepsTheInstalledFacts()
    {
        using var package = ExternalHostReferencePackage();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new PausingBrowsePackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;
        var installed = await CompleteInstallAsync(marketplace, "heartbeat.collector.reference");
        source.PauseBrowse = true;
        var browse = marketplace.BrowseAsync().AsTask();
        await source.BrowseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accepted = marketplace.Retry(installed.PackageId);

        Assert.Equal(
            HostManagementOperationCancellation.Accepted,
            marketplace.CancelOperation(accepted.OperationId));
        source.AllowBrowse.TrySetResult();
        await browse;
        Assert.Equal(
            HostManagementOperationPhase.Cancelled,
            (await marketplace.WaitForOperationAsync(accepted.OperationId)).Phase);
        Assert.Single(runtime.ListInstances());
        Assert.Single(installations.List());
    }

    [Fact]
    public async Task AcceptedAuthorizationOwnsAnImmutableCopyOfTheValues()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            new LocalPackageSource(installations, package.Path, CurrentTarget()),
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter(),
            new ManagedProcessActivationOptions
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "authorization_required"
                }
            });
        marketplace.Start();
        await marketplace.Initialized;
        var installed = await CompleteInstallAsync(
            marketplace,
            "heartbeat.collector.reference-managed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.WaitForManagedProcessPhaseAsync(
            installed.CollectorInstanceId!.Value,
            CollectorRuntimePhase.WaitingForAuthorization,
            timeout.Token);
        var challenge = Assert.IsType<CollectorAuthorizationChallenge>(
            runtime.GetManagedProcessRuntimeState(installed.CollectorInstanceId.Value)
                .AuthorizationChallenge);
        var values = new Dictionary<string, string>
        {
            ["username"] = "collector-user",
            ["password"] = "collector-password"
        };

        var accepted = marketplace.SubmitAuthorization(
            installed.CollectorInstanceId.Value,
            challenge.InteractionId,
            values);
        values.Clear();

        await CompleteOperationAsync(marketplace, accepted);
        await runtime.WaitForManagedProcessPhaseAsync(
            installed.CollectorInstanceId.Value,
            CollectorRuntimePhase.Ready,
            timeout.Token);
    }

    [Fact]
    public async Task DuplicateOperationJoinsWhileConflictingOperationIsRejected()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new PausingInstallPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;
        var first = marketplace.Install("heartbeat.collector.reference-managed");
        await source.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = marketplace.Install("heartbeat.collector.reference-managed");
        var conflict = Assert.Throws<InvalidOperationException>(() =>
            marketplace.Uninstall("heartbeat.collector.reference-managed"));

        Assert.Equal(first.OperationId, duplicate.OperationId);
        Assert.Contains("Install operation", conflict.Message);

        source.AllowInstall.TrySetResult();
        await marketplace.WaitForOperationAsync(first.OperationId);
    }

    [Fact]
    public async Task RestartRestoresCommittedFactsButNotManagementOperationHistory()
    {
        using var package = ExternalHostReferencePackage();
        var installationPath = Path.Combine(_root, "packages");
        var runtimePath = Path.Combine(_root, "runtime.json");
        var installations = new CollectorPackageInstallations(installationPath);
        var source = new LocalPackageSource(installations, package.Path, CurrentTarget());
        using (var runtime = CollectorRuntime.Open(
                   runtimePath,
                   new SegmentIngestService(new FixedClock())))
        {
            await using var marketplace = new CollectorMarketplaceRuntime(
                source,
                installations,
                runtime,
                CurrentTarget(),
                new RecordingHostAdapter());
            marketplace.Start();
            await marketplace.Initialized;
            var accepted = marketplace.Install("heartbeat.collector.reference");
            await marketplace.WaitForOperationAsync(accepted.OperationId);
            Assert.Single(marketplace.OperationsSnapshot());
        }

        var reopenedInstallations = new CollectorPackageInstallations(installationPath);
        using var reopenedRuntime = CollectorRuntime.Open(
            runtimePath,
            new SegmentIngestService(new FixedClock()));
        await using var reopenedMarketplace = new CollectorMarketplaceRuntime(
            new LocalPackageSource(reopenedInstallations, package.Path, CurrentTarget()),
            reopenedInstallations,
            reopenedRuntime,
            CurrentTarget(),
            new RecordingHostAdapter());
        reopenedMarketplace.Start();
        await reopenedMarketplace.Initialized;

        Assert.Empty(reopenedMarketplace.OperationsSnapshot());
        Assert.Single(reopenedRuntime.ListInstances());
        Assert.Single(reopenedInstallations.List());
    }

    [Fact]
    public async Task ManagedPackage_UsesTheSharedInstallActivateStatusAndRemovalLifecycle()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new LocalPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()),
            secretStore: new EncryptedFileCollectorSecretStore(Path.Combine(_root, "secrets")));
        var host = new RecordingHostAdapter();
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            host);

        marketplace.Start();
        await marketplace.Initialized;
        var installed = await CompleteInstallAsync(
            marketplace,
            "heartbeat.collector.reference-managed");
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.WaitForManagedProcessPhaseAsync(
            installed.CollectorInstanceId!.Value,
            CollectorRuntimePhase.Ready,
            ready.Token);

        var running = Assert.Single(await marketplace.BrowseAsync());
        Assert.Equal(CollectorMarketplaceRuntimePhase.Running, running.Phase);
        Assert.Null(running.ConnectedExternalHosts);
        Assert.Equal(1, host.AttachCount);
        Assert.Single(runtime.ListInstances());

        await CompleteOperationAsync(marketplace, marketplace.Uninstall(running.PackageId));

        Assert.Empty(runtime.ListInstances());
        Assert.Empty(installations.List());
        Assert.Equal(1, host.DetachCount);
    }

    [Fact]
    public async Task Stop_CancelsAnInFlightCatalogReadAndWaitsForTheOperationFence()
    {
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new BlockingPackageSource();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;

        var browse = marketplace.BrowseAsync().AsTask();
        await source.BrowseStarted.Task;
        await marketplace.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browse);
        Assert.True(source.BrowseCancelled.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_JoinsInFlightOperationsAndRetainsTheResult(bool cancellationFails)
    {
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new BlockingPackageSource { PauseCancellation = true, CancellationFails = cancellationFails };
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"), new SegmentIngestService(new FixedClock()));
        var marketplace = new CollectorMarketplaceRuntime(
            source, installations, runtime, CurrentTarget(), new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;
        var browse = marketplace.BrowseAsync().AsTask();
        await source.BrowseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = marketplace.DisposeAsync().AsTask();
        await source.BrowseCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = marketplace.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.Same(first, second);
        }
        finally
        {
            source.AllowReturn.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browse);
            if (cancellationFails)
                await Assert.ThrowsAnyAsync<Exception>(() => first);
            else
                await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Same(first, marketplace.DisposeAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(marketplace.Start);
    }

    [Fact]
    public async Task ExternalHostPackage_StatusComesFromTheGenericRuntimeInsteadOfAProductAdapter()
    {
        using var package = ExternalHostReferencePackage();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new LocalPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;

        var installed = await CompleteInstallAsync(marketplace, "heartbeat.collector.reference");

        Assert.Equal(CollectorMarketplaceRuntimePhase.WaitingForExternalHost, installed.Phase);
        Assert.Equal(0, installed.ConnectedExternalHosts);

        var installation = Assert.Single(installations.List());
        var localPackage = installation.Package;
        var artifact = Assert.Single(localPackage.Artifacts);
        var initialization = runtime.BeginExternalHostActivation(
            installed.CollectorInstanceId!.Value,
            localPackage,
            artifact.ArtifactId,
            artifact.ContentHash,
            new ProtocolSupport([1], new Dictionary<string, IReadOnlyList<int>>
            {
                ["facts.segment"] = [1],
                ["diagnostics.stream-gap"] = [1]
            }),
            "reference-host",
            "reference.app",
            Guid.CreateVersion7());
        var activation = runtime.OpenExternalHostStreams(
            initialization.ActivationId,
            initialization.Spec.SpecRevision,
            [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        await runtime.MarkExternalHostReadyAsync(activation, initialization.Spec.SpecRevision);

        var running = Assert.Single(await marketplace.BrowseAsync());
        Assert.Equal(CollectorMarketplaceRuntimePhase.Running, running.Phase);
        Assert.Equal(1, running.ConnectedExternalHosts);

        runtime.ReportExternalHostIdentityConflict(
            running.CollectorInstanceId!.Value,
            "reference-host",
            "another.app");
        var conflict = Assert.Single(marketplace.InstalledSnapshot());
        Assert.Equal(CollectorMarketplaceRuntimePhase.ConnectionConflict, conflict.Phase);
        Assert.Contains("reference-host", conflict.Failure!.Message);

        var unrelated = runtime.BeginExternalHostActivation(
            running.CollectorInstanceId.Value,
            localPackage,
            artifact.ArtifactId,
            artifact.ContentHash,
            new ProtocolSupport([1], new Dictionary<string, IReadOnlyList<int>>
            {
                ["facts.segment"] = [1],
                ["diagnostics.stream-gap"] = [1]
            }),
            "another-reference-host",
            "another.app",
            Guid.CreateVersion7());
        var unrelatedActivation = runtime.OpenExternalHostStreams(
            unrelated.ActivationId,
            unrelated.Spec.SpecRevision,
            [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        await runtime.MarkExternalHostReadyAsync(unrelatedActivation, unrelated.Spec.SpecRevision);

        Assert.Equal(
            CollectorMarketplaceRuntimePhase.ConnectionConflict,
            Assert.Single(marketplace.InstalledSnapshot()).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementReady_PreservesUnknownCustodyFromThePreviousActivation(bool installAgain)
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new LocalPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        var environment = new Dictionary<string, string>
        {
            ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "exit_before_hello"
        };
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter(),
            new ManagedProcessActivationOptions { EnvironmentVariables = environment });
        marketplace.Start();
        await marketplace.Initialized;

        var installed = await CompleteInstallAsync(
            marketplace,
            "heartbeat.collector.reference-managed");
        using var failureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.WaitForManagedProcessPhaseAsync(
            installed.CollectorInstanceId!.Value,
            CollectorRuntimePhase.Failed,
            failureTimeout.Token);
        Assert.Equal(
            CollectorMarketplaceRuntimePhase.Failed,
            Assert.Single(marketplace.InstalledSnapshot()).Phase);

        environment.Clear();
        await CompleteOperationAsync(marketplace, installAgain
            ? marketplace.Install(installed.PackageId) : marketplace.Retry(installed.PackageId));
        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.WaitForManagedProcessPhaseAsync(
            installed.CollectorInstanceId.Value,
            CollectorRuntimePhase.Ready,
            readyTimeout.Token);

        var recovered = Assert.Single(marketplace.InstalledSnapshot());
        await marketplace.StopAsync();
        Assert.False(marketplace.ShutdownRemainder.IsDurable);
        Assert.Equal(CollectorMarketplaceRuntimePhase.Running, recovered.Phase);
        Assert.Null(recovered.Failure);
    }

    [Fact]
    public async Task CatalogFailure_KeepsInstalledFactsWithoutInventingALatestVersion()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        var source = new LocalPackageSource(installations, package.Path, CurrentTarget());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            source,
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter());
        marketplace.Start();
        await marketplace.Initialized;
        await CompleteInstallAsync(marketplace, "heartbeat.collector.reference-managed");
        source.BrowseException = new IOException("registry offline");

        var snapshot = Assert.Single(await marketplace.BrowseAsync());

        Assert.Null(snapshot.LatestVersion);
        Assert.Equal("1.0.0", snapshot.InstalledVersion);
        Assert.Equal("catalog_unavailable", snapshot.CatalogFailure!.Code);
    }

    [Fact]
    public async Task Uninstall_WhileWaitingForAuthorizationStopsThePendingProcess()
    {
        using var package = ManagedReferenceCollectorPackage.Create();
        var installations = new CollectorPackageInstallations(Path.Combine(_root, "packages"));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(_root, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        await using var marketplace = new CollectorMarketplaceRuntime(
            new LocalPackageSource(installations, package.Path, CurrentTarget()),
            installations,
            runtime,
            CurrentTarget(),
            new RecordingHostAdapter(),
            new ManagedProcessActivationOptions
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["HEARTBEAT_REFERENCE_BEHAVIOR"] = "authorization_required"
                }
            });
        marketplace.Start();
        await marketplace.Initialized;
        var installed = await CompleteInstallAsync(
            marketplace,
            "heartbeat.collector.reference-managed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.WaitForManagedProcessPhaseAsync(
            installed.CollectorInstanceId!.Value,
            CollectorRuntimePhase.WaitingForAuthorization,
            timeout.Token);

        await CompleteOperationAsync(marketplace, marketplace.Uninstall(installed.PackageId));

        Assert.Empty(runtime.ListInstances());
        Assert.Empty(installations.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CollectorMarketplaceTarget CurrentTarget() => new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    private static async ValueTask<CollectorMarketplaceRuntimeItem> CompleteInstallAsync(
        CollectorMarketplaceRuntime marketplace,
        string packageId)
    {
        await CompleteOperationAsync(marketplace, marketplace.Install(packageId));
        return marketplace.InstalledSnapshot().Single(item => item.PackageId == packageId);
    }

    private static async ValueTask CompleteOperationAsync(
        CollectorMarketplaceRuntime marketplace,
        HostManagementOperation accepted)
    {
        var completed = await marketplace.WaitForOperationAsync(accepted.OperationId);
        Assert.Equal(HostManagementOperationPhase.Succeeded, completed.Phase);
    }

    private static ReferenceCollectorPackageCopy ExternalHostReferencePackage()
    {
        var copy = ReferenceCollectorPackageCopy.Create(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "ReferenceCollectorPackage"));
        var manifestPath = Path.Combine(copy.Path, "collector-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["artifacts"]![0]!["selector"]!["driver"] = "externalHost";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        return copy;
    }

    private sealed class LocalPackageSource(
        CollectorPackageInstallations installations,
        string packageDirectory,
        CollectorMarketplaceTarget target) : ICollectorPackageMarketplace
    {
        private readonly LocalCollectorPackage _package = LocalCollectorPackage.Load(packageDirectory);
        public Exception? BrowseException { get; set; }

        public ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
            CancellationToken cancellationToken = default)
        {
            if (BrowseException is not null)
                return ValueTask.FromException<IReadOnlyList<CollectorCatalogItem>>(BrowseException);
            return ValueTask.FromResult<IReadOnlyList<CollectorCatalogItem>>([
                new(
                    _package.Manifest.PackageId,
                    _package.Manifest.Presentation!.DisplayName,
                    _package.Manifest.Presentation.Summary,
                    _package.Manifest.Version,
                    target)
            ]);
        }

        public ValueTask<CollectorPackageInstallation> InstallLatestAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(installations.Install(packageDirectory));
    }

    private sealed class BlockingPackageSource : ICollectorPackageMarketplace
    {
        public bool PauseCancellation { get; init; }
        public bool CancellationFails { get; init; }
        public TaskCompletionSource AllowReturn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BrowseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BrowseCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() =>
            {
                if (CancellationFails) throw new IOException("cancellation callback failed");
            });
            BrowseStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            catch (OperationCanceledException)
            {
                BrowseCancelled.TrySetResult();
                if (PauseCancellation) await AllowReturn.Task;
                throw;
            }
        }

        public ValueTask<CollectorPackageInstallation> InstallLatestAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<CollectorPackageInstallation>(new NotSupportedException());
    }

    private sealed class PausingInstallPackageSource(
        CollectorPackageInstallations installations,
        string packageDirectory,
        CollectorMarketplaceTarget target) : ICollectorPackageMarketplace
    {
        private readonly LocalPackageSource _inner = new(installations, packageDirectory, target);
        public TaskCompletionSource InstallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowInstall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
            CancellationToken cancellationToken = default) =>
            _inner.BrowseAsync(cancellationToken);

        public async ValueTask<CollectorPackageInstallation> InstallLatestAsync(
            string packageId,
            CancellationToken cancellationToken = default)
        {
            InstallStarted.TrySetResult();
            await AllowInstall.Task.WaitAsync(cancellationToken);
            return await _inner.InstallLatestAsync(packageId, cancellationToken);
        }
    }

    private sealed class PausingBrowsePackageSource(
        CollectorPackageInstallations installations,
        string packageDirectory,
        CollectorMarketplaceTarget target) : ICollectorPackageMarketplace
    {
        private readonly LocalPackageSource _inner = new(installations, packageDirectory, target);
        public bool PauseBrowse { get; set; }
        public TaskCompletionSource BrowseStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowBrowse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<CollectorCatalogItem>> BrowseAsync(
            CancellationToken cancellationToken = default)
        {
            if (PauseBrowse)
            {
                BrowseStarted.TrySetResult();
                await AllowBrowse.Task.WaitAsync(cancellationToken);
            }
            return await _inner.BrowseAsync(cancellationToken);
        }

        public ValueTask<CollectorPackageInstallation> InstallLatestAsync(
            string packageId,
            CancellationToken cancellationToken = default) =>
            _inner.InstallLatestAsync(packageId, cancellationToken);
    }

    private sealed class RecordingHostAdapter : ICollectorMarketplaceHostAdapter
    {
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }

        public SubjectReference CreateSubject(SubjectKind kind) =>
            new(Guid.Parse("0199a18b-1cc8-7000-8000-000000000001"), kind);

        public ValueTask AttachAsync(
            CollectorInstance instance,
            CollectorPackagePresentation presentation,
            CancellationToken cancellationToken)
        {
            AttachCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DetachAsync(CollectorInstance instance, CancellationToken cancellationToken)
        {
            DetachCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-09-04T00:00:00Z");
    }

    private sealed class PausingAttachHostAdapter : ICollectorMarketplaceHostAdapter
    {
        public TaskCompletionSource AttachStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowAttach { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubjectReference CreateSubject(SubjectKind kind) =>
            new(Guid.Parse("0199a18b-1cc8-7000-8000-000000000001"), kind);

        public async ValueTask AttachAsync(
            CollectorInstance instance,
            CollectorPackagePresentation presentation,
            CancellationToken cancellationToken)
        {
            AttachStarted.TrySetResult();
            await AllowAttach.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DetachAsync(
            CollectorInstance instance,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class PausingDetachHostAdapter : ICollectorMarketplaceHostAdapter
    {
        public TaskCompletionSource DetachStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDetach { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SubjectReference CreateSubject(SubjectKind kind) =>
            new(Guid.Parse("0199a18b-1cc8-7000-8000-000000000001"), kind);

        public ValueTask AttachAsync(
            CollectorInstance instance,
            CollectorPackagePresentation presentation,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask DetachAsync(
            CollectorInstance instance,
            CancellationToken cancellationToken)
        {
            DetachStarted.TrySetResult();
            await AllowDetach.Task.WaitAsync(cancellationToken);
        }
    }

}
