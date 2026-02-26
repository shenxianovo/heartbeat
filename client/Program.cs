using client.Services;
using client.Storage;
using client.Utils;
using Serilog;

// 配置 Serilog 日志
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/heartbeat-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Heartbeat 客户端启动");

    var configService = new ConfigService();
    var config = configService.LoadConfig();
    Log.Information("配置加载完成 - 设备: {Device}, 采样间隔: {Sample}s, 上传间隔: {Upload}min, 监测模式: {Mode}",
        config.DeviceName, config.SampleIntervalSeconds, config.UploadIntervalMinutes, config.MonitorMode);

    var cache = new LocalCache("cache.json");
    var monitorService = new AppMonitorService(config.MonitorMode);
    var uploadService = new UsageUploadService(config.ApiUrl, config.ApiKey, config.DeviceName, cache);

    TimerHelper.RunEvery(TimeSpan.FromSeconds(config.SampleIntervalSeconds), () => monitorService.RecordActiveApps());
    TimerHelper.RunEveryAsync(TimeSpan.FromMinutes(config.UploadIntervalMinutes), async () =>
    {
        var usages = monitorService.GetAndClearUsages();
        if (usages.Count > 0)
            await uploadService.UploadAsync(usages);
    });

    await uploadService.UploadCachedAsync();

    Log.Information("运行中... Ctrl+C 退出");
    await Task.Delay(-1);
}
catch (Exception ex)
{
    Log.Fatal(ex, "客户端异常终止");
}
finally
{
    await Log.CloseAndFlushAsync();
}