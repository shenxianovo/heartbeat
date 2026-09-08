using Heartbeat.Collection.Hub.Http;
using Serilog;

namespace Heartbeat.Collection.Hub.Upload;

/// <summary>Transports retained snapshots and confirms delivered or quarantined records to their owner.</summary>
public sealed class UploadStream<T>
{
    private readonly string _label;
    private readonly IReadOnlyList<IUploadSource<T>> _sources;
    private readonly Func<List<T>, CancellationToken, Task<ApiResult>> _send;
    private readonly IDeadLetterStore<T>? _deadLetterStore;
    private readonly UploadStatusRegistry? _statusRegistry;
    private readonly ClientCompatibilityStatus? _compatibilityStatus;

    public UploadStream(
        string label,
        IReadOnlyList<IUploadSource<T>> sources,
        Func<List<T>, CancellationToken, Task<ApiResult>> send,
        IDeadLetterStore<T>? deadLetterStore = null,
        UploadStatusRegistry? statusRegistry = null,
        ClientCompatibilityStatus? compatibilityStatus = null)
    {
        _label = label;
        _sources = sources;
        _send = send;
        _deadLetterStore = deadLetterStore;
        _statusRegistry = statusRegistry;
        _compatibilityStatus = compatibilityStatus;

        Status = sources.Select(source => source.StorageStatus)
            .FirstOrDefault(status => status.State == UploadStreamState.CacheMigrationFailed) ?? ReadyStatus();
        _statusRegistry?.Update(_label, Status);
    }

    public UploadStreamStatus Status { get; private set; }
    public event Action<UploadStreamStatus>? StatusChanged;
    public UploadDrainResult<T>? LastDrain { get; private set; }

    /// <summary>Current custody, including disabled streams that have not attempted transport.</summary>
    public DeliveryRemainder Remainder
    {
        get
        {
            var remainder = new DeliveryRemainder(_deadLetterStore?.Count ?? 0, 0);
            foreach (var source in _sources)
            {
                try { remainder += source.Remainder; }
                catch { remainder += DeliveryRemainder.Unknown; }
            }
            return remainder;
        }
    }

    /// <summary>
    /// Clears this stream's in-process 426 pause for diagnostics. It intentionally does not clear
    /// the process-wide compatibility state: production recovery requires updating and restarting
    /// the Agent, whose new process retries the durably retained cache once.
    /// </summary>
    public void Resume()
    {
        if (Status.State != UploadStreamState.UpdateRequired) return;
        SetStatus(ReadyStatus());
    }

    public async Task<UploadDrainResult<T>> DrainAsync(CancellationToken cancellationToken = default)
    {
        LastDrain = null;
        List<T> items = [];
        var delivered = 0;
        Exception? error = null;
        UploadStreamStatus? storageFailure = null;
        var hasRetry = false;
        foreach (var source in _sources)
        {
            try
            {
                // Reading also gives the owner a chance to persist fresh data while transport is paused.
                var batch = source.ReadBatch();
                items.AddRange(batch);
                var paused = Status.State is UploadStreamState.UpdateRequired or UploadStreamState.CacheMigrationFailed;
                var result = paused ? BatchResult<T>.Pause(batch) : await ProcessBatchAsync(batch, cancellationToken);
                delivered += result.DeliveredCount;
                var retry = result.RetryItems.ToHashSet();
                var confirmed = batch.Where(item => !retry.Contains(item)).ToList();
                if (confirmed.Count > 0) source.Confirm(confirmed);
                hasRetry |= result.RetryItems.Count > 0;
                if (source.StorageStatus.State != UploadStreamState.Ready)
                    storageFailure = source.StorageStatus;

            }
            catch (Exception exception)
            {
                error = exception;
                Log.Warning(exception, "{Label}源仍保管未确认记录", _label);
                storageFailure = source.StorageStatus.State == UploadStreamState.CacheMigrationFailed
                    ? source.StorageStatus
                    : new UploadStreamStatus(UploadStreamState.CacheWriteFailed, exception.Message,
                        "Repair the source storage and retry.");
            }
        }
        if (Status.State != UploadStreamState.UpdateRequired)
        {
            if (storageFailure is not null)
                SetStatus(storageFailure with
                {
                    DeadLetterCount = _deadLetterStore?.Count ?? 0,
                    DeadLetterPath = _deadLetterStore?.Location
                });
            else if (!hasRetry)
                RecoverTransientStatus(UploadStreamState.CacheWriteFailed, UploadStreamState.DeadLetterWriteFailed);
            else
                RecoverTransientStatus(UploadStreamState.CacheWriteFailed);
        }
        return LastDrain = new(items, delivered, Remainder, error);
    }

