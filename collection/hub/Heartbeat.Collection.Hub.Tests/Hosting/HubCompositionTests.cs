using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Hub.Tests.Hosting;

public class HubCompositionTests
{
    private sealed class HeadlessConfiguration : IHubConfiguration, ICollectorRegistry, IDeviceIdentity
    {
        public HubRuntimeSettings Current => new("test-key", TimeSpan.FromMinutes(1), 0);
        public event Action? Changed { add { } remove { } }
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; } =
            new Dictionary<string, CollectorRegistration>();
        public string HardwareId => "headless-subject";
        public string DeviceName => "headless-hub";
        public CollectorRegistration Touch(string source, int? flushPeriodMs = null)
            => new(true, flushPeriodMs, null, null);
        public void Discover(IEnumerable<string> sources) { }
        public void StoreDeclaration(string source, string declarationJson, int version) { }
    }

    private sealed class EmptySource<T> : IUploadSource<T>
    {
        public List<T> Drain() => [];
        public void Reinject(List<T> items) { }
    }

    private sealed class MemoryCache<T> : ICache<T>
    {
        public CacheFileStatus Status => CacheFileStatus.Ready;
        public void Add(List<T> items) { }
        public List<T> Load() => [];
        public void Replace(List<T> items) { }
        public void Clear() { }
    }

    [Fact]
    public void AddHeartbeatHub_ComposesWithoutDesktopOrPlatformAssembly()
    {
        var services = new ServiceCollection();
        services.AddHeartbeatHub();
        var configuration = new HeadlessConfiguration();
        services.AddSingleton<IHubConfiguration>(configuration);
        services.AddSingleton<ICollectorRegistry>(configuration);
        services.AddSingleton<IDeviceIdentity>(configuration);
        services.AddSingleton<IUploadSource<InputEventItem>, EmptySource<InputEventItem>>();
        services.AddSingleton<ICache<ActivitySegmentItem>, MemoryCache<ActivitySegmentItem>>();
        services.AddSingleton<ICache<InputEventItem>, MemoryCache<InputEventItem>>();
        services.AddSingleton(sp => new UploadStream<ActivitySegmentItem>(
            "segments",
            sp.GetRequiredService<IUploadSource<ActivitySegmentItem>>(),
            _ => Task.FromResult(ApiResult.Ok),
            sp.GetRequiredService<ICache<ActivitySegmentItem>>()));
        services.AddSingleton(sp => new UploadStream<InputEventItem>(
            "input events",
            sp.GetRequiredService<IUploadSource<InputEventItem>>(),
            _ => Task.FromResult(ApiResult.Ok),
            sp.GetRequiredService<ICache<InputEventItem>>()));

        using var provider = services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<SegmentIngestService>(),
            provider.GetRequiredService<ISegmentSink>());
        Assert.Same(
            provider.GetRequiredService<SegmentIngestService>(),
            provider.GetRequiredService<ICollectionStatus>());
        Assert.Same(
            provider.GetRequiredService<UploadStatusRegistry>(),
            provider.GetRequiredService<IUploadStatus>());
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, service => service is UploadWorker);
        Assert.Contains(hosted, service => service is StatusUploadWorker);
        Assert.Contains(hosted, service => service is Heartbeat.Collection.Hub.Ingest.ExternalHostProtocolWorker);

        var references = typeof(SegmentIngestService).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToList();
        Assert.DoesNotContain("Heartbeat.Collector.System", references);
        Assert.DoesNotContain("Heartbeat.Desktop.Windows", references);
        Assert.DoesNotContain(references, name => name?.Contains("WPF", StringComparison.OrdinalIgnoreCase) == true);
    }
}
