using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Storage;
using Serilog;

namespace Heartbeat.Collection.Hub.Upload;

/// <summary>
/// Durable Upload Stream. Retryable failures stay queued; 400/422 are bisected until bad records
/// can be dead-lettered; 413 is bisected until the transport accepts each sub-batch; 426 pauses
/// the stream. At every return point each drained item has either been delivered, durably
/// cached/dead-lettered, or reinjected into its source.
/// </summary>
public sealed class UploadStream<T>
{
    private readonly string _label;
    private readonly IUploadSource<T> _source;
    private readonly Func<List<T>, CancellationToken, Task<ApiResult>> _send;
    private readonly ICache<T> _cache;
    private readonly Func<List<T>, List<T>>? _compactCached;
    private readonly IDeadLetterStore<T>? _deadLetterStore;
    private readonly UploadStatusRegistry? _statusRegistry;
    private readonly ClientCompatibilityStatus? _compatibilityStatus;

    public UploadStream(
        string label,
        IUploadSource<T> source,
        Func<List<T>, CancellationToken, Task<ApiResult>> send,
        ICache<T> cache,
        Func<List<T>, List<T>>? compactCached = null,
        IDeadLetterStore<T>? deadLetterStore = null,
        UploadStatusRegistry? statusRegistry = null,
        ClientCompatibilityStatus? compatibilityStatus = null)
    {
        _label = label;
        _source = source;
        _send = send;
        _cache = cache;
        _compactCached = compactCached;
        _deadLetterStore = deadLetterStore;
        _statusRegistry = statusRegistry;
        _compatibilityStatus = compatibilityStatus;

        Status = cache.Status.State == CacheFileState.MigrationFailed
            ? new UploadStreamStatus(
                UploadStreamState.CacheMigrationFailed,
                cache.Status.Message,
                cache.Status.Action,
                deadLetterStore?.Count ?? 0,
                cache.Status.BackupPath,
                deadLetterStore?.Location)
            : UploadStreamStatus.Ready with
            {
                DeadLetterCount = deadLetterStore?.Count ?? 0,
                DeadLetterPath = deadLetterStore?.Location
            };
        _statusRegistry?.Update(_label, Status);
    }

    public UploadStreamStatus Status { get; private set; }
    public event Action<UploadStreamStatus>? StatusChanged;
    public UploadDrainResult<T>? LastDrain { get; private set; }
    private int? _unconfirmed = 0;

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
        try
        {
            var paused = Status.State is UploadStreamState.UpdateRequired or UploadStreamState.CacheMigrationFailed;
            if (!paused)
            {
                var cached = await UploadCachedAsync(cancellationToken);
                delivered += cached.DeliveredCount;
                paused = cached.Paused;
            }

            if (Status.State != UploadStreamState.CacheMigrationFailed)
            {
                items = _source.Drain();
                var result = paused ? BatchResult<T>.Pause(items) : await ProcessBatchAsync(items, cancellationToken);
                delivered += result.DeliveredCount;
                if (items.Count > 0)
                {
                    if (_source is IDurableUploadSource<T> durableSource)
                        CompleteDurableSourceDrain(durableSource, items, result);
                    else if (result.RetryItems.Count > 0)
                        PreserveFreshRetryItems(result.RetryItems);
                    else
                        RecoverTransientStatus(UploadStreamState.CacheWriteFailed, UploadStreamState.DeadLetterWriteFailed);
                }
            }
        }
        catch (Exception exception)
        {
            error = exception;
            // A source/retention failure has no receipt. Keep that uncertainty across later drains.
            _unconfirmed = null;
            Log.Warning(exception, "{Label}交付责任未确认", _label);
        }
        var remainder = ReadRemainder(() => _source.Remainder) +
            ReadRemainder(() => new DeliveryRemainder(_cache.Load().Count + (_deadLetterStore?.Count ?? 0), 0)) +
            new DeliveryRemainder(0, _unconfirmed);
        return LastDrain = new(items, delivered, remainder, error);
    }

    private static DeliveryRemainder ReadRemainder(Func<DeliveryRemainder> read)
    {
        try { return read(); }
        catch { return DeliveryRemainder.Unknown; }
    }

    private void CompleteDurableSourceDrain(
        IDurableUploadSource<T> source,
        IReadOnlyList<T> drained,
        BatchResult<T> result)
    {
        try
        {
            source.CompleteDrain(drained, result.RetryItems);
            if (!result.Paused)
            {
                RecoverTransientStatus(
                    UploadStreamState.CacheWriteFailed,
                    UploadStreamState.DeadLetterWriteFailed);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "提交{Label}持久源 drain 结果失败；原记录仍保留", _label);
            if (Status.State != UploadStreamState.UpdateRequired)
            {
                SetStatus(new UploadStreamStatus(
                    UploadStreamState.CacheWriteFailed,
                    exception.Message,
                    "Free disk space or repair the durable source path, then retry.",
                    _deadLetterStore?.Count ?? 0,
                    DeadLetterPath: _deadLetterStore?.Location));
            }
        }
    }

    private async Task<BatchResult<T>> UploadCachedAsync(CancellationToken cancellationToken)
    {
        List<T> cached;
        try
        {
            cached = _cache.Load();
        }
        catch (CacheUnavailableException ex)
        {
            SetStatus(new UploadStreamStatus(
                UploadStreamState.CacheMigrationFailed,
                ex.Message,
                _cache.Status.Action,
                _deadLetterStore?.Count ?? 0,
                _cache.Status.BackupPath,
                _deadLetterStore?.Location));
            return BatchResult<T>.Pause([]);
        }

        if (cached.Count == 0) return BatchResult<T>.Complete;
        var toSend = _compactCached?.Invoke(cached) ?? cached;
        Log.Information("发现 {Count} 条缓存{Label}，尝试上传...", toSend.Count, _label);

        var result = await ProcessBatchAsync(toSend, cancellationToken);
        try
        {
            _cache.Replace(result.RetryItems);
            if (result.RetryItems.Count == 0 && !result.Paused)
            {
                RecoverTransientStatus(
                    UploadStreamState.CacheWriteFailed,
                    UploadStreamState.DeadLetterWriteFailed);
            }
            else
            {
                RecoverTransientStatus(UploadStreamState.CacheWriteFailed);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "提交{Label}缓存重传结果失败；原缓存仍保留", _label);
            if (Status.State != UploadStreamState.UpdateRequired)
            {
                SetStatus(new UploadStreamStatus(
                    UploadStreamState.CacheWriteFailed,
                    ex.Message,
                    "Free disk space or repair the cache path, then retry.",
                    _deadLetterStore?.Count ?? 0,
                    DeadLetterPath: _deadLetterStore?.Location));
            }
        }
        return result;
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

    private void PreserveFreshRetryItems(List<T> retryItems)
    {
        try
        {
            _cache.Add(retryItems);
            Log.Information("{Count} 条{Label}已缓存到本地", retryItems.Count, _label);
            RecoverTransientStatus(UploadStreamState.CacheWriteFailed);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Label}缓存写入失败，{Count} 条重注入源 buffer", _label, retryItems.Count);
            _source.Reinject(retryItems);
            if (Status.State != UploadStreamState.UpdateRequired)
            {
                SetStatus(new UploadStreamStatus(
                    UploadStreamState.CacheWriteFailed,
                    ex.Message,
                    "Free disk space or repair the cache path, then retry.",
                    _deadLetterStore?.Count ?? 0,
                    DeadLetterPath: _deadLetterStore?.Location));
            }
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
