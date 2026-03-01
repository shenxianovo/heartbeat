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

    // 加载配置
    var config = ConfigService.Load();
    Log.Information("配置加载完成 - 设备: {Device}, 上传间隔: {Upload}min",
        config.DeviceName, config.UploadIntervalMinutes);

    // 共享 HttpClient & 基础服务
    using var httpClient = new HttpClient();
    var cache = new LocalCache("cache.json");

    // 业务服务
    using var monitorService = new AppMonitorService();
    var usageService = new UsageUploadService(config, httpClient, cache);
    var iconService = new IconUploadService(config, httpClient);
    var statusService = new StatusUploadService(config, httpClient);

    // 启动前台窗口监听
    monitorService.Start();

    // 定时上传使用记录
    TimerHelper.RunEveryAsync(TimeSpan.FromMinutes(config.UploadIntervalMinutes), async () =>
    {
        var usages = monitorService.GetAndClearUsages();
        if (usages.Count > 0)
        {
            await usageService.UploadAsync(usages);

            // 异步上传新应用的图标（不阻塞主上传流程）
            var appNames = usages.Select(u => u.AppName).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var appName in appNames)
            {
                _ = iconService.EnsureIconUploadedAsync(appName);
            }
        }
    });

    // 定时上传设备状态
    TimerHelper.RunEveryAsync(TimeSpan.FromSeconds(config.StatusUploadIntervalSeconds), async () =>
    {
        var currentApp = monitorService.GetCurrentApp();
        await statusService.UploadAsync(currentApp);
    });

    // 启动时尝试上传缓存
    await usageService.UploadCachedAsync();

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