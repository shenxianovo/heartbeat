using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.CollectorProtocol;

namespace Heartbeat.Collection.CollectorProtocol.Tests;

public sealed class CollectorProtocolClientTests
{
    /// <summary>Drain budget spent on the virtual clock instead of on real scheduling.</summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void DrainOutcomeCorpusMatchesClientVocabulary()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Conformance",
            "collector-protocol-conformance.json")));

        Assert.Equal(
            Enum.GetValues<CollectorProtocolDrainReason>().Select(CollectorProtocolDrainVocabulary.Format),
            corpus.RootElement.GetProperty("drainOutcomes").EnumerateArray()
                .Select(item => item.GetProperty("reason").GetString()));
        Assert.Equal(
            Enum.GetValues<CollectorProtocolDrainCompletionReason>().Select(CollectorProtocolDrainVocabulary.Format),
            corpus.RootElement.GetProperty("completionOutcomes").EnumerateArray()
                .Select(item => item.GetString()));
    }

    [Fact]
    public void SynchronousHostWait_DoesNotRequireItsSynchronizationContextToPumpProtocolContinuations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-sync-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var context = new QueuedSynchronizationContext();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var client = new CollectorProtocolClient(Definition(), binding);
                try
                {
                    client.RunAsync(new PublishingApplication()).GetAwaiter().GetResult();
                }
                finally
                {
                    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Collector Protocol synchronous host fixture"
        };

        try
        {
            thread.Start();
            Assert.True(binding.PublishEntered.Wait(TimeSpan.FromSeconds(2)));
            binding.CompletePublish();
            binding.RequestDrain();

            var returnedWithoutPumping = finished.Wait(TimeSpan.FromSeconds(2));

            while (!finished.IsSet && context.RunOne(TimeSpan.FromMilliseconds(100))) { }
            Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
            Assert.Null(failure);
            Assert.True(
                returnedWithoutPumping,
                "Collector Protocol captured the synchronous host context and deadlocked its caller.");
        }
        finally
        {
            binding.CompletePublish();
            binding.RequestDrain();
            while (!finished.IsSet && context.RunOne(TimeSpan.FromMilliseconds(100))) { }
            thread.Join(TimeSpan.FromSeconds(2));
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FactAcknowledgementCorpusDrivesDurableRemovalRetryAndDeadLetter()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Conformance",
            "collector-protocol-conformance.json")));
        foreach (var item in corpus.RootElement.GetProperty("factAcknowledgements").EnumerateArray())
        {
            var status = Enum.Parse<CollectorFactDeliveryStatus>(
                item.GetProperty("status").GetString()!,
                ignoreCase: true);
            var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-client-{Guid.NewGuid():N}");
            try
            {
                var outcomes = status == CollectorFactDeliveryStatus.Retry
                    ? new Queue<CollectorFactDeliveryStatus>([status, CollectorFactDeliveryStatus.Committed])
                    : new Queue<CollectorFactDeliveryStatus>([status]);
                var binding = new FakeBinding(
                    root,
                    outcomes,
                    fallbackOutcome: status == CollectorFactDeliveryStatus.Retry
                        ? CollectorFactDeliveryStatus.Committed
                        : status,
                    drainAfterPublishes: status == CollectorFactDeliveryStatus.Retry ? 2 : 1);
                await using var client = new CollectorProtocolClient(Definition(), binding);

                var result = await client.RunAsync(new PublishingApplication());

                var outbox = CollectorProtocolOutbox.Open(
                    root,
                    16,
                    Definition().Outputs,
                    DateTimeOffset.UtcNow);
                var eventuallyRemoved = item.GetProperty("removesFact").GetBoolean() ||
                    status == CollectorFactDeliveryStatus.Retry;
                Assert.Equal(eventuallyRemoved, outbox.Facts.Count == 0);
                Assert.Equal(
                    item.TryGetProperty("requiresDeadLetter", out var deadLetter) && deadLetter.GetBoolean() ? 1 : 0,
                    outbox.DeadLetterCount);
                Assert.Equal(outbox.Facts.Count, result.PendingFacts);
                if (status == CollectorFactDeliveryStatus.Retry)
                {
                    Assert.True(binding.PublishMessageIds.Count >= 2);
                    Assert.NotEqual(binding.PublishMessageIds[0], binding.PublishMessageIds[1]);
                    Assert.All(binding.PublishedFacts, fact =>
                    {
                        Assert.Equal(PublishingApplication.FactId, fact.FactId);
                        Assert.Equal(1, fact.Revision);
                    });
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CorruptOutboxIsQuarantinedAndBecomesOneGapPerDeclaredOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collector-protocol-outbox.json");
        var recoveredAt = new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);
        var lastWrite = recoveredAt.AddMinutes(-5);
        File.WriteAllText(path, "{truncated");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                recoveredAt);

            var gap = Assert.Single(outbox.Gaps).Gap;
            Assert.Equal("outbox_corrupted", gap.Reason);
            Assert.Equal(lastWrite, gap.Start);
            Assert.Equal(recoveredAt, gap.End);
            Assert.Single(Directory.EnumerateFiles(root, "collector-protocol-outbox.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptOutboxWithSameRecoveryInstantProducesUploadableGap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-corrupt-instant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collector-protocol-outbox.json");
        var recoveredAt = new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);
        File.WriteAllText(path, "{truncated");
        File.SetLastWriteTimeUtc(path, recoveredAt.UtcDateTime);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, recoveredAt);

            var gap = Assert.Single(outbox.Gaps).Gap;
            Assert.Equal(recoveredAt, gap.Start);
            Assert.Equal(recoveredAt.AddTicks(1), gap.End);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptOutbox_FenceBetweenEvidenceAndRecoveryPublicationKeepsAuthoritativeEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-corrupt-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collector-protocol-outbox.json");
        var recoveredAt = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        const string corruptJson = "{truncated";
        File.WriteAllText(path, corruptJson);
        File.SetLastWriteTimeUtc(path, recoveredAt.AddMinutes(-5).UtcDateTime);
        var publicationAttempts = 0;
        try
        {
            Assert.Throws<OperationCanceledException>(() => CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                recoveredAt,
                (preparedPath, authoritativePath) =>
                {
                    publicationAttempts++;
                    if (publicationAttempts != 1)
                        return false;
                    File.Move(preparedPath, authoritativePath, overwrite: true);
                    return true;
                }));

            Assert.Equal(2, publicationAttempts);
            Assert.Equal(corruptJson, File.ReadAllText(path));
            var restarted = CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                recoveredAt.AddMinutes(1));
            var gap = Assert.Single(restarted.Gaps).Gap;
            Assert.Equal("outbox_corrupted", gap.Reason);
            Assert.Equal(recoveredAt.AddMinutes(-5), gap.Start);
            Assert.Equal(recoveredAt.AddMinutes(1), gap.End);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CapacityEvictionPersistsExactGapAndRemainingFactsInOneRestartableMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var start = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var facts = Enumerable.Range(0, 3).Select(index => new CollectorFact(
            "activity",
            1,
            Guid.CreateVersion7(),
            1,
            null,
            CollectorFactRecordState.Present,
            new CollectorEventFactTime(start.AddSeconds(index)),
            JsonSerializer.SerializeToElement(new { code = index }))).ToArray();
        try
        {
            var outbox = CollectorProtocolOutbox.Open(root, 2, Definition().Outputs, start);
            foreach (var fact in facts)
                outbox.Enqueue(fact);

            var restarted = CollectorProtocolOutbox.Open(root, 2, Definition().Outputs, start.AddMinutes(1));
            Assert.Equal(facts.Skip(1).Select(fact => fact.FactId), restarted.Facts.Select(item => item.Fact.FactId));
            var pendingGap = Assert.Single(restarted.Gaps);
            Assert.Equal("outbox_capacity_exceeded", pendingGap.Gap.Reason);
            Assert.Equal(start, pendingGap.Gap.Start);
            Assert.Equal(start.AddTicks(1), pendingGap.Gap.End);
            Assert.Equal(1, pendingGap.Gap.EstimatedFactsLost);
            Assert.Equal(7, pendingGap.Gap.GapId.Version);

            var delivery = new CollectorDeliveryOwnership().BeginBackground();
            Assert.Equal(
                CollectorDeliveryCommitOutcome.Committed,
                restarted.AcknowledgeGap(pendingGap.MessageId, delivery));
            var acknowledged = CollectorProtocolOutbox.Open(root, 2, Definition().Outputs, start.AddMinutes(2));
            Assert.Empty(acknowledged.Gaps);
            Assert.Equal(facts.Skip(1).Select(fact => fact.FactId), acknowledged.Facts.Select(item => item.Fact.FactId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BeginActivation_PreparedOutboxReleasedAfterTerminalFenceDoesNotPublish()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-initial-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collector-protocol-outbox.json");
        var now = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        using var publicationEntered = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        var terminallyFenced = 0;
        try
        {
            CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, now).Enqueue(new CollectorFact(
                "activity",
                1,
                Guid.CreateVersion7(),
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorEventFactTime(now),
                JsonSerializer.SerializeToElement(new { identityKey = "reference|fenced" })));
            var authoritativeBeforeActivation = File.ReadAllText(path);
            var publicationAttempts = 0;
            var restarted = CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                now.AddMinutes(1),
                (preparedPath, authoritativePath) =>
                {
                    publicationAttempts++;
                    Assert.True(File.Exists(preparedPath));
                    publicationEntered.Set();
                    Assert.True(releasePublication.Wait(TimeSpan.FromSeconds(5)));
                    if (Volatile.Read(ref terminallyFenced) != 0)
                        return false;
                    File.Move(preparedPath, authoritativePath, overwrite: true);
                    return true;
                });

            var beginActivation = Task.Run(restarted.BeginActivation);
            Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(2)));
            Volatile.Write(ref terminallyFenced, 1);
            releasePublication.Set();

            await Assert.ThrowsAsync<OperationCanceledException>(() => beginActivation);

            Assert.Equal(1, publicationAttempts);
            Assert.Equal(authoritativeBeforeActivation, File.ReadAllText(path));
        }
        finally
        {
            releasePublication.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClientInitialOutboxMutation_UsesBindingDurablePublicationFence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-client-initial-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collector-protocol-outbox.json");
        var now = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        try
        {
            CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, now).Enqueue(new CollectorFact(
                "activity",
                1,
                Guid.CreateVersion7(),
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorEventFactTime(now),
                JsonSerializer.SerializeToElement(new { identityKey = "reference|binding-fenced" })));
            var authoritativeBeforeActivation = File.ReadAllText(path);
            var publicationAttempts = 0;
            var binding = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>(),
                durableFilePublisher: (_, _) =>
                {
                    publicationAttempts++;
                    return false;
                });
            await using var client = new CollectorProtocolClient(Definition(), binding);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => client.RunAsync(new IdleApplication()));

            Assert.Equal(1, publicationAttempts);
            Assert.Equal(authoritativeBeforeActivation, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestartPreservesInterleavedFactGapFactDeliveryOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var start = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        var first = new CollectorFact(
            "activity", 1, Guid.CreateVersion7(), 1, null, CollectorFactRecordState.Present,
            new CollectorEventFactTime(start),
            JsonSerializer.SerializeToElement(new { code = 1 }));
        var gap = new CollectorStreamGap(
            Guid.CreateVersion7(), "activity", start.AddSeconds(1), start.AddSeconds(2), "fixture", 1);
        var second = new CollectorFact(
            "activity", 1, Guid.CreateVersion7(), 1, null, CollectorFactRecordState.Present,
            new CollectorEventFactTime(start.AddSeconds(3)),
            JsonSerializer.SerializeToElement(new { code = 2 }));
        try
        {
            var outbox = CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, start);
            outbox.Enqueue(first);
            outbox.EnqueueGap(gap);
            outbox.Enqueue(second);

            var restarted = CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, start.AddMinutes(1));
            var delivery = new CollectorDeliveryOwnership().BeginBackground();
            var firstPending = Assert.IsType<PendingCollectorFact>(restarted.FirstFact);
            Assert.Equal(first.FactId, firstPending.Fact.FactId);
            Assert.Equal(
                CollectorDeliveryCommitOutcome.Committed,
                restarted.AcknowledgeFact(firstPending.MessageId, delivery));
            var gapPending = Assert.IsType<PendingCollectorGap>(restarted.FirstGap);
            Assert.Equal(gap.GapId, gapPending.Gap.GapId);
            Assert.Equal(
                CollectorDeliveryCommitOutcome.Committed,
                restarted.AcknowledgeGap(gapPending.MessageId, delivery));
            Assert.Equal(second.FactId, Assert.IsType<PendingCollectorFact>(restarted.FirstFact).Fact.FactId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, "older", 2, "newer")]
    [InlineData(2, "newer", 1, "older")]
    public void OutboxSameFactSnapshotsKeepHighestRevisionAcrossRestart(
        long firstRevision,
        string firstContent,
        long secondRevision,
        string secondContent)
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-revision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var factId = Guid.CreateVersion7();
        var start = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, start);
            foreach (var (revision, content) in new[]
                     {
                         (firstRevision, firstContent),
                         (secondRevision, secondContent)
                     })
            {
                outbox.Enqueue(new CollectorFact(
                    "activity",
                    1,
                    factId,
                    revision,
                    null,
                    CollectorFactRecordState.Present,
                    new CollectorSegmentFactTime(start, start.AddMinutes(revision), IsFinal: false),
                    JsonSerializer.SerializeToElement(new { content })));
            }

            AssertHighestRevision(Assert.Single(outbox.Facts).Fact);

            var restarted = CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                start.AddHours(1));
            AssertHighestRevision(Assert.Single(restarted.Facts).Fact);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        void AssertHighestRevision(CollectorFact fact)
        {
            Assert.Equal(factId, fact.FactId);
            Assert.Equal(2, fact.Revision);
            Assert.Equal(start.AddMinutes(2), Assert.IsType<CollectorSegmentFactTime>(fact.Time).End);
            Assert.Equal("newer", fact.Payload.GetProperty("content").GetString());
        }
    }

    [Fact]
    public void CapacityEvictionOfPointSegmentPersistsUploadableHalfOpenGap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-point-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var occurredAt = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(root, 1, Definition().Outputs, occurredAt);
            outbox.Enqueue(new CollectorFact(
                "activity",
                1,
                Guid.CreateVersion7(),
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorSegmentFactTime(occurredAt, occurredAt, IsFinal: false),
                JsonSerializer.SerializeToElement(new { code = 1 })));
            outbox.Enqueue(new CollectorFact(
                "activity",
                1,
                Guid.CreateVersion7(),
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorEventFactTime(occurredAt.AddMinutes(1)),
                JsonSerializer.SerializeToElement(new { code = 2 })));

            var gap = Assert.Single(outbox.Gaps).Gap;
            Assert.Equal(occurredAt, gap.Start);
            Assert.Equal(occurredAt.AddTicks(1), gap.End);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CurrentOutboxPointGapIsRewrittenWithoutQuarantiningRetainedFacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-point-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var occurredAt = new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var outbox = CollectorProtocolOutbox.Open(root, 1, Definition().Outputs, occurredAt);
            var facts = Enumerable.Range(0, 2).Select(index => new CollectorFact(
                "activity",
                1,
                Guid.CreateVersion7(),
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorEventFactTime(occurredAt.AddSeconds(index)),
                JsonSerializer.SerializeToElement(new { code = index }))).ToArray();
            foreach (var fact in facts)
                outbox.Enqueue(fact);

            var path = Path.Combine(root, "collector-protocol-outbox.json");
            var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var gap = envelope["State"]!["Gaps"]![0]!["Gap"]!.AsObject();
            gap["End"] = gap["Start"]!.DeepClone();
            File.WriteAllText(path, envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var restarted = CollectorProtocolOutbox.Open(
                root,
                1,
                Definition().Outputs,
                occurredAt.AddMinutes(1));

            Assert.Equal(facts[1].FactId, Assert.Single(restarted.Facts).Fact.FactId);
            var migrated = Assert.Single(restarted.Gaps).Gap;
            Assert.Equal("outbox_capacity_exceeded", migrated.Reason);
            Assert.Equal(migrated.Start.AddTicks(1), migrated.End);
            Assert.Empty(Directory.EnumerateFiles(root, "collector-protocol-outbox.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransientPersistenceFailureRetriesWithoutAnotherObservation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var blockingDirectory = Path.Combine(root, "collector-protocol-outbox.json");
        Directory.CreateDirectory(blockingDirectory);
        try
        {
            var binding = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>([CollectorFactDeliveryStatus.Committed]),
                fallbackOutcome: CollectorFactDeliveryStatus.Committed,
                drainAfterPublishes: 1);
            await using var client = new CollectorProtocolClient(Definition(), binding);
            var unblock = Task.Run(async () =>
            {
                await Task.Delay(120);
                Directory.Delete(blockingDirectory);
            });

            var result = await client.RunAsync(new PublishingApplication());
            await unblock;

            Assert.Empty(CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                DateTimeOffset.UtcNow).Facts);
            Assert.Equal(0, result.PendingFacts);
            Assert.NotEmpty(binding.PublishedFacts);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoredBacklogCannotPreventDeadlineBoundedDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-backlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var outbox = CollectorProtocolOutbox.Open(
            root,
            16,
            Definition().Outputs,
            DateTimeOffset.UtcNow);
        outbox.Enqueue(new CollectorFact(
            "activity",
            1,
            PublishingApplication.FactId,
            1,
            null,
            CollectorFactRecordState.Present,
            new CollectorEventFactTime(DateTimeOffset.UtcNow),
            JsonSerializer.SerializeToElement(new { identityKey = "reference|online" })));
        var binding = new SuspendedPublishBinding(root);
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(new IdleApplication());

        try
        {
            Assert.True(binding.PublishEntered.Wait(TimeSpan.FromSeconds(2)));
            binding.RequestDrain(TimeSpan.FromMilliseconds(100));

            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(1, result.PendingFacts);
            Assert.Equal(CollectorProtocolDrainReason.FlushCancelled, result.LogicalResult.Reason);
            Assert.True(result.LogicalResult.RemainderDurable);
            Assert.False(result.IsFullyDrained);
            AssertDrainConformance(result);
            Assert.Single(CollectorProtocolOutbox.Open(
                root,
                16,
                Definition().Outputs,
                DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            binding.CompletePublish();
            binding.RequestDrain();
            Assert.True(binding.PublishExited.Wait(TimeSpan.FromSeconds(2)));
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Preserve the primary assertion failure while ensuring the controlled fixture
                // cannot leave a protocol task behind.
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationIgnoringLateAcknowledgementCannotChangeReportedRemainder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-late-ack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Enqueue(
            new CollectorFact(
                "activity",
                1,
                PublishingApplication.FactId,
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorEventFactTime(DateTimeOffset.UtcNow),
                JsonSerializer.SerializeToElement(new { identityKey = "reference|late-ack" })));
        var binding = new SuspendedPublishBinding(root, ignoreCancellation: true);
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(new IdleApplication());
        try
        {
            Assert.True(binding.PublishEntered.Wait(TimeSpan.FromSeconds(2)));
            binding.RequestDrain(TimeSpan.FromMilliseconds(100));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, result.PendingFacts);

            binding.CompletePublish();
            Assert.True(binding.PublishExited.Wait(TimeSpan.FromSeconds(2)));
            await Task.Delay(20);

            Assert.Single(CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            binding.CompletePublish();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationIgnoringApplicationStopReturnsDeadlineOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var application = new ControlledStopApplication();
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(application);
        binding.RequestDrain(TimeSpan.FromMilliseconds(100));

        try
        {
            await application.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(CollectorProtocolDrainReason.DeadlineExceeded, result.LogicalResult.Reason);
            Assert.False(result.LogicalResult.RemainderDurable);
            Assert.False(result.IsFullyDrained);
            AssertDrainConformance(result);
        }
        finally
        {
            application.ReleaseStop();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationDuringDrainPropagatesWithoutBecomingDeadlineOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-caller-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var application = new ControlledStopApplication();
        using var caller = new CancellationTokenSource();
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(application, caller.Token);
        binding.RequestDrain(TimeSpan.FromMinutes(1));

        try
        {
            await application.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            application.ReleaseStop();
            binding.CompletePublish();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationFencesLateFactAndGapFromCancellationIgnoringStop()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-caller-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var application = new LateTailAfterCancellationApplication();
        using var caller = new CancellationTokenSource();
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(application, caller.Token);
        binding.RequestDrain(TimeSpan.FromMinutes(1));

        try
        {
            await application.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(1)));

            application.ReleaseStop();
            var attempts = await application.LateAttempts.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsType<CollectorAdmissionClosedException>(attempts.FactError);
            Assert.IsType<CollectorAdmissionClosedException>(attempts.GapError);
            Assert.False(binding.PublishEntered.IsSet);
            var restarted = CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow);
            Assert.Empty(restarted.Facts);
            Assert.Empty(restarted.Gaps);
        }
        finally
        {
            application.ReleaseStop();
            binding.CompletePublish();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NormalCompletionTerminalFencesCapturedDrainContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-completed-fence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var binding = new FakeBinding(root, new Queue<CollectorFactDeliveryStatus>());
            var application = new CapturingDrainApplication();
            await using var client = new CollectorProtocolClient(Definition(), binding);

            var result = await client.RunAsync(application);

            Assert.True(result.IsFullyDrained);
            var drain = Assert.IsType<CollectorDrainContext>(application.Drain);
            await Assert.ThrowsAsync<CollectorAdmissionClosedException>(() => drain.PublishAsync(
                new CollectorFact(
                    "activity",
                    1,
                    Guid.CreateVersion7(),
                    1,
                    null,
                    CollectorFactRecordState.Present,
                    new CollectorEventFactTime(DateTimeOffset.UtcNow),
                    JsonSerializer.SerializeToElement(new { identityKey = "reference|completed-fact" })),
                CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<CollectorAdmissionClosedException>(() => drain.ReportGapAsync(
                new CollectorStreamGap(
                    Guid.CreateVersion7(),
                    "activity",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddTicks(1),
                    "completed_gap",
                    1),
                CancellationToken.None).AsTask());
            var restarted = CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow);
            Assert.Empty(restarted.Facts);
            Assert.Empty(restarted.Gaps);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SynchronouslyBlockingApplicationLifetimeCancellationCannotCrossDrainDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-cancel-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var application = new SynchronouslyBlockingCancellationApplication();
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(application);
        binding.RequestDrain(TimeSpan.FromMilliseconds(100));

        try
        {
            await application.CancellationEntered.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Contains(
                result.LogicalResult.Reason,
                new[]
                {
                    CollectorProtocolDrainReason.Drained,
                    CollectorProtocolDrainReason.DeadlineExceeded
                });
            Assert.Equal(
                result.LogicalResult.Reason == CollectorProtocolDrainReason.Drained,
                result.LogicalResult.IsFullyDrained);

            application.ReleaseCancellation();
            await application.CancellationReturned.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(20);
            Assert.Empty(CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            application.ReleaseCancellation();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DrainDeadlineCanFenceSynchronouslyBlockingApplicationStartInvocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-start-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var application = new SynchronouslyBlockingStartApplication();
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = Task.Run(() => client.RunAsync(application));

        try
        {
            await application.StartEntered.WaitAsync(TimeSpan.FromSeconds(2));
            binding.RequestDrain(TimeSpan.FromMilliseconds(100));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(CollectorProtocolDrainReason.DeadlineExceeded, result.LogicalResult.Reason);
            Assert.False(result.IsFullyDrained);

            application.ReleaseStart();
            await application.StartReturned.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(20);
            Assert.Empty(CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            application.ReleaseStart();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SynchronouslyBlockingApplicationStopCannotCrossDeadlineOrPersistLateFact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-stop-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root);
        var application = new SynchronouslyBlockingPublishingStopApplication();
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(application);
        binding.RequestDrain(TimeSpan.FromMilliseconds(100));

        try
        {
            await application.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(CollectorProtocolDrainReason.DeadlineExceeded, result.LogicalResult.Reason);
            Assert.Equal(0, result.PendingFacts);

            application.ReleaseStop();
            await application.StopReturned.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(20);

            Assert.Empty(CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            application.ReleaseStop();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationIgnoringFinalFlushCannotCrossDrainDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-final-flush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(root, ignoreCancellation: true);
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(new PublishingOnStopApplication());
        binding.RequestDrain(TimeSpan.FromMilliseconds(100));

        try
        {
            Assert.True(binding.PublishEntered.Wait(TimeSpan.FromSeconds(2)));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(CollectorProtocolDrainReason.FlushCancelled, result.LogicalResult.Reason);
            Assert.Equal(1, result.PendingFacts);
            Assert.False(result.IsFullyDrained);
        }
        finally
        {
            binding.CompletePublish();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationIgnoringCompletionCannotCrossDrainDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-final-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var binding = new SuspendedPublishBinding(
            root,
            ignoreCompletionCancellation: true);
        await using var client = new CollectorProtocolClient(Definition(), binding);
        var run = client.RunAsync(new IdleApplication());
        binding.RequestDrain(TimeSpan.FromMilliseconds(100));

        try
        {
            Assert.True(binding.CompletionEntered.Wait(TimeSpan.FromSeconds(2)));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(result.LogicalResult.IsFullyDrained);
            Assert.Equal(CollectorProtocolDrainCompletionReason.DeadlineExceeded, result.CompletionReason);
            Assert.False(result.IsFullyDrained);
        }
        finally
        {
            binding.CompleteCompletion();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletionFailureIsSeparateFromFullyDrainedLogicalOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var binding = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>(),
                new IOException("completion unavailable"));
            await using var client = new CollectorProtocolClient(Definition(), binding);

            var result = await client.RunAsync(new IdleApplication());

            Assert.True(result.LogicalResult.IsFullyDrained);
            Assert.Equal(CollectorProtocolDrainCompletionReason.CompletionFailed, result.CompletionReason);
            Assert.Equal("completion unavailable", result.CompletionError);
            Assert.False(result.IsFullyDrained);
            AssertDrainConformance(result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeadlineRemainderReplaysFromDurableOutboxOnRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fact = new CollectorFact(
            "activity",
            1,
            PublishingApplication.FactId,
            1,
            null,
            CollectorFactRecordState.Present,
            new CollectorEventFactTime(DateTimeOffset.UtcNow),
            JsonSerializer.SerializeToElement(new { identityKey = "reference|restart" }));
        CollectorProtocolOutbox.Open(root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Enqueue(fact);
        try
        {
            var retrying = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>(),
                fallbackOutcome: CollectorFactDeliveryStatus.Retry,
                drainGrace: TimeSpan.FromMilliseconds(100));
            await using (var first = new CollectorProtocolClient(Definition(), retrying))
            {
                var result = await first.RunAsync(new IdleApplication());
                Assert.Equal(CollectorProtocolDrainReason.FlushCancelled, result.LogicalResult.Reason);
                Assert.Equal(fact.FactId, Assert.Single(CollectorProtocolOutbox.Open(
                    root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts).Fact.FactId);
            }

            var committed = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>([CollectorFactDeliveryStatus.Committed]),
                fallbackOutcome: CollectorFactDeliveryStatus.Committed);
            await using (var restarted = new CollectorProtocolClient(Definition(), committed))
            {
                var result = await restarted.RunAsync(new IdleApplication());
                Assert.True(result.IsFullyDrained, result.ToString());
                Assert.NotEmpty(committed.PublishedFacts);
                Assert.All(committed.PublishedFacts, published => Assert.Equal(fact.FactId, published.FactId));
            }
            Assert.Empty(CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StopFailureHasStableNonDurableOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-stop-failed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var binding = new FakeBinding(root, new Queue<CollectorFactDeliveryStatus>());
            await using var client = new CollectorProtocolClient(Definition(), binding);

            var result = await client.RunAsync(new ThrowingStopApplication());

            Assert.Equal(CollectorProtocolDrainReason.StopFailed, result.LogicalResult.Reason);
            Assert.False(result.LogicalResult.RemainderDurable);
            Assert.False(result.IsFullyDrained);
            AssertDrainConformance(result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task PermanentPersistenceFailureIsReportedAsUnknownNonDurableRemainder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-drain-persistence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "collector-protocol-outbox.json"));
        try
        {
            var binding = new FakeBinding(
                root,
                new Queue<CollectorFactDeliveryStatus>(),
                drainGrace: TimeSpan.FromMilliseconds(100));
            await using var client = new CollectorProtocolClient(Definition(), binding);

            var result = await client.RunAsync(new PublishingOnStopApplication());

            Assert.Equal(CollectorProtocolDrainReason.PersistenceFailed, result.LogicalResult.Reason);
            Assert.False(result.LogicalResult.RemainderDurable);
            Assert.Equal(1, result.PendingFacts);
            Assert.False(result.IsFullyDrained);
            AssertDrainConformance(result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SupersededFailedAdmissionCannotReportMemoryOnlyRemainderAsDurable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-dirty-supersede-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var time = new VirtualTimeProvider();
        SuspendedPublishBinding? binding = null;
        var publicationAttempts = 0;
        binding = new SuspendedPublishBinding(
            root,
            ignoreCancellation: true,
            durableFilePublisher: (prepared, authoritative) =>
            {
                if (Interlocked.Increment(ref publicationAttempts) == 1)
                {
                    binding!.RequestDrain(DrainBudget);
                    throw new IOException("first ordinary admission publication failed");
                }
                File.Move(prepared, authoritative, overwrite: true);
                return true;
            },
            timeProvider: time);
        await using var client = new CollectorProtocolClient(Definition(), binding, time);
        var run = client.RunAsync(new PublishingApplication());

        try
        {
            Assert.True(binding.PublishEntered.Wait(TimeSpan.FromSeconds(2)));
            // The drain deadline is spent on the virtual clock once it is scheduled, so the superseded
            // admission is always still unpersisted when the deadline fires.
            await time.TimerScheduledAsync(DrainBudget).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(DrainBudget);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CollectorProtocolDrainReason.PersistenceFailed, result.LogicalResult.Reason);
            Assert.False(result.LogicalResult.RemainderDurable);
            Assert.Equal(1, result.PendingFacts);
            Assert.Empty(CollectorProtocolOutbox.Open(
                root, 16, Definition().Outputs, DateTimeOffset.UtcNow).Facts);
        }
        finally
        {
            binding.CompletePublish();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonRetryableGapRejectionBecomesOneDurableDiagnosticWithoutHotLoop(
        bool duringDrain)
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-protocol-gap-rejected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var gapId = Guid.Parse("0198d6d7-9f87-76ef-bf84-62bd5991353b");
        var diagnostics = new RecordingDiagnostics();
        var binding = new NonRetryableGapBinding(root, duringDrain);
        diagnostics.Reported = binding.RequestDrain;
        var definition = Definition() with { Diagnostics = diagnostics };
        await using var client = new CollectorProtocolClient(definition, binding);

        try
        {
            var result = await client.RunAsync(new GapPublishingApplication(gapId, duringDrain))
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(result.IsFullyDrained, result.ToString());
            Assert.Equal(1, binding.ReportCount);
            Assert.Equal(1, diagnostics.Values[^1].DeadLetterCount);
            Assert.Equal(
                Path.Combine(root, "collector-protocol-gap-dead-letter.json"),
                diagnostics.Values[^1].DeadLetterPath);
            Assert.Empty(CollectorProtocolOutbox.Open(
                root, 16, definition.Outputs, DateTimeOffset.UtcNow).Gaps);

            using var deadLetters = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(root, "collector-protocol-gap-dead-letter.json")));
            var rejected = Assert.Single(deadLetters.RootElement
                .GetProperty("State")
                .GetProperty("Entries")
                .EnumerateArray());
            var messageId = rejected.GetProperty("MessageId").GetGuid();
            Assert.NotEqual(Guid.Empty, messageId);
            Assert.NotEqual(gapId, messageId);
            Assert.Equal(gapId, rejected.GetProperty("Gap").GetProperty("GapId").GetGuid());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertDrainConformance(CollectorDrainExecutionResult result)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Conformance",
            "collector-protocol-conformance.json")));
        var reason = CollectorProtocolDrainVocabulary.Format(result.LogicalResult.Reason);
        var expected = corpus.RootElement.GetProperty("drainOutcomes").EnumerateArray()
            .Single(item => item.GetProperty("reason").GetString() == reason);
        Assert.Equal(
            expected.GetProperty("remainderDurable").GetBoolean(),
            result.LogicalResult.RemainderDurable);
        Assert.Equal(
            expected.GetProperty("canBeFullyDrained").GetBoolean(),
            result.LogicalResult.IsFullyDrained);
        Assert.Contains(
            CollectorProtocolDrainVocabulary.Format(result.CompletionReason),
            corpus.RootElement.GetProperty("completionOutcomes").EnumerateArray()
                .Select(item => item.GetString()));
    }

    private static CollectorClientDefinition Definition() => new(
        "reference.managed",
        new Dictionary<string, IReadOnlyList<int>>
        {
            ["facts.segment"] = [1],
            ["diagnostics.stream-gap"] = [1]
        },
        "account",
        [new CollectorOutputBinding("activity", "activity", new Dictionary<string, string>())]);

    private sealed class PublishingApplication : ICollectorProtocolApplication
    {
        public static readonly Guid FactId = Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999");

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            activation.PublishAsync(new CollectorFact(
                "activity",
                1,
                FactId,
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorSegmentFactTime(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), false),
                JsonSerializer.SerializeToElement(new { identityKey = "reference|online", title = "Online" })),
                cancellationToken);

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class GapPublishingApplication(Guid gapId, bool duringDrain)
        : ICollectorProtocolApplication
    {
        private static CollectorStreamGap Gap(Guid id) => new(
            id,
            "activity",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddTicks(1),
            "fixture_gap",
            1);

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            duringDrain
                ? ValueTask.CompletedTask
                : activation.ReportGapAsync(Gap(gapId), cancellationToken);

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            duringDrain
                ? drain.ReportGapAsync(Gap(gapId), cancellationToken)
                : ValueTask.CompletedTask;
    }

    private sealed class RecordingDiagnostics : ICollectorClientDiagnostics
    {
        public List<CollectorClientDiagnostic> Values { get; } = [];
        public Action? Reported { get; set; }

        public void Report(CollectorClientDiagnostic diagnostic)
        {
            Values.Add(diagnostic);
            if (diagnostic.DeadLetterCount != 0)
                Reported?.Invoke();
        }
    }


    private sealed class IdleApplication : ICollectorProtocolApplication
    {
        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ControlledStopApplication : ICollectorProtocolApplication
    {
        private readonly TaskCompletionSource _releaseStop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopEntered => _stopEntered.Task;

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken)
        {
            _stopEntered.TrySetResult();
            return new ValueTask(_releaseStop.Task);
        }

        public void ReleaseStop() => _releaseStop.TrySetResult();
    }

    private sealed class LateTailAfterCancellationApplication : ICollectorProtocolApplication
    {
        private readonly TaskCompletionSource _releaseStop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(Exception? FactError, Exception? GapError)> _lateAttempts =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopEntered => _stopEntered.Task;
        public Task<(Exception? FactError, Exception? GapError)> LateAttempts => _lateAttempts.Task;

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken)
        {
            _stopEntered.TrySetResult();
            await _releaseStop.Task.ConfigureAwait(false);
            Exception? factError = null;
            Exception? gapError = null;
            try
            {
                await drain.PublishAsync(new CollectorFact(
                    "activity",
                    1,
                    Guid.CreateVersion7(),
                    1,
                    null,
                    CollectorFactRecordState.Present,
                    new CollectorEventFactTime(DateTimeOffset.UtcNow),
                    JsonSerializer.SerializeToElement(new { identityKey = "reference|late-caller-fact" })),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                factError = exception;
            }
            try
            {
                await drain.ReportGapAsync(new CollectorStreamGap(
                    Guid.CreateVersion7(),
                    "activity",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddTicks(1),
                    "late_caller_gap",
                    1),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                gapError = exception;
            }
            _lateAttempts.TrySetResult((factError, gapError));
        }

        public void ReleaseStop() => _releaseStop.TrySetResult();
    }

    private sealed class CapturingDrainApplication : ICollectorProtocolApplication
    {
        public CollectorDrainContext? Drain { get; private set; }

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken)
        {
            Drain = drain;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronouslyBlockingCancellationApplication : ICollectorProtocolApplication
    {
        private readonly ManualResetEventSlim _releaseCancellation = new(false);
        private readonly TaskCompletionSource _cancellationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CancellationEntered => _cancellationEntered.Task;
        public Task CancellationReturned => _cancellationReturned.Task;

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() =>
            {
                _cancellationEntered.TrySetResult();
                _releaseCancellation.Wait();
                try
                {
                    activation.PublishAsync(new CollectorFact(
                        "activity",
                        1,
                        Guid.CreateVersion7(),
                        1,
                        null,
                        CollectorFactRecordState.Present,
                        new CollectorEventFactTime(DateTimeOffset.UtcNow),
                        JsonSerializer.SerializeToElement(new { identityKey = "reference|late-cancel" })),
                        CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    // Admission must already be fenced when cancellation work returns late.
                }
                finally
                {
                    _cancellationReturned.TrySetResult();
                }
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ReleaseCancellation() => _releaseCancellation.Set();
    }

    private sealed class SynchronouslyBlockingStartApplication : ICollectorProtocolApplication
    {
        private readonly ManualResetEventSlim _releaseStart = new(false);
        private readonly TaskCompletionSource _startEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartEntered => _startEntered.Task;
        public Task StartReturned => _startReturned.Task;

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken)
        {
            _startEntered.TrySetResult();
            _releaseStart.Wait();
            try
            {
                return activation.PublishAsync(new CollectorFact(
                    "activity",
                    1,
                    Guid.CreateVersion7(),
                    1,
                    null,
                    CollectorFactRecordState.Present,
                    new CollectorEventFactTime(DateTimeOffset.UtcNow),
                    JsonSerializer.SerializeToElement(new { identityKey = "reference|late-start" })),
                    CancellationToken.None);
            }
            finally
            {
                _startReturned.TrySetResult();
            }
        }

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ReleaseStart() => _releaseStart.Set();
    }

    private sealed class SynchronouslyBlockingIdleStartApplication : ICollectorProtocolApplication, IDisposable
    {
        private readonly ManualResetEventSlim _releaseStart = new(false);
        private readonly TaskCompletionSource _startEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _lifetimeToken;

        public Task StartEntered => _startEntered.Task;
        public bool LifetimeCancellationRequested => _lifetimeToken.IsCancellationRequested;

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken)
        {
            _lifetimeToken = cancellationToken;
            _startEntered.TrySetResult();
            _releaseStart.Wait();
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ReleaseStart() => _releaseStart.Set();

        public void Dispose() => _releaseStart.Dispose();
    }

    private sealed class SynchronouslyBlockingPublishingStopApplication : ICollectorProtocolApplication
    {
        private readonly ManualResetEventSlim _releaseStop = new(false);
        private readonly TaskCompletionSource _stopEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopEntered => _stopEntered.Task;
        public Task StopReturned => _stopReturned.Task;

        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken)
        {
            _stopEntered.TrySetResult();
            _releaseStop.Wait();
            try
            {
                return drain.PublishAsync(new CollectorFact(
                    "activity",
                    1,
                    Guid.CreateVersion7(),
                    1,
                    null,
                    CollectorFactRecordState.Present,
                    new CollectorEventFactTime(DateTimeOffset.UtcNow),
                    JsonSerializer.SerializeToElement(new { identityKey = "reference|late-stop" })),
                    CancellationToken.None);
            }
            finally
            {
                _stopReturned.TrySetResult();
            }
        }

        public void ReleaseStop() => _releaseStop.Set();
    }

    private sealed class ThrowingStopApplication : IdleApplicationBase
    {
        public override ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("stop failed"));
    }

    private sealed class PublishingOnStopApplication : IdleApplicationBase
    {
        public override ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken) =>
            drain.PublishAsync(new CollectorFact(
                "activity",
                1,
                PublishingApplication.FactId,
                1,
                null,
                CollectorFactRecordState.Present,
                new CollectorEventFactTime(DateTimeOffset.UtcNow),
                JsonSerializer.SerializeToElement(new { identityKey = "reference|persistence" })),
                cancellationToken);
    }

    private abstract class IdleApplicationBase : ICollectorProtocolApplication
    {
        public ValueTask InitializeAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StartAsync(CollectorActivation activation, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public abstract ValueTask StopAsync(CollectorDrainContext drain, CancellationToken cancellationToken);
    }

    private sealed class FakeBinding(
        string dataDirectory,
        Queue<CollectorFactDeliveryStatus> outcomes,
        Exception? completionFailure = null,
        CollectorFactDeliveryStatus? fallbackOutcome = null,
        TimeSpan? drainGrace = null,
        int drainAfterPublishes = 0,
        Func<string, string, bool>? durableFilePublisher = null) : ICollectorProtocolBinding
    {
        private readonly TaskCompletionSource _publishThresholdReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Guid> PublishMessageIds { get; } = [];
        public List<BoundCollectorFact> PublishedFacts { get; } = [];

        public ValueTask<CollectorClientInitialization> StartAsync(
            CollectorClientDefinition definition,
            CancellationToken cancellationToken) => ValueTask.FromResult(new CollectorClientInitialization(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "account",
                1,
                1,
                JsonSerializer.SerializeToElement(new { }),
                500,
                1_048_576,
                dataDirectory,
                definition.Capabilities.ToDictionary(pair => pair.Key, _ => 1)));

        public ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> OpenStreamsAsync(
            long specRevision,
            IReadOnlyList<CollectorOutputBinding> outputs,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<string, CollectorClientStream>>(
            new Dictionary<string, CollectorClientStream>
            {
                ["activity"] = new(
                    "activity", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "account",
                    "activity", "reference.account", "segment", "heartbeat.reference.segment", 1, 1,
                    "sha256:test", new Dictionary<string, string>())
            });

        public ValueTask ReadyAsync(long appliedSpecRevision, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<CollectorFactBatchAcknowledgement> PublishAsync(
            Guid messageId,
            IReadOnlyList<BoundCollectorFact> facts,
            CancellationToken cancellationToken)
        {
            PublishMessageIds.Add(messageId);
            PublishedFacts.AddRange(facts);
            if (drainAfterPublishes > 0 && PublishMessageIds.Count >= drainAfterPublishes)
                _publishThresholdReached.TrySetResult();
            var status = outcomes.Count == 0
                ? fallbackOutcome ?? throw new InvalidOperationException("Fixture has no Fact outcome.")
                : outcomes.Dequeue();
            var error = status is CollectorFactDeliveryStatus.Rejected or CollectorFactDeliveryStatus.Retry
                ? new CollectorProtocolError("fixture", "Fixture outcome.", status == CollectorFactDeliveryStatus.Retry)
                : null;
            return ValueTask.FromResult(new CollectorFactBatchAcknowledgement(
                [new CollectorFactDeliveryOutcome(
                    0,
                    status,
                    error,
                    status == CollectorFactDeliveryStatus.Retry ? 1 : null)]));
        }

        public ValueTask<CollectorGapDeliveryOutcome> ReportGapAsync(
            Guid messageId,
            Guid streamId,
            CollectorStreamGap gap,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new CollectorGapDeliveryOutcome(CollectorGapDeliveryStatus.Committed));

        public ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
            Guid interactionId,
            string kind,
            string title,
            string? message,
            IReadOnlyList<CollectorAuthorizationField> fields,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async ValueTask<CollectorDrainRequest> WaitForDrainAsync(CancellationToken cancellationToken)
        {
            if (drainAfterPublishes > 0)
                await _publishThresholdReached.Task.WaitAsync(cancellationToken);
            return new CollectorDrainRequest(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow.Add(drainGrace ?? TimeSpan.FromSeconds(5)));
        }

        public ValueTask CompleteDrainAsync(CollectorDrainResult result, CancellationToken cancellationToken) =>
            completionFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(completionFailure);

        public bool TryPublishDurableFile(string preparedPath, string authoritativePath) =>
            durableFilePublisher?.Invoke(preparedPath, authoritativePath) ??
            PublishUnfenced(preparedPath, authoritativePath);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static bool PublishUnfenced(string preparedPath, string authoritativePath)
        {
            File.Move(preparedPath, authoritativePath, overwrite: true);
            return true;
        }
    }

    private sealed class NonRetryableGapBinding(string dataDirectory, bool drainInitially)
        : ICollectorProtocolBinding
    {
        private readonly TaskCompletionSource<CollectorDrainRequest> _drain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reportCount;

        public int ReportCount => Volatile.Read(ref _reportCount);

        public void RequestDrain() => _drain.TrySetResult(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow.AddMilliseconds(100)));

        public ValueTask<CollectorClientInitialization> StartAsync(
            CollectorClientDefinition definition,
            CancellationToken cancellationToken)
        {
            if (drainInitially)
                RequestDrain();
            return ValueTask.FromResult(new CollectorClientInitialization(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "account", 1, 1,
                JsonSerializer.SerializeToElement(new { }), 500, 1_048_576, dataDirectory,
                definition.Capabilities.ToDictionary(pair => pair.Key, _ => 1)));
        }

        public ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> OpenStreamsAsync(
            long specRevision,
            IReadOnlyList<CollectorOutputBinding> outputs,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<string, CollectorClientStream>>(
            new Dictionary<string, CollectorClientStream>
            {
                ["activity"] = new(
                    "activity", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "account",
                    "activity", "reference.account", "segment", "heartbeat.reference.segment", 1, 1,
                    "sha256:test", new Dictionary<string, string>())
            });

        public ValueTask ReadyAsync(long appliedSpecRevision, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<CollectorFactBatchAcknowledgement> PublishAsync(
            Guid messageId,
            IReadOnlyList<BoundCollectorFact> facts,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No Fact expected.");

        public ValueTask<CollectorGapDeliveryOutcome> ReportGapAsync(
            Guid messageId,
            Guid streamId,
            CollectorStreamGap gap,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reportCount) == 2)
                RequestDrain();
            return ValueTask.FromResult(new CollectorGapDeliveryOutcome(
                CollectorGapDeliveryStatus.Rejected,
                new CollectorProtocolError("gap_rejected", "fixture rejection", Retryable: false)));
        }

        public ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
            Guid interactionId, string kind, string title, string? message,
            IReadOnlyList<CollectorAuthorizationField> fields, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<CollectorDrainRequest> WaitForDrainAsync(CancellationToken cancellationToken) =>
            new(_drain.Task.WaitAsync(cancellationToken));
        public ValueTask CompleteDrainAsync(CollectorDrainResult result, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<
            (SendOrPostCallback Callback, object? State)> _callbacks = [];

        public override void Post(SendOrPostCallback d, object? state) => _callbacks.Add((d, state));

        public bool RunOne(TimeSpan timeout)
        {
            if (!_callbacks.TryTake(out var work, timeout))
                return false;
            work.Callback(work.State);
            return true;
        }
    }

    /// <summary>
    /// Virtual clock for drain deadlines and persistence retries. Tests spend the drain budget by
    /// advancing it, and wait for a scheduled delay instead of guessing how fast the machine is.
    /// </summary>
    private sealed class VirtualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<VirtualTimer> _timers = [];
        private readonly Dictionary<TimeSpan, TaskCompletionSource> _scheduled = [];
        private DateTimeOffset _utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state, dueTime, period);
            TaskCompletionSource? scheduled;
            lock (_gate)
            {
                _timers.Add(timer);
                _ = _scheduled.Remove(dueTime, out scheduled);
            }
            scheduled?.TrySetResult();
            if (dueTime <= TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                timer.Fire();
            return timer;
        }

        /// <summary>Completes once a delay of exactly this duration is scheduled on the clock.</summary>
        public Task TimerScheduledAsync(TimeSpan dueTime)
        {
            lock (_gate)
            {
                if (_timers.Any(timer => timer.Matches(dueTime)))
                    return Task.CompletedTask;
                if (!_scheduled.TryGetValue(dueTime, out var scheduled))
                {
                    scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _scheduled[dueTime] = scheduled;
                }
                return scheduled.Task;
            }
        }

        public void Advance(TimeSpan duration)
        {
            VirtualTimer[] due;
            lock (_gate)
            {
                _utcNow += duration;
                due = [.. _timers.Where(timer => timer.IsDue(_utcNow)).OrderBy(timer => timer.DueAt)];
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class VirtualTimer(
            VirtualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private bool _disposed;

            public DateTimeOffset DueAt { get; private set; } = owner.GetUtcNow() + dueTime;

            public bool Matches(TimeSpan scheduledDueTime) => !_disposed && dueTime == scheduledDueTime;

            public bool IsDue(DateTimeOffset now) =>
                !_disposed && dueTime != Timeout.InfiniteTimeSpan && now >= DueAt;

            public void Fire()
            {
                if (_disposed)
                    return;
                if (period == Timeout.InfiniteTimeSpan)
                    _disposed = true;
                else
                    DueAt += period;
                callback(state);
            }

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                dueTime = newDueTime;
                period = newPeriod;
                DueAt = owner.GetUtcNow() + newDueTime;
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

    private sealed class SuspendedPublishBinding(
        string dataDirectory,
        bool ignoreCancellation = false,
        bool ignoreCompletionCancellation = false,
        Func<string, string, bool>? durableFilePublisher = null,
        TimeProvider? timeProvider = null) : ICollectorProtocolBinding
    {
        private readonly TaskCompletionSource<CollectorFactBatchAcknowledgement> _publish =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CollectorDrainRequest> _drain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim PublishEntered { get; } = new();
        public ManualResetEventSlim PublishExited { get; } = new();
        public ManualResetEventSlim CompletionEntered { get; } = new();

        public void CompletePublish() => _publish.TrySetResult(new CollectorFactBatchAcknowledgement(
            [new CollectorFactDeliveryOutcome(0, CollectorFactDeliveryStatus.Committed)]));

        public void CompleteCompletion() => _completion.TrySetResult();

        public void RequestDrain(TimeSpan? grace = null) => _drain.TrySetResult(new CollectorDrainRequest(
            Guid.CreateVersion7(),
            (timeProvider ?? TimeProvider.System).GetUtcNow().Add(grace ?? TimeSpan.FromSeconds(5))));

        public ValueTask<CollectorClientInitialization> StartAsync(
            CollectorClientDefinition definition,
            CancellationToken cancellationToken) => ValueTask.FromResult(new CollectorClientInitialization(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "account",
                1,
                1,
                JsonSerializer.SerializeToElement(new { }),
                500,
                1_048_576,
                dataDirectory,
                definition.Capabilities.ToDictionary(pair => pair.Key, _ => 1)));

        public ValueTask<IReadOnlyDictionary<string, CollectorClientStream>> OpenStreamsAsync(
            long specRevision,
            IReadOnlyList<CollectorOutputBinding> outputs,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<string, CollectorClientStream>>(
                new Dictionary<string, CollectorClientStream>
                {
                    ["activity"] = new(
                        "activity", Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "account",
                        "activity", "reference.account", "segment", "heartbeat.reference.segment", 1, 1,
                        "sha256:test", new Dictionary<string, string>())
                });

        public ValueTask ReadyAsync(long appliedSpecRevision, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<CollectorFactBatchAcknowledgement> PublishAsync(
            Guid messageId,
            IReadOnlyList<BoundCollectorFact> facts,
            CancellationToken cancellationToken)
        {
            PublishEntered.Set();
            try
            {
                return ignoreCancellation
                    ? await _publish.Task
                    : await _publish.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                PublishExited.Set();
            }
        }

        public ValueTask<CollectorGapDeliveryOutcome> ReportGapAsync(
            Guid messageId,
            Guid streamId,
            CollectorStreamGap gap,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new CollectorGapDeliveryOutcome(CollectorGapDeliveryStatus.Committed));

        public ValueTask<CollectorAuthorizationResponse> ChallengeAsync(
            Guid interactionId,
            string kind,
            string title,
            string? message,
            IReadOnlyList<CollectorAuthorizationField> fields,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CompleteAuthorizationAsync(Guid interactionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<string?> ReadSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask WriteSecretAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DeleteSecretAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CollectorDrainRequest> WaitForDrainAsync(CancellationToken cancellationToken) =>
            new(_drain.Task);

        public async ValueTask CompleteDrainAsync(
            CollectorDrainResult result,
            CancellationToken cancellationToken)
        {
            if (!ignoreCompletionCancellation)
                return;
            CompletionEntered.Set();
            await _completion.Task;
        }

        public bool TryPublishDurableFile(string preparedPath, string authoritativePath)
        {
            if (durableFilePublisher is not null)
                return durableFilePublisher(preparedPath, authoritativePath);
            File.Move(preparedPath, authoritativePath, overwrite: true);
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
