using System.Runtime.InteropServices;
using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Segments;

namespace Heartbeat.Collection.Headless;

public static class HeadlessHostServiceCollectionExtensions
{
    public static IServiceCollection AddHeadlessCollectors(this IServiceCollection services, HeadlessFleetOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.AddHttpClient("HeadlessAuthentication");
        services.AddSingleton(provider => provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("HeadlessAuthentication"));
        services.AddSingleton(provider => new TokenManager(
            new FleetHubConfiguration(options), new AuthServiceClient(provider.GetRequiredService<HttpClient>())));
        // Register the disposable pipeline once. Adapters borrow it through its sink registration.
        services.AddSingleton<ISegmentSink>(provider => new HeadlessInstancePipelines(
            options.DataDirectory,
            new HeadlessAnalyticsSegmentUploadAdapter(provider.GetRequiredService<TokenManager>())));
        services.AddSingleton<ICollectorMarketplaceHostAdapter>(provider =>
            new HeadlessMarketplaceHostAdapter(Pipelines(provider)));
        services.AddSingleton(new CollectorPackageInstallations(Path.Combine(options.DataDirectory, "collector-packages")));
        services.AddCollectorRuntime(new CollectorRuntimeStorageOptions(options.DataDirectory));
        services.AddCollectorMarketplace(new CollectorMarketplaceHostOptions(
            new Uri(options.CollectorRegistryUrl), CurrentTarget())
        {
            ManagedProcessOptions = new ManagedProcessActivationOptions
            {
                StartupTimeout = TimeSpan.FromSeconds(options.CollectorStartupTimeoutSeconds),
                DrainGracePeriod = TimeSpan.FromSeconds(options.CollectorDrainGraceSeconds)
            }
        });
        services.AddSingleton(provider => new HeadlessCollectorReadModel(
            provider.GetRequiredService<ICollectorMarketplace>(), Pipelines(provider)));
        services.AddHostedService(provider => new HeadlessUploadWorker(options, Pipelines(provider)));
        return services;
    }

    private static HeadlessInstancePipelines Pipelines(IServiceProvider provider) =>
        (HeadlessInstancePipelines)provider.GetRequiredService<ISegmentSink>();

    private static CollectorMarketplaceTarget CurrentTarget() => new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" : throw new PlatformNotSupportedException("Unsupported Headless OS."),
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Headless Collector marketplace requires x64 or arm64.")
        });

    private sealed class HeadlessMarketplaceHostAdapter(HeadlessInstancePipelines pipelines) : ICollectorMarketplaceHostAdapter
    {
        public SubjectReference CreateSubject(SubjectKind kind) => new(Guid.CreateVersion7(), kind);

        public ValueTask AttachAsync(CollectorInstance instance, CollectorPackagePresentation presentation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pipelines.Add(instance.CollectorInstanceId, instance.Subject, presentation.DisplayName);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DetachAsync(CollectorInstance instance, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pipelines.RemoveAsync(instance.CollectorInstanceId);
        }
    }

    private sealed class FleetHubConfiguration(HeadlessFleetOptions options) : IHubConfiguration
    {
        public HubRuntimeSettings Current { get; } = new(options.ApiKey,
            TimeSpan.FromSeconds(options.UploadIntervalSeconds), 0);
        public event Action? Changed { add { } remove { } }
    }

    private sealed class HeadlessUploadWorker(HeadlessFleetOptions options, HeadlessInstancePipelines pipelines) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.UploadIntervalSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await pipelines.DrainAllAsync();
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Shared Marketplace StoppingAsync has already stopped producers. Join the periodic
            // upload before draining once more; resource disposal belongs to the Host container.
            await base.StopAsync(cancellationToken);
            await pipelines.DrainAllAsync();
        }
    }
}
