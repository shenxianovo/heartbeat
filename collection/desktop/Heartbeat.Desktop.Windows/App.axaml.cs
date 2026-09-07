using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.ViewModels;
using Heartbeat.Desktop.UI.Views;
using Serilog;

namespace Heartbeat.Desktop.Windows;

public partial class App : Application
{
    private NativeMenuItem? _applyUpdateItem;
    private TrayIcon? _trayIcon;

    public static WindowsDesktopRuntime Runtime { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "未处理的 Avalonia UI 异常");
            eventArgs.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(
                Runtime.DesktopState,
                Runtime.Updates,
                Runtime,
                new AvaloniaPresentationScheduler(),
                Runtime.LogFeed,
                Runtime.CollectorMarketplace);
            var window = new MainWindow(viewModel)
            {
                Icon = LoadIcon()
            };
            _trayIcon = CreateTrayIcon();

            Runtime.Attach(desktop, window, _trayIcon);
            if (Environment.GetEnvironmentVariable("HEARTBEAT_SHOW_SETTINGS_ON_START") == "1")
                Runtime.ShowSettings();
            desktop.ShutdownRequested += async (_, eventArgs) =>
            {
                if (Runtime.IsShutdownPrepared) return;
                eventArgs.Cancel = true;
                await Runtime.QuitAsync();
            };
            Runtime.Updates.Changed += HandleUpdateChanged;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon CreateTrayIcon()
    {
        var openItem = new NativeMenuItem("打开设置");
        openItem.Click += (_, _) => Runtime.ShowSettings();

        var checkItem = new NativeMenuItem("检查更新") { IsEnabled = Runtime.Updates.IsSupported };
        checkItem.Click += async (_, _) =>
        {
            var result = await Runtime.Updates.CheckAsync();
            Log.Information("手动检查更新结果: {Result}", result);
        };

        _applyUpdateItem = new NativeMenuItem("应用更新并重启")
        {
            IsEnabled = Runtime.Updates.Current.State == UpdateState.ReadyToApply
        };
        _applyUpdateItem.Click += async (_, _) => await Runtime.Updates.ApplyAsync();

        var quitItem = new NativeMenuItem("退出");
        quitItem.Click += async (_, _) => await Runtime.QuitAsync();

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(checkItem);
        menu.Items.Add(_applyUpdateItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        var trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "Heartbeat — Agent 正在运行",
            Menu = menu,
            IsVisible = true
        };
        trayIcon.Clicked += (_, _) => Runtime.ShowSettings();
        return trayIcon;
    }

    private void HandleUpdateChanged(UpdateSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_applyUpdateItem != null)
                _applyUpdateItem.IsEnabled = snapshot.State == UpdateState.ReadyToApply;
            if (_trayIcon != null)
            {
                _trayIcon.ToolTipText = snapshot.State switch
                {
                    UpdateState.UpdateAvailable => "Heartbeat — 发现新版本",
                    UpdateState.Downloading => "Heartbeat — 正在下载更新",
                    UpdateState.ReadyToApply => "Heartbeat — 更新已就绪",
                    _ => "Heartbeat — Agent 正在运行"
                };
            }
        });
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(
            new Uri("avares://Heartbeat.Desktop.Windows/heartbeat.ico"));
        return new WindowIcon(stream);
    }
}
