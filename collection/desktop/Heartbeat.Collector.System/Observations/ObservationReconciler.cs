namespace Heartbeat.Collector.System.Observations;

/// <summary>
/// Serializes observation transitions. Each pass reads the latest desired state from its owner;
/// overlapping requests share completion and require at most one further pass, without a queue or delay.
/// </summary>
public sealed class ObservationReconciler(Action reconcile)
{
    private readonly object _gate = new();
    private TaskCompletionSource? _pending;
    private long _revision;

    public Task Reconcile()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            _revision++;
            if (_pending is not null) return _pending.Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion;
        }
        while (true)
        {
            long revision;
            lock (_gate) revision = _revision;
            Exception? failure = null;
            try { reconcile(); }
            catch (Exception exception) { failure = exception; }
            lock (_gate)
            {
                if (revision != _revision) continue;
                _pending = null;
                if (failure is null) completion.SetResult();
                else completion.SetException(failure);
                return completion.Task;
            }
        }
    }
}
