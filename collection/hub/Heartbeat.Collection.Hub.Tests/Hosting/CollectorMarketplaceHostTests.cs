using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Hub.Tests.Hosting;

public sealed class CollectorMarketplaceHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_StopsManagementBeforeFinalUploads_RegardlessOfRegistrationOrder(bool uploadFirst)
    {
        var runtime = new ControlledRuntime();
        var transport = new Transport();
        var uploaded = false;
        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(_ => new CollectorMarketplaceHost(runtime, transport));
            if (uploadFirst) services.AddSingleton<IHostedService>(new FinalUpload(() => uploaded = true));
            services.AddCollectorMarketplace(Options());
            if (!uploadFirst) services.AddSingleton<IHostedService>(new FinalUpload(() => uploaded = true));
        }).Build();
        try
        {
            await host.StartAsync();
            Assert.Equal(1, runtime.Starts);
            var management = host.Services.GetRequiredService<ICollectorMarketplace>();
            Assert.False(management is IDisposable or IAsyncDisposable);

            var stopping = host.StopAsync();
            await runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(uploaded);
            Assert.False(stopping.IsCompleted);
            runtime.AllowStop.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(uploaded);
        }
        finally
        {
            runtime.AllowStop.TrySetResult();
            runtime.AllowDispose.TrySetResult();
            await ((IAsyncDisposable)host).DisposeAsync();
        }
        Assert.Equal(1, runtime.Disposals);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task Dispose_JoinsOneResultAndReleasesTransportWhenRuntimeFails()
    {
        var runtime = new ControlledRuntime();
        var transport = new Transport();
        var owner = new CollectorMarketplaceHost(runtime, transport);
        var first = owner.DisposeAsync().AsTask();
        var second = owner.DisposeAsync().AsTask();
        Assert.Same(first, second);
        Assert.False(second.IsCompleted);
        var failure = new IOException("runtime disposal failed");
        runtime.AllowDispose.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first));
        Assert.Same(first, owner.DisposeAsync().AsTask());
        Assert.Equal(1, runtime.Disposals);
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task Composition_StartsWithoutDesktopOrSystemCollector_AndDefersMachineIdentityUntilInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-host-marketplace-{Guid.NewGuid():N}");
        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<ISegmentSink, NullSegmentSink>();
            services.AddSingleton<IDeviceIdentity, PendingIdentity>();
            services.AddSingleton(new CollectorPackageInstallations(Path.Combine(root, "packages")));
            services.AddCollectorRuntime(new CollectorRuntimeStorageOptions(root));
            services.AddMachineCollectorMarketplace("test", Options().RegistryRoot);
        }).Build();
        try
        {
            await host.StartAsync();
            Assert.Empty(host.Services.GetRequiredService<ICollectorMarketplace>().InstalledSnapshot());
            Assert.Throws<InvalidOperationException>(() => host.Services
                .GetRequiredService<ICollectorMarketplaceHostAdapter>().CreateSubject(SubjectKind.Machine));
            // Application exit can close admission before the Generic Host starts stopping.
            host.Services.GetRequiredService<HostOperationAdmission>().Close();
            var management = host.Services.GetRequiredService<ICollectorMarketplace>();
            Assert.Throws<InvalidOperationException>(() => management.Install("missing"));
            Assert.Throws<InvalidOperationException>(() => management.Retry("missing"));
            Assert.Throws<InvalidOperationException>(() => management.Uninstall("missing"));
            Assert.Throws<InvalidOperationException>(() => management.SubmitAuthorization(Guid.NewGuid(), Guid.NewGuid(),
                new Dictionary<string, string>()));
            Assert.Empty(management.OperationsSnapshot());
            await host.StopAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services
                .GetRequiredService<ICollectorMarketplace>().BrowseAsync().AsTask());
        }
        finally
        {
            await ((IAsyncDisposable)host).DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CollectorMarketplaceHostOptions Options() => new(
        new Uri("https://registry.example.invalid/v1/"), new CollectorMarketplaceTarget("test", "arm64"));

    private sealed class FinalUpload(Action upload) : IHostedService
    {
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) { upload(); return Task.CompletedTask; }
    }

    private sealed class Transport : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class NullSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }

    private sealed class PendingIdentity : IDeviceIdentity
    {
        public string HardwareId => "identity-not-ready";
        public string DeviceName => "Test";
    }

    private sealed class ControlledRuntime : ICollectorMarketplaceRuntime
    {
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public Heartbeat.Collection.Hub.Upload.DeliveryRemainder ShutdownRemainder => new(0, 0);
        public Task Initialized => Task.CompletedTask;
        public void Start() => Starts++;
        public ValueTask StopAsync(CancellationToken token = default)
        {
            StopEntered.TrySetResult();
            return new(AllowStop.Task.WaitAsync(token));
        }
        public ValueTask DisposeAsync() { Disposals++; return new(AllowDispose.Task); }
        public IReadOnlyList<CollectorMarketplaceRuntimeItem> InstalledSnapshot() => [];
        public ValueTask<IReadOnlyList<CollectorMarketplaceRuntimeItem>> BrowseAsync(CancellationToken token = default) =>
            throw new NotSupportedException();
        public HostManagementOperation Install(string packageId) => throw new NotSupportedException();
        public HostManagementOperation Retry(string packageId) => throw new NotSupportedException();
        public HostManagementOperation Uninstall(string packageId) => throw new NotSupportedException();
        public HostManagementOperation SubmitAuthorization(Guid instanceId, Guid interactionId,
            IReadOnlyDictionary<string, string> values) => throw new NotSupportedException();
        public IReadOnlyList<HostManagementOperation> OperationsSnapshot() => [];
        public HostManagementOperation GetOperation(Guid operationId) => throw new NotSupportedException();
        public ValueTask<HostManagementOperation> WaitForOperationAsync(
            Guid operationId,
            CancellationToken token = default) => throw new NotSupportedException();
        public HostManagementOperationCancellation CancelOperation(Guid operationId) =>
            HostManagementOperationCancellation.NotFound;
    }
}
