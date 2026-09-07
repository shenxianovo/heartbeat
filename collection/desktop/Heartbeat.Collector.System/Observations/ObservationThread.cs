namespace Heartbeat.Collector.System.Observations;

/// <summary>A single native observation session's thread and stop signal; never reused across starts.</summary>
public sealed class ObservationThread
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private bool _finished;

    public ObservationThread(string name, Action<CancellationToken> run, Action<Exception>? onFailure = null)
    {
        _thread = new Thread(() =>
        {
            try { run(_stop.Token); }
            catch (OperationCanceledException) when (IsStopping) { }
            catch (Exception exception) { Failure = exception; }
            finally
            {
                lock (_gate)
                {
                    _finished = true;
                    _stop.Dispose();
                }
                if (Failure is { } failure)
                {
                    try { onFailure?.Invoke(failure); }
                    catch (Exception exception) { Serilog.Log.Warning(exception, "Observation failure notification failed"); }
                }
                // The final notification belongs to this session too. Do not allow a replacement
                // to start while its predecessor can still publish a failure.
                _completion.SetResult();
            }
        })
        { IsBackground = true, Name = name };
    }

    public Task Completion => _completion.Task;
    public Exception? Failure { get; private set; }
    public bool IsStopping { get { lock (_gate) return _stopping; } }

    public void Start() => _thread.Start();

    public void Fail(Exception failure)
    {
        Failure ??= failure;
        Stop(TimeSpan.Zero);
    }

    public void Stop(TimeSpan wait)
    {
        lock (_gate)
        {
            _stopping = true;
            if (!_finished) _stop.Cancel();
        }
        // A stop requested by a native callback cannot join itself. Its session remains owned
        // until Completion; the platform owner must not replace it during that interval.
        if (_thread == Thread.CurrentThread) return;
        if (!_thread.Join(wait))
            throw new TimeoutException("The native observation session has not finished stopping.");
    }
}
