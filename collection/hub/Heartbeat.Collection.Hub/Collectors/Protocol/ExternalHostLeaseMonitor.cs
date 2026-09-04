using Microsoft.Extensions.Hosting;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

/// <summary>
/// 回收 ExternalHost 协议会话的过期 lease。lease 只表示「谁当前持有这条 Stream 的写入权」，
/// 因此过期只释放 Runtime 侧的所有权与 writer lease，从不试图去终止对端进程——对端是不是还活着，
/// 宿主既不知道也不该假装知道（ADR-046）。
/// </summary>
public sealed class ExternalHostLeaseMonitor(
    ExternalHostCollectorProtocolHandler handler,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await handler.ExpireLeasesAsync();
    }
}
