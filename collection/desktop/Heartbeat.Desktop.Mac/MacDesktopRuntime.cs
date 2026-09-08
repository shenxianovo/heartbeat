using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Heartbeat.Desktop.UI.Logging;
using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Desktop.UI.Presentation;
using Heartbeat.Desktop.UI.Views;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Heartbeat.Collection.Hub.Hosting;

namespace Heartbeat.Desktop.Mac;

public sealed class MacDesktopRuntime : IWindowController, IDesktopApplicationParticipant
{
    private readonly DesktopApplicationLifetime _application;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private MainWindow? _window;
    private TrayIcon? _menuBarIcon;

    public MacDesktopRuntime(
        IHost host, DesktopApplicationLifetime application, MacDesktopState state, RingBufferSink logFeed)
    {
        _application = application;
        DesktopState = new(state, host.Services.GetRequiredService<HostOperationAdmission>());
        LogFeed = logFeed;
        CollectorMarketplace = new DesktopCollectorMarketplace(
            host.Services.GetRequiredService<ICollectorMarketplace>());
        _application.Attach(this);

    }

    public DesktopStateCommands DesktopState { get; }
    public IDesktopUpdates Updates => _application.Updates;
    public RingBufferSink LogFeed { get; }
    public DesktopCollectorMarketplace CollectorMarketplace { get; }
    public bool IsShutdownPrepared => _application.IsShutdownPrepared;

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

    public Task QuitAsync() => _application.QuitAsync();

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        DesktopState.Dispose();
        // MacDesktopState is owned and disposed by the Host service provider.
        return ValueTask.CompletedTask;
    }

    async Task IDesktopApplicationParticipant.ExitAsync(DesktopExitReason reason)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_window != null) _window.AllowClose = true;
            _menuBarIcon?.Dispose();
            _lifetime?.Shutdown();
        });
    }
}
