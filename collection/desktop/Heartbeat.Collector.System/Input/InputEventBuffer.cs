using System.Collections.Concurrent;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collector.System.Collection;

namespace Heartbeat.Collector.System.Input
{
    public sealed class InputEventCapacityExceededException(int capacity, int count) : Exception(
        $"Durable InputEvent projection is applying backpressure at {count}/{capacity} events.")
    {
        public int Capacity { get; } = capacity;
        public int Count { get; } = count;
    }

    /// <summary>
    /// 输入事件的归一化与 legacy upload 缓冲（不含平台钩子，便于单测）。详见 ADR-012/041。
    ///
    /// 职责：
    /// - 过滤长按自动重复（同一键在 KeyUp 之前的重复 KeyDown 丢弃）
    /// - 滚轮碎 delta 累加归一为整档（±120 = 一档）
    /// - 生产观察经 system Collector Protocol 发布，Hub 提交后投影回 legacy upload 缓冲
    /// - durable projection 是待上传 InputEvent 的唯一容量 owner；满容量返回可判定 backpressure
    /// - 为每个事件生成 UUIDv7
    /// </summary>
    public sealed class InputEventBuffer :
        IDurableUploadSource<InputEventItem>,
        IInputEventFactSink,
        IInputEventFactReplaySink
    {
        public const int WheelDelta = 120;
        public const int UploadBatchSize = 5_000;
        public const string StatusStreamName = "输入事件 durable projection";

        private readonly IClock _clock;
        private readonly ISystemInputEventPublisher? _publisher;
        private readonly int _capacity;
        private readonly UploadStatusRegistry? _statusRegistry;
        private readonly JsonFileCache<InputEventItem>? _durableProjectionCache;
        private readonly JsonFileCache<Guid>? _deliveryReceiptCache;
        private readonly HashSet<Guid> _deliveredIds = [];
        private readonly object _durableGate = new();

        private readonly ConcurrentQueue<InputEventItem> _queue = new();
        private int _count;

        // 按住状态：记录当前处于按下状态的物理键位置，用于过滤自动重复
        private readonly HashSet<short> _heldKeys = [];
        private readonly object _heldLock = new();

        // 滚轮累计 delta（按方向分别累计余量）
        private int _scrollAccum;
        private readonly object _scrollLock = new();

        public InputEventBuffer(
            IClock clock,
            int capacity = 100_000,
            ISystemInputEventPublisher? publisher = null,
            string? durableProjectionPath = null,
            UploadStatusRegistry? statusRegistry = null)
        {
            ArgumentNullException.ThrowIfNull(clock);
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _clock = clock;
            _publisher = publisher;
            _capacity = capacity;
            _statusRegistry = statusRegistry;
            if (!string.IsNullOrWhiteSpace(durableProjectionPath))
            {
                _durableProjectionCache = new JsonFileCache<InputEventItem>(
                    durableProjectionPath,
                    int.MaxValue,
                    HeartbeatCacheFormats.InputEventVersion2(),
                    HeartbeatCacheFormats.InputEventMigrations());
                _count = _durableProjectionCache.Load().Count;

                var receiptPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(durableProjectionPath))!,
                    $"{Path.GetFileNameWithoutExtension(durableProjectionPath)}-delivery-receipts.json");
                var receiptCache = new JsonFileCache<Guid>(
                    receiptPath,
                    capacity,
                    HeartbeatCacheFormats.InputEventDeliveryReceiptVersion1());
                if (receiptCache.Status.State == CacheFileState.MigrationFailed)
                {
                    receiptCache.Dispose();
                }
                else
                {
                    _deliveryReceiptCache = receiptCache;
                    _deliveredIds.UnionWith(receiptCache.Load());
                }
            }
            UpdateStatus(_count);
        }

        public int Count => Volatile.Read(ref _count);
        public int Capacity => _capacity;
        public bool IsBackpressured => Count >= _capacity;
        DeliveryRemainder IUploadSource<InputEventItem>.Remainder => _durableProjectionCache is null
            ? new(0, Count)
            : new(Count, 0);

        /// <summary>键盘按下。返回是否记录了事件（自动重复会被丢弃）。</summary>
        public bool OnKeyDown(InputKeyPosition position)
        {
            var code = (short)position;
            lock (_heldLock)
            {
                if (!_heldKeys.Add(code))
                    return false; // 已按住 → 自动重复，丢弃
            }

            Enqueue(InputEventType.KeyDown, code);
            return true;
        }

        /// <summary>键盘抬起。仅解除按住状态，不落盘。</summary>
        public void OnKeyUp(InputKeyPosition position)
        {
            lock (_heldLock)
            {
                _heldKeys.Remove((short)position);
            }
        }

        /// <summary>鼠标按钮按下。code: 1=左 2=右 3=中。</summary>
        public void OnMouseButton(short code)
        {
            Enqueue(InputEventType.MouseButton, code);
        }

        /// <summary>
        /// 滚轮原始 delta（来自 WM_MOUSEWHEEL，通常 ±120 的倍数，触摸板可能更碎）。
        /// 累加后每满一档（±120）记一个事件，余量保留。
        /// </summary>
        public void OnScroll(int rawDelta)
        {
            int notches;
            lock (_scrollLock)
            {
                _scrollAccum += rawDelta;
                notches = _scrollAccum / WheelDelta;
                _scrollAccum -= notches * WheelDelta;
            }

            if (notches == 0) return;

            // notches > 0 上滚(1)，< 0 下滚(2)
            short code = notches > 0 ? (short)1 : (short)2;
            int abs = Math.Abs(notches);
            for (int i = 0; i < abs; i++)
                Enqueue(InputEventType.MouseScroll, code);
        }

        /// <summary>录制关闭时清空仅内存的 repeat / 精细滚轮状态，不触碰已生成事件。</summary>
        public void ResetTransientState()
        {
            lock (_heldLock)
            {
                _heldKeys.Clear();
            }

            lock (_scrollLock)
            {
                _scrollAccum = 0;
            }
        }

        /// <summary>
        /// 读取当前所有事件；内存测试模式会立即清空，生产持久模式等待 UploadStream 提交 drain 结果。
        /// </summary>
        public List<InputEventItem> DrainAll()
        {
            if (_durableProjectionCache is not null)
            {
                lock (_durableGate)
                    return _durableProjectionCache.Load();
            }
            lock (_durableGate)
            {
                var result = new List<InputEventItem>();
                while (_queue.TryDequeue(out var item))
                {
                    _count--;
                    result.Add(item);
                }
                UpdateStatus(_count);
                return result;
            }
        }

        private List<InputEventItem> DrainUploadBatch()
        {
            if (_durableProjectionCache is not null)
            {
                lock (_durableGate)
                    return _durableProjectionCache.Load().Take(UploadBatchSize).ToList();
            }

            lock (_durableGate)
            {
                var result = new List<InputEventItem>(Math.Min(Count, UploadBatchSize));
                while (result.Count < UploadBatchSize && _queue.TryDequeue(out var item))
                {
                    _count--;
                    result.Add(item);
                }
                UpdateStatus(_count);
                return result;
            }
        }

        /// <summary>
        /// 退回重注入（ADR-020 上传通道契约）：既没送达也没缓存住的批原样回队，
        /// 保留原 Id——服务端按 Id 幂等去重，重复注入不产生重复行。
        /// </summary>
        public void Requeue(List<InputEventItem> items)
        {
            foreach (var item in items)
                EnqueueItem(item);
        }

        /// <summary>IUploadSource adapter：出网侧的统一 drain 词汇。</summary>
        List<InputEventItem> IUploadSource<InputEventItem>.Drain() => DrainUploadBatch();

        /// <summary>IUploadSource adapter：退回批保 Id 回队。</summary>
        void IUploadSource<InputEventItem>.Reinject(List<InputEventItem> items) => Requeue(items);

        void IDurableUploadSource<InputEventItem>.CompleteDrain(
            IReadOnlyList<InputEventItem> drained,
            IReadOnlyList<InputEventItem> retryItems)
        {
            if (_durableProjectionCache is null)
            {
                Requeue(retryItems.ToList());
                return;
            }

            var drainedIds = drained.Select(item => item.Id).ToHashSet();
            var retryIds = retryItems.Select(item => item.Id).ToHashSet();
            lock (_durableGate)
            {
                var retained = _durableProjectionCache.Load()
                    .Where(item => !drainedIds.Contains(item.Id) || retryIds.Contains(item.Id))
                    .ToList();
                _durableProjectionCache.Replace(retained);
                Volatile.Write(ref _count, retained.Count);
                UpdateStatus(retained.Count);

                if (_deliveryReceiptCache is not null)
                {
                    var delivered = drainedIds
                        .Where(id => !retryIds.Contains(id) && !_deliveredIds.Contains(id))
                        .ToList();
                    if (delivered.Count > 0)
                    {
                        var receipts = _deliveryReceiptCache.Load();
                        receipts.AddRange(delivered);
                        _deliveryReceiptCache.Replace(receipts.TakeLast(_capacity).ToList());
                        _deliveredIds.Clear();
                        _deliveredIds.UnionWith(_deliveryReceiptCache.Load());
                    }
                }
            }
        }

        bool IInputEventFactSink.TryAccept(
            InputEventItem item,
            bool isReplay,
            ICollectorProjectionCommitFence commitFence) => TryEnqueueItem(item, isReplay, commitFence);

        void IInputEventFactReplaySink.Replay(IReadOnlyList<InputEventItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count == 0)
                return;

            lock (_durableGate)
            {
                var retained = _durableProjectionCache?.Load() ?? _queue.ToList();
                var retainedIds = retained.Select(item => item.Id).ToHashSet();
                var initialCount = retained.Count;
                var rejected = false;

                foreach (var item in items)
                {
                    if (retainedIds.Contains(item.Id) || _deliveredIds.Contains(item.Id))
                        continue;
                    if (retained.Count >= _capacity)
                    {
                        rejected = true;
                        continue;
                    }
                    retainedIds.Add(item.Id);
                    retained.Add(item);
                }

                if (retained.Count != initialCount)
                {
                    if (_durableProjectionCache is not null)
                        _durableProjectionCache.Replace(retained);
                    else
                    {
                        foreach (var item in retained.Skip(initialCount))
                            _queue.Enqueue(item);
                    }
                }
                Volatile.Write(ref _count, retained.Count);
                UpdateStatus(retained.Count);

                if (rejected)
                    throw new InputEventCapacityExceededException(_capacity, retained.Count);
            }
        }

        private void Enqueue(InputEventType type, short code)
        {
            var item = new InputEventItem
            {
                Id = Guid.CreateVersion7(),
                EventType = type,
                CodeSet = InputCodeSets.HeartbeatKeyPositionV1,
                Code = code,
                Timestamp = _clock.UtcNow
            };
            if (_publisher is null)
                EnqueueItem(item);
            else
                _publisher.Publish(item);
        }

        private void EnqueueItem(InputEventItem item)
        {
            if (!TryEnqueueItem(
                    item,
                    isReplay: false,
                    commitFence: UnfencedInputEventCommitFence.Instance))
                throw new InvalidOperationException("The local InputEvent enqueue was unexpectedly fenced.");
        }

        private bool TryEnqueueItem(
            InputEventItem item,
            bool isReplay,
            ICollectorProjectionCommitFence commitFence)
        {
            ArgumentNullException.ThrowIfNull(commitFence);
            lock (_durableGate)
            {
                if (_durableProjectionCache is not null)
                {
                    var retained = _durableProjectionCache.Load();
                    if (retained.Any(existing => existing.Id == item.Id))
                        return true;
                    if (isReplay && _deliveredIds.Contains(item.Id))
                        return true;
                    if (retained.Count >= _capacity)
                    {
                        UpdateStatus(retained.Count);
                        throw new InputEventCapacityExceededException(_capacity, retained.Count);
                    }
                    retained.Add(item);
                    using var replacement = _durableProjectionCache.PrepareReplacement(retained);
                    if (!commitFence.TryPublishFile(
                            replacement.TemporaryPath,
                            replacement.DestinationPath))
                        return false;
                    _durableProjectionCache.AcceptPublishedReplacement(replacement);
                    Volatile.Write(ref _count, retained.Count);
                    UpdateStatus(retained.Count);
                    return true;
                }

                if (_count >= _capacity)
                {
                    UpdateStatus(_count);
                    throw new InputEventCapacityExceededException(_capacity, _count);
                }
                if (commitFence.IsFenced)
                    return false;
                _queue.Enqueue(item);
                _count++;
                UpdateStatus(_count);
                return true;
            }
        }

        private sealed class UnfencedInputEventCommitFence : ICollectorProjectionCommitFence
        {
            public static UnfencedInputEventCommitFence Instance { get; } = new();

            public bool IsFenced => false;

            public bool TryPublishFile(string preparedPath, string authoritativePath)
            {
                File.Move(preparedPath, authoritativePath, overwrite: true);
                return true;
            }
        }

        private void UpdateStatus(int count)
        {
            if (_statusRegistry is null)
                return;
            var status = count switch
            {
                0 => UploadStreamStatus.Ready,
                _ when count >= _capacity => new UploadStreamStatus(
                    UploadStreamState.Backpressure,
                    $"Durable InputEvent backlog reached capacity ({count}/{_capacity}).",
                    "Allow InputEvent upload to drain or repair its upload path; retained events will retry."),
                _ => new UploadStreamStatus(
                    UploadStreamState.Backlog,
                    $"Durable InputEvent backlog: {count}/{_capacity}.")
            };
            _statusRegistry.Update(StatusStreamName, status);
        }
    }
}
