using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Collectors.Packages;

public enum CollectorMarketplaceRuntimePhase
{
    NotInstalled,
    WaitingForExternalHost,
    Starting,
    WaitingForAuthorization,
    Running,
    ConnectionConflict,
    Failed
}

public sealed record CollectorMarketplaceRuntimeItem(
    string PackageId,
    string DisplayName,
    string Summary,
    string? LatestVersion,
    string? InstalledVersion,
    Guid? CollectorInstanceId,
    CollectorMarketplaceRuntimePhase Phase,
    int? ConnectedExternalHosts = null,
    CollectorRuntimeFailure? Failure = null,
    CollectorAuthorizationChallenge? Authorization = null,
    CollectorRuntimeFailure? CatalogFailure = null);

/// <summary>
/// The only Host-specific part of Marketplace lifecycle. Package acquisition, Instance creation,
/// Driver dispatch, activation, recovery, status and removal remain in the shared runtime module.
/// </summary>
public interface ICollectorMarketplaceHostAdapter
{
    SubjectReference CreateSubject(SubjectKind kind);

    ValueTask AttachAsync(
        CollectorInstance instance,
        CollectorPackagePresentation presentation,
        CancellationToken cancellationToken);

    ValueTask DetachAsync(CollectorInstance instance, CancellationToken cancellationToken);
}

public interface ICollectorMarketplace
{
    IReadOnlyList<CollectorMarketplaceRuntimeItem> InstalledSnapshot();
    ValueTask<IReadOnlyList<CollectorMarketplaceRuntimeItem>> BrowseAsync(
        CancellationToken cancellationToken = default);
    ValueTask<CollectorMarketplaceRuntimeItem> InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default);
    ValueTask<CollectorMarketplaceRuntimeItem> RetryAsync(
        string packageId,
        CancellationToken cancellationToken = default);
    ValueTask UninstallAsync(string packageId, CancellationToken cancellationToken = default);
    ValueTask SubmitAuthorizationAsync(
        Guid collectorInstanceId,
        Guid interactionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);
}

public interface ICollectorMarketplaceRuntime : ICollectorMarketplace, IAsyncDisposable
{
    Task Initialized { get; }
    void Start();
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Deep, Host-independent Collector Marketplace runtime. It is the single owner of the complete
/// Catalog → Installation → default Instance → selected Driver → Activation → status → removal
/// transition. Desktop and Headless only provide a Subject/projection adapter.
/// </summary>
public sealed class CollectorMarketplaceRuntime : ICollectorMarketplaceRuntime
{
    private readonly ICollectorPackageMarketplace _packages;
    private readonly CollectorInstallationLifecycle _installations;
    private readonly CollectorRuntime _runtime;
    private readonly CollectorMarketplaceTarget _target;
    private readonly ICollectorMarketplaceHostAdapter _host;
    private readonly ManagedProcessActivationOptions _managedProcessOptions;
    private readonly object _stateGate = new();
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, ManagedEntry> _managed = [];
    private readonly HashSet<Guid> _attached = [];
    private readonly Dictionary<Guid, CollectorRuntimeFailure> _hostFailures = [];
    private Task? _restoration;
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _stopping;
    private int _disposed;

    public CollectorMarketplaceRuntime(
        ICollectorPackageMarketplace packages,
        CollectorPackageInstallations installations,
        CollectorRuntime runtime,
        CollectorMarketplaceTarget target,
        ICollectorMarketplaceHostAdapter host,
        ManagedProcessActivationOptions? managedProcessOptions = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(host);
        _packages = packages;
        _runtime = runtime;
        _target = target;
        _host = host;
        _managedProcessOptions = managedProcessOptions ?? new ManagedProcessActivationOptions();
        _installations = new CollectorInstallationLifecycle(
            packages,
            installations,
            runtime,
            host.CreateSubject);
    }

