using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.Hosting;

/// <summary>Local process readiness and orderly stop, sharing the ordinary UI shutdown transaction.</summary>
public sealed class DesktopProcessControl : IDisposable
{
    private readonly Timer _timer;
    private readonly PosixSignalRegistration? _termination;
    private readonly ConsoleCancelEventHandler _cancel;

    public DesktopProcessControl(string directory, DesktopApplicationLifetime application,
        IDesktopLoginStart loginStart, IUpdateController updates)
    {
        var stop = Path.Combine(directory, "desktop-stop");
        var status = Path.Combine(directory, "desktop-status.json");
        File.Delete(stop);
        File.Delete(status);
        void RequestStop() => Dispatcher.UIThread.Post(async () =>
        {
            try { await application.RequestExitAsync(DesktopExitReason.Quit); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Desktop process stop failed"); }
        });
        _cancel = (_, context) => { context.Cancel = true; RequestStop(); };
        Console.CancelKeyPress += _cancel;
        if (!OperatingSystem.IsWindows())
            _termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                context => { context.Cancel = true; RequestStop(); });
        _timer = new Timer(_ => { if (File.Exists(stop)) RequestStop(); }, null, 250, 250);
        // Runs only when the real native desktop loop is processing UI work.
        Dispatcher.UIThread.Post(() =>
        {
            var temporary = status + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { pid = Environment.ProcessId, uiReady = true,
                loginStartSupported = loginStart.IsSupported, updatesSupported = updates.IsSupported }));
            File.Move(temporary, status, true);
        });
    }

    public void Dispose()
    {
        _timer.Dispose();
        _termination?.Dispose();
        Console.CancelKeyPress -= _cancel;
    }
}
