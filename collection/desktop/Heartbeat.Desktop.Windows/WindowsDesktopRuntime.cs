using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Heartbeat.Desktop.Windows.Configuration;
using Heartbeat.Desktop.Windows.Utils;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Views;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.Windows;

public sealed class WindowsDesktopRuntime : IWindowController, IDesktopApplicationParticipant
{
    private readonly DesktopApplicationLifetime _application;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private MainWindow? _window;
    private TrayIcon? _trayIcon;

    public WindowsDesktopRuntime(
        IHost host,
        DesktopApplicationLifetime application,
        ConfigManager config,
        RingBufferSink logFeed)
    {
        _application = application;
        LogFeed = logFeed;
        DesktopState = new WindowsDesktopState(
            config,
            host.Services.GetRequiredService<ICollectionStatus>(),
            host.Services.GetRequiredService<IDesktopLoginStart>(),
            host.Services.GetRequiredService<IClientCompatibilityStatus>(),
            host.Services.GetRequiredService<IUploadStatus>(),
            host.Services.GetRequiredService<InputObservationStatus>());
        CollectorMarketplace = new DesktopCollectorMarketplace(
            host.Services.GetRequiredService<ICollectorMarketplace>());
        Updates = host.Services.GetRequiredService<DesktopInstallation>().CreateUpdates(PrepareForUpdateAsync);
        _application.Attach(this);
        Updates.Start();
    }

    public WindowsDesktopState DesktopState { get; }
    public IDesktopUpdates Updates { get; }
    public RingBufferSink LogFeed { get; }
    public DesktopCollectorMarketplace CollectorMarketplace { get; }
    public bool IsShutdownPrepared => _application.IsShutdownPrepared;

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

    public Task QuitAsync() => _application.RequestExitAsync(DesktopExitReason.Quit);

    private Task PrepareForUpdateAsync() =>
        _application.RequestExitAsync(DesktopExitReason.UpdateRestart);

    Task IDesktopApplicationParticipant.PrepareToExitAsync(DesktopExitReason? reason)
    {
        if (reason == DesktopExitReason.Quit) Updates.ScheduleOnExitIfReady();
        return Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Updates.Dispose();
        DesktopState.Dispose();
        return ValueTask.CompletedTask;
    }

    async Task IDesktopApplicationParticipant.ExitAsync(DesktopExitReason reason)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _trayIcon?.Dispose();
            _lifetime?.Shutdown();
        });
    }
}
