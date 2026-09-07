using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Serilog;
using Heartbeat.Collection.Hub.Storage;

namespace Heartbeat.Collection.Hub.Segments
{
    /// <summary>
    /// 段的接收与待确认保管，统一拥有内存快照和离线缓存（ADR-017/022）。
    /// 接收 → 校验 → 缓冲，由 UploadWorker 周期性读取上传。
    /// 缓冲按 Id 键控（ADR-018）：legacy 快照按 EndTime 单调合并；Collector Fact
    /// 投影则按显式 Revision 收敛，允许更高 Revision 缩短纠错。
    /// 同时维护集面读模型（ADR-021）：Current Activity + per-Source last-seen，
    /// 与缓冲分离，不随 drain 清空。
    /// </summary>
    public class SegmentIngestService : ISegmentSink, ISegmentRetractionSink, IDurableSegmentProjectionSink, ICollectorTrafficSink, IUploadSource<ActivitySegmentItem>, ICurrentActivitySink, ICollectionStatus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, ActivitySegmentItem> _segments = [];
        private readonly HashSet<Guid> _retractedSegmentIds = [];
        private readonly Dictionary<Guid, DurableProjectionWatermark> _durableWatermarks = [];
        private readonly IClock _clock;
        private readonly ICache<ActivitySegmentItem>? _cache;
        private List<ActivitySegmentItem> _persisted = [];
        public UploadStreamStatus StorageStatus { get; private set; } = UploadStreamStatus.Ready;

        public SegmentIngestService(IClock clock, ICache<ActivitySegmentItem>? cache = null)
        {
            _clock = clock;
            _cache = cache;
            if (cache is null) return;
            if (cache.Status.State == CacheFileState.MigrationFailed)
            {
                StorageStatus = new(UploadStreamState.CacheMigrationFailed, cache.Status.Message,
                    cache.Status.Action, RecoveryPath: cache.Status.BackupPath);
                return;
            }
            _persisted = cache.Load();
            foreach (var item in SnapshotCompaction.KeepLatest(_persisted)) _segments[item.Id] = item;
        }

        // ---- 集面读模型（ADR-021）：独立小锁，读写不与缓冲争用 ----
        private readonly object _statusLock = new();
        private CurrentActivity? _currentActivity;
        private readonly Dictionary<string, DateTimeOffset> _lastSeen = [];

        // Limit transport requests, not already-committed projections. Fact admission owns backpressure.
        private const int UploadBatchSize = 20_000;

        /// <summary>
        /// Runtime projector 与内置采集路径共用的 segment sink。外部 Collector 必须先经
        /// Collector Protocol 的 Package/Stream/schema 校验，不能直接调用此方法。
        /// 缺 Id 的内部兼容输入补 UUIDv7。
        /// </summary>
        public int Accept(List<ActivitySegmentItem> segments)
        {
            if (segments.Count == 0) return 0;

            foreach (var s in segments)
            {
                if (s.Id == Guid.Empty)
                    s.Id = Guid.CreateVersion7();
            }

            var valid = SegmentValidationPolicy.Filter(segments, _clock.UtcNow);
            if (valid.Count == 0) return 0;

            var accepted = Buffer(valid);
            StampSourceLastSeen(accepted);

            Log.Debug("接收段 {Count} 条（source: {Sources}）",
                accepted.Count, string.Join(",", accepted.Select(v => v.Source).Distinct()));
            return accepted.Count;
        }

        /// <summary>ISegmentSink adapter（ADR-020）：内置采集器进程内推送，与 Accept 同一缓冲。</summary>
        public void Push(List<ActivitySegmentItem> snapshots) => Accept(snapshots);

        /// <summary>
        /// Durable Collector Facts have already passed their declared schema and protocol time
        /// rules. Preserve offline/replayed facts and their revision ordering without
        /// revalidating them against the current wall clock.
        /// </summary>
        public void UpsertDurable(ActivitySegmentItem snapshot, long revision) =>
            BufferDurable(snapshot, revision);

        public void ReplayDurable(ActivitySegmentItem snapshot, long revision) =>
            BufferDurable(snapshot, revision);

        public void RetractDurable(Guid segmentId, long revision)
        {
            if (segmentId == Guid.Empty)
                throw new ArgumentException("Durable Segment projection ID must not be empty.", nameof(segmentId));
            if (revision <= 0)
                throw new ArgumentOutOfRangeException(nameof(revision));

            lock (_lock)
            {
                if (_durableWatermarks.TryGetValue(segmentId, out var current) &&
                    current.Revision > revision)
                    return;
                _durableWatermarks[segmentId] = new DurableProjectionWatermark(revision, Retracted: true);
                _retractedSegmentIds.Add(segmentId);
                _segments.Remove(segmentId);
            }
        }

        public void Retract(Guid segmentId)
        {
            lock (_lock)
            {
                _retractedSegmentIds.Add(segmentId);
                _segments.Remove(segmentId);
            }
        }

