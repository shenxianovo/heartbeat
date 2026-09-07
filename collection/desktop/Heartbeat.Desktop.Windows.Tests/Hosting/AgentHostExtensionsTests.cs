using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Hosting;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.Windows.Tests.Hosting;

/// <summary>
/// 组合根的注册顺序契约（ADR-020 §6）：托管服务停止顺序为注册逆序，
/// 输入 hook 最后注册（最先停止），system InProcess Binding 紧随其前（随后停止，终态快照入 hub），
/// UploadWorker 在其之前注册（之后停止，终态 drain 带走快照）。
/// 此前该不变量只有注释钉住——重排两行注册就会让每次关机丢掉最后一段。
/// </summary>
public class AgentHostExtensionsTests : IDisposable
{
    private string TempConfig => Path.Combine(_tempData, "config.json");
    private readonly string _tempRuntime = Path.Combine(Path.GetTempPath(), $"heartbeat-runtime-{Guid.NewGuid():N}");
    // 宿主的本机数据树跟着 ConfigManager 的 DataDirectory 走；配置文件直接放在临时目录根下
    // 会让状态在所有测试与历史运行之间共享，所以需要单独一个隔离的数据目录。
    private readonly string _tempData = Path.Combine(Path.GetTempPath(), $"heartbeat-data-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRuntime)) Directory.Delete(_tempRuntime, recursive: true);
        if (Directory.Exists(_tempData)) Directory.Delete(_tempData, recursive: true);
    }

    [Fact]
    public void HostedServices_MonitorRegisteredLast_AfterUploadWorker()
    {
        var services = new ServiceCollection();
        services.AddHeartbeatAgent(new ConfigManager(TempConfig));
        services.AddSingleton<IDeviceIdentity>(new FakeDeviceIdentity());
        services.AddSingleton(new SystemCollectorBindingOptions(_tempRuntime));
        services.AddSingleton(new CollectorRuntimeStorageOptions(_tempData));

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();


        var monitorIndex = hosted.FindIndex(h => h is SystemCollectorHostedService);
        var inputIndex = hosted.FindIndex(h => h is Heartbeat.Desktop.Windows.Services.InputEventCollector);
        var workerIndex = hosted.FindIndex(h => h is UploadWorker);

        Assert.True(monitorIndex >= 0 && inputIndex >= 0 && workerIndex >= 0);
        Assert.Equal(hosted.Count - 1, inputIndex);   // input hook 最先停止，不再向 Event Stream 写入
        // system Activation 随后 drain。这里只钉相对顺序：通用 binding（如 ExternalHost lease monitor）
        // 允许排在两者之间，停止次序的保证与它们相邻无关。
        Assert.True(monitorIndex < inputIndex);
        Assert.True(workerIndex < monitorIndex);      // worker 在 monitor 之后停止，终态 drain 兜底
        Assert.DoesNotContain(hosted, service => service is AppMonitorService);
        Assert.Same(
            provider.GetRequiredService<SystemCollectorProtocolAdapter>(),
            provider.GetRequiredService<ISystemSegmentPublisher>());
    }

    [Fact]
    public async Task Composition_SystemBindingActivatesAndPublishesThroughProtocol()
    {
        var services = new ServiceCollection();
        services.AddHeartbeatAgent(new ConfigManager(TempConfig));
        var clock = new FakeClock();
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IDeviceIdentity>(new FakeDeviceIdentity());
        services.AddSingleton<IDesktopObservationSource>(new FakeObservations());
        services.AddSingleton<IInputActivitySignal>(new FakeInputActivitySignal());
        services.AddSingleton(new SystemCollectorBindingOptions(_tempRuntime));
        services.AddSingleton(new CollectorRuntimeStorageOptions(_tempData));
        using var provider = services.BuildServiceProvider();
        var binding = Assert.Single(
            provider.GetServices<IHostedService>().OfType<SystemCollectorHostedService>());

        await binding.StartAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        provider.GetRequiredService<AppMonitorService>().PushCurrentSnapshot();

        Assert.True(File.Exists(Path.Combine(_tempData, "collector-runtime.json")));
        Assert.False(File.Exists(Path.Combine(_tempRuntime, "collector-runtime.json")));
        var status = provider.GetRequiredService<ICollectionStatus>();
        Assert.Equal("win:code", status.CurrentActivity!.AppIdentityKey);
        await WaitUntilAsync(() => status.SourceLastSeen.ContainsKey("system"));
        await binding.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// 宿主组合里只允许出现通用概念和唯一写死的 System BuiltIn：
    /// 具名可选 Collector（Browser 之类）不进入 composition，缺席就是缺席（ADR-049）。
    /// </summary>
    [Fact]
    public void Composition_KnowsNoNamedOptionalCollector()
    {
        Directory.CreateDirectory(_tempData);
        var services = new ServiceCollection();
        services.AddHeartbeatAgent(new ConfigManager(Path.Combine(_tempData, "config.json")));
        services.AddSingleton<IDeviceIdentity>(new FakeDeviceIdentity());
        services.AddSingleton(new SystemCollectorBindingOptions(_tempRuntime));
        services.AddSingleton(new CollectorRuntimeStorageOptions(_tempData));

        Assert.DoesNotContain(
            services,
            descriptor => NamesANamedOptionalCollector(descriptor.ServiceType)
                || NamesANamedOptionalCollector(descriptor.ImplementationType));

        using var provider = services.BuildServiceProvider();

        // ExternalHost loopback 绑定是通用能力：注册的 handler 服务所有「自己出现的」Collector，
        // 它认识的是 Package/Installation，而不是任何具体产品（ADR-051）。
        Assert.Equal(
            "ExternalHostCollectorProtocolHandler",
            provider.GetRequiredService<IExternalHostProtocolHttpHandler>().GetType().Name);
    }

    private static bool NamesANamedOptionalCollector(Type? type) =>
        type?.FullName is { } name && (
            name.Contains("Browser", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VRChat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// System 是 BuiltIn Delivery：它必须随 Desktop 一起出现在产物里，缺失即为损坏安装。
    /// </summary>
    [Fact]
    public void SystemCollectorPackage_ShipsWithTheDesktopHost()
    {
        Assert.True(File.Exists(Path.Combine(
            SystemCollectorPackage.Path, "collector-manifest.json")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeDeviceIdentity : IDeviceIdentity
    {
        public string HardwareId => "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        public string DeviceName => "Test desktop";
    }

    private sealed class FakeObservations : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation { add { } remove { } }
        public DesktopActivity CurrentActivity => new("win:code", "Code", "main.cs");
        public void Start() { }
        public void Stop() { }
    }

    private sealed class FakeInputActivitySignal : IInputActivitySignal
    {
        public void MarkClick() { }
        public bool ClickedWithin(TimeSpan window) => false;
    }
}
