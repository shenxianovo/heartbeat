using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Upload;
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
        IHubRuntimeHooks hooks) : PeriodicUploadWorker(() => configuration.Current.UploadInterval)
    {
        public UploadDrainResult<ActivitySegmentItem>? SegmentDrain => segmentStream.LastDrain;
        public UploadDrainResult<InputEventItem>? InputDrain => inputRecording.Enabled ? inputStream.LastDrain : null;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            hooks.OnStarting();
            return base.ExecuteAsync(stoppingToken);
        }

        /// <summary>
        /// 驱动两条流各 drain 一轮；段批次顺带触发图标挂点（ADR-020 §6）与声明上行
        /// （ADR-030 §3，未确认的才发，失败不阻塞下一轮）。
        /// 周期循环与 StopAsync 终态 drain 共用此入口；测试直接调用模拟一轮调度。
        /// </summary>
        public override async Task DrainOnceAsync(CancellationToken cancellationToken = default)
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
                await hooks.SegmentsDrainedAsync(segments.FreshItems, cancellationToken);
        }
    }
}
