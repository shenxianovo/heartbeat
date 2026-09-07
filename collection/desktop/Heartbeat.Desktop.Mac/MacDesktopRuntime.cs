using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Views;
using Heartbeat.Desktop.Updater.Velopack;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Mac;

public sealed class MacDesktopRuntime : IWindowController, IAsyncDisposable
{
    private const string ReleaseChannel = "osx-arm64-stable";

    private readonly IHost _host;
    private int _stopped;
    private int _quitting;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private MainWindow? _window;
    private TrayIcon? _menuBarIcon;

    public MacDesktopRuntime(IHost host, MacDesktopState state, RingBufferSink logFeed)
    {
        _host = host;
        DesktopState = state;
        LogFeed = logFeed;
        CollectorMarketplace = DesktopCollectorMarketplace.Open(
            "macos",
            host.Services.GetRequiredService<IDeviceIdentity>().HardwareId,
            host.Services.GetRequiredService<CollectorPackageInstallations>(),
            host.Services.GetRequiredService<CollectorRuntime>());
        CollectorMarketplace.StartInstalledCollectors();
        Updates = new VelopackUpdateController(ReleaseChannel, PrepareForUpdateAsync);
        Updates.Start();
    }

    public MacDesktopState DesktopState { get; }
    public VelopackUpdateController Updates { get; }
    public RingBufferSink LogFeed { get; }
    public DesktopCollectorMarketplace CollectorMarketplace { get; }
    public bool IsShutdownPrepared => Volatile.Read(ref _stopped) != 0;

    public void Attach(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindow window,
        TrayIcon menuBarIcon)
    {
        _lifetime = lifetime;
        _window = window;
        _menuBarIcon = menuBarIcon;
    }

    public void ShowSettings() => Dispatcher.UIThread.Post(() =>
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    });

    public void HideSettings() => Dispatcher.UIThread.Post(() => _window?.Hide());

    public void CopyTextToClipboard(string text) => Dispatcher.UIThread.Post(async () =>
    {
        if (_window?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    });

    public async Task QuitAsync()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0) return;
        await StopAgentAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _menuBarIcon?.Dispose();
        });

        Updates.ScheduleOnExitIfReady();
        await Dispatcher.UIThread.InvokeAsync(() => _lifetime?.Shutdown());
    }

    private async Task PrepareForUpdateAsync()
    {
        await StopAgentAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _menuBarIcon?.Dispose();
            _lifetime?.Shutdown();
        });
    }

    private async Task StopAgentAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        Log.Information("正在停止 macOS Heartbeat Agent...");
        await CollectorMarketplace.StopAsync();
        await _host.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        await CollectorMarketplace.DisposeAsync();
        Updates.Dispose();
        DesktopState.Dispose();
        _menuBarIcon?.Dispose();
    }
}
