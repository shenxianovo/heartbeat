using System.Net;
using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Upload;

public class UploadStreamTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"heartbeat-upload-tests-{Guid.NewGuid()}");
    public UploadStreamTests() => Directory.CreateDirectory(_tempDirectory);
    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);
    private sealed class FakeCache : ICache<ActivitySegmentItem>
    {
        public List<ActivitySegmentItem> Items { get; private set; } = [];
        public bool ThrowOnReplace { get; set; }
        public CacheFileStatus Status { get; set; } = CacheFileStatus.Ready;

        public void Add(List<ActivitySegmentItem> items)
        {
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

    private UploadStream<ActivitySegmentItem> Stream(
        SegmentIngestService source, HttpMessageHandler handler,
        IDeadLetterStore<ActivitySegmentItem>? deadLetters = null)
    {
        var api = new HeartbeatApiClient(new HttpClient(handler));
        return new("segments", [source],
            (batch, ct) => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct),
            deadLetters ?? new JsonDeadLetterStore<ActivitySegmentItem>(Path.Combine(_tempDirectory, "dead-letter.json")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RetryableFailure_RetainsBatch_AndRestartUploadsStableIds(HttpStatusCode failure)
    {
        var path = Path.Combine(_tempDirectory, "segments.json");
        using var cache = RealCache(path);
        var source = new SegmentIngestService(new SystemClock(), cache);
        var item = Segment();
        source.Accept([item]);
        var result = await Stream(source, new CapturingHandler(failure)).DrainAsync();
        Assert.Equal(0, result.DeliveredCount);
        Assert.Equal(new DeliveryRemainder(1, 0), result.Remainder);
        using var recovered = RealCache(path);
        var restart = new SegmentIngestService(new SystemClock(), recovered);
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var delivered = await Stream(restart, handler).DrainAsync();
        Assert.Equal(item.Id, Assert.Single(ParseBody(Assert.Single(handler.Requests).Body).Segments!).Id);
        Assert.Equal(1, delivered.DeliveredCount);
        Assert.True(delivered.Remainder.IsEmpty);
        Assert.Empty(recovered.Load());
    }

    [Fact]
    public async Task UnexpectedTransportException_PreservesBatch_AndLaterSuccessClearsEvidence()
    {
        var source = new SegmentIngestService(new SystemClock());
        var item = Segment();
        source.Accept([item]);
        var fail = true;
        var stream = new UploadStream<ActivitySegmentItem>("segments", [source], (_, _) =>
            fail ? throw new InvalidOperationException("unexpected transport failure") : Task.FromResult(ApiResult.Ok));
        var failed = await stream.DrainAsync();
        Assert.Equal(new DeliveryRemainder(0, 1), failed.Remainder);
        Assert.Same(item, Assert.Single(source.ReadBatch()));
        fail = false;
        Assert.True((await stream.DrainAsync()).Remainder.IsEmpty);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task PermanentRejection_ConfirmsValidItems_AndQuarantinesOnlyPoison(HttpStatusCode rejection)
    {
        var source = new SegmentIngestService(new SystemClock());
        var good = Segment();
        var poison = Segment();
        source.Accept([good, poison]);
        var handler = new ControlledHandler((_, body, _) => new HttpResponseMessage(
            ParseBody(body).Segments!.Any(item => item.Id == poison.Id) ? rejection : HttpStatusCode.OK));
        var stream = Stream(source, handler);
        var result = await stream.DrainAsync();
        Assert.Equal(1, result.DeliveredCount);
        Assert.Equal(new DeliveryRemainder(1, 0), result.Remainder);
        Assert.Equal(1, stream.Status.DeadLetterCount);
        Assert.Empty(source.ReadBatch());
        Assert.Equal(3, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task PayloadTooLarge_SplitsUntilAccepted_WithoutRetryingSuccessfulItems()
    {
        var source = new SegmentIngestService(new SystemClock());
        source.Accept(Enumerable.Range(0, 8).Select(_ => Segment()).ToList());
        var handler = new ControlledHandler((_, body, _) => new HttpResponseMessage(
            ParseBody(body).Segments!.Count > 2 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.OK));
        var stream = Stream(source, handler);
        var result = await stream.DrainAsync();
        Assert.Equal(8, result.DeliveredCount);
        Assert.True(result.Remainder.IsEmpty);
        Assert.Equal(7, handler.RequestBodies.Count);
        await stream.DrainAsync();
        Assert.Equal(7, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task OversizedSingleRecord_RemainsPending()
    {
        var source = new SegmentIngestService(new SystemClock());
        source.Accept([Segment()]);
        var result = await Stream(source, new CapturingHandler(HttpStatusCode.RequestEntityTooLarge)).DrainAsync();
        Assert.Equal(0, result.DeliveredCount);
        Assert.Equal(new DeliveryRemainder(0, 1), result.Remainder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Split_CancellationOrPause_PreservesEarlierSuccessAndUnsentTail(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var source = new SegmentIngestService(new SystemClock());
        var items = Enumerable.Range(0, 4).Select(_ => Segment()).ToList();
        source.Accept(items);
        var calls = 0;
        var stream = new UploadStream<ActivitySegmentItem>("segments", [source], (batch, _) =>
        {
            calls++;
            if (batch.Count > 1) return Task.FromResult(new ApiResult(false, 422));
            if (batch[0].Id == items[0].Id) return Task.FromResult(ApiResult.Ok);
            if (cancel) cts.Cancel();
            return Task.FromResult(new ApiResult(false, cancel ? 503 : 426));
        });
        var result = await stream.DrainAsync(cts.Token);
        Assert.Equal(1, result.DeliveredCount);
        Assert.Equal(3, result.Remainder.Unconfirmed);
        Assert.Equal(items.Skip(1).Select(item => item.Id), source.ReadBatch().Select(item => item.Id));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Pause_PersistsNewArrivals_AndRestartResumes()
    {
        var path = Path.Combine(_tempDirectory, "paused.json");
        using var cache = RealCache(path);
        var source = new SegmentIngestService(new SystemClock(), cache);
        source.Accept([Segment()]);
        var handler = new CapturingHandler(HttpStatusCode.UpgradeRequired);
        var stream = Stream(source, handler);
        await stream.DrainAsync();
        source.Accept([Segment()]);
        Assert.Equal(new DeliveryRemainder(2, 0), (await stream.DrainAsync()).Remainder);
        Assert.Single(handler.Requests);
        using var recovered = RealCache(path);
        var restarted = Stream(new SegmentIngestService(new SystemClock(), recovered), new CapturingHandler(HttpStatusCode.OK));
        Assert.True((await restarted.DrainAsync()).Remainder.IsEmpty);
    }

    [Fact]
    public async Task Confirmation_DoesNotRemoveCorrectionArrivingDuringSend()
    {
        var cache = new FakeCache();
        var source = new SegmentIngestService(new SystemClock(), cache);
        var original = Segment(endSec: 120);
        var corrected = Segment(original.Id, endSec: 30);
        source.UpsertDurable(original, 1);
        var stream = new UploadStream<ActivitySegmentItem>("segments", [source], (_, _) =>
        {
            source.UpsertDurable(corrected, 2);
            return Task.FromResult(ApiResult.Ok);
        });
        var result = await stream.DrainAsync();
        Assert.Equal(1, result.DeliveredCount);
        Assert.Equal(new DeliveryRemainder(1, 0), result.Remainder);
        Assert.Same(corrected, Assert.Single(cache.Load()));
        Assert.Same(corrected, Assert.Single(source.ReadBatch()));
    }

    [Fact]
    public async Task CacheFailure_KeepsMemoryCustody_AndRecoversAfterRepair()
    {
        var cache = new FakeCache { ThrowOnReplace = true };
        var source = new SegmentIngestService(new SystemClock(), cache);
        var item = Segment();
        source.Accept([item]);
        var stream = Stream(source, new CapturingHandler(HttpStatusCode.ServiceUnavailable));
        var failed = await stream.DrainAsync();
        Assert.Equal(new DeliveryRemainder(0, 1), failed.Remainder);
        Assert.Equal(UploadStreamState.CacheWriteFailed, stream.Status.State);
        cache.ThrowOnReplace = false;
        var recovered = await stream.DrainAsync();
        Assert.Equal(new DeliveryRemainder(1, 0), recovered.Remainder);
        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
    }

    [Fact]
    public async Task ConfirmationWriteFailure_ReplaysSuccessfulBatchUntilCommitSucceeds()
    {
        var cache = new FakeCache();
        var source = new SegmentIngestService(new SystemClock(), cache);
        source.Accept([Segment()]);
        var fail = true;
        var stream = new UploadStream<ActivitySegmentItem>("segments", [source], (_, _) =>
        {
            cache.ThrowOnReplace = fail;
            return Task.FromResult(ApiResult.Ok);
        });
        var result = await stream.DrainAsync();
        Assert.Equal(1, result.DeliveredCount);
        Assert.NotNull(result.Error);
        Assert.Equal(new DeliveryRemainder(1, 0), result.Remainder);
        fail = false;
        cache.ThrowOnReplace = false;
        Assert.True((await stream.DrainAsync()).Remainder.IsEmpty);
        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
    }

    [Fact]
    public async Task DeadLetterFailure_RetainsRejectedRecord_AndRecoversAfterRepair()
    {
        var source = new SegmentIngestService(new SystemClock(), new FakeCache());
        source.Accept([Segment()]);
        var deadLetters = new FlakyDeadLetterStore<ActivitySegmentItem>();
        var stream = Stream(source, new CapturingHandler(HttpStatusCode.UnprocessableEntity), deadLetters);
        Assert.Equal(new DeliveryRemainder(1, 0), (await stream.DrainAsync()).Remainder);
        Assert.Equal(UploadStreamState.DeadLetterWriteFailed, stream.Status.State);
        await stream.DrainAsync();
        Assert.Equal(UploadStreamState.Ready, stream.Status.State);
        Assert.Equal(1, stream.Status.DeadLetterCount);
        Assert.Empty(source.ReadBatch());
    }

    [Fact]
    public async Task FailedMigration_PausesWithoutSendingOrDiscardingFreshData()
    {
        var path = Path.Combine(_tempDirectory, "broken.json");
        File.WriteAllText(path, "{\"schemaVersion\":999,\"items\":[]}");
        using var cache = RealCache(path);
        var source = new SegmentIngestService(new SystemClock(), cache);
        source.Accept([Segment()]);
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var stream = Stream(source, handler);
        var result = await stream.DrainAsync();
        Assert.Equal(UploadStreamState.CacheMigrationFailed, stream.Status.State);
        Assert.Null(result.Remainder.Unconfirmed);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Restart_CompactsHistoricalSnapshots_AndCombinesFreshData()
    {
        var cache = new FakeCache();
        var original = Segment(endSec: 20);
        var later = Segment(original.Id, endSec: 80);
        cache.Add([original, later]);
        var source = new SegmentIngestService(new SystemClock(), cache);
        source.Accept([Segment()]);
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var result = await Stream(source, handler).DrainAsync();
        Assert.Equal(2, result.DeliveredCount);
        Assert.Equal(2, ParseBody(Assert.Single(handler.Requests).Body).Segments!.Count);
        Assert.True(result.Remainder.IsEmpty);
    }

    [Fact]
    public async Task HistoricalInputStyleCache_UsesSameConfirmationContract_WithoutReplayingSuccess()
    {
        var cache = new FakeCache();
        cache.Add([Segment()]);
        var source = new CachedUploadSource<ActivitySegmentItem>(cache);
        var calls = 0;
        var stream = new UploadStream<ActivitySegmentItem>("historical", [source], (_, _) =>
        {
            calls++;
            return Task.FromResult(ApiResult.Ok);
        });
        Assert.True((await stream.DrainAsync()).Remainder.IsEmpty);
        Assert.True((await stream.DrainAsync()).Remainder.IsEmpty);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task EarlierSourceConfirmationFailure_IsNotClearedByLaterHealthySource()
    {
        var cache = new FakeCache();
        cache.Add([Segment()]);
        var first = new CachedUploadSource<ActivitySegmentItem>(cache);
        var second = new SegmentIngestService(new SystemClock());
        var stream = new UploadStream<ActivitySegmentItem>("segments", [first, second], (_, _) =>
        {
            cache.ThrowOnReplace = true;
            return Task.FromResult(ApiResult.Ok);
        });
        var result = await stream.DrainAsync();
        Assert.NotNull(result.Error);
        Assert.Equal(UploadStreamState.CacheWriteFailed, stream.Status.State);
        cache.ThrowOnReplace = false;
        var recovered = new UploadStream<ActivitySegmentItem>("segments", [first, second], (_, _) => Task.FromResult(ApiResult.Ok));
        Assert.True((await recovered.DrainAsync()).Remainder.IsEmpty);
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
            [new SegmentIngestService(new SystemClock(), cache)],
            (batch, ct) => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct));

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

}
