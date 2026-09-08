using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Collection.Hub.Runtime;

/// <summary>Owns periodic/final upload exclusion for both Desktop and Headless hosts.</summary>
public abstract class PeriodicUploadWorker(Func<TimeSpan> interval) : BackgroundService
{
    public abstract Task DrainOnceAsync(CancellationToken cancellationToken = default);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval(), stoppingToken);
                await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { Log.Error(exception, "上传调度异常"); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The periodic attempt must return custody before a final attempt can touch its data.
        await base.StopAsync(CancellationToken.None);
        // One final round shares a short network budget. Even after cancellation the sources
        // still get their local persistence attempt; this is not a deadline for native cleanup.
        using var finalDelivery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        finalDelivery.CancelAfter(TimeSpan.FromSeconds(5));
        try { await DrainOnceAsync(finalDelivery.Token); }
        catch (Exception exception) { Log.Warning(exception, "停止时上传收尾失败；数据责任由各上传流报告"); }
    }
}
