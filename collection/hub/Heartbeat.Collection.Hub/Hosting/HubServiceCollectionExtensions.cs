using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Heartbeat.Collection.Hub.Hosting;

/// <summary>
/// 可被桌面 Agent 或无头 host 复用的 hub 运行时组合入口。这里只注册进程内运行时状态；
/// HTTP transport、凭证、缓存路径与托管 worker 由各 composition root 提供。
///
/// 组合只认通用领域概念：具名可选 Collector 不进入宿主组合，宿主不带它们的 binding、状态或
/// 安装目录知识（ADR-049）。
/// </summary>
public static class HubServiceCollectionExtensions
{
    public static IServiceCollection AddHeartbeatHub(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<SegmentIngestService>();
        services.TryAddSingleton<ISegmentSink>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<IUploadSource<ActivitySegmentItem>>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<ICurrentActivitySink>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<ICollectionStatus>(sp => sp.GetRequiredService<SegmentIngestService>());
        services.TryAddSingleton<IHubRuntimeHooks, NullHubRuntimeHooks>();
        services.TryAddSingleton<IInputEventRecordingPolicy, EnabledInputEventRecordingPolicy>();
        services.TryAddSingleton<ClientCompatibilityStatus>();
        services.TryAddSingleton<IClientCompatibilityStatus>(sp =>
            sp.GetRequiredService<ClientCompatibilityStatus>());
        services.TryAddSingleton<UploadStatusRegistry>();
        services.TryAddSingleton<IUploadStatus>(sp => sp.GetRequiredService<UploadStatusRegistry>());
        services.TryAddSingleton<TokenManager>();
        services.TryAddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<TokenManager>());
        services.AddHttpClient<AuthServiceClient>();
        services.AddTransient<BearerTokenHandler>();
        services.AddHttpClient<HeartbeatApiClient>().AddHttpMessageHandler<BearerTokenHandler>();
        services.TryAddSingleton<IExternalHostProtocolHttpHandler, NullExternalHostProtocolHttpHandler>();
        services.TryAddSingleton<DeclarationUplinkService>();
        services.TryAddSingleton<ICollectorDeclarationStore>(provider =>
            provider.GetRequiredService<ICollectorRegistry>());
        services.AddHostedService<UploadWorker>();
        services.AddHostedService<StatusUploadWorker>();
        services.AddHostedService<ExternalHostProtocolWorker>();
        return services;
    }

    /// <summary>
    /// 注册通用 ExternalHost Collector 绑定：一条 loopback 路由服务所有「自己出现的」Collector。
    /// 宿主不认识任何具体产品——能不能接入只由「本机有没有对应 Installation」决定，因此新增一个
    /// ExternalHost Collector 不需要改宿主组合（ADR-049、ADR-051）。
    ///
    /// 装不上、没装、机器身份还没就绪都是合法状态：只体现为该连接被拒，不影响宿主启动（ADR-048）。
    /// </summary>
    public static IServiceCollection AddExternalHostCollectorBinding(
        this IServiceCollection services,
        string installRoot,
        ExternalHostProtocolBindingOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        services.TryAddSingleton(new CollectorPackageInstallations(installRoot));
        services.TryAddSingleton(options ?? new ExternalHostProtocolBindingOptions());
        services.TryAddSingleton(provider => new ExternalHostCollectorProtocolHandler(
            provider.GetRequiredService<CollectorRuntime>(),
            provider.GetRequiredService<ICollectorDeclarationStore>(),
            provider.GetRequiredService<CollectorPackageInstallations>(),
            () => MachineSubject(provider.GetRequiredService<IDeviceIdentity>()),
            provider.GetRequiredService<ExternalHostProtocolBindingOptions>()));
        services.Replace(ServiceDescriptor.Singleton<IExternalHostProtocolHttpHandler>(provider =>
            provider.GetRequiredService<ExternalHostCollectorProtocolHandler>()));
        services.AddHostedService<ExternalHostLeaseMonitor>();
        return services;
    }

    /// <summary>
    /// 本机 Machine Subject。口径与 System Collector 一致：机器身份就是那个 UUID 形态的 hardware id，
    /// 因此同一台机器上不同 Collector 的 Instance 落在同一个 Subject 下。
    /// </summary>
    private static SubjectReference MachineSubject(IDeviceIdentity deviceIdentity)
    {
        if (!Guid.TryParse(deviceIdentity.HardwareId, out var machineId) || machineId == Guid.Empty)
            throw new InvalidOperationException(
                "The host machine identity must be a non-empty UUID before an ExternalHost Collector can activate.");
        return new SubjectReference(machineId, SubjectKind.Machine);
    }
}
