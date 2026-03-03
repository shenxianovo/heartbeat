using client.Models;
using client.Services;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace client.Workers
{
    public class UsageUploadWorker(
        AppMonitorService monitor,
        UsageUploadService usageService,
        IconUploadService iconService,
        Config config) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Log.Information("使用记录上传服务启动，间隔 {Interval} 分钟", config.UploadIntervalMinutes);

            // 启动时尝试上传缓存
            await usageService.UploadCachedAsync();

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.UploadIntervalMinutes));

                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await UploadUsagesAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "使用记录上传异常");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Log.Information("使用记录上传服务正在停止，上传剩余数据...");

            try
            {
                await UploadUsagesAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止时上传剩余数据失败");
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task UploadUsagesAsync()
        {
            var usages = monitor.GetAndClearUsages();
            if (usages.Count == 0) return;

            await usageService.UploadAsync(usages);

            // 异步上传新应用的图标（不阻塞主上传流程）
            var appNames = usages.Select(u => u.AppName).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var appName in appNames)
            {
                _ = iconService.EnsureIconUploadedAsync(appName);
            }
        }
    }
}
