using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Headless;

public sealed record HeadlessCollectorStatusResponse(
    string PackageId,
    string DisplayName,
    string Summary,
    string? LatestVersion,
    bool IsInstalled,
    string? InstalledVersion,
    Guid? CollectorInstanceId,
    string Phase,
    CollectorAuthorizationChallenge? Authorization,
    HeadlessCurrentSubjectActivity? CurrentActivity,
    string? StatusDetail = null);

/// <summary>
/// Generic Hub Host adapter. CollectorRuntime is the only durable Instance authority; this class
/// restores exact installed Packages, exposes Catalog-driven lifecycle operations, and owns only
/// transient activation/pipeline handles.
/// </summary>
public sealed class HeadlessFleetManager(HeadlessFleetOptions options) : BackgroundService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _ownedDisposables = [];
    private CollectorRuntime? _runtime;
    private HeadlessInstancePipelines? _pipelines;
    private CollectorPackageInstallations? _installations;
    private ICollectorPackageMarketplace? _marketplace;

    internal Task Initialized => _initialized.Task;
    internal Func<CollectorPackageInstallations, ICollectorPackageMarketplace>? MarketplaceFactory { get; init; }

    internal IReadOnlyList<HeadlessCollectorStatusResponse> InstalledSnapshot()
    {
        lock (_gate)
            return _entries.Values
                .Select(entry => ToResponse(
                    entry,
                    entry.Instance.PackageId,
                    entry.DisplayName,
                    entry.Summary,
                    latestVersion: null))
                .OrderBy(entry => entry.DisplayName, StringComparer.Ordinal)
                .ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Initialize();
            Entry[] entries;
            lock (_gate) entries = _entries.Values.Where(entry => entry.Package is not null).ToArray();
            foreach (var entry in entries)
                StartActivation(entry);
            _initialized.TrySetResult();
        }
        catch (Exception exception)
        {
            _initialized.TrySetException(exception);
            throw;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdown.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.UploadIntervalSeconds), linked.Token);
                await DrainPipelinesAsync();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async ValueTask<IReadOnlyList<HeadlessCollectorStatusResponse>> BrowseAsync(
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var catalog = await _marketplace!.BrowseAsync(cancellationToken);
        Dictionary<string, Entry> entries;
        lock (_gate) entries = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
        var result = catalog.Select(item => ToResponse(
                entries.GetValueOrDefault(item.PackageId),
                item.PackageId,
                item.DisplayName,
                item.Summary,
                item.Version))
            .ToList();
        foreach (var entry in entries.Values.Where(entry =>
                     catalog.All(item => item.PackageId != entry.Instance.PackageId)))
            result.Add(ToResponse(
                entry,
                entry.Instance.PackageId,
                entry.DisplayName,
                entry.Summary,
                latestVersion: null));
        return result.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
    }

    public async ValueTask<HeadlessCollectorStatusResponse> InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(packageId, out var existing))
                    return ToResponse(
                        existing,
                        packageId,
                        existing.DisplayName,
                        existing.Summary,
                        existing.Instance.PackageVersion);
            }

            var installation = await _marketplace!.InstallLatestAsync(packageId, cancellationToken);
            var package = installation.Package;
            var presentation = package.Manifest.Presentation
                               ?? throw new PackageValidationException(
                                   "Marketplace Package does not declare presentation.");
            var blueprint = package.Manifest.DefaultInstance
                            ?? throw new PackageValidationException(
                                "Marketplace Package does not declare defaultInstance.");
            var subject = new SubjectReference(Guid.CreateVersion7(), ParseSubjectKind(blueprint.SubjectKind));
            CollectorInstance? instance = null;
            try
            {
                instance = _runtime!.CreateInstance(
                    package,
                    subject,
                    new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()),
                    "default");
                _pipelines!.Add(instance.CollectorInstanceId, instance.Subject, presentation.DisplayName);
                var entry = new Entry(instance, package, presentation.DisplayName, presentation.Summary);
                lock (_gate) _entries.Add(packageId, entry);
                StartActivation(entry);
                return ToResponse(
                    entry,
                    packageId,
                    presentation.DisplayName,
                    presentation.Summary,
                    package.Manifest.Version);
            }
            catch
            {
                if (instance is not null)
                {
                    await _runtime!.RemoveInstanceAsync(instance.CollectorInstanceId, CancellationToken.None);
                    await _pipelines!.RemoveAsync(instance.CollectorInstanceId);
                }
                if (_runtime!.ListInstances().All(candidate =>
                        candidate.PackageId != installation.Reference.PackageId ||
                        candidate.PackageVersion != installation.Reference.Version ||
                        candidate.PackageContentHash != installation.Reference.PackageContentHash))
                    _installations!.Uninstall(installation.Reference);
                throw;
            }
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
        await _initialized.Task.WaitAsync(cancellationToken);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(packageId, out entry!))
                    return;
            }

            await StopEntryActivationAsync(entry, cancellationToken);
            await _pipelines!.RemoveAsync(entry.Instance.CollectorInstanceId);
            if (_runtime!.ListInstances().Any(instance =>
                    instance.CollectorInstanceId == entry.Instance.CollectorInstanceId))
                await _runtime.RemoveInstanceAsync(entry.Instance.CollectorInstanceId, cancellationToken);
            var reference = new CollectorPackageReference(
                entry.Instance.PackageId,
                entry.Instance.PackageVersion,
                entry.Instance.PackageContentHash);
            if (_runtime.ListInstances().All(candidate =>
                    candidate.PackageId != reference.PackageId ||
                    candidate.PackageVersion != reference.Version ||
                    candidate.PackageContentHash != reference.PackageContentHash))
                _installations!.Uninstall(reference);
            lock (_gate) _entries.Remove(packageId);
            entry.Dispose();
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask<HeadlessCollectorStatusResponse> RetryActivationAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            Entry entry;
            lock (_gate)
            {
                entry = _entries.GetValueOrDefault(packageId)
                        ?? throw new KeyNotFoundException($"Collector Package '{packageId}' is not installed.");
                if (entry.Package is null)
                    throw new InvalidOperationException("The exact Collector Package Installation is unavailable.");
                if (entry.ActivationTask is { IsCompleted: false })
                    throw new InvalidOperationException("Collector activation is already in progress.");
                entry.Activation = null;
                entry.ActivationTask = null;
                StartActivation(entry);
            }
            return ToResponse(
                entry,
                entry.Instance.PackageId,
                entry.DisplayName,
                entry.Summary,
                entry.Instance.PackageVersion);
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
        CancellationToken cancellationToken)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var runtime = _runtime!;
        if (runtime.ListInstances().All(instance => instance.CollectorInstanceId != collectorInstanceId))
            throw new KeyNotFoundException(
                $"Collector Instance '{collectorInstanceId:D}' is not managed by this Hub.");
        var state = runtime.GetManagedProcessRuntimeState(collectorInstanceId);
        if (state.AuthorizationChallenge?.InteractionId != interactionId)
            throw new InvalidOperationException("Authorization interaction is no longer current.");
        await runtime.SubmitManagedProcessAuthorizationAsync(
            collectorInstanceId, interactionId, values, cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        foreach (var entry in entries)
            await StopEntryActivationAsync(entry, cancellationToken);
        await DrainPipelinesAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _shutdown.Dispose();
        _operations.Dispose();
        lock (_gate)
        {
            foreach (var entry in _entries.Values) entry.Dispose();
            _entries.Clear();
        }
        _runtime?.Dispose();
        _pipelines?.Dispose();
        foreach (var disposable in _ownedDisposables) disposable.Dispose();
        base.Dispose();
    }

    private void Initialize()
    {
        options.Validate();
        Directory.CreateDirectory(options.DataDirectory);
        var configuration = new FleetHubConfiguration(options);
        var authHttp = new HttpClient();
        _ownedDisposables.Add(authHttp);
        var tokens = new TokenManager(configuration, new AuthServiceClient(authHttp));
        var pipelines = new HeadlessInstancePipelines(
            options.DataDirectory,
            new HeadlessAnalyticsSegmentUploadAdapter(tokens));
        _pipelines = pipelines;
        _runtime = CollectorRuntime.Open(
            Path.Combine(options.DataDirectory, "collector-runtime.json"),
            pipelines,
            secretStore: new EncryptedFileCollectorSecretStore(
                Path.Combine(options.DataDirectory, "collector-secrets")));
        _installations = new CollectorPackageInstallations(
            Path.Combine(options.DataDirectory, "collector-packages"));
        var packageHttp = new HttpClient();
        _ownedDisposables.Add(packageHttp);
        _marketplace = MarketplaceFactory?.Invoke(_installations) ??
            new CollectorPackageMarketplace(
                packageHttp,
                new Uri(options.CollectorRegistryUrl, UriKind.Absolute),
                new CollectorMarketplaceTarget("linux", RuntimeArchitecture()),
                _installations);

        foreach (var instance in _runtime.ListInstances())
        {
            if (_entries.ContainsKey(instance.PackageId))
                throw new CollectorRuntimeStateException(
                    $"Headless Hub supports one installed Instance per Package; '{instance.PackageId}' has more than one.");
            var reference = new CollectorPackageReference(
                instance.PackageId, instance.PackageVersion, instance.PackageContentHash);
            try
            {
                var package = _installations.Open(reference).Package;
                var displayName = package.Manifest.Presentation?.DisplayName ?? instance.PackageId;
                var summary = package.Manifest.Presentation?.Summary ?? "Installed Collector Package";
                pipelines.Add(instance.CollectorInstanceId, instance.Subject, displayName);
                _entries.Add(instance.PackageId, new Entry(instance, package, displayName, summary));
            }
            catch (Exception exception) when (exception is PackageValidationException or IOException)
            {
                _entries.Add(instance.PackageId, new Entry(
                    instance,
                    package: null,
                    instance.PackageId,
                    "Installed Collector Package",
                    exception.Message));
            }
        }
    }

    private void StartActivation(Entry entry)
    {
        lock (_gate)
        {
            if (entry.ActivationTask is { IsCompleted: false })
                return;
            var startupCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            entry.StartupCompletion = startupCompletion;
            entry.ActivationTask = ActivateAsync(entry, startupCompletion);
        }
    }

    private async Task ActivateAsync(Entry entry, TaskCompletionSource startupCompletion)
    {
        try
        {
            var activation = await _runtime!.ActivateManagedProcessAsync(
                entry.Instance.CollectorInstanceId,
                entry.Package!,
                new ManagedProcessActivationOptions
                {
                    StartupTimeout = TimeSpan.FromSeconds(options.CollectorStartupTimeoutSeconds),
                    DrainGracePeriod = TimeSpan.FromSeconds(options.CollectorDrainGraceSeconds)
                },
                entry.ActivationCancellation.Token);
            lock (_gate) entry.Activation = activation;
            startupCompletion.TrySetResult();
            await activation.Completion;
        }
        catch (OperationCanceledException) when (entry.ActivationCancellation.IsCancellationRequested) { }
        catch
        {
            // CollectorRuntime owns the structured failure exposed below.
        }
        finally
        {
            startupCompletion.TrySetResult();
        }
    }

    private async Task StopEntryActivationAsync(Entry entry, CancellationToken cancellationToken)
    {
        entry.ActivationCancellation.Cancel();
        Task? startup;
        lock (_gate) startup = entry.StartupCompletion?.Task;
        if (startup is not null)
            await startup.WaitAsync(cancellationToken);

        ManagedProcessCollectorActivation? activation;
        Task? activationTask;
        lock (_gate)
        {
            activation = entry.Activation;
            activationTask = entry.ActivationTask;
        }
        if (activation is not null)
            await activation.StopAsync(cancellationToken);
        if (activationTask is null)
            return;
        try
        {
            await activationTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch when (!cancellationToken.IsCancellationRequested) { }
    }

    private HeadlessCollectorStatusResponse ToResponse(
        Entry? entry,
        string packageId,
        string displayName,
        string summary,
        string? latestVersion)
    {
        if (entry is null)
            return new HeadlessCollectorStatusResponse(
                packageId, displayName, summary, latestVersion, false, null, null, "NotInstalled", null, null);
        CollectorRuntimeSnapshot? runtime = null;
        try { runtime = _runtime!.GetManagedProcessRuntimeState(entry.Instance.CollectorInstanceId); }
        catch (KeyNotFoundException) { }
        return new HeadlessCollectorStatusResponse(
            packageId,
            displayName,
            summary,
            latestVersion,
            true,
            entry.Instance.PackageVersion,
            entry.Instance.CollectorInstanceId,
            entry.Failure is null ? DescribePhase(entry, runtime) : "Failed",
            runtime?.AuthorizationChallenge,
            entry.Failure is null ? _pipelines?.CurrentActivity(entry.Instance.CollectorInstanceId) : null,
            entry.Failure ?? DescribeFailure(runtime?.Failure));
    }

    /// <summary>
    /// Phase 只报真实观察到的状态。没有 ManagedProcess runtime 不等于「正在启动」：ExternalHost 交付的
    /// Collector 由对端自己连上来，宿主此时的真实状态是「已就绪，等对端接入」。以前统一兜底成 Starting
    /// 会让「永远不会有人来连」和「马上就起来」在管理面上长得一样。
    /// </summary>
    private string DescribePhase(Entry entry, CollectorRuntimeSnapshot? runtime)
    {
        if (runtime is not null)
            return runtime.Phase.ToString();
        var externalHost = _runtime!.DescribeExternalHostInstance(entry.Instance.CollectorInstanceId);
        return externalHost.State switch
        {
            CollectorInstanceExternalHostState.Connected => nameof(CollectorRuntimePhase.Ready),
            CollectorInstanceExternalHostState.Negotiating => nameof(CollectorRuntimePhase.Negotiating),
            _ => nameof(CollectorInstanceExternalHostState.WaitingForExternalHost)
        };
    }

    private static string? DescribeFailure(CollectorRuntimeFailure? failure) =>
        failure is null
            ? null
            : failure.ProcessExitCode is { } exitCode
                ? $"{failure.Code}: {failure.Message} (exit code {exitCode})"
                : $"{failure.Code}: {failure.Message}";

    private static SubjectKind ParseSubjectKind(string value) => value switch
    {
        "machine" => SubjectKind.Machine,
        "account" => SubjectKind.Account,
        "person" => SubjectKind.Person,
        _ => throw new PackageValidationException($"Unknown default Instance subject kind '{value}'.")
    };

    private static string RuntimeArchitecture() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("Headless Collector marketplace supports x64 and arm64.")
    };

    private async Task DrainPipelinesAsync()
    {
        if (_pipelines is not null)
            await _pipelines.DrainAllAsync();
    }

    private sealed class Entry(
        CollectorInstance instance,
        LocalCollectorPackage? package,
        string displayName,
        string summary,
        string? failure = null) : IDisposable
    {
        public CollectorInstance Instance { get; } = instance;
        public LocalCollectorPackage? Package { get; } = package;
        public string DisplayName { get; } = displayName;
        public string Summary { get; } = summary;
        public string? Failure { get; } = failure;
        public CancellationTokenSource ActivationCancellation { get; } = new();
        public TaskCompletionSource? StartupCompletion { get; set; }
        public Task? ActivationTask { get; set; }
        public ManagedProcessCollectorActivation? Activation { get; set; }
        public void Dispose() => ActivationCancellation.Dispose();
    }

    private sealed class FleetHubConfiguration(HeadlessFleetOptions options) : IHubConfiguration
    {
        public HubRuntimeSettings Current { get; } = new(
            options.ApiKey,
            TimeSpan.FromSeconds(options.UploadIntervalSeconds),
            0);
        public event Action? Changed { add { } remove { } }
    }
}
