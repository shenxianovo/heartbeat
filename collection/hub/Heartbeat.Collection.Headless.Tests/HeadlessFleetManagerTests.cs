using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessFleetManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"heartbeat-headless-composition-{Guid.NewGuid():N}");

    [Fact]
    public void FleetConfiguration_ContainsInfrastructureOnly()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless.json");
        File.WriteAllText(path, """
        {
          "apiKey": "test-key",
          "dataDirectory": "data",
          "collectorRegistryUrl": "https://registry.example/v1/",
          "management": {
            "ownerSubject": "owner-1",
            "authority": "https://auth.example.test",
            "issuer": "https://auth.example.test/",
            "clientId": "heartbeat-web"
          }
        }
        """);

        var fleet = HeadlessFleetOptions.Load(path);
        fleet.Validate();

        Assert.Equal(Path.Combine(_directory, "data"), fleet.DataDirectory);
        Assert.Equal("https://registry.example/v1/", fleet.CollectorRegistryUrl);
    }

    [Fact]
    public void FleetConfiguration_OldInstancesArray_IsRejectedWithoutCompatibilityMigration()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "heartbeat-headless.json");
        File.WriteAllText(path, """
        {
          "apiKey": "test-key",
          "dataDirectory": "data",
          "management": {
            "ownerSubject": "owner-1",
            "authority": "https://auth.example.test",
            "issuer": "https://auth.example.test/",
            "clientId": "heartbeat-web"
          },
          "instances": []
        }
        """);

        Assert.Throws<JsonException>(() => HeadlessFleetOptions.Load(path));
    }

    [Fact]
    public async Task CancelledUpload_RetainsEachInstanceAndExposesTheResult()
    {
        var upload = new RecordingSegmentUpload();
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var id = Guid.CreateVersion7();
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        pipelines.Add(id, subject, "Account");
        pipelines.UpsertDurable(new CollectorProjectionContext(id, subject),
            Segment("pending", DateTimeOffset.UtcNow), 1, false);

        await pipelines.DrainAllAsync(new CancellationToken(canceled: true));

        Assert.Empty(upload.Sent);
        Assert.Equal(1, pipelines.UploadResults[id]!.Remainder.RetainedLocally);
        Assert.True(pipelines.UploadResults[id]!.Remainder.IsDurable);
        var result = pipelines.UploadResults[id];
        pipelines.Dispose();
        Assert.Equal(result, pipelines.UploadResults[id]);
    }

    [Fact]
    public async Task InstanceRemoval_CleanupFailureLeavesThePipelineRetryable()
    {
        var upload = new RecordingSegmentUpload { FailRemoval = true };
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var id = Guid.CreateVersion7();
        pipelines.Add(id, new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account), "Account");

        await Assert.ThrowsAsync<IOException>(() => pipelines.RemoveAsync(id));
        upload.FailRemoval = false;
        await pipelines.RemoveAsync(id);

        Assert.Contains(id, upload.Removed);
    }

    [Fact]
    public async Task InstanceRemoval_OfflineRetainsCustodyAndCanRetryAfterDelivery()
    {
        var upload = new RecordingSegmentUpload { Result = new ApiResult(false, 503) };
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var id = Guid.CreateVersion7();
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        pipelines.Add(id, subject, "Account");
        pipelines.UpsertDurable(new CollectorProjectionContext(id, subject),
            Segment("pending", DateTimeOffset.UtcNow), 1, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipelines.RemoveAsync(id));

        Assert.Equal("pending", pipelines.CurrentActivity(id)?.Title);
        Assert.DoesNotContain(id, upload.Removed);
        upload.Result = ApiResult.Ok;
        await pipelines.RemoveAsync(id);
        Assert.Contains(id, upload.Removed);
    }

    [Fact]
    public async Task InstancePipelines_IsolateProjectionAndCanDeleteOneInstanceDataSet()
    {
        Directory.CreateDirectory(_directory);
        var upload = new RecordingSegmentUpload();
        using var pipelines = new HeadlessInstancePipelines(_directory, upload);
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        var secondSubject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        pipelines.Add(firstId, subject, "First");
        pipelines.Add(secondId, secondSubject, "Second");
        var projection = (ISubjectSegmentProjectionSink)pipelines;
        var now = DateTimeOffset.UtcNow;
        projection.UpsertDurable(new CollectorProjectionContext(firstId, subject), Segment("first", now), 1, false);
        projection.UpsertDurable(new CollectorProjectionContext(secondId, secondSubject), Segment("second", now), 1, false);

        await pipelines.DrainAllAsync();
        Assert.Contains(upload.Sent, sent => sent.InstanceId == firstId && sent.Subject == subject && sent.Title == "first");
        Assert.Contains(upload.Sent, sent => sent.InstanceId == secondId && sent.Subject == secondSubject && sent.Title == "second");
        Assert.DoesNotContain(upload.Sent, sent => sent.InstanceId == firstId && sent.Subject == secondSubject);
        await pipelines.RemoveAsync(firstId);

        Assert.False(Directory.Exists(Path.Combine(_directory, "instances", firstId.ToString("D"))));
        Assert.True(Directory.Exists(Path.Combine(_directory, "instances", secondId.ToString("D"))));
        Assert.Equal("second", pipelines.CurrentActivity(secondId)?.Title);
        Assert.Contains(firstId, upload.Removed);
    }

    private static ActivitySegmentItem Segment(string title, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = "reference",
        IdentityKey = title,
        Title = title,
        StartTime = now,
        EndTime = now
    };

    private sealed class RecordingSegmentUpload : IHeadlessSegmentUpload
    {
        public ApiResult Result { get; set; } = ApiResult.Ok;
        public bool FailRemoval { get; set; }
        public HashSet<Guid> Removed { get; } = [];
        public List<(Guid InstanceId, SubjectReference Subject, string? Title)> Sent { get; } = [];

        public Task<ApiResult> SendAsync(
            Guid collectorInstanceId,
            SubjectReference subject,
            string displayName,
            List<ActivitySegmentItem> batch, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(batch.Select(item => (collectorInstanceId, subject, item.Title)));
            return Task.FromResult(Result);
        }

        public void Remove(Guid collectorInstanceId)
        {
            if (FailRemoval) throw new IOException("cleanup unavailable");
            Removed.Add(collectorInstanceId);
        }
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