    private async Task<BatchResult<T>> ProcessBatchAsync(List<T> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return BatchResult<T>.Complete;
        if (cancellationToken.IsCancellationRequested) return BatchResult<T>.Retry(items);

        ApiResult response;
        try
        {
            response = await _send(items, cancellationToken);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "{Label}传输未确认，保留本批重试", _label);
            return BatchResult<T>.Retry(items);
        }
        if (response.Success) return new([], false, items.Count);
        if (cancellationToken.IsCancellationRequested) return BatchResult<T>.Retry(items);

        if (response.StatusCode == 426)
        {
            SetStatus(new UploadStreamStatus(
                UploadStreamState.UpdateRequired,
                response.ResponseBody ?? "The server requires a newer Heartbeat version.",
                "Update and restart Heartbeat; the retained data will retry automatically.",
                _deadLetterStore?.Count ?? 0,
                DeadLetterPath: _deadLetterStore?.Location));
            _compatibilityStatus?.RequireUpdate(response.ResponseBody);
            return BatchResult<T>.Pause(items);
        }

        if (response.StatusCode is 400 or 422)
        {
            if (items.Count == 1)
                return DeadLetterOrRetry(items[0], response);

            return await SplitBatchAsync(items, cancellationToken);
        }

        if (response.StatusCode == 413)
        {
            // A proxy can reject a backlog even though every record is valid. Splitting preserves
            // successful sub-batches and prevents the same oversized request from retrying forever.
            if (items.Count == 1)
                return BatchResult<T>.Retry(items);

            return await SplitBatchAsync(items, cancellationToken);
        }

        // Network failures, recoverable auth (401), 408, 429 and 5xx are explicitly retryable.
        // Unknown statuses also default to retain-and-retry: data loss is worse than delayed progress.
        return BatchResult<T>.Retry(items);
    }

    private async Task<BatchResult<T>> SplitBatchAsync(List<T> items, CancellationToken cancellationToken)
    {
        var midpoint = items.Count / 2;
        var left = await ProcessBatchAsync(items.GetRange(0, midpoint), cancellationToken);
        if (left.Paused)
            return new(left.RetryItems.Concat(items.Skip(midpoint)).ToList(), true, left.DeliveredCount);

        var right = await ProcessBatchAsync(items.GetRange(midpoint, items.Count - midpoint), cancellationToken);
        return new BatchResult<T>(
            left.RetryItems.Concat(right.RetryItems).ToList(),
            right.Paused,
            left.DeliveredCount + right.DeliveredCount);
    }

    private BatchResult<T> DeadLetterOrRetry(T item, ApiResult rejection)
    {
        try
        {
            if (_deadLetterStore == null)
                throw new InvalidOperationException("No dead-letter store is configured.");
            _deadLetterStore.Append(_label, item, rejection);
            SetStatus(ReadyStatus());
            return BatchResult<T>.Complete;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label}坏记录写入 dead letter 失败，保留重试", _label);
            SetStatus(new UploadStreamStatus(
                UploadStreamState.DeadLetterWriteFailed,
                ex.Message,
                "Repair the dead-letter path; the rejected record remains queued.",
                _deadLetterStore?.Count ?? 0,
                DeadLetterPath: _deadLetterStore?.Location));
            return BatchResult<T>.Retry([item]);
        }
    }

    private void SetStatus(UploadStreamStatus status)
    {
        if (Status == status) return;
        Status = status;
        _statusRegistry?.Update(_label, status);
        StatusChanged?.Invoke(status);
    }

    private void RecoverTransientStatus(params UploadStreamState[] recoverableStates)
    {
        if (!recoverableStates.Contains(Status.State)) return;
        SetStatus(ReadyStatus());
    }

    private UploadStreamStatus ReadyStatus() => UploadStreamStatus.Ready with
    {
        DeadLetterCount = _deadLetterStore?.Count ?? 0,
        DeadLetterPath = _deadLetterStore?.Location
    };

    private sealed record BatchResult<TItem>(List<TItem> RetryItems, bool Paused, int DeliveredCount = 0)
    {
        public static BatchResult<TItem> Complete { get; } = new([], false);
        public static BatchResult<TItem> Retry(List<TItem> items) => new(new(items), false);
        public static BatchResult<TItem> Pause(List<TItem> items) => new(new(items), true);
    }
}
