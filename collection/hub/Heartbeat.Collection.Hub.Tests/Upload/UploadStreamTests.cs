using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Upload;
using System.Net;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Tests.Upload;

/// <summary>
/// 上传流契约（ADR-020/022）：drain 一轮 = 先重传缓存，再取 fresh 出网——
/// 送达，或落离线缓存，否则重注入源。"批次不蒸发"由流自持。
/// 经真实 HeartbeatApiClient + 桩 HttpMessageHandler 驱动，传输层（URL/负载）一并覆盖。
/// </summary>
public class UploadStreamTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"heartbeat-upload-tests-{Guid.NewGuid()}");

    public UploadStreamTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class FakeSource : IUploadSource<ActivitySegmentItem>
    {
        public List<ActivitySegmentItem> Items { get; } = [];
        public List<ActivitySegmentItem> Reinjected { get; } = [];

        public List<ActivitySegmentItem> Drain()
        {
            var copy = new List<ActivitySegmentItem>(Items);
            Items.Clear();
            return copy;
        }

        public void Reinject(List<ActivitySegmentItem> items) => Reinjected.AddRange(items);
    }

    private sealed class FakeDurableSource : IDurableUploadSource<ActivitySegmentItem>
    {
        public List<ActivitySegmentItem> Items { get; } = [];
        public List<ActivitySegmentItem> Drain() => new(Items);
        public void Reinject(List<ActivitySegmentItem> items) => Items.AddRange(items);

        public void CompleteDrain(
            IReadOnlyList<ActivitySegmentItem> drained,
            IReadOnlyList<ActivitySegmentItem> retryItems)
        {
            var drainedIds = drained.Select(item => item.Id).ToHashSet();
            var retryIds = retryItems.Select(item => item.Id).ToHashSet();
            Items.RemoveAll(item => drainedIds.Contains(item.Id) && !retryIds.Contains(item.Id));
        }
    }

    private sealed class FakeCache : ICache<ActivitySegmentItem>
    {
        public List<ActivitySegmentItem> Items { get; private set; } = [];
        public bool ThrowOnAdd { get; set; }
        public bool ThrowOnReplace { get; set; }
        public CacheFileStatus Status { get; set; } = CacheFileStatus.Ready;

        public void Add(List<ActivitySegmentItem> items)
        {
            if (ThrowOnAdd) throw new IOException("disk full");
            Items.AddRange(items);
        }

        public List<ActivitySegmentItem> Load() => new(Items);
        public void Replace(List<ActivitySegmentItem> items)
        {
            if (ThrowOnReplace) throw new IOException("replace disk full");
            Items = new(items);
        }
        public void Clear() => Items = [];
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ControlledHandler(
        Func<HttpRequestMessage, string, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" :
                await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            return respond(request, body, RequestBodies.Count);
        }
    }

    private sealed class StrictContractHandler : HttpMessageHandler
    {
        public List<(string? ProtocolVersion, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var protocol = request.Headers.TryGetValues(HeartbeatProtocol.VersionHeader, out var values)
                ? values.SingleOrDefault()
                : null;
            Requests.Add((protocol, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingDeadLetterStore<T> : IDeadLetterStore<T>
    {
        public int Count => 0;
        public string? Location => "unavailable-dead-letter.json";
        public void Append(string stream, T item, ApiResult rejection) =>
            throw new IOException("dead-letter disk full");
    }

    private sealed class FlakyDeadLetterStore<T> : IDeadLetterStore<T>
    {
        public int FailuresRemaining { get; set; } = 1;
        public int Count { get; private set; }
        public string? Location => "recovered-dead-letter.json";

        public void Append(string stream, T item, ApiResult rejection)
        {
            if (FailuresRemaining-- > 0) throw new IOException("temporarily unavailable");
            Count++;
        }
    }

    private static (UploadStream<ActivitySegmentItem> stream, FakeSource source, FakeCache cache, CapturingHandler handler) Build(HttpStatusCode status)
    {
        var handler = new CapturingHandler(status);
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var source = new FakeSource();
        var cache = new FakeCache();

        // 与 AgentHostExtensions 的段流同构：compact 策略 = KeepLatest，只作用于出缓存的批
        var stream = new UploadStream<ActivitySegmentItem>(
            "段",
            source,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            cache,
            SnapshotCompaction.KeepLatest);
        return (stream, source, cache, handler);
    }

    private static ActivitySegmentItem Segment(Guid? id = null, int endSec = 60)
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new ActivitySegmentItem
        {
            Id = id ?? Guid.CreateVersion7(),
            Source = "browser",
            IdentityKey = "https://example.com",
            StartTime = t0,
            EndTime = t0.AddSeconds(endSec)
        };
    }

    private static SegmentUploadRequest ParseBody(string body) =>
        System.Text.Json.JsonSerializer.Deserialize<SegmentUploadRequest>(
            body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private JsonFileCache<ActivitySegmentItem> RealCache(string path) =>
        new(
            path,
            maxItems: 20_000,
            new JsonCacheFileFormat<ActivitySegmentItem, JsonElement>(
                version: 1,
                item => JsonSerializer.SerializeToElement(item),
                item => item.Deserialize<ActivitySegmentItem>()!));

    private static JsonFileCache<ActivitySegmentItem> StrictSegmentCache(string path) => new(
        path,
        maxItems: 20_000,
        HeartbeatCacheFormats.SegmentVersion2(),
        HeartbeatCacheFormats.SegmentMigrations());

    private UploadStream<ActivitySegmentItem> RealStream(
        FakeSource source,
        string cachePath,
        string deadLetterPath,
        HttpMessageHandler handler,
        UploadStatusRegistry? statusRegistry = null)
    {
        var api = new HeartbeatApiClient(new HttpClient(handler));
        return new UploadStream<ActivitySegmentItem>(
            "segments",
            source,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            RealCache(cachePath),
            SnapshotCompaction.KeepLatest,
            new JsonDeadLetterStore<ActivitySegmentItem>(deadLetterPath),
            statusRegistry);
    }

    [Fact]
    public async Task Drain_Success_SendsFresh_EmptiesSource_DoesNotCache()
    {
        var (stream, source, cache, handler) = Build(HttpStatusCode.OK);
        source.Items.AddRange([Segment(), Segment()]);

        var drained = await stream.DrainAsync();

        Assert.Equal(2, drained.Count);       // 返回本轮取走的 fresh 批（图标挂点用）
        Assert.Empty(source.Items);
        Assert.Empty(source.Reinjected);
        Assert.Empty(cache.Items);
        var req = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/v1/segments", req.Url);
    }

    [Fact]
    public async Task Drain_SendFails_CachesItems_NoReinject()
    {
        var (stream, source, cache, _) = Build(HttpStatusCode.InternalServerError);
        source.Items.AddRange([Segment(), Segment()]);

        var drained = await stream.DrainAsync();

        Assert.Equal(2, drained.Count);
        Assert.Equal(2, cache.Items.Count);
        Assert.Empty(source.Reinjected);
    }

    [Fact]
    public async Task DurableSource_CommitsSuccess_AndRetainsRetryWithoutCopyingToCache()
    {
        var cache = new FakeCache();
        var source = new FakeDurableSource();
        var item = Segment();
        source.Items.Add(item);
        var attempts = 0;
        var stream = new UploadStream<ActivitySegmentItem>(
            "段",
            source,
            _ => Task.FromResult(++attempts == 1
                ? new ApiResult(false, 500)
                : ApiResult.Ok),
            cache);

        await stream.DrainAsync();
        Assert.Equal(item.Id, Assert.Single(source.Items).Id);
        Assert.Empty(cache.Items);

        await stream.DrainAsync();
        Assert.Empty(source.Items);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task Drain_DoubleFailure_ReinjectsToSource()
    {
        // 不蒸发不变量（ADR-022）：既没送达也没缓存住 → 流自己重注入源
        var (stream, source, cache, _) = Build(HttpStatusCode.InternalServerError);
        cache.ThrowOnAdd = true;
        var a = Segment();
        var b = Segment();
        source.Items.AddRange([a, b]);

        var drained = await stream.DrainAsync();

        Assert.Equal(2, drained.Count);
        Assert.Equal(2, source.Reinjected.Count);
        Assert.Same(a, source.Reinjected[0]); // 原样退回，不复制不丢字段
        Assert.Same(b, source.Reinjected[1]);
    }

    [Fact]
    public async Task Drain_EmptySourceAndCache_NoRequest()
    {
        var (stream, _, _, handler) = Build(HttpStatusCode.OK);

        var drained = await stream.DrainAsync();

        Assert.Empty(drained);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Drain_RetriesCachedFirst_ThenFresh_ClearsCache()
    {
        // cached 先于 fresh（流内局部时序，ADR-022）：离线恢复后先清积压
        var (stream, source, cache, handler) = Build(HttpStatusCode.OK);
        var cachedSeg = Segment();
        var freshSeg = Segment();
        cache.Add([cachedSeg]);
        source.Items.Add(freshSeg);

        await stream.DrainAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(cachedSeg.Id, Assert.Single(ParseBody(handler.Requests[0].Body).Segments).Id);
        Assert.Equal(freshSeg.Id, Assert.Single(ParseBody(handler.Requests[1].Body).Segments).Id);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task Drain_CachedSendFails_KeepsCache_StillDrainsFresh()
    {
        var (stream, source, cache, handler) = Build(HttpStatusCode.InternalServerError);
        cache.Add([Segment(), Segment()]);
        source.Items.Add(Segment());

        await stream.DrainAsync();

        // 缓存重传失败：保留不清（ADR-008）；fresh 照常尝试，失败后追加入缓存
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(3, cache.Items.Count);
        Assert.Empty(source.Reinjected);
    }

    [Fact]
    public async Task Drain_CompactsCachedBatchOnly_NotFresh()
    {
        // 缓存纯追加，离线期间积累同 Id 快照 → 出网前 KeepLatest（ADR-018）；
        // fresh 批来自按 Id 键控的 buffer，天然无重复，不压缩
        var (stream, source, cache, handler) = Build(HttpStatusCode.OK);
        var id = Guid.CreateVersion7();
        cache.Add([Segment(id, endSec: 30), Segment(id, endSec: 60), Segment(id, endSec: 90)]);
        source.Items.AddRange([Segment(), Segment()]);

        await stream.DrainAsync();

        Assert.Equal(2, handler.Requests.Count);
        var cachedSent = Assert.Single(ParseBody(handler.Requests[0].Body).Segments);
        Assert.Equal(id, cachedSent.Id);
        Assert.Equal(2, ParseBody(handler.Requests[1].Body).Segments.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RetryableHttpFailure_RetainsBatch_AndRestartUploadsIt(HttpStatusCode failure)
    {
        var cachePath = Path.Combine(_tempDirectory, $"retry-{(int)failure}.json");
        var deadLetterPath = cachePath + ".dead-letter.json";
        var source = new FakeSource();
        var segment = Segment();
        source.Items.Add(segment);
        var failing = new ControlledHandler((_, _, _) => new HttpResponseMessage(failure));

        await RealStream(source, cachePath, deadLetterPath, failing).DrainAsync();

        Assert.Equal(segment.Id, Assert.Single(RealCache(cachePath).Load()).Id);

        var recoveredSource = new FakeSource();
        var recovered = new ControlledHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK));
        await RealStream(recoveredSource, cachePath, deadLetterPath, recovered).DrainAsync();

        Assert.Empty(RealCache(cachePath).Load());
        Assert.Equal(segment.Id, Assert.Single(ParseBody(Assert.Single(recovered.RequestBodies)).Segments).Id);
    }

    [Fact]
    public async Task Restart_CompactsCachedSnapshotsBeforeUpload()
    {
        var cachePath = Path.Combine(_tempDirectory, "restart-compaction.json");
        var id = Guid.CreateVersion7();
        using (var cache = RealCache(cachePath))
            cache.Add([Segment(id, 30), Segment(id, 60), Segment(id, 90)]);
        var handler = new ControlledHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK));

        await RealStream(
            new FakeSource(), cachePath, cachePath + ".dead-letter.json", handler).DrainAsync();

        var uploaded = Assert.Single(ParseBody(Assert.Single(handler.RequestBodies)).Segments);
        Assert.Equal(id, uploaded.Id);
        Assert.Equal(90, (uploaded.EndTime - uploaded.StartTime).TotalSeconds);
        Assert.Empty(RealCache(cachePath).Load());
    }

    [Fact]
    public async Task MigratedLegacyCache_UploadsStrictPayloadOnce_ClearsCache_AndDoesNotReplayAgain()
    {
        var cachePath = Path.Combine(_tempDirectory, "legacy-v1-strict-replay.json");
        var id = Guid.CreateVersion7();
        const string originalIdentityKey = "Code.exe\nMain.cs\r\nraw-evidence";
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            items = new[]
            {
                new
                {
                    id,
                    source = "system",
                    identityKey = originalIdentityKey,
                    appIdentityKey = (string?)null,
                    appName = "Code.exe",
                    title = "Main.cs",
                    startTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                    endTime = DateTimeOffset.UtcNow.AddMinutes(-4),
                    attributes = (object?)null
                }
            }
        }));

        using var cache = StrictSegmentCache(cachePath);
        Assert.Equal(CacheFileState.Migrated, cache.Status.State);
        var handler = new StrictContractHandler();
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var stream = new UploadStream<ActivitySegmentItem>(
            "segments",
            new FakeSource(),
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            cache,
            SnapshotCompaction.KeepLatest);

        await stream.DrainAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HeartbeatProtocol.RequiredVersion, request.ProtocolVersion);
        using (var payload = JsonDocument.Parse(request.Body))
        {
            var item = Assert.Single(payload.RootElement.GetProperty("segments").EnumerateArray());
            Assert.Equal(id, item.GetProperty("id").GetGuid());
            Assert.Equal("win:code", item.GetProperty("appIdentityKey").GetString());
            Assert.Equal("Code.exe", item.GetProperty("appDisplayName").GetString());
            Assert.Equal(originalIdentityKey, item.GetProperty("identityKey").GetString());
            Assert.False(item.TryGetProperty("appName", out _));
        }
        Assert.Empty(cache.Load());

        await stream.DrainAsync();

        Assert.Single(handler.Requests);
        Assert.Empty(cache.Load());
    }

    [Fact]
    public async Task NetworkFailure_RetainsBatch_AndRetriesAfterRestart()
    {
        var cachePath = Path.Combine(_tempDirectory, "network.json");
        var source = new FakeSource();
        source.Items.Add(Segment());
        var networkFailure = new ControlledHandler((_, _, _) => throw new HttpRequestException("offline"));

        await RealStream(source, cachePath, cachePath + ".dead-letter.json", networkFailure).DrainAsync();

        Assert.Single(RealCache(cachePath).Load());
        var recovered = new ControlledHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK));
        await RealStream(new FakeSource(), cachePath, cachePath + ".dead-letter.json", recovered).DrainAsync();
        Assert.Empty(RealCache(cachePath).Load());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task PermanentRejection_SplitsBatch_UploadsValidItems_AndDeadLettersPoison(
        HttpStatusCode rejection)
    {
        var cachePath = Path.Combine(_tempDirectory, $"split-{(int)rejection}.json");
        var deadLetterPath = cachePath + ".dead-letter.json";
        var validA = Segment();
        var poison = Segment();
        var validB = Segment();
        var source = new FakeSource();
        source.Items.AddRange([validA, poison, validB]);
        var handler = new ControlledHandler((_, body, _) =>
        {
            var ids = ParseBody(body).Segments.Select(item => item.Id).ToList();
            return ids.Contains(poison.Id)
                ? new HttpResponseMessage(rejection)
                {
                    Content = new StringContent("invalid app identity")
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var stream = RealStream(source, cachePath, deadLetterPath, handler);
        await stream.DrainAsync();

        Assert.Empty(RealCache(cachePath).Load());
        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
        Assert.Equal(1, stream.Status.DeadLetterCount);
        using var deadLetters = JsonDocument.Parse(File.ReadAllText(deadLetterPath));
        var entry = Assert.Single(deadLetters.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal((int)rejection, entry.GetProperty("statusCode").GetInt32());
        Assert.Contains("invalid app identity", entry.GetProperty("responseBody").GetString());
        Assert.Equal(poison.Id, entry.GetProperty("item").GetProperty("id").GetGuid());

        var sentSingletons = handler.RequestBodies
            .Select(ParseBody)
            .Where(request => request.Segments.Count == 1)
            .Select(request => request.Segments[0].Id)
            .ToList();
        Assert.Contains(validA.Id, sentSingletons);
        Assert.Contains(validB.Id, sentSingletons);

        var restarted = RealStream(
            new FakeSource(), cachePath, deadLetterPath,
            new ControlledHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal(1, restarted.Status.DeadLetterCount);
        Assert.Equal(deadLetterPath, restarted.Status.DeadLetterPath);
    }

    [Fact]
    public async Task PayloadTooLarge_SplitsBatchUntilAccepted_WithoutDeadLetteringOrRetryingDeliveredItems()
    {
        var cachePath = Path.Combine(_tempDirectory, "payload-too-large.json");
        var deadLetterPath = cachePath + ".dead-letter.json";
        var source = new FakeSource();
        source.Items.AddRange([Segment(), Segment(), Segment()]);
        var handler = new ControlledHandler((_, body, _) =>
            new HttpResponseMessage(
                ParseBody(body).Segments.Count > 1
                    ? HttpStatusCode.RequestEntityTooLarge
                    : HttpStatusCode.OK));

        var stream = RealStream(source, cachePath, deadLetterPath, handler);
        await stream.DrainAsync();

        Assert.Empty(RealCache(cachePath).Load());
        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
        Assert.Equal(0, stream.Status.DeadLetterCount);
        Assert.Equal(5, handler.RequestBodies.Count);
        Assert.Equal(
            3,
            handler.RequestBodies.Count(body => ParseBody(body).Segments.Count == 1));
        Assert.False(File.Exists(deadLetterPath));
    }

    [Fact]
    public async Task UpgradeRequired_PausesStream_RetainsQueue_AndCanRecoverAfterRestart()
    {
        var cachePath = Path.Combine(_tempDirectory, "upgrade.json");
        var source = new FakeSource();
        var queued = Segment();
        source.Items.Add(queued);
        var handler = new ControlledHandler((_, _, _) =>
            new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
            {
                Content = new StringContent("install 2.0")
            });
        var registry = new UploadStatusRegistry();
        var stream = RealStream(
            source, cachePath, cachePath + ".dead-letter.json", handler, registry);

        await stream.DrainAsync();
        source.Items.Add(Segment());
        await stream.DrainAsync();

        Assert.Equal(UploadStreamState.UpdateRequired, stream.Status.State);
        Assert.Equal(UploadStreamState.UpdateRequired, registry.Snapshot["segments"].State);
        Assert.Contains("install 2.0", stream.Status.Message);
        Assert.Single(handler.RequestBodies);
        Assert.Single(source.Items);
        Assert.Equal(queued.Id, Assert.Single(RealCache(cachePath).Load()).Id);

        var recovered = new ControlledHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var restarted = RealStream(new FakeSource(), cachePath, cachePath + ".dead-letter.json", recovered);
        await restarted.DrainAsync();
        Assert.Empty(RealCache(cachePath).Load());
        Assert.Equal(UploadStreamState.Ready, restarted.Status.State);
    }

    [Fact]
    public async Task DeadLetterWriteFailure_KeepsRejectedRecordInDurableCache()
    {
        var cachePath = Path.Combine(_tempDirectory, "dead-letter-failure.json");
        var source = new FakeSource();
        var rejected = Segment();
        source.Items.Add(rejected);
        var handler = new ControlledHandler((_, _, _) =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var stream = new UploadStream<ActivitySegmentItem>(
            "segments",
            source,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            RealCache(cachePath),
            SnapshotCompaction.KeepLatest,
            new ThrowingDeadLetterStore<ActivitySegmentItem>());

        await stream.DrainAsync();

        Assert.Equal(UploadStreamState.DeadLetterWriteFailed, stream.Status.State);
        Assert.Equal(rejected.Id, Assert.Single(RealCache(cachePath).Load()).Id);
        Assert.Empty(source.Reinjected);
    }

    [Fact]
    public async Task CacheWriteFailure_StatusReturnsToReadyAfterLaterSuccessfulSend()
    {
        var source = new FakeSource();
        source.Items.Add(Segment());
        var cache = new FakeCache { ThrowOnAdd = true };
        var handler = new ControlledHandler((_, _, requestNumber) =>
            new HttpResponseMessage(requestNumber == 1
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK));
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var stream = new UploadStream<ActivitySegmentItem>(
            "segments",
            source,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            cache);

        await stream.DrainAsync();
        Assert.Equal(UploadStreamState.CacheWriteFailed, stream.Status.State);

        cache.ThrowOnAdd = false;
        source.Items.AddRange(source.Reinjected);
        source.Reinjected.Clear();
        await stream.DrainAsync();

        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
        Assert.Empty(source.Items);
        Assert.Empty(source.Reinjected);
    }

    [Fact]
    public async Task CacheReplaceFailure_StatusReturnsToReadyAfterLaterSuccessfulCommit()
    {
        var source = new FakeSource();
        var cache = new FakeCache { ThrowOnReplace = true };
        cache.Add([Segment()]);
        var stream = new UploadStream<ActivitySegmentItem>(
            "segments",
            source,
            _ => Task.FromResult(ApiResult.Ok),
            cache);

        await stream.DrainAsync();
        Assert.Equal(UploadStreamState.CacheWriteFailed, stream.Status.State);
        Assert.Single(cache.Items);

        cache.ThrowOnReplace = false;
        await stream.DrainAsync();

        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task DeadLetterWriteFailure_StatusReturnsToReadyAfterLaterDeadLetterCommit()
    {
        var source = new FakeSource();
        source.Items.Add(Segment());
        var cache = new FakeCache();
        var deadLetters = new FlakyDeadLetterStore<ActivitySegmentItem>();
        var handler = new ControlledHandler((_, _, _) =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var stream = new UploadStream<ActivitySegmentItem>(
            "segments",
            source,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            cache,
            deadLetterStore: deadLetters);

        await stream.DrainAsync();
        Assert.Equal(UploadStreamState.DeadLetterWriteFailed, stream.Status.State);
        Assert.Single(cache.Items);

        await stream.DrainAsync();

        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
        Assert.Equal(1, stream.Status.DeadLetterCount);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task FailedCacheMigration_PausesStreamBeforeDrainingFreshItems()
    {
        var cachePath = Path.Combine(_tempDirectory, "broken-cache.json");
        File.WriteAllText(cachePath, "{\"schemaVersion\":999,\"items\":[]}");
        var source = new FakeSource();
        source.Items.Add(Segment());
        var handler = new ControlledHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK));

        var stream = RealStream(source, cachePath, cachePath + ".dead-letter.json", handler);
        await stream.DrainAsync();

        Assert.Equal(UploadStreamState.CacheMigrationFailed, stream.Status.State);
        Assert.Contains("restore", stream.Status.Action, StringComparison.OrdinalIgnoreCase);
        Assert.Single(source.Items);
        Assert.Empty(handler.RequestBodies);
    }
}
