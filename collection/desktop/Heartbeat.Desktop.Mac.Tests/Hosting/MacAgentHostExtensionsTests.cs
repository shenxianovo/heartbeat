using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Hosting;
using Heartbeat.Desktop.Mac.Identity;
using Heartbeat.Desktop.Mac.Observations;
using Heartbeat.Desktop.Mac.Native;
using Heartbeat.Desktop.Mac.Input;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.Mac.Tests.Hosting;

public sealed class MacAgentHostExtensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heartbeat-mac-{Guid.NewGuid()}");

    [Fact]
    public void Composition_UsesMacAdapters_AndStopsMonitorBeforeUploadWorker()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<MacDesktopObservationSource>(
            provider.GetRequiredService<IDesktopObservationSource>());

        var hosted = provider.GetServices<IHostedService>().ToList();
        var monitorIndex = hosted.FindIndex(item => item is SystemCollectorHostedService);
        var uploadIndex = hosted.FindIndex(item => item is UploadWorker);
        var inputCollector = provider.GetRequiredService<MacInputEventCollector>();
        var inputIndex = hosted.FindIndex(item => ReferenceEquals(item, inputCollector));
        Assert.Equal(hosted.Count - 1, inputIndex);
        // 只钉相对顺序：通用 binding（如 ExternalHost lease monitor）允许排在两者之间。
        Assert.True(monitorIndex < inputIndex);
        Assert.True(uploadIndex >= 0 && uploadIndex < monitorIndex);
        Assert.True(inputIndex >= 0);
        Assert.DoesNotContain(hosted, service => service is AppMonitorService);
        Assert.Same(
            provider.GetRequiredService<SystemCollectorProtocolAdapter>(),
            provider.GetRequiredService<ISystemSegmentPublisher>());
        Assert.Same(inputCollector, provider.GetRequiredService<IMacInputMonitoringEvents>());
        Assert.Same(inputCollector, provider.GetRequiredService<IInputEventRecordingPolicy>());
    }

    [Fact]
    public void DeviceIdentity_UsesIOPlatformUuid_AndHostnameOnlyAsDefaultName()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var identity = provider.GetRequiredService<IDeviceIdentity>();

        Assert.Equal("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", identity.HardwareId);
        Assert.Equal(Environment.MachineName, identity.DeviceName);

        provider.GetRequiredService<MacConfigManager>().Update(config =>
            config.DeviceName = "Studio Mac");

        Assert.Equal("Studio Mac", identity.DeviceName);
    }

    [Fact]
    public async Task Composition_SystemBindingActivatesAndPublishesThroughProtocol()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var binding = Assert.Single(
            provider.GetServices<IHostedService>().OfType<SystemCollectorHostedService>());

        await binding.StartAsync(CancellationToken.None);
        provider.GetRequiredService<FakeClock>().Advance(TimeSpan.FromSeconds(2));
        provider.GetRequiredService<AppMonitorService>().PushCurrentSnapshot();

        Assert.True(File.Exists(Path.Combine(_root, "collector-runtime.json")));
        Assert.NotNull(provider.GetRequiredService<ICollectionStatus>().CurrentActivity);
        var status = provider.GetRequiredService<ICollectionStatus>();
        await WaitUntilAsync(() => status.SourceLastSeen.ContainsKey("system"));
        await binding.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// 宿主组合里只允许出现通用概念和唯一写死的 System BuiltIn：
    /// 具名可选 Collector（Browser 之类）不进入 composition，缺席就是缺席（ADR-049）。
    /// </summary>
    [Fact]
    public async Task Composition_KnowsNoNamedOptionalCollector_AndStillStartsAndStops()
    {
        var services = BuildServices();
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

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
        foreach (var service in Enumerable.Reverse(hosted))
            await service.StopAsync(CancellationToken.None);
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

    [Fact]
    public void FirstLaunch_DisablesInputRecording_AndUsesVersionedPlatformCaches()
    {
        var services = BuildServices();
        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<MacConfigManager>();

        Assert.False(config.Current.InputEventRecordingEnabled);
        Assert.True(File.Exists(Path.Combine(_root, "config.json")));

        _ = provider.GetRequiredService<Heartbeat.Collection.Hub.Storage.ICache<Heartbeat.Core.DTOs.Segments.ActivitySegmentItem>>();
        _ = provider.GetRequiredService<Heartbeat.Collection.Hub.Storage.ICache<Heartbeat.Core.DTOs.Input.InputEventItem>>();
        Assert.Equal(_root, provider.GetRequiredService<MacAgentPaths>().DataDirectory);
    }

    private ServiceCollection BuildServices()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<FakeClock>();
        services.AddSingleton<IClock>(provider => provider.GetRequiredService<FakeClock>());
        services.AddSingleton<IMacWorkspaceNative, FakeWorkspace>();
        services.AddSingleton<IMacAccessibilityNative, FakeAccessibilityNative>();
        services.AddSingleton<IMacInputMonitoringNative, FakeInputMonitoringNative>();
        services.AddSingleton<IMacPlatformUuid>(new StubPlatformUuid(
            "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"));
        services.AddHeartbeatMacAgent(new MacAgentPaths(_root));
        return services;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeWorkspace : IMacWorkspaceNative
    {
        public event Action<string>? Notification { add { } remove { } }
        public MacApplication? FrontmostApplication =>
            new("com.apple.Terminal", "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", "Terminal");
        public void Start(IReadOnlyCollection<string> notificationNames) { }
        public void Stop() { }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class StubPlatformUuid(string value) : IMacPlatformUuid
    {
        public string? Read() => value;
    }

    private sealed class FakeAccessibilityNative : IMacAccessibilityNative
    {
        public event Action<Exception>? Failed { add { } remove { } }
        public event Action<MacAccessibilityObservation>? Observation { add { } remove { } }
        public bool IsAvailable => true;
        public bool IsProcessTrusted => false;
        public void RequestProcessTrust() { }
        public string? ReadFocusedWindowTitle(int processIdentifier) => null;
        public void ObserveApplication(int processIdentifier) { }
        public void StopObserving() { }
    }

    private sealed class FakeInputMonitoringNative : IMacInputMonitoringNative
    {
        public event Action<Exception>? Failed { add { } remove { } }
        public event Action<MacInputObservation>? Observation { add { } remove { } }
        public bool IsAvailable => true;
        public bool IsAuthorized => false;
        public void RequestAuthorization() { }
        public void StartListening() { }
        public void StopListening() { }
    }
}
