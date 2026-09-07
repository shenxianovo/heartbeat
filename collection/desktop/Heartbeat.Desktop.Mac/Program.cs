using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Heartbeat.Desktop.Mac.Configuration;
using Heartbeat.Desktop.Mac.Hosting;
using Heartbeat.Desktop.UI.Diagnostics;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Velopack;

namespace Heartbeat.Desktop.Mac;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var smokeRequested = DesktopStartupSmoke.TryGetRequest(args, out var smoke);
        var logFeed = new RingBufferSink(200);
        var smokeLifecycle = smokeRequested ? DesktopStartupSmoke.BeginLifecycle(smoke) : null;
        try
        {
            // smoke 连日志都写进隔离目录，真实用户数据目录一个字节都不碰。
            ConfigureLogging(logFeed, smokeRequested ? smoke.DataDirectory : null);
            RegisterUnhandledExceptionLogging();
            using var guard = new MacSingleInstanceGuard();
            if (!guard.IsFirstInstance)
            {
                Log.Warning("Heartbeat 已在运行中，当前实例退出");
                if (smokeRequested)
                    Environment.ExitCode = DesktopStartupSmoke.Inconclusive(
                        smoke, "another instance already holds the single-instance guard");
                return;
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            builder.Services.AddSingleton<IMacLoginStart, LaunchAgentLoginStart>();
            builder.Services.AddHeartbeatMacAgent(
                smokeRequested ? new MacAgentPaths(smoke.DataDirectory) : null);
            builder.Services.AddSingleton<MacDesktopState>();
            var host = builder.Build();

            // 发布产物的启动 smoke：只起停 host，不拉起 UI，也不认识任何具名可选 Collector。
            if (smokeRequested)
            {
                using (host)
                    Environment.ExitCode = DesktopStartupSmoke.Run(host, smoke);
                return;
            }

            using var application = new DesktopApplicationLifetime(host);
            host.StartAsync().GetAwaiter().GetResult();
            var runtime = new MacDesktopRuntime(
                host,
                application,
                host.Services.GetRequiredService<MacDesktopState>(),
                logFeed);
            App.Runtime = runtime;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            try
            {
                Log.CloseAndFlush();
            }
            finally
            {
                smokeLifecycle?.Dispose();
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    private static void ConfigureLogging(RingBufferSink sink, string? dataDirectory = null)
    {
        var logDirectory = Path.Combine(
            dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Heartbeat"),
            "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(logDirectory, "heartbeat-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.RingBuffer(sink)
            .CreateLogger();
    }

    private static void RegisterUnhandledExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                Log.Fatal(exception, "未处理的域异常");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "未观察的 Task 异常");
            eventArgs.SetObserved();
        };
    }
}
