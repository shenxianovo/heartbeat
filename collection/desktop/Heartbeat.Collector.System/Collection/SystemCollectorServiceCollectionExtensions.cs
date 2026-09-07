using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collector.System.Input;

namespace Heartbeat.Collector.System.Collection;

public static class SystemCollectorServiceCollectionExtensions
{
    public static IServiceCollection AddSystemCollectorInProcessBinding(
        this IServiceCollection services,
        SystemCollectorBindingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<SystemCollectorProtocolAdapter>();
        services.AddSingleton<ISystemInputEventPublisher>(provider =>
            provider.GetRequiredService<SystemCollectorProtocolAdapter>());
        services.AddSingleton<ISystemSegmentPublisher>(provider =>
            provider.GetRequiredService<SystemCollectorProtocolAdapter>());
        services.TryAddSingleton<IInputEventFactSink>(provider =>
            provider.GetRequiredService<InputEventBuffer>());
        services.AddSingleton<AppMonitorService>();
        services.AddSingleton<SystemInProcessCollector>();
        services.TryAddSingleton(provider =>
        {
            var bindingOptions = provider.GetRequiredService<SystemCollectorBindingOptions>();
            return CollectorRuntime.Open(
                Path.Combine(bindingOptions.DataDirectory, "collector-runtime.json"),
                provider.GetRequiredService<ISegmentSink>(),
                inputEventSink: provider.GetRequiredService<IInputEventFactSink>(),
                secretStore: new EncryptedFileCollectorSecretStore(
                    Path.Combine(bindingOptions.DataDirectory, "collector-secrets")));
        });
        services.AddHostedService<SystemCollectorHostedService>();
        return services;
    }
}
