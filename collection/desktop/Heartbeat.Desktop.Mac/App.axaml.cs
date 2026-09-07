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

namespace Heartbeat.Desktop.Mac;

public partial class App : Application
{
    private TrayIcon? _menuBarIcon;

    public static MacDesktopRuntime Runtime { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        MacAccessoryApplication.Enable();
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
            var window = new MainWindow(viewModel) { Icon = LoadIcon() };
            _menuBarIcon = CreateMenuBarIcon();
            Runtime.Attach(desktop, window, _menuBarIcon);
            if (Environment.GetEnvironmentVariable("HEARTBEAT_SHOW_SETTINGS_ON_START") == "1")
                Runtime.ShowSettings();
            desktop.ShutdownRequested += (_, eventArgs) =>
            {
                if (Runtime.IsShutdownPrepared) return;
                eventArgs.Cancel = true;
                _ = Runtime.QuitAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon CreateMenuBarIcon()
    {
        var open = new NativeMenuItem("打开 Heartbeat");
        open.Click += (_, _) => Runtime.ShowSettings();
        var quit = new NativeMenuItem("退出 Heartbeat");
        quit.Click += async (_, _) => await Runtime.QuitAsync();
        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        var icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "Heartbeat — App-only Agent 正在运行",
            Menu = menu,
            IsVisible = true,
        };
        icon.Clicked += (_, _) => Runtime.ShowSettings();
        return icon;
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Heartbeat.Desktop.Mac/heartbeat.ico"));
        return new WindowIcon(stream);
    }
}
