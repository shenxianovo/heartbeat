using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Desktop.UI.Hosting;

public enum DesktopExitReason
{
    Quit,
    UpdateRestart
}

public sealed record DesktopDeliveryEvidence(string Owner, DeliveryRemainder Remainder, string? Details = null);

public sealed record DesktopExitResult(
    IReadOnlyList<DesktopDeliveryEvidence> Delivery,
    IReadOnlyList<Exception> CleanupErrors,
    Exception? FinalActionError = null)
{
    public bool IsDataSafe => Delivery.Count > 0 && Delivery.All(item => item.Remainder.IsDurable);
}

/// <summary>The platform-owned resources that share the desktop Host's lifetime.</summary>
public interface IDesktopApplicationParticipant : IAsyncDisposable
{
    Task ExitAsync(DesktopExitReason reason);
}

/// <summary>
/// Owns one desktop shutdown transaction. The UI loop remains available until both the desktop
/// resources and the Host have been asynchronously disposed. All exit requests share its result.
/// </summary>
public sealed class DesktopApplicationLifetime(IHost host) : IDisposable
{
    private readonly object _gate = new();
    private readonly HostOperationAdmission _admission = host.Services.GetRequiredService<HostOperationAdmission>();
    private IDesktopApplicationParticipant? _desktop;
    private Task? _cleanup;
    private Task? _exit;
    private DesktopExitReason? _reason;
    private bool _exitAuthorized;
    private readonly IHostShutdownEvidence[] _evidence = host.Services.GetServices<IHostedService>()
        .OfType<IHostShutdownEvidence>().ToArray();

    public IDesktopUpdates Updates { get; private set; } = null!;

    public DesktopExitResult? Result { get; private set; }

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
            Updates = host.Services.GetRequiredService<DesktopInstallation>()
                .CreateUpdates(() => RequestExitAsync(DesktopExitReason.UpdateRestart));
        }
        Updates.Start();
    }

    /// <summary>Called from the running desktop UI thread, including native shutdown callbacks.</summary>
    public Task RequestExitAsync(DesktopExitReason reason)
    {
        TaskCompletionSource? completion = null;
        Task exiting = null!;
        _admission.Close(() =>
        {
            lock (_gate)
            {
                if (_exit is not null)
                {
                    if (_reason != reason)
                        throw new InvalidOperationException($"Desktop exit intent is already fixed as {_reason}.");
                }
                else
                {
                    if (_desktop is null) throw new InvalidOperationException("No desktop is attached.");
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _exit = completion.Task;
                    _reason = reason;
                }
                exiting = _exit;
            }
        });
        if (completion is not null) _ = CompleteExitAsync(reason, completion);
        return exiting;
    }

    /// <summary>Native async event handlers must not turn a refused exit into an unhandled exception.</summary>
    public async Task QuitAsync()
    {
        try { await RequestExitAsync(DesktopExitReason.Quit); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "退出请求未完成，保留当前退出结果"); }
    }

    private async Task CompleteExitAsync(DesktopExitReason reason, TaskCompletionSource completion)
    {
        try
        {
            var finalAction = Updates.PrepareExit(reason);
            await CleanupAsync(reason);
            try { finalAction(); }
            catch (Exception exception)
            {
                Result = Result! with { FinalActionError = exception };
                Serilog.Log.Error(exception, "最终动作失败，继续退出；数据保管结果不变");
            }
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
        if (reason is not null)
        {
            var delivery = _evidence.Select(source =>
            {
                try { return new DesktopDeliveryEvidence(source.GetType().Name, source.ShutdownRemainder, source.ShutdownDetails); }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    return new DesktopDeliveryEvidence(source.GetType().Name, DeliveryRemainder.Unknown);
                }
            }).ToArray();
            Result = new(delivery, failures.ToArray());
            if (!Result.IsDataSafe)
            {
                // Best effort is an exit policy, not evidence that unknown data became durable.
                Serilog.Log.Error("部分数据尚未确认保存，退出可能丢失；继续清理和退出。数据保管结果 {@Delivery}，收尾错误 {@Errors}",
                    Result.Delivery, Result.CleanupErrors);
            }
        }
        if (_desktop is not null)
        {
            await AttemptAsync(_desktop.DisposeAsync);
            await AttemptAsync(() => { Updates.Dispose(); return ValueTask.CompletedTask; });
        }
        await AttemptAsync(() =>
        {
            if (host is IAsyncDisposable asynchronous) return asynchronous.DisposeAsync();
            host.Dispose();
            return ValueTask.CompletedTask;
        });

        if (Result is not null) Result = Result with { CleanupErrors = failures.ToArray() };
        foreach (var failure in failures) Serilog.Log.Error(failure, "Heartbeat 清理失败");
        if (reason is not null || failures.Count == 0) completion.SetResult();
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
