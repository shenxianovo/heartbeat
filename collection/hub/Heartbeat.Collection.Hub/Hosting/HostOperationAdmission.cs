namespace Heartbeat.Collection.Hub.Hosting;

/// <summary>Linearizes short command admission with shutdown; accepted work stays with its owner.</summary>
public sealed class HostOperationAdmission
{
    private readonly object _gate = new();
    private bool _closed;

    public event Action? Closed;
    public bool IsOpen { get { lock (_gate) return !_closed; } }

    public T Run<T>(Func<T> accept)
    {
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("Host is stopping; new changes are not accepted.");
            return accept();
        }
    }

    public void Run(Action accept) => Run(() => { accept(); return true; });

    public void Close(Action? commit = null)
    {
        lock (_gate)
        {
            commit?.Invoke();
            if (_closed) return;
            _closed = true;
        }
        if (Closed is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception exception) { Serilog.Log.Warning(exception, "接纳已关闭，忽略观察者通知失败"); }
        }
    }
}
