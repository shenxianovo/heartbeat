using client.Models;
using client.Services;
using client.Storage;
using client.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    // 绑定配置
    var config = builder.Configuration.Get<Config>()!;
    builder.Services.AddSingleton(config);

    Log.Information("Heartbeat 客户端启动，环境: {Env}", builder.Environment.EnvironmentName);
    Log.Information("配置加载完成 - 设备: {Device}, 上传间隔: {Upload}min, 状态间隔: {Status}s",
        config.DeviceName, config.UploadIntervalMinutes, config.StatusUploadIntervalSeconds);

    // 基础设施服务
    builder.Services.AddSingleton<LocalCache>(_ => new LocalCache("cache.json"));
    builder.Services.AddSingleton<HttpClient>();

    // 业务服务
    builder.Services.AddSingleton<AppMonitorService>();
    builder.Services.AddSingleton<UsageUploadService>();
    builder.Services.AddSingleton<IconUploadService>();
    builder.Services.AddSingleton<StatusUploadService>();

    // 托管后台服务（AppMonitorService 既是单例也是 HostedService）
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AppMonitorService>());
    builder.Services.AddHostedService<UsageUploadWorker>();
    builder.Services.AddHostedService<StatusUploadWorker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "客户端异常终止");
}
finally
{
    await Log.CloseAndFlushAsync();
}