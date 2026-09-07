using System.Net;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Runtime;

public class UploadWorkerShutdownTests
{
    [Fact]
    public async Task Stop_CancelsAndJoinsPeriodicUpload_BeforePersistingFinalTail()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var requests = 0;
        var cache = new Cache<ActivitySegmentItem>();
        var source = new Heartbeat.Collection.Hub.Segments.SegmentIngestService(new Heartbeat.Collection.Hub.Time.SystemClock(), cache);
        var first = new ActivitySegmentItem { Id = Guid.CreateVersion7() };
        var tail = new ActivitySegmentItem { Id = Guid.CreateVersion7() };
        source.UpsertDurable(first, 1);
        var segments = new UploadStream<ActivitySegmentItem>("段", [source], async (_, ct) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled = ct.IsCancellationRequested; }
            return ApiResult.Ok;
        });
        var inputs = new UploadStream<InputEventItem>("输入", [],
            (_, _) => Task.FromResult(ApiResult.Ok));
        var settings = new Settings { Interval = TimeSpan.Zero };
        using var http = new HttpClient();
        using var worker = new UploadWorker(segments, inputs, settings,
            new EnabledInputEventRecordingPolicy(), new DeclarationUplinkService(new HeartbeatApiClient(http), settings),
            new NullHubRuntimeHooks());

        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.UpsertDurable(tail, 1);
        await worker.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(cancelled);
        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.Equal(1, requests);
        Assert.Equal(new[] { first.Id, tail.Id }, cache.Items.Select(item => item.Id));
        Assert.Equal(cache.Items.Count, source.ReadBatch().Count);
    }

    [Fact]
    public async Task Stop_WhenDeadlineExpiresDuring422Split_PreservesBatchAndStopsSending()
    {
        using var deadline = new CancellationTokenSource();
        var handler = new RejectingHandler(deadline);
        using var http = new HttpClient(handler);
        var api = new HeartbeatApiClient(http);
        var cache = new Cache<ActivitySegmentItem>();
        var source = new Heartbeat.Collection.Hub.Segments.SegmentIngestService(new Heartbeat.Collection.Hub.Time.SystemClock(), cache);
        foreach (var item in Enumerable.Range(0, 16).Select(_ => new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(), Source = "system", IdentityKey = "mac:test",
            AppIdentityKey = "mac:test", StartTime = DateTimeOffset.UtcNow.AddDays(-3),
            EndTime = DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(1)
        })) source.UpsertDurable(item, 1);
        var segments = new UploadStream<ActivitySegmentItem>("段", [source],
            (batch, ct) => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct));
        var inputs = new UploadStream<InputEventItem>("输入", [],
            (_, _) => Task.FromResult(ApiResult.Ok));
        var settings = new Settings();
        using var worker = new UploadWorker(segments, inputs, settings,
            new EnabledInputEventRecordingPolicy(), new DeclarationUplinkService(api, settings),
            new NullHubRuntimeHooks());

        await worker.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(16, cache.Items.Count);
        Assert.Equal(cache.Items.Count, source.ReadBatch().Count);
    }

    private sealed class RejectingHandler(CancellationTokenSource deadline) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            deadline.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("Segment batch contains an invalid Id, identity, source, or time range.")
            });
        }
    }

    private sealed class Cache<T> : ICache<T>
    {
        public List<T> Items { get; private set; } = [];
        public CacheFileStatus Status => CacheFileStatus.Ready;
        public List<T> Load() => Items.ToList();
        public void Add(List<T> items) => Items.AddRange(items);
        public void Replace(List<T> items) => Items = items.ToList();
        public void Clear() => Items.Clear();
    }

    private sealed class Settings : IHubConfiguration, ICollectorDeclarationStore
    {
        public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
        public HubRuntimeSettings Current => new("", Interval, 0);
        public event Action? Changed { add { } remove { } }
        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; } =
            new Dictionary<string, CollectorRegistration>();
        public void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version) { }
    }
}