    public Task Initialized => _initialized.Task;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
                throw new InvalidOperationException("Collector Marketplace Runtime is stopping.");
            _restoration ??= RestoreAsync(_shutdown.Token);
        }
    }

    public IReadOnlyList<CollectorMarketplaceRuntimeItem> InstalledSnapshot() =>
        _installations.InspectInstalled()
            .Select(inspection => Snapshot(FallbackCatalogItem(inspection), inspection))
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();

    public async ValueTask<IReadOnlyList<CollectorMarketplaceRuntimeItem>> BrowseAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkedOperation(cancellationToken);
        var operationToken = operation.Token;
        await Initialized.WaitAsync(operationToken);
        await _operations.WaitAsync(operationToken);
        try
        {
            ThrowIfStopping();
            IReadOnlyList<CollectorCatalogItem>? catalog = null;
            Exception? catalogFailure = null;
            try { catalog = await _packages.BrowseAsync(operationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                catalogFailure = exception;
            }

            var installed = _installations.InspectInstalled().ToDictionary(
                item => item.Instance.PackageId,
                StringComparer.Ordinal);
            if (catalog is null && installed.Count == 0)
                throw catalogFailure!;
            catalog ??= [];

            var result = catalog.Select(item => Snapshot(
                item,
                installed.GetValueOrDefault(item.PackageId))).ToList();
            foreach (var item in installed.Values.Where(item =>
                         catalog.All(candidate => candidate.PackageId != item.Instance.PackageId)))
            {
                result.Add(Snapshot(FallbackCatalogItem(item), item));
            }
            if (catalogFailure is not null)
            {
                var failure = new CollectorRuntimeFailure("catalog_unavailable", catalogFailure.Message);
                result = result.Select(item => item with { CatalogFailure = failure }).ToList();
            }
            return result.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask<CollectorMarketplaceRuntimeItem> InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkedOperation(cancellationToken);
        var operationToken = operation.Token;
        await Initialized.WaitAsync(operationToken);
        await _operations.WaitAsync(operationToken);
        try
        {
            ThrowIfStopping();
            var installed = await _installations.InstallAsync(packageId, operationToken);
            await AttachAsync(installed, operationToken);
            StartSelectedDriver(installed);
            return Snapshot(CatalogItem(installed), Inspection(installed));
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask<CollectorMarketplaceRuntimeItem> RetryAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkedOperation(cancellationToken);
        var operationToken = operation.Token;
        await Initialized.WaitAsync(operationToken);
        await _operations.WaitAsync(operationToken);
        try
        {
            ThrowIfStopping();
            var inspection = _installations.InspectInstalled()
                .SingleOrDefault(item => item.Instance.PackageId == packageId)
                ?? throw new KeyNotFoundException($"Collector Package '{packageId}' is not installed.");
            if (!inspection.IsValid)
                throw new PackageValidationException(
                    inspection.Failure ?? "Collector Package Installation is unavailable.");
            var installed = new InstalledCollectorPackage(inspection.Installation!, inspection.Instance);
            await AttachAsync(installed, operationToken);
            // The caller token is honored before this point. Once replacement begins, finish
            // stopping the old owner so cancellation cannot orphan it outside the shutdown fence.
            await StopManagedAsync(inspection.Instance.CollectorInstanceId, CancellationToken.None);
            ClearHostFailure(inspection.Instance.CollectorInstanceId);
            StartSelectedDriver(installed);
            return Snapshot(CatalogItem(installed), inspection);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask UninstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkedOperation(cancellationToken);
        var operationToken = operation.Token;
        await Initialized.WaitAsync(operationToken);
        await _operations.WaitAsync(operationToken);
        try
        {
            ThrowIfStopping();
            var inspection = _installations.InspectInstalled()
                .SingleOrDefault(item => item.Instance.PackageId == packageId);
            if (inspection is null)
                return;
            // Cancellation is honored before the commit begins. Once begun, finish the ordered
            // teardown so caller cancellation or Host shutdown cannot publish a half-uninstall.
            await StopManagedAsync(inspection.Instance.CollectorInstanceId, CancellationToken.None);
            await _installations.UninstallAsync(
                packageId,
                DetachAsync,
                CancellationToken.None);
            ClearHostFailure(inspection.Instance.CollectorInstanceId);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask SubmitAuthorizationAsync(
        Guid collectorInstanceId,
        Guid interactionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        using var operation = LinkedOperation(cancellationToken);
        var operationToken = operation.Token;
        await Initialized.WaitAsync(operationToken);
        await _operations.WaitAsync(operationToken);
        try
        {
            ThrowIfStopping();
            if (_runtime.ListInstances().All(item => item.CollectorInstanceId != collectorInstanceId))
                throw new KeyNotFoundException(
                    $"Collector Instance '{collectorInstanceId:D}' is not managed by this Host.");
            var state = _runtime.GetManagedProcessRuntimeState(collectorInstanceId);
            if (state.AuthorizationChallenge?.InteractionId != interactionId)
                throw new InvalidOperationException("Authorization interaction is no longer current.");
            await _runtime.SubmitManagedProcessAuthorizationAsync(
                collectorInstanceId,
                interactionId,
                values,
                operationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? completion = null;
        Task stopTask;
        lock (_lifecycleGate)
        {
            Volatile.Write(ref _stopping, 1);
            if (_stopTask is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
            }
            stopTask = _stopTask;
        }
        // Publish the shared result before cancellation invokes arbitrary operation callbacks.
        if (completion is not null) _ = CompleteStopAsync(completion);
        return new(stopTask.WaitAsync(cancellationToken));
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { _shutdown.Cancel(); }
        catch (Exception exception) { failures.Add(exception); }
        // Even a throwing cancellation callback must not bypass the operation fence.
        try
        {
            if (_restoration is not null) await _restoration;
            else _initialized.TrySetResult();
            await _operations.WaitAsync();
            try
            {
                foreach (var instanceId in _managed.Keys.ToArray())
                {
                    try { await StopManagedAsync(instanceId, CancellationToken.None); }
                    catch (Exception exception) { failures.Add(exception); }
                }
            }
            finally { _operations.Release(); }
        }
        catch (Exception exception) { failures.Add(exception); }
        Complete(completion, failures);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            Volatile.Write(ref _disposed, 1);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposeAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try { await StopAsync(CancellationToken.None); }
        catch (Exception exception) { failures.Add(exception); }
        foreach (var entry in _managed.Values) Release(entry);
        _managed.Clear();
        Release(_installations);
        Release(_operations);
        Release(_shutdown);
        Complete(completion, failures);

        void Release(IDisposable resource)
        {
            try { resource.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    private static void Complete(TaskCompletionSource completion, List<Exception> failures)
    {
        if (failures.Count == 0) completion.SetResult();
        else completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _operations.WaitAsync(cancellationToken);
            try
            {
                foreach (var inspection in _installations.InspectInstalled())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!inspection.IsValid)
                        continue;
                    var installed = new InstalledCollectorPackage(
                        inspection.Installation!,
                        inspection.Instance);
                    try
                    {
                        await AttachAsync(installed, cancellationToken);
                        StartSelectedDriver(installed);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        SetHostFailure(inspection.Instance.CollectorInstanceId, Failure(exception));
                    }
                }
            }
            finally
            {
                _operations.Release();
            }
            _initialized.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialized.TrySetResult();
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
        }
    }

    private async ValueTask AttachAsync(
        InstalledCollectorPackage installed,
        CancellationToken cancellationToken)
    {
        if (_attached.Contains(installed.Instance.CollectorInstanceId))
            return;
        try
        {
            await _host.AttachAsync(installed.Instance, installed.Presentation, cancellationToken);
            _attached.Add(installed.Instance.CollectorInstanceId);
            ClearHostFailure(installed.Instance.CollectorInstanceId);
        }
        catch (Exception exception)
        {
            SetHostFailure(installed.Instance.CollectorInstanceId, Failure(exception));
            throw;
        }
    }

    private async ValueTask DetachAsync(
        CollectorInstance instance,
        CancellationToken cancellationToken)
    {
        // Host adapters are idempotent. Remove the local attachment fact before calling out so a
        // failed/partial detach can be repaired by Retry, which will attach from durable state.
        _attached.Remove(instance.CollectorInstanceId);
        await _host.DetachAsync(instance, cancellationToken);
    }

    private void StartSelectedDriver(InstalledCollectorPackage installed)
    {
        var driver = DriverForCurrentTarget(installed.Package);
        if (driver == "externalHost")
            return;
        if (driver != "managedProcess")
            throw new PackageValidationException($"Unsupported Collector driver '{driver}'.");
        var id = installed.Instance.CollectorInstanceId;
        if (_managed.TryGetValue(id, out var current))
        {
            if (current.Task is { IsCompleted: false })
                return;
            current.Dispose();
        }
        var entry = new ManagedEntry();
        _managed[id] = entry;
        entry.Task = ActivateManagedAsync(installed, entry);
    }

    private async Task ActivateManagedAsync(InstalledCollectorPackage installed, ManagedEntry entry)
    {
        try
        {
            var activation = await _runtime.ActivateManagedProcessAsync(
                installed.Instance.CollectorInstanceId,
                installed.Package,
                _managedProcessOptions,
                entry.Cancellation.Token);
            entry.Activation = activation;
            entry.Started.TrySetResult();
            await activation.Completion;
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Started.TrySetResult();
        }
        catch (Exception exception)
        {
            SetHostFailure(installed.Instance.CollectorInstanceId, Failure(exception));
            entry.Started.TrySetResult();
        }
        finally
        {
            entry.Started.TrySetResult();
        }
    }

    private async ValueTask StopManagedAsync(Guid collectorInstanceId, CancellationToken cancellationToken)
    {
        if (!_managed.Remove(collectorInstanceId, out var entry))
            return;
        entry.Cancel();
        await entry.Started.Task.WaitAsync(cancellationToken);
        if (entry.Activation is not null)
            await entry.Activation.StopAsync(cancellationToken);
        if (entry.Task is not null)
        {
            try { await entry.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch when (!cancellationToken.IsCancellationRequested) { }
        }
        entry.Dispose();
    }

    private CollectorMarketplaceRuntimeItem Snapshot(
        CollectorCatalogItem catalog,
        CollectorInstallationInspection? inspection)
    {
        if (inspection is null)
            return new CollectorMarketplaceRuntimeItem(
                catalog.PackageId,
                catalog.DisplayName,
                catalog.Summary,
                catalog.Version,
                null,
                null,
                CollectorMarketplaceRuntimePhase.NotInstalled);
        if (!inspection.IsValid)
            return Failed(catalog, inspection.Instance, inspection.Failure);
        if (TryGetHostFailure(inspection.Instance.CollectorInstanceId, out var hostFailure))
            return Failed(catalog, inspection.Instance, hostFailure.Message, hostFailure);

        var installed = new InstalledCollectorPackage(inspection.Installation!, inspection.Instance);
        try
        {
            return DriverForCurrentTarget(installed.Package) switch
            {
                "externalHost" => ExternalSnapshot(catalog, installed),
                "managedProcess" => ManagedSnapshot(catalog, installed),
                var driver => Failed(catalog, installed.Instance, $"Unsupported Collector driver '{driver}'.")
            };
        }
        catch (Exception exception) when (exception is
                   CollectorRuntimeStateException or PackageValidationException or KeyNotFoundException)
        {
            return Failed(catalog, installed.Instance, exception.Message, Failure(exception));
        }
    }

    private CollectorMarketplaceRuntimeItem ExternalSnapshot(
        CollectorCatalogItem catalog,
        InstalledCollectorPackage installed)
    {
        var status = _runtime.DescribeExternalHostInstance(installed.Instance.CollectorInstanceId);
        var phase = status.Failure is not null
            ? CollectorMarketplaceRuntimePhase.ConnectionConflict
            : status.State switch
            {
                CollectorInstanceExternalHostState.Connected => CollectorMarketplaceRuntimePhase.Running,
                CollectorInstanceExternalHostState.Negotiating => CollectorMarketplaceRuntimePhase.Starting,
                _ => CollectorMarketplaceRuntimePhase.WaitingForExternalHost
            };
        return Installed(
            catalog,
            installed.Instance,
            phase,
            status.ConnectedExternalHosts,
            status.Failure);
    }

    private CollectorMarketplaceRuntimeItem ManagedSnapshot(
        CollectorCatalogItem catalog,
        InstalledCollectorPackage installed)
    {
        CollectorRuntimeSnapshot state;
        try { state = _runtime.GetManagedProcessRuntimeState(installed.Instance.CollectorInstanceId); }
        catch (KeyNotFoundException)
        {
            return Installed(catalog, installed.Instance, CollectorMarketplaceRuntimePhase.Starting);
        }
        var phase = state.Phase switch
        {
            CollectorRuntimePhase.Ready => CollectorMarketplaceRuntimePhase.Running,
            CollectorRuntimePhase.WaitingForAuthorization => CollectorMarketplaceRuntimePhase.WaitingForAuthorization,
            CollectorRuntimePhase.Failed => CollectorMarketplaceRuntimePhase.Failed,
            CollectorRuntimePhase.Stopped => CollectorMarketplaceRuntimePhase.Failed,
            _ => CollectorMarketplaceRuntimePhase.Starting
        };
        var failure = phase == CollectorMarketplaceRuntimePhase.Failed && state.Failure is null
            ? new CollectorRuntimeFailure("collector_stopped", "Collector process stopped.")
            : state.Failure;
        return Installed(
            catalog,
            installed.Instance,
            phase,
            null,
            failure,
            state.AuthorizationChallenge);
    }

    private string DriverForCurrentTarget(LocalCollectorPackage package)
    {
        var candidates = package.Manifest.Artifacts.Where(artifact =>
            artifact.OperatingSystems.Contains(_target.OperatingSystem, StringComparer.Ordinal) &&
            artifact.Architectures.Contains(_target.Architecture, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1)
            throw new PackageValidationException(
                $"Collector Package must have exactly one Artifact for {_target.OperatingSystem}/{_target.Architecture}; found {candidates.Length}.");
        return candidates[0].Driver;
    }

    private CollectorCatalogItem CatalogItem(InstalledCollectorPackage installed) => new(
        installed.Instance.PackageId,
        installed.Presentation.DisplayName,
        installed.Presentation.Summary,
        installed.Instance.PackageVersion,
        _target);

    private CollectorCatalogItem FallbackCatalogItem(CollectorInstallationInspection inspection) => new(
        inspection.Instance.PackageId,
        inspection.Installation?.Package.Manifest.Presentation?.DisplayName ?? inspection.Instance.PackageId,
        inspection.Installation?.Package.Manifest.Presentation?.Summary ?? "Installed Collector Package",
        string.Empty,
        _target);

    private static CollectorInstallationInspection Inspection(InstalledCollectorPackage installed) =>
        new(installed.Instance, installed.Installation, null);

    private static CollectorMarketplaceRuntimeItem Installed(
        CollectorCatalogItem catalog,
        CollectorInstance instance,
        CollectorMarketplaceRuntimePhase phase,
        int? connections = null,
        CollectorRuntimeFailure? failure = null,
        CollectorAuthorizationChallenge? authorization = null) => new(
            catalog.PackageId,
            catalog.DisplayName,
            catalog.Summary,
            string.IsNullOrEmpty(catalog.Version) ? null : catalog.Version,
            instance.PackageVersion,
            instance.CollectorInstanceId,
            phase,
            connections,
            failure,
            authorization);

    private static CollectorMarketplaceRuntimeItem Failed(
        CollectorCatalogItem catalog,
        CollectorInstance instance,
        string? detail,
        CollectorRuntimeFailure? failure = null) => Installed(
            catalog,
            instance,
            CollectorMarketplaceRuntimePhase.Failed,
            failure: failure ?? new CollectorRuntimeFailure("collector_unavailable", detail ?? "Collector is unavailable."));

    private static CollectorRuntimeFailure Failure(Exception exception) => exception switch
    {
        CollectorActivationException activation =>
            new CollectorRuntimeFailure(activation.Error.Code, activation.Error.Message),
        PackageValidationException validation =>
            new CollectorRuntimeFailure("package_invalid", validation.Message),
        IOException io => new CollectorRuntimeFailure("package_io_failed", io.Message),
        _ => new CollectorRuntimeFailure("host_adapter_failed", exception.Message)
    };

    private void SetHostFailure(Guid collectorInstanceId, CollectorRuntimeFailure failure)
    {
        lock (_stateGate)
            _hostFailures[collectorInstanceId] = failure;
    }

    private void ClearHostFailure(Guid collectorInstanceId)
    {
        lock (_stateGate)
            _hostFailures.Remove(collectorInstanceId);
    }

    private bool TryGetHostFailure(Guid collectorInstanceId, out CollectorRuntimeFailure failure)
    {
        lock (_stateGate)
            return _hostFailures.TryGetValue(collectorInstanceId, out failure!);
    }

    private void ThrowIfStopping()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _stopping) != 0)
            throw new InvalidOperationException("Collector Marketplace Runtime is stopping.");
    }

    private CancellationTokenSource LinkedOperation(CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
    }

    private sealed class ManagedEntry : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManagedProcessCollectorActivation? Activation { get; set; }
        public Task? Task { get; set; }
        public void Cancel() => Cancellation.Cancel();
        public void Dispose() => Cancellation.Dispose();
    }
}