        public void MarkSourceActive(string source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            StampSourceLastSeen([source]);
        }

        // ---- 集面读模型（ADR-021） ----

        public event Action<CurrentActivity?>? CurrentActivityChanged;

        public CurrentActivity? CurrentActivity
        {
            get { lock (_statusLock) return _currentActivity; }
        }

        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen
        {
            get { lock (_statusLock) return new Dictionary<string, DateTimeOffset>(_lastSeen); }
        }

        /// <summary>
        /// ICurrentActivitySink adapter（ADR-021）：system 采集器在转场点推送。
        /// 值实际变化才广播，重复上报静默——下游（心跳的变了就推）依赖此去重。
        /// </summary>
        public void Report(CurrentActivity? activity)
        {
            lock (_statusLock)
            {
                if (_currentActivity == activity)
                    return;
                _currentActivity = activity;
            }
            CurrentActivityChanged?.Invoke(activity);
        }

        /// <summary>Reading preserves custody, including while sending is paused or cancelled.</summary>
        public List<ActivitySegmentItem> ReadBatch()
        {
            lock (_lock)
            {
                if (StorageStatus.State == UploadStreamState.CacheMigrationFailed)
                    throw new CacheUnavailableException(StorageStatus.Message ?? "Segment cache is unavailable.");
                var batch = _segments.Values.OrderBy(item => item.StartTime).ToList();
                try { Persist(batch); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    StorageStatus = new(UploadStreamState.CacheWriteFailed, exception.Message,
                        "Repair the segment cache path and retry.");
                }
                return batch.Take(UploadBatchSize).ToList();
            }
        }

        public void Confirm(IReadOnlyList<ActivitySegmentItem> items)
        {
            lock (_lock)
            {
                var confirmed = items.ToHashSet(ReferenceEqualityComparer.Instance);
                var remaining = _segments.Values.Where(item => !confirmed.Contains(item)).ToList();
                // Commit storage before releasing memory. Failure only causes an idempotent resend.
                Persist(remaining);
                foreach (var item in items)
                    if (_segments.TryGetValue(item.Id, out var current) && ReferenceEquals(current, item))
                        _segments.Remove(item.Id);
            }
        }

        public DeliveryRemainder Remainder
        {
            get
            {
                lock (_lock)
                {
                    if (StorageStatus.State == UploadStreamState.CacheMigrationFailed) return DeliveryRemainder.Unknown;
                    var persisted = _persisted.ToHashSet(ReferenceEqualityComparer.Instance);
                    var pending = _segments.Values.Where(item => !persisted.Contains(item)).ToList();
                    var durable = pending.Count(item => _durableWatermarks.ContainsKey(item.Id));
                    return new(_persisted.Count + durable, pending.Count - durable);
                }
            }
        }

        private void Persist(List<ActivitySegmentItem> items)
        {
            if (_cache is null) return;
            if (!_persisted.SequenceEqual(items)) _cache.Replace(items);
            _persisted = items;
            StorageStatus = UploadStreamStatus.Ready;
        }

        private List<ActivitySegmentItem> Buffer(List<ActivitySegmentItem> snapshots)
        {
            var accepted = new List<ActivitySegmentItem>(snapshots.Count);
            lock (_lock)
            {
                foreach (var snapshot in snapshots)
                {
                    if (_retractedSegmentIds.Contains(snapshot.Id) ||
                        _durableWatermarks.ContainsKey(snapshot.Id))
                        continue;
                    _segments[snapshot.Id] = snapshot;
                    accepted.Add(snapshot);
                }
            }
            return accepted;
        }

        private void BufferDurable(ActivitySegmentItem snapshot, long revision)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Id == Guid.Empty)
                throw new ArgumentException("Durable Segment projection ID must not be empty.", nameof(snapshot));
            if (revision <= 0)
                throw new ArgumentOutOfRangeException(nameof(revision));

            lock (_lock)
            {
                if (_durableWatermarks.TryGetValue(snapshot.Id, out var current) &&
                    (current.Revision > revision || current.Retracted))
                    return;
                if (_retractedSegmentIds.Contains(snapshot.Id) &&
                    !_durableWatermarks.ContainsKey(snapshot.Id))
                    return;

                _durableWatermarks[snapshot.Id] = new DurableProjectionWatermark(revision, Retracted: false);
                _segments[snapshot.Id] = snapshot;
            }
        }

        private void StampSourceLastSeen(List<ActivitySegmentItem> accepted) =>
            StampSourceLastSeen(accepted.Select(item => item.Source));

        private void StampSourceLastSeen(IEnumerable<string?> sources)
        {
            // 读模型盖戳（ADR-021）：per-Source last-seen 从实时流量派生，即 Active 的机制。
            var now = _clock.UtcNow;
            lock (_statusLock)
            {
                foreach (var source in sources.Where(source => source is not null).Distinct())
                    _lastSeen[source!] = now;
            }
        }

        private sealed record DurableProjectionWatermark(long Revision, bool Retracted);
    }
}
