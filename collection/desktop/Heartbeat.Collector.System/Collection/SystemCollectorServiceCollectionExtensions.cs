using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Heartbeat.Collection.Hub.Ingest;
using Heartbeat.Collection.Hub.Collectors.Runtime;
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
        services.AddHostedService<SystemCollectorHostedService>();
        return services;
    }
}
