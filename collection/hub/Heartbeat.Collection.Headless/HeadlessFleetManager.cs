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
/// Headless Host shell around the shared Collector Marketplace Runtime. The shared runtime owns
/// Package, Instance, Driver, Activation, recovery and status transitions; this class only supplies
/// per-Instance analytics pipelines and exposes the management HTTP read model.
/// </summary>
public sealed class HeadlessFleetManager(HeadlessFleetOptions options) : BackgroundService
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _ownedDisposables = [];
    private CollectorRuntime? _collectorRuntime;
    private HeadlessInstancePipelines? _pipelines;
    private ICollectorMarketplaceRuntime? _marketplaceRuntime;

    internal Task Initialized => _initialized.Task;
    internal Func<CollectorPackageInstallations, ICollectorPackageMarketplace>? MarketplaceFactory { get; init; }

    internal IReadOnlyList<HeadlessCollectorStatusResponse> InstalledSnapshot() =>
        _marketplaceRuntime?.InstalledSnapshot().Select(ToResponse).ToArray() ?? [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Initialize();
            _marketplaceRuntime!.Start();
            await _marketplaceRuntime.Initialized.WaitAsync(stoppingToken);
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
        return (await RequiredMarketplace().BrowseAsync(cancellationToken)).Select(ToResponse).ToArray();
    }

    public async ValueTask<HeadlessCollectorStatusResponse> InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var marketplace = RequiredMarketplace();
        await marketplace.WaitForSuccessAsync(marketplace.Install(packageId), cancellationToken);
        return ToResponse(AssertInstalled(packageId, marketplace));
    }

    public async ValueTask UninstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var marketplace = RequiredMarketplace();
        await marketplace.WaitForSuccessAsync(marketplace.Uninstall(packageId), cancellationToken);
    }

    public async ValueTask<HeadlessCollectorStatusResponse> RetryActivationAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var marketplace = RequiredMarketplace();
        await marketplace.WaitForSuccessAsync(marketplace.Retry(packageId), cancellationToken);
        return ToResponse(AssertInstalled(packageId, marketplace));
    }

    public async ValueTask SubmitAuthorizationAsync(
        Guid collectorInstanceId,
        Guid interactionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        await _initialized.Task.WaitAsync(cancellationToken);
        var marketplace = RequiredMarketplace();
        await marketplace.WaitForSuccessAsync(
            marketplace.SubmitAuthorization(collectorInstanceId, interactionId, values),
            cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        if (_marketplaceRuntime is not null)
            await _marketplaceRuntime.StopAsync(cancellationToken);
        await DrainPipelinesAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _shutdown.Cancel();
        if (_marketplaceRuntime is not null)
            _marketplaceRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _collectorRuntime?.Dispose();
        _pipelines?.Dispose();
        foreach (var disposable in _ownedDisposables)
            disposable.Dispose();
        _shutdown.Dispose();
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

        var collectorRuntime = CollectorRuntime.Open(
            Path.Combine(options.DataDirectory, "collector-runtime.json"),
            pipelines,
            secretStore: new EncryptedFileCollectorSecretStore(
                Path.Combine(options.DataDirectory, "collector-secrets")));
        _collectorRuntime = collectorRuntime;
        var installations = new CollectorPackageInstallations(
            Path.Combine(options.DataDirectory, "collector-packages"));
        var target = new CollectorMarketplaceTarget(RuntimeOperatingSystem(), RuntimeArchitecture());
        var packageHttp = new HttpClient();
        _ownedDisposables.Add(packageHttp);
        var packageMarketplace = MarketplaceFactory?.Invoke(installations) ??
            new CollectorPackageMarketplace(
                packageHttp,
                new Uri(options.CollectorRegistryUrl, UriKind.Absolute),
                target,
                installations);

        _marketplaceRuntime = new CollectorMarketplaceRuntime(
            packageMarketplace,
            installations,
            collectorRuntime,
            target,
            new HeadlessMarketplaceHostAdapter(pipelines),
            new ManagedProcessActivationOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(options.CollectorStartupTimeoutSeconds),
                DrainGracePeriod = TimeSpan.FromSeconds(options.CollectorDrainGraceSeconds)
            });
    }

    private ICollectorMarketplaceRuntime RequiredMarketplace()
    {
        if (_marketplaceRuntime is null)
            throw new InvalidOperationException("Headless Collector Marketplace is not initialized.");
        return _marketplaceRuntime;
    }

    private static CollectorMarketplaceRuntimeItem AssertInstalled(
        string packageId,
        ICollectorMarketplace marketplace) => marketplace.InstalledSnapshot()
            .Single(item => item.PackageId == packageId);

    private HeadlessCollectorStatusResponse ToResponse(CollectorMarketplaceRuntimeItem item)
    {
        var installed = item.InstalledVersion is not null && item.CollectorInstanceId is not null;
        return new HeadlessCollectorStatusResponse(
            item.PackageId,
            item.DisplayName,
            item.Summary,
            item.LatestVersion,
            installed,
            item.InstalledVersion,
            item.CollectorInstanceId,
            item.Phase switch
            {
                CollectorMarketplaceRuntimePhase.Running => nameof(CollectorRuntimePhase.Ready),
                _ => item.Phase.ToString()
            },
            item.Authorization,
            installed && item.Phase is not CollectorMarketplaceRuntimePhase.Failed
                ? CurrentActivity(item.CollectorInstanceId!.Value)
                : null,
            DescribeFailure(item.Failure));
    }

    private HeadlessCurrentSubjectActivity? CurrentActivity(Guid collectorInstanceId)
    {
        try { return _pipelines?.CurrentActivity(collectorInstanceId); }
        catch (KeyNotFoundException) { return null; }
    }

    private static string? DescribeFailure(CollectorRuntimeFailure? failure) =>
        failure is null
            ? null
            : failure.ProcessExitCode is { } exitCode
                ? $"{failure.Code}: {failure.Message} (exit code {exitCode})"
                : $"{failure.Code}: {failure.Message}";

    private static string RuntimeArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                "Headless Collector marketplace supports x64 and arm64.")
        };

    private static string RuntimeOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        throw new PlatformNotSupportedException(
            "Headless Collector marketplace supports Windows, macOS and Linux.");

    private async Task DrainPipelinesAsync()
    {
        if (_pipelines is not null)
            await _pipelines.DrainAllAsync();
    }

    private sealed class HeadlessMarketplaceHostAdapter(HeadlessInstancePipelines pipelines) :
        ICollectorMarketplaceHostAdapter
    {
        public SubjectReference CreateSubject(SubjectKind kind) =>
            new(Guid.CreateVersion7(), kind);

        public ValueTask AttachAsync(
            CollectorInstance instance,
            CollectorPackagePresentation presentation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pipelines.Add(
                instance.CollectorInstanceId,
                instance.Subject,
                presentation.DisplayName);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DetachAsync(
            CollectorInstance instance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pipelines.RemoveAsync(instance.CollectorInstanceId);
        }
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
