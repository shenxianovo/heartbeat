using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core.DTOs.Segments;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public class InProcessCollectorProtocolTranscriptTests
{
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public async Task DeadlineFenceLinearizesWithAcknowledgementReplacementCommit()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        using var commitEntered = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        var committed = false;
        var commit = Task.Run(() => fixture.Activation.TryCommitAcknowledgement(() =>
        {
            commitEntered.Set();
            releaseCommit.Wait();
            committed = true;
        }));
        Assert.True(commitEntered.Wait(TimeSpan.FromSeconds(2)));

        var fence = Task.Run(fixture.Activation.Session.FenceDeliveryAfterDeadline);
        releaseCommit.Set();

        Assert.True(await commit.WaitAsync(TimeSpan.FromSeconds(2)));
        await fence.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(committed);
        Assert.False(fixture.Activation.TryCommitAcknowledgement(() =>
            throw new InvalidOperationException("A fenced acknowledgement replacement ran.")));
    }

    [Fact]
    public async Task CooperativeStop_FencesDurablePublicationBeforeWriterOwnershipRelease()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var durableCommitFence = fixture.Activation.Session.DurableCommitFence;
        var directory = Path.GetDirectoryName(fixture.StatePath)!;
        var preparedPath = Path.Combine(directory, "late-cooperative-stop.prepared");
        var authoritativePath = Path.Combine(directory, "late-cooperative-stop.json");
        File.WriteAllText(preparedPath, "late durable mutation");

        await fixture.Activation.StopAsync();

        Assert.False(durableCommitFence.TryPublishFile(preparedPath, authoritativePath));
        Assert.False(File.Exists(authoritativePath));
    }

    [Fact]
    public async Task HappyPath_HelloInitializeOpenReadyThenSegmentIsDurablyAcknowledged()
    {
        using var stateDirectory = TemporaryDirectory.Create();
        var statePath = Path.Combine(stateDirectory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var sink = new SegmentIngestService(new FixedClock(
            new DateTimeOffset(2026, 8, 22, 9, 10, 0, TimeSpan.Zero)));
        var ids = new Queue<Guid>([
            Guid.Parse("0198d5e0-5d15-73d8-a6d8-84a50ddf855f"),
            Guid.Parse("0198d5e8-30cb-7d54-bab1-250087147e4c"),
            Guid.Parse("0198d5e2-e0d4-7b30-9da7-342ee261bf62")]);
        using var runtime = CollectorRuntime.Open(
            statePath,
            sink,
            new CollectorRuntimeOptions { IdGenerator = ids.Dequeue });
        var subject = new SubjectReference(
            Guid.Parse("0198d5df-5df3-70a1-937d-68a7d64623e2"),
            SubjectKind.Machine);
        using var configDocument = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            subject,
            new CollectorInstanceSpec(7, 1, configDocument.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(
            publishReferenceSegment: true,
            referenceSegmentStart: new DateTimeOffset(2021, 8, 22, 9, 0, 0, TimeSpan.Zero));

        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector,
            Guid.Parse("0198d5e8-2b66-7a27-91b8-6524bdca51c5"));

        CollectorProtocolTranscriptContract.AssertHappyPath(
            activation.State,
            activation.DeliveryCapability,
            activation.HandshakeTranscript,
            activation.Streams.ToDictionary(pair => pair.Key, pair => pair.Value.Descriptor),
            subject,
            "reference");
        Assert.Equal(7, collector.Initialization!.Spec.SpecRevision);
        Assert.Equal(1_048_576, collector.Initialization.Limits.MaxBatchBytes);
        Assert.Equal(
            Path.Combine(stateDirectory.Path, "collector-data", instance.CollectorInstanceId.ToString("N")),
            collector.Initialization.Resources.DataDirectory);
        Assert.Equal(
            "sha256:de359aff82ed415958d16bc5fd08caad6b8fabdb8bf82556505dd2506399e02b",
            collector.Initialization.Artifact.ContentHash);
        var ack = Assert.IsType<FactBatchAcknowledgement>(collector.InitialAcknowledgement);

        Assert.False(ack.IsMessageRejected);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(ack.Results).Status);
        var buffered = Assert.Single(sink.ReadBatch());
        Assert.NotEqual(Guid.Empty, buffered.Id);
        Assert.NotEqual(
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            buffered.Id);
        Assert.Equal("reference|work", buffered.IdentityKey);
        Assert.True(File.Exists(statePath));
    }

    [Fact]
    public async Task Hello_MutableProtocolCollectionsAreSnapshottedBeforeHashAndValidation()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(protocolSupport: new ProtocolSupport(
            new FlappingProtocolMajorList(),
            new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                ["facts.segment"] = [1],
                ["diagnostics.stream-gap"] = [1]
            }));

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector));

        Assert.Equal("protocol_no_common_major", error.Error.Code);
        Assert.Null(collector.Initialization);
    }

    [Fact]
    public async Task StreamGap_IsPersistedBeforeAcknowledgement()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "outbox_overflow",
            12);

        var acknowledgement = await stream.ReportGapAsync(
            Guid.Parse("0198d5ed-5322-7ff9-a783-322190b14999"),
            gap);

        Assert.Equal(GapDeliveryStatus.Committed, acknowledgement.Status);
        Assert.Equal(stream.Descriptor.StreamId, acknowledgement.StreamId);
        Assert.True(acknowledgement.IsAcknowledged);
        Assert.Contains("outbox_overflow", File.ReadAllText(fixture.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamGap_NewAttemptForSameGap_IsIdempotentDuplicate()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "outbox_overflow",
            12);

        await stream.ReportGapAsync(Guid.CreateVersion7(), gap);
        var duplicate = await stream.ReportGapAsync(Guid.CreateVersion7(), gap);

        Assert.Equal(GapDeliveryStatus.Duplicate, duplicate.Status);
        Assert.True(duplicate.IsAcknowledged);
    }

    [Fact]
    public async Task StreamGap_DistinctLossAtSameInstantIsNotDeduplicated()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero).AddTicks(1),
            "input_ingress_capacity_exceeded",
            1);

        var first = await stream.ReportGapAsync(Guid.CreateVersion7(), gap);
        var second = await stream.ReportGapAsync(
            Guid.CreateVersion7(),
            gap with { GapId = Guid.CreateVersion7() });

        Assert.Equal(GapDeliveryStatus.Committed, first.Status);
        Assert.Equal(GapDeliveryStatus.Committed, second.Status);
    }

    [Fact]
    public async Task StreamGap_SameIdentityWithDifferentLossCountIsRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "outbox_overflow",
            1);

        await stream.ReportGapAsync(Guid.CreateVersion7(), gap);
        var conflict = await stream.ReportGapAsync(
            Guid.CreateVersion7(),
            gap with { EstimatedFactsLost = 2 });

        Assert.Equal(GapDeliveryStatus.Rejected, conflict.Status);
        Assert.Equal("gap_identity_conflict", conflict.Error!.Code);
    }

    [Fact]
    public async Task StreamGap_CurrentStateWithoutIdentityBindsMatchingLostAckRetryExactlyOnce()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "buffer_overflow",
            1);
        await stream.ReportGapAsync(Guid.CreateVersion7(), gap);
        await fixture.Activation.StopAsync();
        fixture.Runtime.Dispose();

        var state = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        state["gaps"]![0]!.AsObject().Remove("gapId");
        File.WriteAllText(
            fixture.StatePath,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        using var reopened = CollectorRuntime.Open(fixture.StatePath, new RecordingSegmentSink());
        await using var activation = await reopened.ActivateInProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            new ReferenceInProcessCollector());
        var retried = await activation.Streams["activity"].ReportGapAsync(Guid.CreateVersion7(), gap);

        Assert.Equal(GapDeliveryStatus.Duplicate, retried.Status);
        var rebound = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        Assert.Single(rebound["gaps"]!.AsArray());
        Assert.Equal(gap.GapId, rebound["gaps"]![0]!["gapId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task StreamGap_SameMessageIdWithEquivalentInstantButDifferentOffsets_IsRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "outbox_overflow",
            12);

        var committed = await stream.ReportGapAsync(messageId, gap);
        var rejected = await stream.ReportGapAsync(
            messageId,
            gap with
            {
                Start = gap.Start.ToOffset(TimeSpan.FromHours(8)),
                End = gap.End.ToOffset(TimeSpan.FromHours(8))
            });

        Assert.Equal(GapDeliveryStatus.Committed, committed.Status);
        Assert.Equal(GapDeliveryStatus.Rejected, rejected.Status);
        Assert.Equal("protocol_invalid_message", rejected.Error!.Code);
    }

    [Fact]
    public async Task StreamGap_ZeroLossEstimate_IsMessageLevelRejectedWithoutPersistence()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "outbox_overflow",
            0);

        var rejected = await stream.ReportGapAsync(Guid.CreateVersion7(), gap);

        Assert.Equal(GapDeliveryStatus.Rejected, rejected.Status);
        Assert.Equal("protocol_invalid_message", rejected.Error!.Code);
        Assert.DoesNotContain("outbox_overflow", File.ReadAllText(fixture.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_ResponseLostAndSameMessageRetransmitted_ReplaysAckWithoutSecondCommit()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var fact = CreateFact(stream.Descriptor.StreamId);
        var messageId = Guid.Parse("0198d5ec-04f4-73ab-9785-c13bef872f91");

        var first = await stream.PublishAsync(messageId, [fact]);
        var replay = await stream.PublishAsync(messageId, [fact]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(first.Results).Status);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(replay.Results).Status);
        Assert.Single(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_SameMessageIdWithDifferentContent_IsMessageRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        await stream.PublishAsync(messageId, [CreateFact(stream.Descriptor.StreamId)]);

        var rejected = await stream.PublishAsync(
            messageId,
            [CreateFact(stream.Descriptor.StreamId, title: "Changed under the same attempt")]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
        Assert.Single(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_SameMessageIdWithEquivalentInstantButDifferentOffsets_IsMessageRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        var fact = CreateFact(stream.Descriptor.StreamId);
        await stream.PublishAsync(messageId, [fact]);
        var offset = TimeSpan.FromHours(8);
        var changed = fact with
        {
            ObservedAt = fact.ObservedAt!.Value.ToOffset(offset),
            Time = fact.Time with
            {
                Start = fact.Time.Start!.Value.ToOffset(offset),
                End = fact.Time.End!.Value.ToOffset(offset)
            }
        };

        var rejected = await stream.PublishAsync(messageId, [changed]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
    }

    [Fact]
    public async Task Publish_SameRetractionMessageIdWithNewPayload_IsMessageRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var present = CreateFact(stream.Descriptor.StreamId);
        await stream.PublishAsync(Guid.CreateVersion7(), [present]);
        var messageId = Guid.CreateVersion7();
        var retraction = present with
        {
            Revision = 2,
            RecordState = FactRecordState.Retracted,
            Payload = default
        };
        await stream.PublishAsync(messageId, [retraction]);
        using var payload = JsonDocument.Parse("{}");

        var rejected = await stream.PublishAsync(
            messageId,
            [retraction with { Payload = payload.RootElement.Clone() }]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
    }

    [Fact]
    public async Task MessageId_ReusedAcrossPublishAndGap_IsRejectedAcrossMessageTypes()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        await stream.PublishAsync(messageId, [CreateFact(stream.Descriptor.StreamId)]);

        var rejected = await stream.ReportGapAsync(
            messageId,
            new StreamGapReport(
                Guid.CreateVersion7(),
                new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
                "outbox_overflow"));

        Assert.Equal(GapDeliveryStatus.Rejected, rejected.Status);
        Assert.Equal("protocol_invalid_message", rejected.Error!.Code);
    }

    [Fact]
    public async Task MessageId_ReusedFromActivationHelloForPublish_IsRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];

        var rejected = await stream.PublishAsync(
            fixture.Activation.HelloMessageId,
            [CreateFact(stream.Descriptor.StreamId)]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_NewAttemptWithSameCanonicalFact_ReturnsDuplicate()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var fact = CreateFact(stream.Descriptor.StreamId);

        await stream.PublishAsync(Guid.CreateVersion7(), [fact]);
        var duplicate = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [fact with { ObservedAt = fact.ObservedAt!.Value.AddSeconds(30) }]);

        var result = Assert.Single(duplicate.Results);
        Assert.Equal(FactDeliveryStatus.Duplicate, result.Status);
        Assert.True(result.IsAcknowledged);
        Assert.Single(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_LowerRevisionAfterNewerSnapshot_ReturnsSuperseded()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var revisionThree = CreateFact(stream.Descriptor.StreamId, revision: 3);
        var revisionTwo = CreateFact(stream.Descriptor.StreamId, revision: 2);

        await stream.PublishAsync(Guid.CreateVersion7(), [revisionThree]);
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [revisionTwo]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Superseded, result.Status);
        Assert.True(result.IsAcknowledged);
    }

    [Fact]
    public async Task Publish_LowerRevisionWithStaleInvalidPayload_ReturnsSupersededBeforeSchemaValidation()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var current = CreateFact(stream.Descriptor.StreamId, revision: 2);
        using var stalePayload = JsonDocument.Parse("""{"identityKey":"reference|work"}""");
        var stale = CreateFact(stream.Descriptor.StreamId, revision: 1) with
        {
            Payload = stalePayload.RootElement.Clone()
        };

        await stream.PublishAsync(Guid.CreateVersion7(), [current]);
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [stale]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Superseded, result.Status);
        Assert.True(result.IsAcknowledged);
    }

    [Fact]
    public async Task Publish_SameRevisionWithDifferentCanonicalContent_IsRejectedAsConflict()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var original = CreateFact(stream.Descriptor.StreamId);
        var conflicting = CreateFact(stream.Descriptor.StreamId, title: "Different title");

        await stream.PublishAsync(Guid.CreateVersion7(), [original]);
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [conflicting]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_revision_conflict", result.Error!.Code);
        Assert.False(result.IsAcknowledged);
    }

    [Fact]
    public async Task Publish_SameRevisionZeroAndTinyNonzeroNumber_AreConflictingContent()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"]!["properties"]!["value"] = new JsonObject { ["type"] = "number" };
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        var stream = activation.Streams["activity"];
        using var zeroPayload = JsonDocument.Parse(
            """{"identityKey":"reference|work","title":"Reference work","value":0}""");
        using var tinyPayload = JsonDocument.Parse(
            """{"identityKey":"reference|work","title":"Reference work","value":1e-400}""");
        var zero = CreateFact(stream.Descriptor.StreamId) with { Payload = zeroPayload.RootElement.Clone() };
        var tiny = zero with { Payload = tinyPayload.RootElement.Clone() };

        await stream.PublishAsync(Guid.CreateVersion7(), [zero]);
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [tiny]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_revision_conflict", result.Error!.Code);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task Publish_DuplicateFactIdentityWithinBatch_IsMessageRejectedWithoutPartialCommit()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var revisionOne = CreateFact(stream.Descriptor.StreamId, revision: 1);
        var revisionTwo = CreateFact(stream.Descriptor.StreamId, revision: 2);

        var rejected = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [revisionOne, revisionTwo]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("batch_limit_exceeded", rejected.MessageError!.Code);
        Assert.Empty(rejected.Results);
        Assert.Empty(fixture.Sink.Segments);

        var retry = await stream.PublishAsync(Guid.CreateVersion7(), [revisionOne]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(retry.Results).Status);
    }

    [Fact]
    public async Task Publish_LogicalMessageExceedsNegotiatedByteLimit_IsRejectedBeforeCommit()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(
            new CollectorRuntimeOptions { MaxBatchBytes = 128 });
        var stream = fixture.Activation.Streams["activity"];

        var rejected = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(stream.Descriptor.StreamId)]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("batch_limit_exceeded", rejected.MessageError!.Code);
        Assert.Empty(fixture.Sink.Segments);
        var state = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        Assert.Empty(state["facts"]!.AsArray());
    }

    [Fact]
    public async Task Publish_OversizedSameLengthContentReusingMessageId_IsProtocolRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(
            new CollectorRuntimeOptions { MaxBatchBytes = 128 });
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();

        var first = await stream.PublishAsync(
            messageId,
            [CreateFact(stream.Descriptor.StreamId, title: "First")]);
        var replay = await stream.PublishAsync(
            messageId,
            [CreateFact(stream.Descriptor.StreamId, title: "First")]);
        var changed = await stream.PublishAsync(
            messageId,
            [CreateFact(stream.Descriptor.StreamId, title: "Other")]);

        Assert.Equal("batch_limit_exceeded", first.MessageError!.Code);
        Assert.Equal("batch_limit_exceeded", replay.MessageError!.Code);
        Assert.Equal("protocol_invalid_message", changed.MessageError!.Code);
    }

    [Fact]
    public async Task Publish_OverCountDifferentContentReusingMessageId_IsProtocolRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(
            new CollectorRuntimeOptions { MaxFactsPerBatch = 1 });
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        var firstFacts = new[]
        {
            CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7()),
            CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7())
        };
        var changedFacts = new[]
        {
            CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7()),
            CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7())
        };

        var first = await stream.PublishAsync(messageId, firstFacts);
        var replay = await stream.PublishAsync(messageId, firstFacts);
        var changed = await stream.PublishAsync(messageId, changedFacts);

        Assert.Equal("batch_limit_exceeded", first.MessageError!.Code);
        Assert.Equal("batch_limit_exceeded", replay.MessageError!.Code);
        Assert.Equal("protocol_invalid_message", changed.MessageError!.Code);
    }

    [Fact]
    public async Task Publish_DurableInboxAtCapacity_ReturnsRetryableBackpressure()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(
            new CollectorRuntimeOptions { MaxDurableFacts = 1 });
        var stream = fixture.Activation.Streams["activity"];
        var first = CreateFact(stream.Descriptor.StreamId);
        var second = CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7());

        await stream.PublishAsync(Guid.CreateVersion7(), [first]);
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [second]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Retry, result.Status);
        Assert.Equal("hub_backpressure", result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.Equal(1_000, result.RetryAfterMilliseconds);
        Assert.False(result.IsAcknowledged);
        Assert.Single(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_StatePrepareFailureCanRetrySameMessageAfterStorageRecovers()
    {
        var failNextPrepare = 0;
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(
            new CollectorRuntimeOptions
            {
                BeforeStatePrepare = () =>
                {
                    if (Interlocked.Exchange(ref failNextPrepare, 0) != 0)
                    {
                        throw new CollectorRuntimeStateException(
                            "Injected transient state prepare failure.");
                    }
                }
            });
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        var fact = CreateFact(stream.Descriptor.StreamId);
        Volatile.Write(ref failNextPrepare, 1);

        var first = await stream.PublishAsync(messageId, [fact]);
        var retry = Assert.Single(first.Results);
        Assert.Equal(FactDeliveryStatus.Retry, retry.Status);
        Assert.True(retry.Error!.Retryable);

        var recovered = await stream.PublishAsync(messageId, [fact]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(recovered.Results).Status);
        Assert.Single(fixture.Sink.Segments);
    }

    [Fact]
    public async Task StreamGap_StatePrepareFailureCanRetrySameMessageAfterStorageRecovers()
    {
        var failNextPrepare = 0;
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync(
            new CollectorRuntimeOptions
            {
                BeforeStatePrepare = () =>
                {
                    if (Interlocked.Exchange(ref failNextPrepare, 0) != 0)
                    {
                        throw new CollectorRuntimeStateException(
                            "Injected transient state prepare failure.");
                    }
                }
            });
        var stream = fixture.Activation.Streams["activity"];
        var messageId = Guid.CreateVersion7();
        var gap = new StreamGapReport(
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 10, 5, 0, TimeSpan.Zero),
            "storage_unavailable",
            1);
        Volatile.Write(ref failNextPrepare, 1);

        var first = await stream.ReportGapAsync(messageId, gap);
        Assert.Equal(GapDeliveryStatus.Retry, first.Status);
        Assert.True(first.Error!.Retryable);

        var recovered = await stream.ReportGapAsync(messageId, gap);
        Assert.Equal(GapDeliveryStatus.Committed, recovered.Status);
    }

    [Fact]
    public async Task HistoricalCacheAndCommittedFact_KeepAllCustodyAcrossRestartAndBoundedUploads()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "runtime.json");
        var cachePath = Path.Combine(directory.Path, "segments.json");
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        using var cache = new Heartbeat.Collection.Hub.Storage.JsonFileCache<ActivitySegmentItem>(cachePath,
            int.MaxValue, Heartbeat.Collection.Hub.Storage.HeartbeatCacheFormats.SegmentVersion2());
        cache.Add(Enumerable.Range(0, 20_000).Select(_ => new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(), Source = "reference", IdentityKey = "historical",
            StartTime = clock.UtcNow.AddDays(-1), EndTime = clock.UtcNow.AddDays(-1).AddMinutes(1)
        }).ToList());
        var source = new SegmentIngestService(clock, cache);
        using var runtime = CollectorRuntime.Open(statePath, source);
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(instance.CollectorInstanceId,
            package, new ReferenceInProcessCollector());
        var stream = activation.Streams["activity"];
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [CreateFact(stream.Descriptor.StreamId)]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(acknowledgement.Results).Status);
        Assert.Equal(new DeliveryRemainder(20_001, 0), source.Remainder);
        await activation.StopAsync();
        runtime.Dispose();

        // The live Fact has not yet been copied to the old cache; startup must merge both owners' history.
        using var recoveredCache = new Heartbeat.Collection.Hub.Storage.JsonFileCache<ActivitySegmentItem>(cachePath,
            int.MaxValue, Heartbeat.Collection.Hub.Storage.HeartbeatCacheFormats.SegmentVersion2());
        var recovered = new SegmentIngestService(clock, recoveredCache);
        using var reopened = CollectorRuntime.Open(statePath, recovered);
        Assert.Equal(new DeliveryRemainder(20_001, 0), recovered.Remainder);
        var upload = new UploadStream<ActivitySegmentItem>("segments", [recovered],
            (_, _) => Task.FromResult(Heartbeat.Collection.Hub.Http.ApiResult.Ok));
        var first = await upload.DrainAsync();
        Assert.Equal(20_000, first.DeliveredCount);
        Assert.Equal(new DeliveryRemainder(1, 0), first.Remainder);
        var last = await upload.DrainAsync();
        Assert.Equal(1, last.DeliveredCount);
        Assert.Equal("reference|work", Assert.Single(last.Items).IdentityKey);
        Assert.True(last.Remainder.IsEmpty);
    }

    [Fact]
    public async Task RuntimeReopen_ReplaysDurableFactAndKeepsDuplicateAndStreamIdentity()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var firstStream = fixture.Activation.Streams["activity"];
        var fact = CreateFact(firstStream.Descriptor.StreamId);
        await firstStream.PublishAsync(Guid.CreateVersion7(), [fact]);
        var firstProjectionId = Assert.Single(fixture.Sink.Segments).Id;
        await fixture.Activation.StopAsync();
        fixture.Runtime.Dispose();

        var recoveredSink = new RecordingSegmentSink();
        using var reopened = CollectorRuntime.Open(fixture.StatePath, recoveredSink);
        var nextActivation = await reopened.ActivateInProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            new ReferenceInProcessCollector());
        var recoveredStream = nextActivation.Streams["activity"];
        var acknowledgement = await recoveredStream.PublishAsync(Guid.CreateVersion7(), [fact]);

        Assert.Equal(firstStream.Descriptor.StreamId, recoveredStream.Descriptor.StreamId);
        Assert.Equal(FactDeliveryStatus.Duplicate, Assert.Single(acknowledgement.Results).Status);
        var recoveredProjection = Assert.Single(recoveredSink.Segments);
        Assert.Equal(firstProjectionId, recoveredProjection.Id);
        Assert.Equal('7', recoveredProjection.Id.ToString("D")[14]);
        await nextActivation.DisposeAsync();
    }

    [Fact]
    public async Task Projection_LiveFactStampsSourceActiveButRuntimeReplayDoesNot()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        var liveSink = new SegmentIngestService(clock);
        var runtime = CollectorRuntime.Open(statePath, liveSink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        var stream = activation.Streams["activity"];

        await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(stream.Descriptor.StreamId, start: clock.UtcNow.AddYears(-5))]);

        Assert.Equal(clock.UtcNow, liveSink.SourceLastSeen["reference"]);
        await activation.StopAsync();
        runtime.Dispose();

        var replaySink = new SegmentIngestService(clock);
        using var reopened = CollectorRuntime.Open(statePath, replaySink);

        Assert.Single(replaySink.ReadBatch());
        Assert.Empty(replaySink.SourceLastSeen);
    }

    [Fact]
    public async Task Projection_AcknowledgedDuplicateSupersededAndRetractionStampLiveTraffic()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var clock = new MutableClock(
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        var sink = new SegmentIngestService(clock);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        var stream = activation.Streams["activity"];
        var current = CreateFact(stream.Descriptor.StreamId, revision: 3);
        var committedMessageId = Guid.CreateVersion7();
        await stream.PublishAsync(committedMessageId, [current]);
        var delivered = sink.ReadBatch();
        Assert.Single(delivered);
        sink.Confirm(delivered);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var replayed = await stream.PublishAsync(committedMessageId, [current]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(replayed.Results).Status);
        Assert.Equal(clock.UtcNow, sink.SourceLastSeen["reference"]);
        Assert.Empty(sink.ReadBatch());

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var duplicate = await stream.PublishAsync(Guid.CreateVersion7(), [current]);
        Assert.Equal(FactDeliveryStatus.Duplicate, Assert.Single(duplicate.Results).Status);
        Assert.Equal(clock.UtcNow, sink.SourceLastSeen["reference"]);
        Assert.Empty(sink.ReadBatch());

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var superseded = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(stream.Descriptor.StreamId, revision: 2)]);
        Assert.Equal(FactDeliveryStatus.Superseded, Assert.Single(superseded.Results).Status);
        Assert.Equal(clock.UtcNow, sink.SourceLastSeen["reference"]);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var retracted = current with
        {
            Revision = 4,
            RecordState = FactRecordState.Retracted,
            Payload = default
        };
        var retraction = await stream.PublishAsync(Guid.CreateVersion7(), [retracted]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(retraction.Results).Status);
        Assert.Equal(clock.UtcNow, sink.SourceLastSeen["reference"]);
    }

    [Fact]
    public async Task Projection_ConfirmationDoesNotRemoveHigherRevisionThatShortensSegment()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var sink = new SegmentIngestService(new FixedClock(
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero)));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        var stream = activation.Streams["activity"];
        var start = new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
        var factId = Guid.CreateVersion7();
        await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(
                stream.Descriptor.StreamId,
                factId,
                revision: 1,
                title: "Stale long snapshot",
                start: start,
                end: start.AddMinutes(20))]);
        var drained = sink.ReadBatch();

        await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(
                stream.Descriptor.StreamId,
                factId,
                revision: 2,
                title: "Corrected short snapshot",
                start: start,
                end: start.AddMinutes(10))]);
        ((IUploadSource<ActivitySegmentItem>)sink).Confirm(drained);

        var projected = Assert.Single(sink.ReadBatch());
        Assert.Equal("Corrected short snapshot", projected.Title);
        Assert.Equal(start.AddMinutes(10), projected.EndTime);
    }

    [Fact]
    public async Task Publish_SubjectAwareDurableSegmentSink_AcceptsAndProjectsFact()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var sink = new RecordingSubjectSegmentSink();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            sink);
        using var config = JsonDocument.Parse("{}");
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
        var instance = runtime.CreateInstance(
            package,
            subject,
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        var stream = activation.Streams["activity"];

        var acknowledgement = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(stream.Descriptor.StreamId)]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(acknowledgement.Results).Status);
        var projected = Assert.Single(sink.Items);
        Assert.Equal(instance.CollectorInstanceId, projected.Context.CollectorInstanceId);
        Assert.Equal(subject, projected.Context.Subject);
        Assert.False(projected.IsFinal);
        Assert.Equal("Reference work", projected.Item.Title);
    }

    [Fact]
    public async Task RuntimeReopen_TamperedDurableFactPayloadFailsClosed()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(stream.Descriptor.StreamId)]);
        await fixture.Activation.StopAsync();
        fixture.Runtime.Dispose();
        var state = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        state["facts"]![0]!["payload"]!["title"] = "tampered after durable ACK";
        File.WriteAllText(
            fixture.StatePath,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var error = Assert.Throws<CollectorRuntimeStateException>(() =>
            CollectorRuntime.Open(fixture.StatePath, new RecordingSegmentSink()));

        Assert.Contains("Unable to load", error.Message, StringComparison.Ordinal);
        Assert.Contains("content hash", error.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeReopen_NullDurableHashFailsWithStateException()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        await stream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(stream.Descriptor.StreamId)]);
        await fixture.Activation.StopAsync();
        fixture.Runtime.Dispose();
        var state = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        state["facts"]![0]!["contentHash"] = null;
        File.WriteAllText(
            fixture.StatePath,
            state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var error = Assert.Throws<CollectorRuntimeStateException>(() =>
            CollectorRuntime.Open(fixture.StatePath, new RecordingSegmentSink()));

        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task WriterLease_StopOldActivationThenNewActivationReusesStream()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var oldStream = fixture.Activation.Streams["activity"];
        var blockedCandidate = new ReferenceInProcessCollector();

        var conflict = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateInProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                blockedCandidate));
        Assert.Equal("stream_writer_conflict", conflict.Error.Code);
        Assert.Null(blockedCandidate.Initialization);

        await fixture.Activation.StopAsync();
        var replacement = await fixture.Runtime.ActivateInProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            new ReferenceInProcessCollector());

        Assert.Equal(
            oldStream.Descriptor.StreamId,
            replacement.Streams["activity"].Descriptor.StreamId);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await oldStream.PublishAsync(
                Guid.CreateVersion7(),
                [CreateFact(oldStream.Descriptor.StreamId)]));
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task WriterLease_ReplacementCannotInitializeUntilCollectorStopCompletes()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var oldCollector = new ReferenceInProcessCollector(blockStop: true);
        var oldActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            oldCollector);

        var stopping = oldActivation.StopAsync().AsTask();
        await oldCollector.StopEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var candidate = new ReferenceInProcessCollector();
        var conflict = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                candidate));

        Assert.Equal(CollectorActivationState.Draining, oldActivation.State);
        Assert.Equal("stream_writer_conflict", conflict.Error.Code);
        Assert.Null(candidate.Initialization);

        oldCollector.ReleaseStop();
        await stopping;
        var replacement = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            candidate);
        Assert.Equal(CollectorActivationState.Stopped, oldActivation.State);
        Assert.Equal(CollectorActivationState.Ready, replacement.State);
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task WriterLease_DeadlineFencesCancellationIgnoringCollectorAndAllowsReplacement()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var time = new ControlledTimeProvider();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMinutes(10),
                TimeProvider = time
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var oldCollector = new ReferenceInProcessCollector(blockStop: true, ignoreStopCancellation: true);
        var oldActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            oldCollector);
        var oldStream = oldActivation.Streams["activity"];

        var stopping = oldActivation.StopAsync().AsTask();
        await oldCollector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopping.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(10));
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(CollectorActivationState.Stopped, oldActivation.State);
        Assert.Equal(CollectorDrainReason.DeadlineExceeded, oldActivation.DrainResult!.LogicalResult.Reason);
        Assert.False(oldActivation.DrainResult.IsFullyDrained);
        CollectorDrainDriverConformance.AssertObserved(
            "in_process",
            await oldActivation.Lifetime.Terminal,
            hubInitiated: true,
            "fence_and_release");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await oldStream.PublishAsync(
                Guid.CreateVersion7(),
                [CreateFact(oldStream.Descriptor.StreamId, factId: Guid.CreateVersion7())]));

        var replacement = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        oldCollector.ReleaseStop();
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task WriterLease_DeadlineBoundsSynchronousCollectorStopInvocation()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMilliseconds(100)
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(synchronouslyBlockStop: true);
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        var stopping = Task.Run(async () => await activation.StopAsync());
        try
        {
            await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            await stopping.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(CollectorActivationState.Stopped, activation.State);
            Assert.Equal(CollectorDrainReason.DeadlineExceeded, activation.DrainResult!.LogicalResult.Reason);
            var replacement = await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                new ReferenceInProcessCollector());
            await replacement.DisposeAsync();
        }
        finally
        {
            collector.ReleaseStop();
        }
    }

    [Fact]
    public async Task WriterLease_DeadlineBoundsSynchronousCollectorDeadlineFenceInvocation()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMilliseconds(100)
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(
            blockStop: true,
            ignoreStopCancellation: true,
            synchronouslyBlockDeadlineFence: true);
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        var stopping = Task.Run(async () => await activation.StopAsync());
        try
        {
            await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            await collector.DeadlineFenceEntered.WaitAsync(TimeSpan.FromSeconds(2));
            await stopping.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(CollectorActivationState.Stopped, activation.State);
            Assert.Equal(CollectorDrainReason.DeadlineExceeded, activation.DrainResult!.LogicalResult.Reason);
            var replacement = await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                new ReferenceInProcessCollector());
            await replacement.DisposeAsync();
        }
        finally
        {
            collector.ReleaseDeadlineFence();
            collector.ReleaseStop();
        }
    }

    [Fact]
    public async Task WriterLease_StopAllowsCollectorToFlushPendingFactBeforeLeaseRelease()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var sink = new RecordingSegmentSink();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(publishOnStop: true);
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);
        var stream = activation.Streams["activity"];

        await activation.StopAsync();

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(collector.StopAcknowledgement!.Results).Status);
        Assert.Single(sink.Segments);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await stream.PublishAsync(
                Guid.CreateVersion7(),
                [CreateFact(stream.Descriptor.StreamId, factId: Guid.CreateVersion7())]));
    }

    [Fact]
    public async Task WriterLease_TransientStopFailureRetriesInsideOneTerminalTransaction()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var oldCollector = new ReferenceInProcessCollector(stopFailures: 1);
        var oldActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            oldCollector);

        await oldActivation.StopAsync();
        var candidate = new ReferenceInProcessCollector();
        var replacement = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            candidate);
        Assert.Equal(2, oldCollector.StopCalls);
        Assert.Equal(CollectorActivationState.Stopped, oldActivation.State);
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task WriterLease_ReturnedStopFailedOutcomeIsAlreadyFencedAndAllowsReplacement()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var oldCollector = new ReferenceInProcessCollector(
            stopResultReason: CollectorDrainReason.StopFailed);
        var oldActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            oldCollector);

        await oldActivation.StopAsync();

        Assert.Equal(CollectorActivationState.Stopped, oldActivation.State);
        Assert.Equal(CollectorDrainReason.StopFailed, oldActivation.DrainResult!.LogicalResult.Reason);
        Assert.False(oldActivation.DrainResult.IsFullyDrained);
        var replacement = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task Activation_CollectorReturnsWithoutReady_IsRejectedAndStopped()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(sendReady: false);

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        Assert.Equal(1, collector.StopCalls);
    }

    [Fact]
    public async Task Activation_InitializeThrows_StopsOwnedWorkBeforeAllowingReplacement()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var failingCollector = new ReferenceInProcessCollector(
            throwOnInitialize: true,
            blockStop: true);

        var failingActivation = runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            failingCollector).AsTask();
        await failingCollector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
        var candidate = new ReferenceInProcessCollector();
        var conflict = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                candidate));

        Assert.Equal("stream_writer_conflict", conflict.Error.Code);
        Assert.Null(candidate.Initialization);
        failingCollector.ReleaseStop();
        await Assert.ThrowsAsync<CollectorActivationException>(() => failingActivation);
        Assert.Equal(1, failingCollector.StopCalls);

        var replacement = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            candidate);
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeDispose_WaitsForCollectorStopBeforeReleasingStateOwnership()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(blockStop: true);
        await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        var disposing = Task.Run(runtime.Dispose);
        await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<CollectorRuntimeStateException>(() =>
            CollectorRuntime.Open(statePath, new RecordingSegmentSink()));

        collector.ReleaseStop();
        await disposing;
        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        Assert.Equal(instance.CollectorInstanceId, reopened.GetInstance(instance.CollectorInstanceId).CollectorInstanceId);
    }

    [Fact]
    public async Task RuntimeDispose_AllowsCollectorToFlushPendingFactWhileDraining()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var sink = new RecordingSegmentSink();
        var runtime = CollectorRuntime.Open(statePath, sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(publishOnStop: true);
        await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);

        await runtime.DisposeAsync();

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(collector.StopAcknowledgement!.Results).Status);
        Assert.Single(sink.Segments);
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        Assert.Single(state["facts"]!.AsArray());
    }

    [Fact]
    public async Task RuntimeDispose_CancelsStreamsOpenedCallbackBeforeReleasingOwnership()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(blockStreamsOpened: true);
        var activating = runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector).AsTask();
        await collector.StreamsOpenedEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = runtime.DisposeAsync().AsTask();
        await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => activating);
        await disposing;

        Assert.Equal(1, collector.StopCalls);
        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        Assert.Equal(instance.CollectorInstanceId, reopened.GetInstance(instance.CollectorInstanceId).CollectorInstanceId);
    }

    [Fact]
    public async Task RuntimeDispose_DeadlineBoundsCancellationIgnoringStartingActivation()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var time = new ControlledTimeProvider();
        var runtime = CollectorRuntime.Open(
            statePath,
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMinutes(10),
                TimeProvider = time
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(
            blockInitialize: true,
            ignoreInitializeCancellation: true,
            blockStop: true,
            ignoreStopCancellation: true);
        var activating = runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector).AsTask();
        await collector.InitializeEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var disposing = runtime.DisposeAsync().AsTask();
        await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposing.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(10));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        using (var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink()))
            Assert.Equal(instance.CollectorInstanceId, reopened.GetInstance(instance.CollectorInstanceId).CollectorInstanceId);
        collector.ReleaseStop();
        collector.ReleaseInitialize();
        await Assert.ThrowsAnyAsync<Exception>(() => activating);
    }

    [Fact]
    public async Task RuntimeDispose_FencesStartingActivationInitialDurableMutationBeforeOwnershipRelease()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var time = new ControlledTimeProvider();
        var runtime = CollectorRuntime.Open(
            statePath,
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMinutes(10),
                TimeProvider = time
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(
            blockInitialize: true,
            ignoreInitializeCancellation: true,
            blockStop: true,
            ignoreStopCancellation: true,
            prepareDurableMutationDuringInitialize: true);
        var activating = runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector).AsTask();
        await collector.DurableMutationPrepared.WaitAsync(TimeSpan.FromSeconds(2));

        var disposing = runtime.DisposeAsync().AsTask();
        try
        {
            await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromMinutes(10));
            await disposing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(File.Exists(collector.AuthoritativeMutationPath));

            collector.ReleaseStop();
            await collector.DurableMutationAttempted.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(collector.DurableMutationPublished);
            Assert.False(File.Exists(collector.AuthoritativeMutationPath));
        }
        finally
        {
            collector.ReleaseStop();
            collector.ReleaseInitialize();
            try
            {
                await activating.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The fixture intentionally releases initialization after Runtime disposal.
            }
        }
    }

    [Fact]
    public async Task RuntimeDispose_DeadlineBoundsSynchronouslyBlockingStartingActivationStopInvocation()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var time = new ControlledTimeProvider();
        var runtime = CollectorRuntime.Open(
            statePath,
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMinutes(10),
                TimeProvider = time
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(
            blockInitialize: true,
            ignoreInitializeCancellation: true,
            synchronouslyBlockStop: true);
        var activating = runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector).AsTask();
        await collector.InitializeEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var disposing = runtime.DisposeAsync().AsTask();
        try
        {
            await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromMinutes(10));

            await disposing.WaitAsync(TimeSpan.FromSeconds(1));
            using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
            Assert.Equal(instance.CollectorInstanceId, reopened.GetInstance(instance.CollectorInstanceId).CollectorInstanceId);
        }
        finally
        {
            collector.ReleaseStop();
            collector.ReleaseInitialize();
            try
            {
                await activating.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The fixture intentionally races activation with Runtime disposal.
            }
        }
    }

    [Fact]
    public async Task RuntimeDispose_DeadlineBoundsSynchronouslyBlockingStreamsOpenedInvocation()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var time = new ControlledTimeProvider();
        var runtime = CollectorRuntime.Open(
            statePath,
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions
            {
                InProcessDrainGracePeriod = TimeSpan.FromMinutes(10),
                TimeProvider = time
            });
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(synchronouslyBlockStreamsOpened: true);
        var activating = Task.Run(async () => await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector));
        await collector.StreamsOpenedEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var disposing = Task.Run(async () => await runtime.DisposeAsync());
        try
        {
            await collector.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            time.Advance(TimeSpan.FromMinutes(10));

            await disposing.WaitAsync(TimeSpan.FromSeconds(1));
            using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
            Assert.Equal(instance.CollectorInstanceId, reopened.GetInstance(instance.CollectorInstanceId).CollectorInstanceId);
        }
        finally
        {
            collector.ReleaseStreamsOpened();
            try
            {
                await activating.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The fixture intentionally races activation with Runtime disposal.
            }
        }
    }

    [Fact]
    public async Task StreamsOpen_ConcurrentPendingPlansCannotReserveTheSameStreamId()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var duplicateStreamId = Guid.CreateVersion7();
        var ids = new Queue<Guid>(
        [
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            duplicateStreamId,
            Guid.CreateVersion7(),
            duplicateStreamId
        ]);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink(),
            new CollectorRuntimeOptions { IdGenerator = ids.Dequeue });
        using var config = JsonDocument.Parse("{}");
        var firstInstance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var secondInstance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var firstCollector = new ReferenceInProcessCollector(blockStreamsOpened: true);
        var firstActivationTask = runtime.ActivateInProcessAsync(
            firstInstance.CollectorInstanceId,
            package,
            firstCollector).AsTask();
        await firstCollector.StreamsOpenedEntered.WaitAsync(TimeSpan.FromSeconds(5));

        InProcessCollectorActivation? unexpectedActivation = null;
        var secondError = await Record.ExceptionAsync(async () =>
            unexpectedActivation = await runtime.ActivateInProcessAsync(
                secondInstance.CollectorInstanceId,
                package,
                new ReferenceInProcessCollector()));
        if (unexpectedActivation is not null)
            await unexpectedActivation.StopAsync();
        firstCollector.ReleaseStreamsOpened();
        var firstActivation = await firstActivationTask;

        Assert.IsType<InvalidOperationException>(secondError);
        Assert.Equal(
            duplicateStreamId,
            firstActivation.Streams["activity"].Descriptor.StreamId);
        await firstActivation.StopAsync();
    }

    [Fact]
    public async Task RuntimeDispose_WaitsForCollectorBlockedInInitializeBeforeReleasingOwnership()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(blockInitialize: true, blockStop: true);
        var activating = runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector).AsTask();
        await collector.InitializeEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var disposing = runtime.DisposeAsync().AsTask();
        await Task.Yield();
        var disposedPrematurely = disposing.IsCompleted;
        CollectorRuntime? competing = null;
        Exception? ownershipError = null;
        try
        {
            competing = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        }
        catch (Exception exception)
        {
            ownershipError = exception;
        }
        competing?.Dispose();

        collector.ReleaseStop();
        await Assert.ThrowsAnyAsync<Exception>(() => activating);
        await disposing;

        Assert.False(disposedPrematurely);
        Assert.IsType<CollectorRuntimeStateException>(ownershipError);
        Assert.Equal(1, collector.StopCalls);
        Assert.False(collector.StreamsOpenedEntered.IsCompleted);
        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        Assert.Equal(instance.CollectorInstanceId, reopened.GetInstance(instance.CollectorInstanceId).CollectorInstanceId);
    }

    [Fact]
    public async Task Hello_ResponseLostAndSameMessageRetransmitted_ReplaysActivationWithoutReinitialize()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var replayCollector = new ReferenceInProcessCollector();

        var replay = await fixture.Runtime.ActivateInProcessAsync(
            fixture.Instance.CollectorInstanceId,
            fixture.Package,
            replayCollector,
            fixture.Activation.HelloMessageId);

        Assert.Same(fixture.Activation, replay);
        Assert.Equal(fixture.Activation.ActivationId, replay.ActivationId);
        Assert.Null(replayCollector.Initialization);
    }

    [Fact]
    public async Task Hello_SameAttemptAfterRuntimeRestart_IsRejectedInsteadOfAllocatingNewActivation()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var helloMessageId = fixture.Activation.HelloMessageId;
        await fixture.Activation.StopAsync();
        fixture.Runtime.Dispose();
        using var reopened = CollectorRuntime.Open(
            fixture.StatePath,
            new RecordingSegmentSink());
        var replayCollector = new ReferenceInProcessCollector();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await reopened.ActivateInProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                replayCollector,
                helloMessageId));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        Assert.Contains("previous Runtime session", error.Message, StringComparison.Ordinal);
        Assert.Null(replayCollector.Initialization);
    }

    [Fact]
    public async Task Hello_ConflictingAttemptIsDurableAcrossRuntimeRestart()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var helloMessageId = Guid.CreateVersion7();
        var firstCandidate = new ReferenceInProcessCollector();
        var conflict = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await fixture.Runtime.ActivateInProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                firstCandidate,
                helloMessageId));
        Assert.Equal("stream_writer_conflict", conflict.Error.Code);
        await fixture.Activation.StopAsync();
        fixture.Runtime.Dispose();
        using var reopened = CollectorRuntime.Open(fixture.StatePath, new RecordingSegmentSink());
        var replayCollector = new ReferenceInProcessCollector();

        var replayError = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await reopened.ActivateInProcessAsync(
                fixture.Instance.CollectorInstanceId,
                fixture.Package,
                replayCollector,
                helloMessageId));

        Assert.Equal("protocol_invalid_message", replayError.Error.Code);
        Assert.Contains("previous Runtime session", replayError.Message, StringComparison.Ordinal);
        Assert.Null(replayCollector.Initialization);
    }

    [Fact]
    public async Task StreamsOpen_EquivalentBindingsWithinOneRequestReuseOneStableStream()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var bindings = new[]
        {
            new OutputBinding("activity-primary", "activity", new Dictionary<string, string>()),
            new OutputBinding("activity-secondary", "activity", new Dictionary<string, string>())
        };

        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector(bindings: bindings));

        Assert.Equal(
            activation.Streams["activity-primary"].Descriptor.StreamId,
            activation.Streams["activity-secondary"].Descriptor.StreamId);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task StreamsOpen_InconsistentMutableDimensionsAreRejectedWithoutPersistingAnyStream()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(bindings:
        [
            new OutputBinding("activity", "activity", new InconsistentDimensions())
        ]);

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector));

        Assert.Equal("output_not_declared", error.Error.Code);
        Assert.Equal(1, collector.StopCalls);
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        Assert.Empty(state["streams"]!.AsArray());
    }

    [Fact]
    public async Task Publish_OlderCataloguedSchemaRevisionSurvivesRestartAndReturnsDuplicate()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var currentSchemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var revisionOnePath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment-v1.schema.json");
        var revisionOneBytes = File.ReadAllBytes(currentSchemaPath);
        File.WriteAllBytes(revisionOnePath, revisionOneBytes);
        var currentSchema = JsonNode.Parse(revisionOneBytes)!.AsObject();
        currentSchema["schemaRevision"] = 2;
        File.WriteAllText(
            currentSchemaPath,
            currentSchema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var revisionOneHash = Sha256(revisionOneBytes);
        var revisionTwoHash = Sha256(File.ReadAllBytes(currentSchemaPath));
        var manifest = packageCopy.ReadManifest();
        var outputs = manifest["outputs"]!.AsArray();
        var currentOutput = outputs[0]!.AsObject();
        currentOutput["schema"]!["revision"] = 2;
        currentOutput["schema"]!["hash"] = revisionTwoHash;
        var catalogOutput = currentOutput.DeepClone().AsObject();
        catalogOutput["outputId"] = "activity-schema-v1";
        catalogOutput["schema"]!["revision"] = 1;
        catalogOutput["schema"]!["document"] = "schemas/reference-segment-v1.schema.json";
        catalogOutput["schema"]!["hash"] = revisionOneHash;
        outputs.Add(catalogOutput);
        packageCopy.WriteManifest(manifest);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        var bindings = new[]
        {
            new OutputBinding("activity", "activity", new Dictionary<string, string>()),
            new OutputBinding("schema-v1", "activity-schema-v1", new Dictionary<string, string>())
        };
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var sink = new RecordingSegmentSink();
        var runtime = CollectorRuntime.Open(statePath, sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector(bindings: bindings));
        var stream = activation.Streams["activity"];
        Assert.Equal(2, stream.Descriptor.Schema.Revision);
        var oldOutboxFact = CreateFact(stream.Descriptor.StreamId, schemaRevision: 1);

        var committed = await stream.PublishAsync(Guid.CreateVersion7(), [oldOutboxFact]);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(committed.Results).Status);
        await activation.StopAsync();
        runtime.Dispose();

        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        await using var recovered = await reopened.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector(bindings: bindings));
        var duplicate = await recovered.Streams["activity"].PublishAsync(
            Guid.CreateVersion7(),
            [oldOutboxFact]);
        Assert.Equal(FactDeliveryStatus.Duplicate, Assert.Single(duplicate.Results).Status);
    }

    [Fact]
    public async Task Publish_GenericSegmentSchemaIdentityInsideActivitySegmentShape_IsProjected()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["schemaId"] = "heartbeat.alternate.segment";
        schema["schemaMajor"] = 7;
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);
        var manifest = packageCopy.ReadManifest();
        manifest["outputs"]![0]!["schema"]!["id"] = "heartbeat.alternate.segment";
        manifest["outputs"]![0]!["schema"]!["major"] = 7;
        packageCopy.WriteManifest(manifest);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        var sink = new RecordingSegmentSink();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            new ReferenceInProcessCollector());
        var fact = CreateFact(activation.Streams["activity"].Descriptor.StreamId);

        var outcome = await activation.Streams["activity"].PublishAsync(
            Guid.CreateVersion7(),
            [fact]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(outcome.Results).Status);
        Assert.Equal("reference|work", Assert.Single(sink.Segments).IdentityKey);

        // Package schema 的 minLength 接受纯空白；通用 ActivitySegment shape 必须与
        // Analytics 的 identity 契约一致，在 Hub 持久接收前明确拒绝。
        using var whitespacePayload = JsonDocument.Parse("""{"identityKey":" ","title":"Alternate work"}""");
        var whitespaceFact = CreateFact(activation.Streams["activity"].Descriptor.StreamId) with
        {
            Payload = whitespacePayload.RootElement.Clone()
        };
        var rejected = await activation.Streams["activity"].PublishAsync(
            Guid.CreateVersion7(),
            [whitespaceFact]);
        var rejectedResult = Assert.Single(rejected.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, rejectedResult.Status);
        Assert.Equal("fact_schema_invalid", rejectedResult.Error!.Code);
        Assert.False(rejectedResult.Error.Retryable);
    }

    [Fact]
    public async Task Publish_GenericSegmentSchemaOutsideActivitySegmentShape_IsRejected()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["schemaId"] = "heartbeat.alternate.segment";
        schema["payloadSchema"]!["required"] = new JsonArray("activityKey", "title");
        schema["payloadSchema"]!["properties"]!.AsObject().Remove("identityKey");
        schema["payloadSchema"]!["properties"]!["activityKey"] =
            new JsonObject { ["type"] = "string", ["minLength"] = 1 };
        File.WriteAllText(schemaPath, schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        packageCopy.UpdateSchemaHash(schemaPath);
        var manifest = packageCopy.ReadManifest();
        manifest["outputs"]![0]!["schema"]!["id"] = "heartbeat.alternate.segment";
        packageCopy.WriteManifest(manifest);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector();
        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);
        using var incompatiblePayload = JsonDocument.Parse(
            """{"activityKey":"alternate|work","title":"Alternate work"}""");
        var fact = CreateFact(activation.Streams["activity"].Descriptor.StreamId) with
        {
            Payload = incompatiblePayload.RootElement.Clone()
        };

        var outcome = await activation.Streams["activity"].PublishAsync(
            Guid.CreateVersion7(),
            [fact]);

        var result = Assert.Single(outcome.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_schema_invalid", result.Error!.Code);
        Assert.Contains("projection shape", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Error.Retryable);
    }

    [Fact]
    public async Task StreamsOpen_RejectedAfterInitialize_StopsCollectorOwnedWork()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(bindings:
        [
            new OutputBinding("unknown", "not-declared", new Dictionary<string, string>())
        ]);

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector));

        Assert.Equal("output_not_declared", error.Error.Code);
        Assert.Equal(1, collector.StopCalls);
    }

    [Fact]
    public async Task ActivationWithoutStreamGapCapability_CannotBecomeReadyForFactDelivery()
    {
        using var directory = TemporaryDirectory.Create();
        var package = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                new ReferenceInProcessCollector(includeStreamGap: false)));

        Assert.Equal("capability_no_common_version", error.Error.Code);
        Assert.Contains("diagnostics.stream-gap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstanceBoundToAnotherPackageId_RejectsActivationAndRequiresNewInstance()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        using var otherCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = otherCopy.ReadManifest();
        manifest["packageId"] = "heartbeat.collector.other-reference";
        otherCopy.WriteManifest(manifest);
        var other = LocalCollectorPackage.Load(otherCopy.Path);

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                other,
                new ReferenceInProcessCollector()));

        Assert.Equal("package_mismatch", error.Error.Code);
        Assert.Equal(original.Manifest.PackageId, runtime.GetInstance(instance.CollectorInstanceId).PackageId);

        var replacement = runtime.CreateInstance(other, instance.Subject, instance.Spec);
        var activation = await runtime.ActivateInProcessAsync(
            replacement.CollectorInstanceId,
            other,
            new ReferenceInProcessCollector());
        Assert.NotEqual(instance.CollectorInstanceId, replacement.CollectorInstanceId);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task PackageUpdate_CompatibleRevisionReusesInstanceAndStreamAndAdvancesSchema()
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var updated = LoadRevisionTwoPackage(packageCopy, "1.1.0");
        var runtime = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var originalActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        var originalStreamId = originalActivation.Streams["activity"].Descriptor.StreamId;
        var oldOutboxFact = CreateFact(
            originalStreamId,
            factId: Guid.CreateVersion7(),
            schemaRevision: 1);
        await originalActivation.StopAsync();

        var updatedActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            updated,
            new ReferenceInProcessCollector());
        var updatedStream = updatedActivation.Streams["activity"];
        var acknowledgement = await updatedStream.PublishAsync(
            Guid.CreateVersion7(),
            [CreateFact(updatedStream.Descriptor.StreamId, schemaRevision: 2)]);

        Assert.Equal(instance.CollectorInstanceId, updatedStream.Descriptor.CollectorInstanceId);
        Assert.Equal(originalStreamId, updatedStream.Descriptor.StreamId);
        Assert.Equal(2, updatedStream.Descriptor.Schema.Revision);
        Assert.Equal(updated.Manifest.Outputs[0].Schema.Hash, updatedStream.Descriptor.Schema.Hash);
        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(acknowledgement.Results).Status);
        var resolved = runtime.GetInstance(instance.CollectorInstanceId);
        Assert.Equal("1.1.0", resolved.PackageVersion);
        Assert.Equal(updated.PackageContentHash, resolved.PackageContentHash);
        var durableState = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        var durableStream = Assert.Single(durableState["streams"]!.AsArray());
        var schemaCatalog = durableStream!["schemaCatalog"]!.AsObject();
        Assert.Equal(original.Manifest.Outputs[0].Schema.Hash, schemaCatalog["1"]!.GetValue<string>());
        Assert.Equal(updated.Manifest.Outputs[0].Schema.Hash, schemaCatalog["2"]!.GetValue<string>());

        await updatedActivation.StopAsync();
        runtime.Dispose();

        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        await using var recovered = await reopened.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            updated,
            new ReferenceInProcessCollector());
        Assert.Equal(originalStreamId, recovered.Streams["activity"].Descriptor.StreamId);
        Assert.Equal(2, recovered.Streams["activity"].Descriptor.Schema.Revision);
        var oldOutboxAcknowledgement = await recovered.Streams["activity"].PublishAsync(
            Guid.CreateVersion7(),
            [oldOutboxFact]);
        Assert.Equal(
            FactDeliveryStatus.Committed,
            Assert.Single(oldOutboxAcknowledgement.Results).Status);
    }

    [Fact]
    public async Task PackageUpdate_CandidateThatDoesNotReachReadyLeavesResolvedStateUnchanged()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var updated = LoadRevisionTwoPackage(packageCopy, "1.1.0");
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var originalActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        var originalStreamId = originalActivation.Streams["activity"].Descriptor.StreamId;
        await originalActivation.StopAsync();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                updated,
                new ReferenceInProcessCollector(sendReady: false)));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        var resolved = runtime.GetInstance(instance.CollectorInstanceId);
        Assert.Equal(original.Manifest.Version, resolved.PackageVersion);
        Assert.Equal(original.PackageContentHash, resolved.PackageContentHash);
        await using var replacement = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        Assert.Equal(originalStreamId, replacement.Streams["activity"].Descriptor.StreamId);
        Assert.Equal(1, replacement.Streams["activity"].Descriptor.Schema.Revision);
    }

    [Fact]
    public async Task PackageUpdate_SameVersionWithDifferentFingerprint_ActivatesCandidate()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var changedCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = changedCopy.ReadManifest();
        manifest["outputs"]![0]!["dimensionKeys"] = new JsonArray("slot");
        changedCopy.WriteManifest(manifest);
        var changed = LocalCollectorPackage.Load(changedCopy.Path);
        Assert.Equal(original.Manifest.Version, changed.Manifest.Version);
        Assert.NotEqual(original.PackageContentHash, changed.PackageContentHash);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        await using var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            changed,
            new ReferenceInProcessCollector());

        var resolved = runtime.GetInstance(instance.CollectorInstanceId);
        Assert.Equal(changed.Manifest.Version, resolved.PackageVersion);
        Assert.Equal(changed.PackageContentHash, resolved.PackageContentHash);
    }

    [Fact]
    public void CreateInstance_SamePackageVersionWithDifferentFingerprintAcrossInstances_IsAllowed()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var changedCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = changedCopy.ReadManifest();
        manifest["outputs"]![0]!["dimensionKeys"] = new JsonArray("slot");
        changedCopy.WriteManifest(manifest);
        var changed = LocalCollectorPackage.Load(changedCopy.Path);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var originalInstance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        var changedInstance = runtime.CreateInstance(
            changed,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        Assert.Equal(original.PackageContentHash, originalInstance.PackageContentHash);
        Assert.Equal(changed.PackageContentHash, changedInstance.PackageContentHash);
    }

    [Fact]
    public async Task PackageUpdate_ConcurrentInitializeAllowsDifferentFingerprintsForOneVersion()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var firstCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var firstCandidate = LoadRevisionTwoPackage(firstCopy, "1.1.0");
        using var secondCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        _ = LoadRevisionTwoPackage(secondCopy, "1.1.0");
        var secondManifest = secondCopy.ReadManifest();
        secondManifest["outputs"]![0]!["dimensionKeys"] = new JsonArray("slot");
        secondCopy.WriteManifest(secondManifest);
        var secondCandidate = LocalCollectorPackage.Load(secondCopy.Path);
        Assert.NotEqual(firstCandidate.PackageContentHash, secondCandidate.PackageContentHash);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var firstInstance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var secondInstance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var firstCollector = new ReferenceInProcessCollector(blockInitialize: true);
        var firstActivationTask = runtime.ActivateInProcessAsync(
            firstInstance.CollectorInstanceId,
            firstCandidate,
            firstCollector).AsTask();
        await firstCollector.InitializeEntered.WaitAsync(TimeSpan.FromSeconds(5));

        InProcessCollectorActivation? unexpectedSecondActivation = null;
        var secondError = await Record.ExceptionAsync(async () =>
            unexpectedSecondActivation = await runtime.ActivateInProcessAsync(
                secondInstance.CollectorInstanceId,
                secondCandidate,
                new ReferenceInProcessCollector()));
        if (unexpectedSecondActivation is not null)
            await unexpectedSecondActivation.StopAsync();
        firstCollector.ReleaseStop();
        var firstError = await Record.ExceptionAsync(async () =>
        {
            var firstActivation = await firstActivationTask;
            await firstActivation.StopAsync();
        });

        Assert.Null(secondError);
        Assert.Null(firstError);
    }

    [Fact]
    public async Task PackageUpdate_PreviouslySeenVersionWithDifferentFingerprint_ActivatesAfterUpgrade()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var updatedCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var updated = LoadRevisionTwoPackage(updatedCopy, "1.1.0");
        using var changedOldCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var changedOldManifest = changedOldCopy.ReadManifest();
        changedOldManifest["outputs"]![0]!["dimensionKeys"] = new JsonArray("slot");
        changedOldCopy.WriteManifest(changedOldManifest);
        var changedOld = LocalCollectorPackage.Load(changedOldCopy.Path);
        Assert.Equal(original.Manifest.Version, changedOld.Manifest.Version);
        Assert.NotEqual(original.PackageContentHash, changedOld.PackageContentHash);
        var statePath = Path.Combine(directory.Path, "collector-runtime.json");
        var runtime = CollectorRuntime.Open(
            statePath,
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var originalActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        await originalActivation.StopAsync();
        var updatedActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            updated,
            new ReferenceInProcessCollector());
        await updatedActivation.StopAsync();
        runtime.Dispose();

        using var reopened = CollectorRuntime.Open(statePath, new RecordingSegmentSink());
        await using var changedOldActivation = await reopened.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            changedOld,
            new ReferenceInProcessCollector());

        var resolved = reopened.GetInstance(instance.CollectorInstanceId);
        Assert.Equal(changedOld.Manifest.Version, resolved.PackageVersion);
        Assert.Equal(changedOld.PackageContentHash, resolved.PackageContentHash);
    }

    [Fact]
    public async Task PackageUpdate_SchemaFormattingOnly_IsAcceptedForSameRevision()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var reformattedCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            reformattedCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        var documentVersion = schema["documentVersion"]!.DeepClone();
        schema.Remove("documentVersion");
        schema["documentVersion"] = documentVersion;
        File.WriteAllText(schemaPath, schema.ToJsonString());
        reformattedCopy.UpdateSchemaHash(schemaPath);
        var reformatted = LocalCollectorPackage.Load(reformattedCopy.Path);
        Assert.Equal(original.Manifest.Version, reformatted.Manifest.Version);
        Assert.NotEqual(
            original.Manifest.Outputs[0].Schema.Hash,
            reformatted.Manifest.Outputs[0].Schema.Hash);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var originalActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        await originalActivation.StopAsync();

        await using var reformattedActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            reformatted,
            new ReferenceInProcessCollector());

        Assert.Equal(
            reformatted.Manifest.Outputs[0].Schema.Hash,
            reformattedActivation.Streams["activity"].Descriptor.Schema.Hash);
    }

    [Fact]
    public async Task PackageActivation_SchemaFormattingOnly_IsAcceptedAcrossInstances()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var reformattedCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            reformattedCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        var documentVersion = schema["documentVersion"]!.DeepClone();
        schema.Remove("documentVersion");
        schema["documentVersion"] = documentVersion;
        File.WriteAllText(schemaPath, schema.ToJsonString());
        reformattedCopy.UpdateSchemaHash(schemaPath);
        var reformatted = LocalCollectorPackage.Load(reformattedCopy.Path);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var firstInstance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var secondInstance = runtime.CreateInstance(
            reformatted,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        await using var firstActivation = await runtime.ActivateInProcessAsync(
            firstInstance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        await using var secondActivation = await runtime.ActivateInProcessAsync(
            secondInstance.CollectorInstanceId,
            reformatted,
            new ReferenceInProcessCollector());

        Assert.NotEqual(
            firstActivation.Streams["activity"].Descriptor.Schema.Hash,
            secondActivation.Streams["activity"].Descriptor.Schema.Hash);
    }

    [Fact]
    public async Task PackageUpdate_SchemaIdentityHashConflictIsRejectedEvenWhenOutputCreatesNewStream()
    {
        using var directory = TemporaryDirectory.Create();
        var original = LocalCollectorPackage.Load(ReferencePackagePath);
        using var conflictingCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var schemaPath = Path.Combine(
            conflictingCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["payloadSchema"]!["title"] = "Conflicting bytes under revision one";
        File.WriteAllText(
            schemaPath,
            schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var conflictingManifest = conflictingCopy.ReadManifest();
        conflictingManifest["version"] = "1.1.0";
        conflictingManifest["outputs"]![0]!["outputId"] = "activity-v2";
        conflictingManifest["outputs"]![0]!["schema"]!["hash"] =
            Sha256(File.ReadAllBytes(schemaPath));
        conflictingCopy.WriteManifest(conflictingManifest);
        var conflicting = LocalCollectorPackage.Load(conflictingCopy.Path);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            original,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var originalActivation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            original,
            new ReferenceInProcessCollector());
        await originalActivation.StopAsync();

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                conflicting,
                new ReferenceInProcessCollector(bindings:
                [
                    new OutputBinding(
                        "activity",
                        "activity-v2",
                        new Dictionary<string, string>())
                ])));

        Assert.Equal("package_mismatch", error.Error.Code);
    }

    [Fact]
    public async Task Activation_MultipleArtifactsMatchCurrentInProcessTarget_RejectsAmbiguousPackage()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        var artifacts = manifest["artifacts"]!.AsArray();
        var duplicate = artifacts[0]!.DeepClone().AsObject();
        duplicate["artifactId"] = "reference.inprocess.duplicate";
        artifacts.Add(duplicate);
        packageCopy.WriteManifest(manifest);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            new RecordingSegmentSink());
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));

        var error = await Assert.ThrowsAsync<CollectorActivationException>(async () =>
            await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                new ReferenceInProcessCollector()));

        Assert.Equal("package_mismatch", error.Error.Code);
        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publish_PayloadViolatesFactSchema_IsPermanentlyRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        using var payload = JsonDocument.Parse("""{"identityKey":"reference|work"}""");
        var invalid = CreateFact(stream.Descriptor.StreamId) with
        {
            Payload = payload.RootElement.Clone()
        };

        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [invalid]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_schema_invalid", result.Error!.Code);
        Assert.False(result.Error.Retryable);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_NumberOutsideCanonicalRange_IsMessageRejectedInsteadOfThrowing()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        using var payload = JsonDocument.Parse(
            """{"identityKey":"reference|work","title":"Reference work","overflow":1e400}""");
        var invalid = CreateFact(stream.Descriptor.StreamId) with
        {
            Payload = payload.RootElement.Clone()
        };

        var rejected = await stream.PublishAsync(Guid.CreateVersion7(), [invalid]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_DuplicatePayloadKey_IsMessageRejectedBeforeAnyFactIsCommitted()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        using var duplicatePayload = JsonDocument.Parse(
            """{"identityKey":"first","identityKey":"second","title":"Duplicate"}""");
        var valid = CreateFact(
            stream.Descriptor.StreamId,
            factId: Guid.CreateVersion7());
        var invalid = CreateFact(
            stream.Descriptor.StreamId,
            factId: Guid.CreateVersion7()) with
        {
            Payload = duplicatePayload.RootElement.Clone()
        };

        var rejected = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [valid, invalid]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
        Assert.Empty(rejected.Results);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_IntegerOutsideJsonSafeRange_IsMessageRejectedBeforeSchemaEvaluation()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        using var payload = JsonDocument.Parse(
            """{"identityKey":"reference|work","title":"Unsafe integer","unsafe":9007199254740992}""");
        var fact = CreateFact(stream.Descriptor.StreamId) with
        {
            Payload = payload.RootElement.Clone()
        };

        var rejected = await stream.PublishAsync(Guid.CreateVersion7(), [fact]);

        Assert.True(rejected.IsMessageRejected);
        Assert.Equal("protocol_invalid_message", rejected.MessageError!.Code);
        Assert.Empty(rejected.Results);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_FinalSegmentCannotReturnToOpenInHigherRevision()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var final = CreateFact(stream.Descriptor.StreamId, revision: 1, isFinal: true);
        var reopened = CreateFact(stream.Descriptor.StreamId, revision: 2, isFinal: false);

        await stream.PublishAsync(Guid.CreateVersion7(), [final]);
        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [reopened]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_schema_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Publish_FinalSegmentRetractionCannotReturnToOpenInHigherRevision()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var final = CreateFact(stream.Descriptor.StreamId, revision: 1, isFinal: true);
        var reopenedRetraction = CreateFact(
            stream.Descriptor.StreamId,
            revision: 2,
            isFinal: false) with
        {
            RecordState = FactRecordState.Retracted,
            Payload = default
        };

        await stream.PublishAsync(Guid.CreateVersion7(), [final]);
        var acknowledgement = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [reopenedRetraction]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_schema_invalid", result.Error!.Code);
    }

    [Fact]
    public async Task Publish_RetractedFactWithPayload_IsPermanentlyRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var retractedWithPayload = CreateFact(stream.Descriptor.StreamId) with
        {
            RecordState = FactRecordState.Retracted
        };

        var acknowledgement = await stream.PublishAsync(
            Guid.CreateVersion7(),
            [retractedWithPayload]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_schema_invalid", result.Error!.Code);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_RevisionOneRetractionWithoutPriorFact_IsRejected()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var retraction = CreateFact(stream.Descriptor.StreamId) with
        {
            RecordState = FactRecordState.Retracted,
            Payload = default
        };

        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [retraction]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Equal(FactDeliveryStatus.Rejected, result.Status);
        Assert.Equal("fact_schema_invalid", result.Error!.Code);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Publish_HigherRetractionRemovesSnapshotFromExistingHubBuffer()
    {
        await using var fixture = await ActivatedRuntimeFixture.CreateAsync();
        var stream = fixture.Activation.Streams["activity"];
        var present = CreateFact(stream.Descriptor.StreamId);
        await stream.PublishAsync(Guid.CreateVersion7(), [present]);
        var retracted = CreateFact(stream.Descriptor.StreamId, revision: 2) with
        {
            RecordState = FactRecordState.Retracted,
            Payload = default
        };

        var acknowledgement = await stream.PublishAsync(Guid.CreateVersion7(), [retracted]);

        Assert.Equal(FactDeliveryStatus.Committed, Assert.Single(acknowledgement.Results).Status);
        Assert.Empty(fixture.Sink.Segments);
    }

    [Fact]
    public async Task Projection_SameFactIdInTwoStreamsRemainsIndependentIncludingRetraction()
    {
        using var packageCopy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = packageCopy.ReadManifest();
        manifest["outputs"]![0]!["dimensionKeys"] = new JsonArray("slot");
        packageCopy.WriteManifest(manifest);
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var directory = TemporaryDirectory.Create();
        var sink = new SegmentIngestService(new FixedClock(
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero)));
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "collector-runtime.json"),
            sink);
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var collector = new ReferenceInProcessCollector(bindings:
        [
            new OutputBinding("first", "activity", new Dictionary<string, string> { ["slot"] = "first" }),
            new OutputBinding("second", "activity", new Dictionary<string, string> { ["slot"] = "second" })
        ]);
        var activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId,
            package,
            collector);
        var sharedFactId = Guid.CreateVersion7();
        var first = CreateFact(
            activation.Streams["first"].Descriptor.StreamId,
            factId: sharedFactId,
            title: "First stream");
        var second = CreateFact(
            activation.Streams["second"].Descriptor.StreamId,
            factId: sharedFactId,
            title: "Second stream");

        await activation.Streams["first"].PublishAsync(Guid.CreateVersion7(), [first]);
        await activation.Streams["second"].PublishAsync(Guid.CreateVersion7(), [second]);

        var projected = sink.ReadBatch();
        Assert.Equal(2, projected.Count);
        Assert.Equal(2, projected.Select(segment => segment.Id).Distinct().Count());
        Assert.All(projected, segment => Assert.Equal('7', segment.Id.ToString("D")[14]));

        var retraction = first with
        {
            Revision = 2,
            RecordState = FactRecordState.Retracted,
            Payload = default
        };
        await activation.Streams["first"].PublishAsync(Guid.CreateVersion7(), [retraction]);

        Assert.Equal("Second stream", Assert.Single(sink.ReadBatch()).Title);
        await activation.DisposeAsync();
    }

    private static FactSubmission CreateFact(
        Guid streamId,
        Guid? factId = null,
        long revision = 1,
        string identityKey = "reference|work",
        string title = "Reference work",
        DateTimeOffset? observedAt = null,
        DateTimeOffset? start = null,
        bool isFinal = false,
        DateTimeOffset? end = null,
        int schemaRevision = 1)
    {
        var segmentStart = start ?? new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
        using var payload = JsonDocument.Parse(
            $$"""{"identityKey":"{{identityKey}}","title":"{{title}}"}""");
        return new FactSubmission(
            streamId,
            schemaRevision,
            factId ?? Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            revision,
            observedAt ?? new DateTimeOffset(2026, 8, 22, 9, 5, 0, TimeSpan.Zero),
            FactRecordState.Present,
            new SegmentFactTime(segmentStart, end ?? segmentStart.AddMinutes(5 + revision), isFinal),
            payload.RootElement.Clone());
    }

    private static string Sha256(byte[] bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static LocalCollectorPackage LoadRevisionTwoPackage(
        ReferenceCollectorPackageCopy packageCopy,
        string version)
    {
        var schemaPath = Path.Combine(
            packageCopy.Path,
            "schemas",
            "reference-segment.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
        schema["schemaRevision"] = 2;
        File.WriteAllText(
            schemaPath,
            schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var manifest = packageCopy.ReadManifest();
        manifest["version"] = version;
        manifest["outputs"]![0]!["schema"]!["revision"] = 2;
        manifest["outputs"]![0]!["schema"]!["hash"] = Sha256(File.ReadAllBytes(schemaPath));
        packageCopy.WriteManifest(manifest);
        return LocalCollectorPackage.Load(packageCopy.Path);
    }

    private sealed class ReferenceInProcessCollector :
        IInProcessCollector,
        IInProcessCollectorDeadlineFence
    {
        private readonly IReadOnlyList<OutputBinding> _bindings;
        private readonly bool _publishReferenceSegment;
        private readonly bool _sendReady;
        private readonly TaskCompletionSource _stopEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStop = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _initializeEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseInitialize = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _streamsOpenedEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStreamsOpened = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deadlineFenceEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDeadlineFence = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _durableMutationPrepared = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _durableMutationAttempted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockStop;
        private readonly bool _ignoreStopCancellation;
        private readonly bool _blockInitialize;
        private readonly bool _ignoreInitializeCancellation;
        private readonly bool _blockStreamsOpened;
        private readonly bool _synchronouslyBlockStreamsOpened;
        private readonly bool _throwOnInitialize;
        private readonly bool _publishOnStop;
        private readonly bool _synchronouslyBlockStop;
        private readonly bool _synchronouslyBlockDeadlineFence;
        private readonly bool _prepareDurableMutationDuringInitialize;
        private readonly CollectorDrainReason _stopResultReason;
        private readonly DateTimeOffset _referenceSegmentStart;
        private InProcessCollectorActivation? _activation;
        private int _stopCalls;
        private int _stopFailuresRemaining;

        public ReferenceInProcessCollector(
            bool includeStreamGap = true,
            bool publishReferenceSegment = false,
            bool sendReady = true,
            bool blockStop = false,
            bool ignoreStopCancellation = false,
            bool blockInitialize = false,
            bool ignoreInitializeCancellation = false,
            bool blockStreamsOpened = false,
            bool synchronouslyBlockStreamsOpened = false,
            bool throwOnInitialize = false,
            bool publishOnStop = false,
            bool synchronouslyBlockStop = false,
            bool synchronouslyBlockDeadlineFence = false,
            bool prepareDurableMutationDuringInitialize = false,
            CollectorDrainReason stopResultReason = CollectorDrainReason.Drained,
            int stopFailures = 0,
            DateTimeOffset? referenceSegmentStart = null,
            IReadOnlyList<OutputBinding>? bindings = null,
            ProtocolSupport? protocolSupport = null)
        {
            var capabilities = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                ["facts.segment"] = [1]
            };
            if (includeStreamGap)
                capabilities["diagnostics.stream-gap"] = [1];
            ProtocolSupport = protocolSupport ?? new ProtocolSupport([1], capabilities);
            _publishReferenceSegment = publishReferenceSegment;
            _sendReady = sendReady;
            _blockStop = blockStop;
            _ignoreStopCancellation = ignoreStopCancellation;
            _blockInitialize = blockInitialize;
            _ignoreInitializeCancellation = ignoreInitializeCancellation;
            _blockStreamsOpened = blockStreamsOpened;
            _synchronouslyBlockStreamsOpened = synchronouslyBlockStreamsOpened;
            _throwOnInitialize = throwOnInitialize;
            _publishOnStop = publishOnStop;
            _synchronouslyBlockStop = synchronouslyBlockStop;
            _synchronouslyBlockDeadlineFence = synchronouslyBlockDeadlineFence;
            _prepareDurableMutationDuringInitialize = prepareDurableMutationDuringInitialize;
            _stopResultReason = stopResultReason;
            _stopFailuresRemaining = stopFailures;
            _referenceSegmentStart = referenceSegmentStart ??
                new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
            _bindings = bindings ??
                [new OutputBinding("activity", "activity", new Dictionary<string, string>())];
        }

        public string ArtifactId => "reference.inprocess";

        public ProtocolSupport ProtocolSupport { get; }

        public CollectorInitialization? Initialization { get; private set; }

        public FactBatchAcknowledgement? InitialAcknowledgement { get; private set; }

        public FactBatchAcknowledgement? StopAcknowledgement { get; private set; }

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public Task StopEntered => _stopEntered.Task;

        public Task InitializeEntered => _initializeEntered.Task;

        public Task StreamsOpenedEntered => _streamsOpenedEntered.Task;

        public Task DeadlineFenceEntered => _deadlineFenceEntered.Task;

        public Task DurableMutationPrepared => _durableMutationPrepared.Task;

        public Task DurableMutationAttempted => _durableMutationAttempted.Task;

        public string? AuthoritativeMutationPath { get; private set; }

        public bool DurableMutationPublished { get; private set; }

        public async ValueTask<InProcessCollectorInitialization> InitializeAsync(
            CollectorInitialization initialization,
            CancellationToken cancellationToken)
        {
            Initialization = initialization;
            string? preparedMutationPath = null;
            if (_prepareDurableMutationDuringInitialize)
            {
                var dataDirectory = initialization.Resources.DataDirectory
                    ?? throw new InvalidOperationException("Hub did not provide Collector durable storage.");
                Directory.CreateDirectory(dataDirectory);
                AuthoritativeMutationPath = Path.Combine(dataDirectory, "initial-outbox.json");
                preparedMutationPath = Path.Combine(dataDirectory, "initial-outbox.prepared.json");
                File.WriteAllText(preparedMutationPath, "prepared outbox mutation");
                _durableMutationPrepared.TrySetResult();
            }
            _initializeEntered.TrySetResult();
            if (_throwOnInitialize)
                throw new InvalidOperationException("Collector failed after starting initialization work.");
            if (_blockInitialize)
            {
                if (_ignoreInitializeCancellation)
                    await _releaseInitialize.Task;
                else
                    await _releaseInitialize.Task.WaitAsync(cancellationToken);
            }
            if (preparedMutationPath is not null)
            {
                var durableCommitFence = initialization.Resources.DurableCommitFence
                    ?? throw new InvalidOperationException("Hub did not provide Collector durable commit fence.");
                DurableMutationPublished = durableCommitFence.TryPublishFile(
                    preparedMutationPath,
                    AuthoritativeMutationPath!);
                _durableMutationAttempted.TrySetResult();
            }
            return new InProcessCollectorInitialization(
                initialization.Spec.SpecRevision,
                _bindings);
        }

        public async ValueTask OnStreamsOpenedAsync(
            InProcessCollectorStreamsOpened opened,
            CancellationToken cancellationToken)
        {
            _streamsOpenedEntered.TrySetResult();
            if (_synchronouslyBlockStreamsOpened)
                _releaseStreamsOpened.Task.GetAwaiter().GetResult();
            if (_blockStreamsOpened)
                await _releaseStreamsOpened.Task.WaitAsync(cancellationToken);
            if (!_sendReady)
                return;
            var activation = await opened.ReadyAsync(cancellationToken);
            _activation = activation;
            if (!_publishReferenceSegment)
                return;

            var stream = activation.Streams["activity"];
            var start = _referenceSegmentStart;
            using var payload = JsonDocument.Parse(
                """{"identityKey":"reference|work","title":"Reference work"}""");
            var fact = new FactSubmission(
                stream.Descriptor.StreamId,
                1,
                Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
                1,
                new DateTimeOffset(2026, 8, 22, 9, 5, 0, TimeSpan.Zero),
                FactRecordState.Present,
                new SegmentFactTime(start, start.AddMinutes(5), false),
                payload.RootElement.Clone());
            InitialAcknowledgement = await stream.PublishAsync(
                Guid.Parse("0198d5ec-04f4-73ab-9785-c13bef872f91"),
                [fact],
                cancellationToken);
        }

        public async ValueTask<InProcessCollectorDrainResult> StopAsync(
            DateTimeOffset deadline,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopCalls);
            _stopEntered.TrySetResult();
            if (_synchronouslyBlockStop)
                _releaseStop.Task.GetAwaiter().GetResult();
            if (_publishOnStop)
            {
                var stream = _activation!.Streams["activity"];
                StopAcknowledgement = await stream.PublishAsync(
                    Guid.CreateVersion7(),
                    [CreateFact(stream.Descriptor.StreamId)],
                    cancellationToken);
            }
            if (_blockStop)
            {
                if (_ignoreStopCancellation)
                    await _releaseStop.Task;
                else
                    await _releaseStop.Task.WaitAsync(cancellationToken);
            }
            _releaseInitialize.TrySetResult();
            if (Interlocked.Decrement(ref _stopFailuresRemaining) >= 0)
                throw new InvalidOperationException("Collector stop failed before owned work ended.");
            return new InProcessCollectorDrainResult(
                new InProcessCollectorLogicalDrainResult(
                    0,
                    0,
                    _stopResultReason,
                    RemainderDurable: _stopResultReason == CollectorDrainReason.Drained));
        }

        public void ReleaseStop()
        {
            _releaseStop.TrySetResult();
            _releaseInitialize.TrySetResult();
        }

        public void ReleaseStreamsOpened() => _releaseStreamsOpened.TrySetResult();

        public void FenceAfterDeadline()
        {
            _deadlineFenceEntered.TrySetResult();
            if (_synchronouslyBlockDeadlineFence)
                _releaseDeadlineFence.Task.GetAwaiter().GetResult();
        }

        public void ReleaseDeadlineFence() => _releaseDeadlineFence.TrySetResult();

        public void ReleaseInitialize() => _releaseInitialize.TrySetResult();
    }

    private sealed class FlappingProtocolMajorList : IReadOnlyList<int>
    {
        private int _enumerations;

        public int Count => 1;

        public int this[int index] => index == 0 ? 1 : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<int> GetEnumerator()
        {
            var value = Interlocked.Increment(ref _enumerations) == 1 ? 2 : 1;
            return ((IEnumerable<int>)[value]).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class InconsistentDimensions : IReadOnlyDictionary<string, string>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => [];
        public IEnumerable<string> Values => [];
        public string this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, string>>)
            [
                new KeyValuePair<string, string>("undeclared", "value")
            ]).GetEnumerator();

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state, dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            ControlledTimer[] due;
            lock (_gate)
            {
                _utcNow += duration;
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToArray();
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ControlledTimer(
            ControlledTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _dueAt = owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool IsDue(DateTimeOffset now) => !_disposed && now >= _dueAt;

            public void Fire()
            {
                if (_disposed)
                    return;
                if (period == Timeout.InfiniteTimeSpan)
                    _disposed = true;
                else
                    _dueAt += period;
                callback(state);
            }

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                dueTime = newDueTime;
                period = newPeriod;
                _dueAt = owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingSegmentSink : ISegmentSink, ISegmentRetractionSink, IDurableSegmentProjectionSink
    {
        public List<ActivitySegmentItem> Segments { get; } = [];

        public void Push(List<ActivitySegmentItem> snapshots) => Segments.AddRange(snapshots);

        public void Retract(Guid segmentId) => Segments.RemoveAll(segment => segment.Id == segmentId);

        public void UpsertDurable(ActivitySegmentItem snapshot, long revision)
        {
            Retract(snapshot.Id);
            Segments.Add(snapshot);
        }

        public void ReplayDurable(ActivitySegmentItem snapshot, long revision) =>
            UpsertDurable(snapshot, revision);

        public void RetractDurable(Guid segmentId, long revision) => Retract(segmentId);
    }

    private sealed class RecordingSubjectSegmentSink : ISegmentSink, ISubjectSegmentProjectionSink
    {
        public List<(CollectorProjectionContext Context, ActivitySegmentItem Item, bool IsFinal)> Items { get; } = [];

        public void Push(List<ActivitySegmentItem> snapshots) =>
            throw new NotSupportedException("Subject-aware projection requires Collector Instance context.");

        public void UpsertDurable(
            CollectorProjectionContext context,
            ActivitySegmentItem snapshot,
            long revision,
            bool isFinal) => Items.Add((context, snapshot, isFinal));

        public void ReplayDurable(
            CollectorProjectionContext context,
            ActivitySegmentItem snapshot,
            long revision,
            bool isFinal) => Items.Add((context, snapshot, isFinal));

        public void RetractDurable(
            CollectorProjectionContext context,
            Guid segmentId,
            long revision) { }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-collector-protocol-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ActivatedRuntimeFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;

        private ActivatedRuntimeFixture(
            TemporaryDirectory directory,
            string statePath,
            CollectorRuntime runtime,
            RecordingSegmentSink sink,
            LocalCollectorPackage package,
            CollectorInstance instance,
            InProcessCollectorActivation activation)
        {
            _directory = directory;
            StatePath = statePath;
            Runtime = runtime;
            Sink = sink;
            Package = package;
            Instance = instance;
            Activation = activation;
        }

        public string StatePath { get; }
        public CollectorRuntime Runtime { get; }
        public RecordingSegmentSink Sink { get; }
        public LocalCollectorPackage Package { get; }
        public CollectorInstance Instance { get; }
        public InProcessCollectorActivation Activation { get; }

        public static async Task<ActivatedRuntimeFixture> CreateAsync(CollectorRuntimeOptions? options = null)
        {
            var directory = TemporaryDirectory.Create();
            try
            {
                var statePath = Path.Combine(directory.Path, "collector-runtime.json");
                var package = LocalCollectorPackage.Load(ReferencePackagePath);
                var sink = new RecordingSegmentSink();
                var runtime = CollectorRuntime.Open(statePath, sink, options);
                var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
                using var config = JsonDocument.Parse("{}");
                var instance = runtime.CreateInstance(
                    package,
                    subject,
                    new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
                var activation = await runtime.ActivateInProcessAsync(
                    instance.CollectorInstanceId,
                    package,
                    new ReferenceInProcessCollector());
                return new ActivatedRuntimeFixture(
                    directory,
                    statePath,
                    runtime,
                    sink,
                    package,
                    instance,
                    activation);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Activation.DisposeAsync();
            Runtime.Dispose();
            _directory.Dispose();
        }
    }
}
