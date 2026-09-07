using Serilog;

namespace Heartbeat.Collector.System.Observations;

public readonly record struct ObservationTarget<TState, TTarget>(TState State, TTarget? Target)
    where TTarget : struct;

/// <summary>
/// Owns one capability's execution, state publication and stop completion. Callers only invalidate
/// the desired state; one background worker reads its latest value without a history queue or delay.
/// </summary>
public sealed class ObservationCapability<TState, TTarget>(
    TState initialState, TState unavailable,
    Func<ObservationTarget<TState, TTarget>> readDesired,
    Action<TTarget> start, Action stop, TimeSpan? pollInterval = null) where TTarget : struct
{
    private readonly object _gate = new();
    private TState _state = initialState;
    private bool _active;
    private bool _accepting;
    private bool _retry;
    private Exception? _reportedFailure;
    private long _revision;
    private TaskCompletionSource? _pending;
    private Task? _stopping;
    private Timer? _poll;
    // Only the worker accesses native ownership and transition results.
    private TTarget? _running;
    private bool _ownsSession;
    private Exception? _failure;
    private Exception? _cleanupFailure;

    public event Action<TState>? Changed;
    public TState State { get { lock (_gate) return _state; } }
    public bool AcceptsObservations { get { lock (_gate) return _active && _accepting; } }

    public Task Start()
    {
        lock (_gate)
        {
            if (_active) return _pending?.Task ?? Task.CompletedTask;
            if (_stopping is { IsCompleted: false }) throw new InvalidOperationException("Observation is still stopping.");
            _stopping?.GetAwaiter().GetResult();
            _stopping = null;
            _active = true;
            _retry = true;
            if (pollInterval is { } interval)
                _poll = new Timer(_ => Refresh(), null, interval, interval);
            return Schedule();
        }
    }

    public Task Refresh(bool retry = false)
    {
        lock (_gate)
        {
            if (!_active) return _stopping ?? Task.CompletedTask;
            if (retry) { _retry = true; _reportedFailure = null; }
            return Schedule();
        }
    }

    public void ReportFailure(Exception failure)
    {
        lock (_gate)
        {
            if (!_active) return;
            _reportedFailure = failure;
            _retry = false;
            _accepting = false;
            Schedule();
        }
    }

    public Task Stop()
    {
        lock (_gate)
        {
            if (_stopping is not null) return _stopping;
            _active = false;
            _accepting = false;
            _poll?.Dispose();
            _poll = null;
            return _stopping = FinishStop(Schedule());
        }
    }

    private async Task FinishStop(Task transition)
    {
        await transition.ConfigureAwait(false);
        if (_cleanupFailure is { } failure) throw new InvalidOperationException("Native observation cleanup failed.", failure);
    }

    // Called under _gate. The worker never runs on the caller's synchronization context.
    private Task Schedule()
    {
        _revision++;
        if (_pending is not null) return _pending.Task;
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(Run);
        return _pending.Task;
    }

    private void Run()
    {
        while (true)
        {
            long revision;
            bool active;
            lock (_gate)
            {
                revision = _revision;
                active = _active;
                if (_retry) _failure = null;
                if (_reportedFailure is { } failure) _failure = failure;
                _retry = false;
                _reportedFailure = null;
            }
            try
            {
                var desired = readDesired();
                var target = active && _failure is null ? desired.Target : null;
                var state = _failure is null || desired.Target is null ? desired.State : unavailable;
                if (target is null || _cleanupFailure is not null)
                {
                    Publish(state, revision);
                    ReleaseSession();
                }
                if (target is { } next && !EqualityComparer<TTarget?>.Default.Equals(_running, next))
                {
                    lock (_gate) _accepting = false;
                    _ownsSession = true;
                    start(next);
                    _running = next;
                }
                Publish(state, revision,
                    active && _running.HasValue && _failure is null);
            }
            catch (Exception failure)
            {
                _failure = failure;
                Log.Warning(failure, "Observation capability transition failed");
                Publish(unavailable, revision);
                if (_cleanupFailure is null)
                {
                    try { ReleaseSession(); }
                    catch (Exception cleanup) { Log.Warning(cleanup, "Observation session remains owned after cleanup failure"); }
                }
            }
            lock (_gate)
            {
                if (revision != _revision) continue;
                _accepting = _active && _running.HasValue && _failure is null;
                var completion = _pending!;
                _pending = null;
                completion.SetResult();
                return;
            }
        }
    }

    private void ReleaseSession()
    {
        if (!_ownsSession) return;
        try
        {
            stop();
            _running = null;
            _ownsSession = false;
            _cleanupFailure = null;
        }
        catch (Exception failure) { _cleanupFailure = failure; throw; }
    }

    private void Publish(TState state, long revision, bool accepting = false)
    {
        lock (_gate)
        {
            if (_revision != revision) return;
            _accepting = _active && accepting;
            if (EqualityComparer<TState>.Default.Equals(_state, state)) return;
            _state = state;
        }
        // Subscribers cannot change state or prevent ownership cleanup by throwing.
        try { Changed?.Invoke(state); }
        catch (Exception failure) { Log.Warning(failure, "Observation state subscriber failed"); }
    }
}
