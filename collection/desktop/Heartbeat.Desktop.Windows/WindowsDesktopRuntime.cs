using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Views;
using Heartbeat.Desktop.Updater.Velopack;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopRuntime : IWindowController, IAsyncDisposable
{
    private readonly IHost _host;
    private readonly SingleInstanceGuard _guard;
    private int _stopped;
    private int _quitting;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private MainWindow? _window;
    private TrayIcon? _trayIcon;

    public WindowsDesktopRuntime(
        IHost host,
        SingleInstanceGuard guard,
        ConfigManager config,
        RingBufferSink logFeed)
    {
        _host = host;
        _guard = guard;
        LogFeed = logFeed;
        DesktopState = new WindowsDesktopState(
            config,
            host.Services.GetRequiredService<ICollectionStatus>(),
            host.Services.GetRequiredService<IAutoStartService>(),
            host.Services.GetRequiredService<IClientCompatibilityStatus>(),
            host.Services.GetRequiredService<IUploadStatus>());
        CollectorMarketplace = DesktopCollectorMarketplace.Open(
            "windows",
            host.Services.GetRequiredService<IDeviceIdentity>().HardwareId,
            host.Services.GetRequiredService<CollectorPackageInstallations>(),
            host.Services.GetRequiredService<CollectorRuntime>());
        CollectorMarketplace.StartInstalledCollectors();
        var channel = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "win-arm64"
            : "win-x64";
        Updates = new VelopackUpdateController(channel, PrepareForUpdateAsync);
        Updates.Start();
    }

    public WindowsDesktopState DesktopState { get; }
    public VelopackUpdateController Updates { get; }
    public RingBufferSink LogFeed { get; }
    public DesktopCollectorMarketplace CollectorMarketplace { get; }
    public bool IsShutdownPrepared => Volatile.Read(ref _stopped) != 0;

    public void Attach(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindow window,
        TrayIcon trayIcon)
    {
        _lifetime = lifetime;
        _window = window;
        _trayIcon = trayIcon;
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
            _trayIcon?.Dispose();
        });
        _guard.Dispose();

        Updates.ScheduleOnExitIfReady();
        await Dispatcher.UIThread.InvokeAsync(() => _lifetime?.Shutdown());
    }

    private async Task PrepareForUpdateAsync()
    {
        await StopAgentAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _trayIcon?.Dispose();
            _lifetime?.Shutdown();
        });
        _guard.Dispose();
    }

    private async Task StopAgentAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        Log.Information("正在停止 Heartbeat Agent...");
        await CollectorMarketplace.StopAsync();
        await _host.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        await CollectorMarketplace.DisposeAsync();
        Updates.Dispose();
        DesktopState.Dispose();
        _trayIcon?.Dispose();
    }
}
