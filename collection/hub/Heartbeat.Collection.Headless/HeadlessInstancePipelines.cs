using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Headless;

internal interface IHeadlessSegmentUpload : IDisposable
{
    Task<ApiResult> SendAsync(
        Guid collectorInstanceId,
        SubjectReference subject,
        string displayName,
        List<ActivitySegmentItem> batch, CancellationToken cancellationToken = default);

    void Remove(Guid collectorInstanceId);
}

/// <summary>
/// Owns every per-Instance projection, current-activity view, durable upload stream and local
/// storage lifecycle. The Fleet only registers Instances, reads their status and drains the module.
/// </summary>
internal sealed class HeadlessInstancePipelines(
    string dataDirectory,
    IHeadlessSegmentUpload segmentUpload) :
    ISegmentSink,
    ISubjectSegmentProjectionSink,
    IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pipeline> _pipelines = [];
    private readonly SemaphoreSlim _uploads = new(1, 1);
    private IReadOnlyDictionary<Guid, UploadDrainResult<ActivitySegmentItem>?>? _terminalUploads;

    public IReadOnlyDictionary<Guid, UploadDrainResult<ActivitySegmentItem>?> UploadResults
    {
        get { lock (_gate) return _terminalUploads ?? _pipelines.ToDictionary(pair => pair.Key, pair => pair.Value.LastDrain); }
    }

    public void Add(Guid collectorInstanceId, SubjectReference subject, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        lock (_gate)
        {
            if (_pipelines.TryGetValue(collectorInstanceId, out var existing))
            {
                existing.Configure(subject, displayName);
                return;
            }
        }
        var directory = Path.Combine(dataDirectory, "instances", collectorInstanceId.ToString("D"));
        Directory.CreateDirectory(directory);
        var registration = new PipelineRegistration(subject, displayName);
        var ingest = new SegmentIngestService(new SystemClock());
        var cache = new JsonFileCache<ActivitySegmentItem>(
            Path.Combine(directory, "segments-cache.json"),
            20_000,
            HeartbeatCacheFormats.SegmentVersion2(),
            HeartbeatCacheFormats.SegmentMigrations());
        var upload = new UploadStream<ActivitySegmentItem>(
            $"段/{collectorInstanceId:D}",
            ingest,
            (batch, ct) => segmentUpload.SendAsync(
                collectorInstanceId,
                registration.Subject,
                registration.DisplayName,
                batch, ct),
            cache,
            SnapshotCompaction.KeepLatest,
            new JsonDeadLetterStore<ActivitySegmentItem>(
                Path.Combine(directory, "segments-dead-letter.json")),
            new UploadStatusRegistry(),
            new ClientCompatibilityStatus());
        var pipeline = new Pipeline(directory, registration, ingest, cache, upload);
        lock (_gate)
        {
            if (!_pipelines.TryAdd(collectorInstanceId, pipeline))
            {
                pipeline.Dispose();
                throw new InvalidOperationException(
                    $"Collector Instance '{collectorInstanceId:D}' already has a projection pipeline.");
            }
        }
    }

    public async Task RemoveAsync(Guid collectorInstanceId)
    {
        await _uploads.WaitAsync().ConfigureAwait(false);
        try
        {
            Pipeline? pipeline;
            lock (_gate) _pipelines.TryGetValue(collectorInstanceId, out pipeline);
            if (pipeline is null) return;
            var result = await pipeline.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            if (!result.Remainder.IsEmpty)
                throw new InvalidOperationException("Collector data remains undelivered; retained the Instance for retry.");
            if (Directory.Exists(pipeline.Directory))
                Directory.Delete(pipeline.Directory, recursive: true);
            segmentUpload.Remove(collectorInstanceId);
            pipeline.Dispose();
            lock (_gate) _pipelines.Remove(collectorInstanceId);
        }
        finally { _uploads.Release(); }
    }

    public HeadlessCurrentSubjectActivity? CurrentActivity(Guid collectorInstanceId) =>
        Required(collectorInstanceId).CurrentActivity;

    public async Task DrainAllAsync(CancellationToken cancellationToken = default)
    {
        // Joining local custody is mandatory even after the network budget expires.
        await _uploads.WaitAsync().ConfigureAwait(false);
        try
        {
            Pipeline[] pipelines;
            lock (_gate) pipelines = _pipelines.Values.ToArray();
            foreach (var pipeline in pipelines)
                await pipeline.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _uploads.Release(); }
    }

    public void Push(List<ActivitySegmentItem> snapshots) =>
        throw new NotSupportedException("Multi-Subject projection requires Collector Instance context.");

    public void UpsertDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal) => Required(context).Upsert(snapshot, revision, isFinal);

    public void ReplayDurable(
        CollectorProjectionContext context,
        ActivitySegmentItem snapshot,
        long revision,
        bool isFinal) => Required(context).Replay(snapshot, revision, isFinal);

    public void RetractDurable(
        CollectorProjectionContext context,
        Guid segmentId,
        long revision) => Required(context).Retract(segmentId, revision);

    public void Dispose()
    {
        Pipeline[] pipelines;
        lock (_gate)
        {
            if (_terminalUploads is not null) return;
            _terminalUploads = UploadResults;
            pipelines = _pipelines.Values.ToArray();
            _pipelines.Clear();
        }
        foreach (var pipeline in pipelines)
            pipeline.Dispose();
        segmentUpload.Dispose();
        _uploads.Dispose();
    }

    private Pipeline Required(Guid collectorInstanceId)
    {
        lock (_gate)
            return _pipelines.TryGetValue(collectorInstanceId, out var pipeline)
                ? pipeline
                : throw new KeyNotFoundException(
                    $"Collector Instance '{collectorInstanceId:D}' has no projection pipeline.");
    }

    private Pipeline Required(CollectorProjectionContext context)
    {
        lock (_gate)
        {
            if (_pipelines.TryGetValue(context.CollectorInstanceId, out var pipeline))
                return pipeline;
        }
        Add(context.CollectorInstanceId, context.Subject, $"Collector {context.CollectorInstanceId:D}");
        return Required(context.CollectorInstanceId);
    }

    private sealed class PipelineRegistration(SubjectReference subject, string displayName)
    {
        private readonly object _gate = new();
        private SubjectReference _subject = subject;
        private string _displayName = displayName;

        public SubjectReference Subject { get { lock (_gate) return _subject; } }
        public string DisplayName { get { lock (_gate) return _displayName; } }

        public void Configure(SubjectReference nextSubject, string nextDisplayName)
        {
            lock (_gate)
            {
                _subject = nextSubject;
                _displayName = nextDisplayName;
            }
        }
    }

    private sealed class Pipeline(
        string directory,
        PipelineRegistration registration,
        SegmentIngestService ingest,
        JsonFileCache<ActivitySegmentItem> cache,
        UploadStream<ActivitySegmentItem> upload) : IDisposable
    {
        private readonly object _gate = new();
        private HeadlessCurrentSubjectActivity? _current;
        private Guid? _currentSegmentId;

        public string Directory { get; } = directory;

        public void Configure(SubjectReference subject, string displayName) =>
            registration.Configure(subject, displayName);

        public HeadlessCurrentSubjectActivity? CurrentActivity
        {
            get { lock (_gate) return _current; }
        }

        public void Upsert(ActivitySegmentItem item, long revision, bool isFinal)
        {
            ingest.UpsertDurable(item, revision);
            Observe(item, isFinal);
        }

        public void Replay(ActivitySegmentItem item, long revision, bool isFinal)
        {
            ingest.ReplayDurable(item, revision);
            Observe(item, isFinal);
        }

        public void Retract(Guid segmentId, long revision)
        {
            ingest.RetractDurable(segmentId, revision);
            lock (_gate)
            {
                if (_currentSegmentId != segmentId)
                    return;
                _current = null;
                _currentSegmentId = null;
            }
        }

        public UploadDrainResult<ActivitySegmentItem>? LastDrain => upload.LastDrain;
        public Task<UploadDrainResult<ActivitySegmentItem>> DrainAsync(CancellationToken cancellationToken) =>
            upload.DrainAsync(cancellationToken);

        public void Dispose() => cache.Dispose();

        private void Observe(ActivitySegmentItem item, bool isFinal)
        {
            lock (_gate)
            {
                if (_current is not null && item.StartTime < _current.StartTime)
                    return;
                if (isFinal)
                {
                    if (_currentSegmentId == item.Id)
                    {
                        _current = null;
                        _currentSegmentId = null;
                    }
                    return;
                }
                _current = new HeadlessCurrentSubjectActivity(
                    item.Title,
                    item.IdentityKey,
                    item.StartTime,
                    item.EndTime,
                    item.Attributes);
                _currentSegmentId = item.Id;
            }
        }
    }
}

