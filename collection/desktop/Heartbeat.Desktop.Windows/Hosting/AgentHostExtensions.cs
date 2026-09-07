using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Desktop.Windows.Hosting
{
    public static class AgentHostExtensions
    {
        /// <summary>
        /// 注册 Heartbeat Agent 的所有服务和后台任务
        /// </summary>
        public static IServiceCollection AddHeartbeatAgent(
            this IServiceCollection services,
            ConfigManager? configManager = null,
            SingleInstanceGuard? guard = null)
        {
            // 宿主的本机数据全部落在同一个数据目录下。调用方注入 ConfigManager 即可把整棵树重定向到
            // 隔离目录，打包产物的启动 smoke 靠这一点做到不读写真实用户数据。
            var dataDirectory = configManager?.DataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heartbeat");
            // 纯 .NET hub 运行时；无头 host 可独立调用同一入口，不会带入 desktop/UI。
            services.AddHeartbeatHub();
            services.AddCollectorRuntime(new CollectorRuntimeStorageOptions(dataDirectory));
            services.AddMachineCollectorMarketplace("windows");

            // 单实例守卫（由调用方创建并传入，Agent 负责管理生命周期）
            if (guard != null)
            {
                services.AddSingleton(guard);
            }

            // ConfigManager（外部可注入已有实例，如桌面 platform head 已创建）
            if (configManager != null)
            {
                services.AddSingleton(configManager);
            }
            else
            {
                services.AddSingleton<ConfigManager>();
            }

            services.AddSingleton<HubConfigurationAdapter>();
            services.AddSingleton<IHubConfiguration>(sp => sp.GetRequiredService<HubConfigurationAdapter>());
            services.AddSingleton<ICollectorRegistry>(sp => sp.GetRequiredService<HubConfigurationAdapter>());

            // 本地缓存（JsonFileCache 直接充当 ICache<T> 生产 adapter，ADR-020）
            services.AddSingleton<ICache<InputEventItem>>(sp =>
            {
                var cachePath = Path.Combine(dataDirectory, "input-events-cache.json");
                return new JsonFileCache<InputEventItem>(
                    cachePath,
                    // Replay the current v2 retry file verbatim while the durable projection owns
                    // new InputEvent capacity/backpressure.
                    maxItems: int.MaxValue,
                    HeartbeatCacheFormats.InputEventVersion2(),
                    HeartbeatCacheFormats.InputEventMigrations());
            });

            services.AddSingleton<ICache<ActivitySegmentItem>>(sp =>
            {
                var cachePath = Path.Combine(dataDirectory, "segments-cache.json");
                return new JsonFileCache<ActivitySegmentItem>(
                    cachePath,
                    maxItems: 20_000,
                    HeartbeatCacheFormats.SegmentVersion2(),
                    HeartbeatCacheFormats.SegmentMigrations());
            });

            // 基础设施
            services.AddSingleton<IMachineIdentity, WindowsMachineIdentity>();
            services.AddSingleton<IDeviceIdentity, WindowsDeviceIdentity>();
            services.AddSingleton<IHubRuntimeHooks, WindowsHubRuntimeHooks>();
            services.AddSingleton<IWindowEventMonitor, WindowsWindowEventMonitor>();
            services.AddSingleton<ILowLevelInputHook, WindowsLowLevelInputHook>();
            services.AddSingleton<IPowerMonitor, WindowsPowerMonitor>();
            services.AddSingleton<IDesktopObservationSource, WindowsDesktopObservationSource>();
            services.AddSingleton<IDesktopSettings, DesktopSettingsAdapter>();
            services.AddSingleton<IWindowActivityCollectionPolicy, WindowActivitySettingsAdapter>();
            services.AddSingleton<IInputEventRecordingPolicy, InputEventRecordingSettingsAdapter>();
            services.AddSingleton<IInteractionSignalPolicy, InteractionSignalSettingsAdapter>();
            services.AddSingleton<IInputActivitySignal, InputActivitySignal>();

            // 业务服务
            services.AddSingleton<IAppIconExtractor, WindowsAppIconExtractor>();
            services.AddSingleton<IconUploadService>();
            services.AddSingleton<IIconUploadService>(sp => sp.GetRequiredService<IconUploadService>());
            // 输入事件经 system Collector Protocol 回投到同一 legacy upload buffer。
            services.AddSingleton(sp => new InputEventBuffer(
                sp.GetRequiredService<IClock>(),
                publisher: sp.GetRequiredService<ISystemInputEventPublisher>(),
                durableProjectionPath: Path.Combine(dataDirectory, "input-event-facts-buffer.json"),
                statusRegistry: sp.GetRequiredService<UploadStatusRegistry>()));
            services.AddSingleton<IUploadSource<InputEventItem>>(sp => sp.GetRequiredService<InputEventBuffer>());
            // 上传流（ADR-020/022）：绑定源 + 出网 + 缓存；行为差异只剩注入的 compact 策略
            services.AddSingleton(sp =>
            {
                var api = sp.GetRequiredService<HeartbeatApiClient>();
                return new UploadStream<ActivitySegmentItem>(
                    "段",
                    sp.GetRequiredService<IUploadSource<ActivitySegmentItem>>(),
                    (batch, ct) => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct),
                    sp.GetRequiredService<ICache<ActivitySegmentItem>>(),
                    SnapshotCompaction.KeepLatest,
                    new JsonDeadLetterStore<ActivitySegmentItem>(
                        Path.Combine(dataDirectory, "segments-dead-letter.json")),
                    sp.GetRequiredService<UploadStatusRegistry>(),
                    sp.GetRequiredService<ClientCompatibilityStatus>());
            });
            services.AddSingleton(sp =>
            {
                var api = sp.GetRequiredService<HeartbeatApiClient>();
                return new UploadStream<InputEventItem>(
                    "输入事件",
                    sp.GetRequiredService<IUploadSource<InputEventItem>>(),
                    (batch, ct) => api.UploadInputEventsAsync(new InputEventUploadRequest { Events = batch }, ct),
                    sp.GetRequiredService<ICache<InputEventItem>>(),
                    deadLetterStore: new JsonDeadLetterStore<InputEventItem>(
                        Path.Combine(dataDirectory, "input-events-dead-letter.json")),
                    statusRegistry: sp.GetRequiredService<UploadStatusRegistry>(),
                    compatibilityStatus: sp.GetRequiredService<ClientCompatibilityStatus>());
            });

            // 自启动服务
            services.AddSingleton<IAutoStartService, RegistryAutoStartService>();

            // 托管后台服务。停止顺序为注册的逆序：输入 hook 最后注册、最先停止，随后
            // system Binding 停止并推入终态快照，最后由 UploadWorker 的最终 drain 带走（ADR-020）。
            // 此顺序由 AgentHostExtensionsTests 钉住。
            // 注意：IDisposable 的托管服务只通过 AddHostedService 注册一次。此前 AppMonitorService /
            // InputEventCollector 另有 AddSingleton 注册，容器把同一实例捕获进 disposables 两次，
            // host.Dispose() 双重 Dispose → 对已释放 CTS 调 Cancel 抛异常 → 退出流程中断、端口不释放。
            // System 是唯一写死进宿主的 BuiltIn Collector；其余 Collector 是独立发布单元，不进入
            // 宿主组合（ADR-049）。
            services.AddSystemCollectorInProcessBinding(
                new SystemCollectorBindingOptions(dataDirectory));
            // 通用 ExternalHost 绑定：一条 loopback 路由服务所有「自己出现的」Collector。宿主不认识
            // 任何具体产品，能不能接入只由本机有没有对应 Installation 决定（ADR-049、ADR-051）。
            services.AddExternalHostCollectorBinding(
                Path.Combine(Path.GetFullPath(dataDirectory), "collector-packages"));

            // Input hook starts only after the system Activation has opened its Event Stream and
            // stops before that Activation drains.
            services.AddHostedService<InputEventCollector>();

            return services;
        }
    }
}
