using System.Runtime.InteropServices;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Collection.Hub.Hosting;

public sealed record CollectorRuntimeStorageOptions(string DataDirectory);

public sealed record CollectorMarketplaceHostOptions(Uri RegistryRoot, CollectorMarketplaceTarget Target)
{
    public static readonly Uri OfficialRegistryRoot =
        new("https://heartbeat.shenxianovo.com/collector-registry/v1/");
}

public static class CollectorHostServiceCollectionExtensions
{
    public static IServiceCollection AddCollectorRuntime(
        this IServiceCollection services, CollectorRuntimeStorageOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        services.TryAddSingleton(options);
        services.TryAddSingleton(provider =>
        {
            var storage = provider.GetRequiredService<CollectorRuntimeStorageOptions>();
            return CollectorRuntime.Open(
                Path.Combine(storage.DataDirectory, "collector-runtime.json"),
                provider.GetRequiredService<ISegmentSink>(),
                inputEventSink: provider.GetService<IInputEventFactSink>(),
                secretStore: new EncryptedFileCollectorSecretStore(
                    Path.Combine(storage.DataDirectory, "collector-secrets")));
        });
        return services;
    }

    public static IServiceCollection AddMachineCollectorMarketplace(
        this IServiceCollection services, string operatingSystem, Uri? registryRoot = null)
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Collector Marketplace requires x64 or arm64.")
        };
        services.TryAddSingleton<ICollectorMarketplaceHostAdapter, MachineCollectorMarketplaceHostAdapter>();
        return services.AddCollectorMarketplace(new CollectorMarketplaceHostOptions(
            registryRoot ?? CollectorMarketplaceHostOptions.OfficialRegistryRoot,
            new CollectorMarketplaceTarget(operatingSystem, architecture)));
    }

    public static IServiceCollection AddCollectorMarketplace(
        this IServiceCollection services, CollectorMarketplaceHostOptions options)
    {
        services.TryAddSingleton(options);
        services.AddHttpClient(CollectorMarketplaceHost.HttpClientName);
        services.TryAddSingleton<CollectorMarketplaceHost>();
        // The facade is deliberately not disposable: DI must not capture its owner's lifetime twice.
        services.TryAddSingleton<ICollectorMarketplace>(provider =>
            provider.GetRequiredService<CollectorMarketplaceHost>().Management);
        services.AddHostedService<CollectorMarketplaceWorker>();
        return services;
    }

    private sealed class MachineCollectorMarketplaceHostAdapter(IDeviceIdentity identity)
        : ICollectorMarketplaceHostAdapter
    {
        public SubjectReference CreateSubject(SubjectKind kind)
        {
            if (kind != SubjectKind.Machine)
                throw new PackageValidationException("Machine Host Marketplace requires a Machine Subject.");
            if (!Guid.TryParse(identity.HardwareId, out var machineId) || machineId == Guid.Empty)
                throw new InvalidOperationException("The machine identity must be a non-empty UUID before installation.");
            return new SubjectReference(machineId, kind);
        }
        public ValueTask AttachAsync(CollectorInstance instance, CollectorPackagePresentation presentation,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DetachAsync(CollectorInstance instance, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
