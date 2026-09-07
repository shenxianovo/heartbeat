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
using Heartbeat.Desktop.Updater.Velopack;

namespace Heartbeat.Desktop.Mac;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var bootstrap = new DesktopBootstrap(args, MacAgentPaths.Default.DataDirectory);
        if (bootstrap.AllowsInstallationBinding) VelopackApp.Build().Run();
        var smoke = bootstrap.Smoke;
        var smokeRequested = smoke is not null;
        var logFeed = new RingBufferSink(200);
        try
        {
            if (!bootstrap.TryAcquire(@"com.shenxianovo.heartbeat.agent"))
            {
                Console.Error.WriteLine("The Desktop data directory is already in use.");
                Environment.ExitCode = smokeRequested
                    ? DesktopStartupSmoke.Inconclusive(smoke!, "data directory already in use") : 3;
                return;
            }
            ConfigureLogging(logFeed, bootstrap.DataDirectory);
            RegisterUnhandledExceptionLogging();
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            builder.Services.AddHeartbeatMacAgent(
                new MacAgentPaths(bootstrap.DataDirectory));
            builder.Services.AddSingleton<MacDesktopState>();
            var installation = DesktopInstallationBinding.Resolve(bootstrap.AllowsInstallationBinding,
                "osx-arm64-stable", () => new LaunchAgentLoginStart(new Heartbeat.Desktop.Mac.Native.MacCommandRunner()));
            builder.Services.AddSingleton(installation);
            builder.Services.AddSingleton(installation.LoginStart);
            var host = builder.Build();

            // 发布产物的启动 smoke：只起停 host，不拉起 UI，也不认识任何具名可选 Collector。
            if (smokeRequested)
            {
                using (host)
                    Environment.ExitCode = DesktopStartupSmoke.Run(host, smoke!);
                return;
            }

            using var application = new DesktopApplicationLifetime(host);
            installation.ReconcileLoginStart();
            host.StartAsync().GetAwaiter().GetResult();
            var runtime = new MacDesktopRuntime(
                host,
                application,
                host.Services.GetRequiredService<MacDesktopState>(),
                logFeed);
            App.Runtime = runtime;
            using var control = new DesktopProcessControl(bootstrap.DataDirectory, application,
                installation.LoginStart, runtime.Updates);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    private static void ConfigureLogging(RingBufferSink sink, string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "logs");
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
