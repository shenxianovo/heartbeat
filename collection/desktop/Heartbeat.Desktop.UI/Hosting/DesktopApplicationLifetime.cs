using Microsoft.Extensions.Hosting;

namespace Heartbeat.Desktop.UI.Hosting;

public enum DesktopExitReason
{
    Quit,
    UpdateRestart
}

/// <summary>The platform-owned resources that share the desktop Host's lifetime.</summary>
public interface IDesktopApplicationParticipant : IAsyncDisposable
{
    // Runs after hosted services stop and before desktop resources are disposed.
    Task PrepareToExitAsync(DesktopExitReason? reason);
    Task ExitAsync(DesktopExitReason reason);
}

/// <summary>
/// Owns one desktop shutdown transaction. The UI loop remains available until both the desktop
/// resources and the Host have been asynchronously disposed. All exit requests share its result.
/// </summary>
public sealed class DesktopApplicationLifetime(IHost host) : IDisposable
{
    private readonly object _gate = new();
    private IDesktopApplicationParticipant? _desktop;
    private Task? _cleanup;
    private Task? _exit;
    private bool _exitAuthorized;

    public bool IsShutdownPrepared
    {
        get { lock (_gate) return _exitAuthorized; }
    }

    public void Attach(IDesktopApplicationParticipant desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        lock (_gate)
        {
            if (_desktop is not null || _cleanup is not null)
                throw new InvalidOperationException("The desktop lifetime is already attached or stopping.");
            _desktop = desktop;
        }
    }

    /// <summary>Called from the running desktop UI thread, including native shutdown callbacks.</summary>
    public Task RequestExitAsync(DesktopExitReason reason)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_exit is not null) return _exit;
            if (_desktop is null) throw new InvalidOperationException("No desktop is attached.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _exit = completion.Task;
        }
        _ = CompleteExitAsync(reason, completion);
        return completion.Task;
    }

    private async Task CompleteExitAsync(DesktopExitReason reason, TaskCompletionSource completion)
    {
        try
        {
            await CleanupAsync(reason);
            lock (_gate) _exitAuthorized = true;
            // Only this final callback may end the UI loop. Do not require a UI continuation after it.
            await _desktop!.ExitAsync(reason).ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            lock (_gate) _exitAuthorized = false;
            Serilog.Log.Error(exception, "Heartbeat 退出失败");
            completion.SetException(exception);
        }
    }

    private Task CleanupAsync(DesktopExitReason? reason = null)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_cleanup is not null) return _cleanup;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanup = completion.Task;
        }
        _ = CompleteCleanupAsync(reason, completion);
        return completion.Task;
    }

    private async Task CompleteCleanupAsync(DesktopExitReason? reason, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        Serilog.Log.Information("正在停止 Heartbeat Agent...");
        await AttemptAsync(() => new ValueTask(host.StopAsync()));
        // Platform preparation may schedule an update. A failed stop must not initiate it.
        if (_desktop is not null && failures.Count == 0)
            await AttemptAsync(() => new ValueTask(_desktop.PrepareToExitAsync(reason)));
        if (_desktop is not null)
            await AttemptAsync(_desktop.DisposeAsync);
        await AttemptAsync(() =>
        {
            if (host is IAsyncDisposable asynchronous) return asynchronous.DisposeAsync();
            host.Dispose();
            return ValueTask.CompletedTask;
        });

        if (failures.Count == 0) completion.SetResult();
        else completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));

        async Task AttemptAsync(Func<ValueTask> action)
        {
            try { await action(); }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    /// <summary>
    /// Entrypoint fallback for startup failures or an unexpectedly returned UI loop. Normal exit
    /// has already completed cleanup; never wait on an in-flight task bound to a stopped UI loop.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_cleanup is { IsCompleted: false })
                throw new InvalidOperationException("The UI loop ended before desktop shutdown completed.");
        }
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            CleanupAsync().GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
