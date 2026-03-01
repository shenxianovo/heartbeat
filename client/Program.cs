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
    var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
    Log.Information("Heartbeat 客户端启动，环境: {Env}", env);

    var configService = new ConfigService();
    var config = configService.LoadConfig();
    Log.Information("配置加载完成 - 设备: {Device}, 上传间隔: {Upload}min",
        config.DeviceName, config.UploadIntervalMinutes);

    var cache = new LocalCache("cache.json");
    using var monitorService = new AppMonitorService();
    var uploadService = new UsageUploadService(config.ApiUrl, config.ApiKey, config.DeviceName, cache);
    var iconService = new IconUploadService(config.ApiUrl, config.ApiKey);
    var statusService = new StatusUploadService(config.ApiUrl, config.ApiKey, config.DeviceName);

    // 启动事件驱动的前台窗口监听
    monitorService.Start();

    // 定时上传使用记录
    TimerHelper.RunEveryAsync(TimeSpan.FromMinutes(config.UploadIntervalMinutes), async () =>
    {
        var usages = monitorService.GetAndClearUsages();
        if (usages.Count > 0)
        {
            await uploadService.UploadAsync(usages);

            // 异步上传新应用的图标（不阻塞主上传流程）
            var appNames = usages.Select(u => u.AppName).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var appName in appNames)
            {
                _ = iconService.EnsureIconUploadedAsync(appName);
            }
        }
    });

    // 定时上传设备状态（每30秒）
    TimerHelper.RunEveryAsync(TimeSpan.FromSeconds(5), async () =>
    {
        var currentApp = monitorService.GetCurrentApp();
        await statusService.UploadAsync(currentApp);
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