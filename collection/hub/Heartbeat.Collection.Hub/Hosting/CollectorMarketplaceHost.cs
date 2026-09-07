using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Hub.Hosting;

/// <summary>Owns the Marketplace runtime and its transport. Runtime and installations are borrowed.</summary>
public sealed class CollectorMarketplaceHost : IAsyncDisposable, IDisposable
{
    internal const string HttpClientName = "CollectorMarketplace";
    private readonly ICollectorMarketplaceRuntime _runtime;
    private readonly IDisposable _transport;
    private readonly object _gate = new();
    private Task? _disposeTask;

    public CollectorMarketplaceHost(IHttpClientFactory clients, CollectorMarketplaceHostOptions options,
        CollectorPackageInstallations installations, CollectorRuntime runtime, ICollectorMarketplaceHostAdapter adapter)
    {
        var http = clients.CreateClient(HttpClientName);
        try
        {
            _runtime = new CollectorMarketplaceRuntime(
                new CollectorPackageMarketplace(http, options.RegistryRoot, options.Target, installations),
                installations, runtime, options.Target, adapter);
            _transport = http;
            Management = new ManagementFacade(_runtime);
        }
        catch { http.Dispose(); throw; }
    }

    internal CollectorMarketplaceHost(ICollectorMarketplaceRuntime runtime, IDisposable transport)
    {
        _runtime = runtime;
        _transport = transport;
        Management = new ManagementFacade(runtime);
    }

    public ICollectorMarketplace Management { get; }
    internal void Start() => _runtime.Start();
    internal ValueTask StopAsync(CancellationToken cancellationToken) => _runtime.StopAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposeAsync(completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await _runtime.DisposeAsync(); }
        catch (Exception exception) { failure = exception; }
        try { _transport.Dispose(); }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        if (failure is null) completion.SetResult();
        else completion.SetException(failure);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class ManagementFacade(ICollectorMarketplace marketplace) : ICollectorMarketplace
    {
        public IReadOnlyList<CollectorMarketplaceRuntimeItem> InstalledSnapshot() => marketplace.InstalledSnapshot();
        public ValueTask<IReadOnlyList<CollectorMarketplaceRuntimeItem>> BrowseAsync(CancellationToken token = default) =>
            marketplace.BrowseAsync(token);
        public ValueTask<CollectorMarketplaceRuntimeItem> InstallAsync(string packageId, CancellationToken token = default) =>
            marketplace.InstallAsync(packageId, token);
        public ValueTask<CollectorMarketplaceRuntimeItem> RetryAsync(string packageId, CancellationToken token = default) =>
            marketplace.RetryAsync(packageId, token);
        public ValueTask UninstallAsync(string packageId, CancellationToken token = default) =>
            marketplace.UninstallAsync(packageId, token);
        public ValueTask SubmitAuthorizationAsync(Guid instanceId, Guid interactionId,
            IReadOnlyDictionary<string, string> values, CancellationToken token = default) =>
            marketplace.SubmitAuthorizationAsync(instanceId, interactionId, values, token);
    }
}

/// <summary>Stopping runs before every hosted StopAsync, so management stops before final uploads.</summary>
internal sealed class CollectorMarketplaceWorker(CollectorMarketplaceHost marketplace) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) { marketplace.Start(); return Task.CompletedTask; }
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => marketplace.StopAsync(cancellationToken).AsTask();
    public Task StopAsync(CancellationToken cancellationToken) => marketplace.StopAsync(cancellationToken).AsTask();
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
