using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Identity;
using Heartbeat.Desktop.Mac.Icons;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Desktop.Mac.Hosting;

public static class MacAgentHostExtensions
{
    public static IServiceCollection AddHeartbeatMacAgent(
        this IServiceCollection services,
        MacAgentPaths? paths = null)
    {
        // 宿主的本机数据全部落在 paths.DataDirectory 下。调用方传入隔离 paths 即可让整棵树离开真实
        // 用户目录，打包产物的启动 smoke 靠这一点做到不读写真实用户数据。
        paths ??= MacAgentPaths.Default;
        services.AddSingleton(paths);
        services.AddHeartbeatHub();

        services.TryAddSingleton<MacConfigManager>();
        services.TryAddSingleton<IMacCommandRunner, MacCommandRunner>();
        services.TryAddSingleton<IMacApplicationLocator, MacApplicationLocator>();
        services.TryAddSingleton<IMacPlatformUuid, IoregPlatformUuid>();
        services.TryAddSingleton(sp => new MacMachineIdentity(sp.GetRequiredService<IMacPlatformUuid>()));
        services.TryAddSingleton<IMacWorkspaceNative, CocoaWorkspaceNative>();
        services.TryAddSingleton<IMacAccessibilityNative, MacAccessibilityNative>();
        services.TryAddSingleton<IMacInputMonitoringNative, MacInputMonitoringNative>();
        services.TryAddSingleton<MacAccessibilityEvents>();
        services.TryAddSingleton<IMacAccessibilityEvents>(sp =>
            sp.GetRequiredService<MacAccessibilityEvents>());
        services.TryAddSingleton<MacApplicationCatalog>();
        services.TryAddSingleton<MacWorkspaceEvents>();
        services.TryAddSingleton<IMacDesktopEvents>(sp => sp.GetRequiredService<MacWorkspaceEvents>());
        services.TryAddSingleton<MacDesktopObservationSource>();
        services.TryAddSingleton<IDesktopObservationSource>(sp =>
            sp.GetRequiredService<MacDesktopObservationSource>());

        services.TryAddSingleton<MacHubConfiguration>(sp =>
            new MacHubConfiguration(sp.GetRequiredService<MacConfigManager>()).Initialize());
        services.TryAddSingleton<IHubConfiguration>(sp => sp.GetRequiredService<MacHubConfiguration>());
        services.TryAddSingleton<ICollectorRegistry>(sp => sp.GetRequiredService<MacHubConfiguration>());

        services.TryAddSingleton<MacDesktopSettings>(sp =>
            new MacDesktopSettings(sp.GetRequiredService<MacConfigManager>()).Initialize());
        services.TryAddSingleton<IDesktopSettings>(sp => sp.GetRequiredService<MacDesktopSettings>());
        services.TryAddSingleton<IInputActivitySignal, InputActivitySignal>();
        services.TryAddSingleton<IDeviceIdentity, MacDeviceIdentity>();
        services.TryAddSingleton<IMacIconTools, MacIconTools>();
        services.TryAddSingleton<MacBundleIconExtractor>();
        services.TryAddSingleton<MacIconUploadService>();
        services.Replace(ServiceDescriptor.Singleton<IHubRuntimeHooks, MacHubRuntimeHooks>());

        services.TryAddSingleton<ICache<ActivitySegmentItem>>(sp =>
            new JsonFileCache<ActivitySegmentItem>(
                Path.Combine(sp.GetRequiredService<MacAgentPaths>().DataDirectory, "segments-cache.json"),
                20_000,
                HeartbeatCacheFormats.SegmentVersion2(),
                HeartbeatCacheFormats.SegmentMigrations()));
        services.TryAddSingleton<ICache<InputEventItem>>(sp =>
            new JsonFileCache<InputEventItem>(
                Path.Combine(sp.GetRequiredService<MacAgentPaths>().DataDirectory, "input-events-cache.json"),
                // Replay the current v2 retry file verbatim; new capacity is owned by InputEventBuffer.
                int.MaxValue,
                HeartbeatCacheFormats.InputEventVersion2(),
                HeartbeatCacheFormats.InputEventMigrations()));

        services.TryAddSingleton(sp => new InputEventBuffer(
            sp.GetRequiredService<IClock>(),
            publisher: sp.GetRequiredService<ISystemInputEventPublisher>(),
            durableProjectionPath: Path.Combine(
                sp.GetRequiredService<MacAgentPaths>().DataDirectory,
                "input-event-facts-buffer.json"),
            statusRegistry: sp.GetRequiredService<UploadStatusRegistry>()));
        services.TryAddSingleton<IUploadSource<InputEventItem>>(sp => sp.GetRequiredService<InputEventBuffer>());
        services.TryAddSingleton<MacInputEventCollector>();
        services.TryAddSingleton<IMacInputMonitoringEvents>(sp =>
            sp.GetRequiredService<MacInputEventCollector>());
        services.Replace(ServiceDescriptor.Singleton<IInputEventRecordingPolicy>(sp =>
            sp.GetRequiredService<MacInputEventCollector>()));
        services.TryAddSingleton(sp =>
        {
            var root = sp.GetRequiredService<MacAgentPaths>().DataDirectory;
            return new UploadStream<ActivitySegmentItem>(
                "段",
                sp.GetRequiredService<IUploadSource<ActivitySegmentItem>>(),
                (batch, ct) => sp.GetRequiredService<HeartbeatApiClient>()
                    .UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct),
                sp.GetRequiredService<ICache<ActivitySegmentItem>>(),
                Heartbeat.Core.SnapshotCompaction.KeepLatest,
                new JsonDeadLetterStore<ActivitySegmentItem>(Path.Combine(root, "segments-dead-letter.json")),
                sp.GetRequiredService<UploadStatusRegistry>(),
                sp.GetRequiredService<ClientCompatibilityStatus>());
        });
        services.TryAddSingleton(sp =>
        {
            var root = sp.GetRequiredService<MacAgentPaths>().DataDirectory;
            return new UploadStream<InputEventItem>(
                "输入事件",
                sp.GetRequiredService<IUploadSource<InputEventItem>>(),
                (batch, ct) => sp.GetRequiredService<HeartbeatApiClient>()
                    .UploadInputEventsAsync(new InputEventUploadRequest { Events = batch }, ct),
                sp.GetRequiredService<ICache<InputEventItem>>(),
                deadLetterStore: new JsonDeadLetterStore<InputEventItem>(Path.Combine(root, "input-events-dead-letter.json")),
                statusRegistry: sp.GetRequiredService<UploadStatusRegistry>(),
                compatibilityStatus: sp.GetRequiredService<ClientCompatibilityStatus>());
        });

        // AddHeartbeatHub registers workers first. The system Binding stops after input monitoring
        // and before UploadWorker so no Event arrives after drain and its terminal Segment is uploaded.
        // System 是唯一写死进宿主的 BuiltIn Collector；其余 Collector 是独立发布单元，不进入宿主
        // 组合（ADR-049）。
        services.AddSystemCollectorInProcessBinding(new SystemCollectorBindingOptions(
            paths.DataDirectory));
        // 通用 ExternalHost 绑定：一条 loopback 路由服务所有「自己出现的」Collector。宿主不认识
        // 任何具体产品，能不能接入只由本机有没有对应 Installation 决定（ADR-049、ADR-051）。
        services.AddExternalHostCollectorBinding(
            Path.Combine(Path.GetFullPath(paths.DataDirectory), "collector-packages"));

        // Input monitoring starts after the system Activation opens its Event Stream and stops
        // before that Activation drains.
        services.AddHostedService(sp => sp.GetRequiredService<MacInputEventCollector>());
        return services;
    }
}
