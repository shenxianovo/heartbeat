using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Presence;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Serilog;
using System.Runtime.CompilerServices;

namespace Heartbeat.Collection.Hub.Segments
{
    /// <summary>
    /// 段的内存缓冲（ADR-017 枢纽的接收侧）。
    /// 接收 → 校验 → 缓冲，由 UploadWorker 周期性取走上传。
    /// 缓冲按 Id 键控（ADR-018）：legacy 快照按 EndTime 单调合并；Collector Fact
    /// 投影则按显式 Revision 收敛，允许更高 Revision 缩短纠错。
    /// 同时维护集面读模型（ADR-021）：Current Activity + per-Source last-seen，
    /// 与缓冲分离，不随 drain 清空。
    /// </summary>
    public class SegmentIngestService(IClock clock) : ISegmentSink, ISegmentRetractionSink, IDurableSegmentProjectionSink, ICollectorTrafficSink, IUploadSource<ActivitySegmentItem>, ICurrentActivitySink, ICollectionStatus
    {
        private readonly object _lock = new();
        private readonly Dictionary<Guid, ActivitySegmentItem> _segments = [];
        private readonly HashSet<Guid> _retractedSegmentIds = [];
        private readonly Dictionary<Guid, DurableProjectionWatermark> _durableWatermarks = [];
        private readonly ConditionalWeakTable<ActivitySegmentItem, DurableRevisionStamp> _durableRevisionStamps = new();
        private readonly Dictionary<Guid, bool> _evicted = [];

        // ---- 集面读模型（ADR-021）：独立小锁，读写不与缓冲争用 ----
        private readonly object _statusLock = new();
        private CurrentActivity? _currentActivity;
        private readonly Dictionary<string, DateTimeOffset> _lastSeen = [];

        /// <summary>缓冲上限：防失控采集器把 Agent 内存吃满（超出丢最旧）。</summary>
        private const int MaxBuffered = 20000;

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

            var valid = SegmentValidationPolicy.Filter(segments, clock.UtcNow);
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
                _evicted.Remove(segmentId);
                _retractedSegmentIds.Add(segmentId);
                _segments.Remove(segmentId);
            }
        }

        public void Retract(Guid segmentId)
        {
            lock (_lock)
            {
                _evicted.Remove(segmentId);
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

        /// <summary>IUploadSource adapter：出网侧的统一 drain 词汇。</summary>
        List<ActivitySegmentItem> IUploadSource<ActivitySegmentItem>.Drain() => GetAndClearSegments();

        DeliveryRemainder IUploadSource<ActivitySegmentItem>.Remainder
        {
            get
            {
                lock (_lock)
                {
                    var durable = _segments.Values.Count(item => _durableRevisionStamps.TryGetValue(item, out _));
                    var evictedDurable = _evicted.Count(pair => pair.Value);
                    return new(durable + evictedDurable, _segments.Count - durable + _evicted.Count - evictedDurable);
                }
            }
        }

        /// <summary>
        /// IUploadSource adapter：退回批重注入（ADR-022）。legacy 项在位时保留
        /// EndTime 更晚的快照；durable Collector 投影按非序列化 Revision watermark
        /// 判断，避免较长但更旧的在途快照覆盖缩短纠错。
        /// 退回批已过一次门卫，不再校验——重过滤即丢数据，违背不蒸发不变量。
        /// </summary>
        void IUploadSource<ActivitySegmentItem>.Reinject(List<ActivitySegmentItem> items)
        {
            if (items.Count == 0) return;

            lock (_lock)
            {
                foreach (var s in items)
                {
                    if (_retractedSegmentIds.Contains(s.Id))
                        continue;
                    if (_durableRevisionStamps.TryGetValue(s, out var incomingStamp))
                    {
                        if (!_durableWatermarks.TryGetValue(s.Id, out var watermark) ||
                            watermark.Retracted || watermark.Revision != incomingStamp.Revision)
                            continue;
                        if (_segments.TryGetValue(s.Id, out var durableExisting) &&
                            _durableRevisionStamps.TryGetValue(durableExisting, out var existingStamp) &&
                            existingStamp.Revision >= incomingStamp.Revision)
                            continue;
                    }
                    else
                    {
                        if (_durableWatermarks.ContainsKey(s.Id))
                            continue;
                        if (_segments.TryGetValue(s.Id, out var existing) && existing.EndTime >= s.EndTime)
                            continue;
                    }
                    if (_segments.Count >= MaxBuffered && !_segments.ContainsKey(s.Id))
                        EvictOldest();
                    _evicted.Remove(s.Id);
                    _segments[s.Id] = s;
                }
            }
            Log.Debug("重注入退回段 {Count} 条", items.Count);
        }

        public List<ActivitySegmentItem> GetAndClearSegments()
        {
            lock (_lock)
            {
                var copy = _segments.Values.OrderBy(s => s.StartTime).ToList();
                _segments.Clear();
                return copy;
            }
        }

        /// <summary>失效安全阀（调用方必须持有 _lock）：缓冲满时丢最旧段，留痕。</summary>
        private void EvictOldest()
        {
            var oldest = _segments.Values.MinBy(s => s.StartTime)!;
            _evicted[oldest.Id] = _durableRevisionStamps.TryGetValue(oldest, out _);
            _segments.Remove(oldest.Id);
            Log.Warning("段缓冲已满（{Max} 条），丢弃最旧段 {Id}（source: {Source}）",
                MaxBuffered, oldest.Id, oldest.Source);
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
                    if (_segments.Count >= MaxBuffered && !_segments.ContainsKey(snapshot.Id))
                        EvictOldest();
                    _evicted.Remove(snapshot.Id);
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
                if (_segments.Count >= MaxBuffered && !_segments.ContainsKey(snapshot.Id))
                    EvictOldest();

                _durableWatermarks[snapshot.Id] = new DurableProjectionWatermark(revision, Retracted: false);
                _durableRevisionStamps.Remove(snapshot);
                _durableRevisionStamps.Add(snapshot, new DurableRevisionStamp(revision));
                _evicted.Remove(snapshot.Id);
                _segments[snapshot.Id] = snapshot;
            }
        }

        private void StampSourceLastSeen(List<ActivitySegmentItem> accepted) =>
            StampSourceLastSeen(accepted.Select(item => item.Source));

        private void StampSourceLastSeen(IEnumerable<string?> sources)
        {
            // 读模型盖戳（ADR-021）：per-Source last-seen 从实时流量派生，即 Active 的机制。
            var now = clock.UtcNow;
            lock (_statusLock)
            {
                foreach (var source in sources.Where(source => source is not null).Distinct())
                    _lastSeen[source!] = now;
            }
        }

        private sealed record DurableProjectionWatermark(long Revision, bool Retracted);
        private sealed class DurableRevisionStamp(long revision)
        {
            public long Revision { get; } = revision;
        }
    }
}
