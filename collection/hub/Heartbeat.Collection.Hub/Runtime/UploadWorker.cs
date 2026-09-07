using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Upload;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Collection.Hub.Runtime
{
    /// <summary>
    /// 出网调度（ADR-020/022）：周期性驱动各上传流 drain 一轮。
    /// 退回重注入由流自持（ADR-022），本类只负责节律与图标挂点。
    /// </summary>
    public class UploadWorker(
        UploadStream<ActivitySegmentItem> segmentStream,
        UploadStream<InputEventItem> inputStream,
        IHubConfiguration configuration,
        IInputEventRecordingPolicy inputRecording,
        DeclarationUplinkService declarationUplink,
        IHubRuntimeHooks hooks) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("上传调度服务启动");
            hooks.OnStarting();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 每次循环读取最新配置
                    var interval = configuration.Current.UploadInterval;
                    Log.Debug("上传间隔: {Interval}", interval);

                    await Task.Delay(interval, stoppingToken);
                    await DrainOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "上传调度异常");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("上传调度服务正在停止，上传剩余数据...");

            // Cancel and join the periodic drain before touching its sources/cache again.
            // The stream persists unfinished batches on cancellation before returning.
            await base.StopAsync(CancellationToken.None);

            // AppMonitorService 已先停止（注册逆序，ADR-020），其终态快照已在 hub 缓冲中。
            try
            {
                await DrainOnceAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止时上传剩余数据失败");
            }
        }

        /// <summary>
        /// 驱动两条流各 drain 一轮；段批次顺带触发图标挂点（ADR-020 §6）与声明上行
        /// （ADR-030 §3，未确认的才发，失败不阻塞下一轮）。
        /// 周期循环与 StopAsync 终态 drain 共用此入口；测试直接调用模拟一轮调度。
        /// </summary>
        public async Task DrainOnceAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested)
                    await declarationUplink.PushOnceAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "采集器声明上行异常");
            }

            if (inputRecording.Enabled)
                await inputStream.DrainAsync(cancellationToken);
            var segments = await segmentStream.DrainAsync(cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
                await hooks.SegmentsDrainedAsync(segments, cancellationToken);
        }
    }
}