internal sealed class HeadlessAnalyticsSegmentUploadAdapter(TokenManager tokens) : IHeadlessSegmentUpload
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HttpClient> _clients = [];

    public Task<ApiResult> SendAsync(
        Guid collectorInstanceId,
        SubjectReference subject,
        string displayName,
        List<ActivitySegmentItem> batch, CancellationToken cancellationToken = default)
    {
        HttpClient http;
        lock (_gate)
        {
            if (!_clients.TryGetValue(collectorInstanceId, out http!))
            {
                var identity = new FixedSubjectIdentity(
                    $"subject:{subject.Kind.ToString().ToLowerInvariant()}:{subject.SubjectId:D}",
                    displayName);
                var handler = new BearerTokenHandler(tokens, identity)
                {
                    InnerHandler = new HttpClientHandler()
                };
                http = new HttpClient(handler, disposeHandler: true);
                _clients.Add(collectorInstanceId, http);
            }
        }
        return new HeartbeatApiClient(http)
            .UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, cancellationToken);
    }

    public void Remove(Guid collectorInstanceId)
    {
        HttpClient? client;
        lock (_gate)
        {
            _clients.Remove(collectorInstanceId, out client);
        }
        client?.Dispose();
    }

    public void Dispose()
    {
        HttpClient[] clients;
        lock (_gate)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }
        foreach (var client in clients)
            client.Dispose();
    }

    private sealed record FixedSubjectIdentity(
        string HardwareId,
        string DeviceName) : IDeviceIdentity;
}
