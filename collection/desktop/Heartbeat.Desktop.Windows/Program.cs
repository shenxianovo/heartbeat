using Avalonia;
using Avalonia.Controls;
using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Hosting;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Desktop.UI.Diagnostics;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Velopack;
using Heartbeat.Desktop.Updater.Velopack;
using System.Runtime.InteropServices;

namespace Heartbeat.Desktop.Windows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var bootstrap = new DesktopBootstrap(args, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heartbeat"));
        if (bootstrap.AllowsInstallationBinding) VelopackApp.Build().Run();
        var smoke = bootstrap.Smoke;
        var smokeRequested = smoke is not null;
        var logSink = new RingBufferSink(200);
        try
        {
            if (!bootstrap.TryAcquire(@"Global\Heartbeat.Desktop.Windows.SingleInstance"))
            {
                Console.Error.WriteLine("The Desktop data directory is already in use.");
                Environment.ExitCode = smokeRequested
                    ? DesktopStartupSmoke.Inconclusive(smoke!, "data directory already in use") : 3;
                if (!smokeRequested && bootstrap.AllowsInstallationBinding) WindowsMessageBox.ShowAlreadyRunning();
                return;
            }
            ConfigureLogging(logSink, bootstrap.DataDirectory);
            RegisterUnhandledExceptionLogging();
            var config = new ConfigManager(Path.Combine(bootstrap.DataDirectory, "config.json"));
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSerilog();
            builder.Services.AddHeartbeatAgent(config);
            var installation = DesktopInstallationBinding.Resolve(bootstrap.AllowsInstallationBinding,
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", () => new Heartbeat.Desktop.Windows.Services.RegistryAutoStartService());
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
            var runtime = new WindowsDesktopRuntime(host, application, config, logSink);
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
        AppBuilder.Configure<App>()
            .UsePlatformDetect();

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
