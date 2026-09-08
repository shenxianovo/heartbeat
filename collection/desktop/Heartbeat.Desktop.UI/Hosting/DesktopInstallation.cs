using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.Hosting;

public interface IDesktopLoginStart
{
    bool IsSupported => true;
    bool IsEnabled { get; }
    void Enable(string executablePath);
    void Disable();
}

public interface IDesktopUpdates : IUpdateController, IDisposable
{
    void Start();
    // Freeze the ready release now; the application invokes the returned action only after safe cleanup.
    Action PrepareExit(DesktopExitReason reason);
}

/// <summary>Installation mutations are available only through an explicitly bound adapter.</summary>
public sealed class DesktopInstallation(IDesktopLoginStart loginStart,
    Func<Func<Task>, IDesktopUpdates> createUpdates)
{
    public IDesktopLoginStart LoginStart { get; } = loginStart;
    public IDesktopUpdates CreateUpdates(Func<Task> prepareForRestart) => createUpdates(prepareForRestart);

    public void ReconcileLoginStart()
    {
        if (LoginStart.IsSupported && LoginStart.IsEnabled && Environment.ProcessPath is { Length: > 0 } executable)
            LoginStart.Enable(executable);
    }

    public static DesktopInstallation Detached { get; } = new(new DetachedLoginStart(), _ => new DetachedUpdates());

    private sealed class DetachedLoginStart : IDesktopLoginStart
    {
        public bool IsSupported => false;
        public bool IsEnabled => false;
        public void Enable(string executablePath) => throw new InvalidOperationException("This run is not bound to an installation.");
        public void Disable() => throw new InvalidOperationException("This run is not bound to an installation.");
    }

    private sealed class DetachedUpdates : IDesktopUpdates
    {
        public bool IsSupported => false;
        public UpdateSnapshot Current => UpdateSnapshot.Idle;
        public event Action<UpdateSnapshot>? Changed { add { } remove { } }
        public Task<UpdateCheckResult> CheckAsync() => Task.FromResult(UpdateCheckResult.Skipped);
        public Task<bool> ApplyAsync() => Task.FromResult(false);
        public void Start() { }
        public Action PrepareExit(DesktopExitReason reason) => () => { };
        public void Dispose() { }
    }
}
