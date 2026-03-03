using Heartbeat.Client.Models;
using Heartbeat.Client.Services;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Client.Workers
{
    public class StatusUploadWorker(
        AppMonitorService monitor,
        StatusUploadService statusService,
        Config config) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("状态上传服务启动，间隔 {Interval} 秒", config.StatusUploadIntervalSeconds);

            // 立即上传一次状态
            await UploadStatusAsync();

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.StatusUploadIntervalSeconds));

                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await UploadStatusAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "状态上传异常");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
            }
        }

        private async Task UploadStatusAsync()
        {
            var currentApp = monitor.GetCurrentApp();
            await statusService.UploadAsync(currentApp);
        }
    }
}
