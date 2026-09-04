using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Input;
using Serilog;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public sealed partial class CollectorRuntime
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> HubProtocolCapabilities =
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["facts.event"] = [1],
            ["auth.interactive"] = [1],
            ["secrets.instance"] = [1],
            ["resources.instance-data"] = [1],
            ["diagnostics.stream-gap"] = [1]
        };

    private readonly Dictionary<Guid, InProcessCollectorActivation> _activations = [];
    private readonly Dictionary<Guid, PendingActivationCommit> _pendingActivationCommits = [];
    private readonly Dictionary<string, FactSchemaDocument> _factSchemasByHash = new(StringComparer.Ordinal);
    // Collector Protocol v1 has one executable Segment shape: ActivitySegment. Package-owned
    // schemas refine that payload, while this projector enforces the common projection fields.
    private readonly ActivitySegmentFactProjector _segmentProjector;
    private readonly IReadOnlyList<IEventFactProjector> _eventProjectors;
    private readonly Dictionary<Guid, Guid> _streamWriters = [];
    private readonly HashSet<Guid> _startingInstances = [];
    private readonly Dictionary<Guid, CollectorActivationLifetime> _activationLifetimes = [];
    private readonly object _helloAttemptGate = new();
    private readonly Dictionary<(Guid InstanceId, Guid MessageId), HelloAttempt> _helloAttempts = [];

    public ValueTask<InProcessCollectorActivation> ActivateInProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        IInProcessCollector collector,
        CancellationToken cancellationToken = default) =>
        ActivateInProcessAsync(
            collectorInstanceId,
            package,
            collector,
            Guid.CreateVersion7(),
            cancellationToken);

    public ValueTask<InProcessCollectorActivation> ActivateInProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        IInProcessCollector collector,
        Guid helloMessageId,
        CancellationToken cancellationToken = default) =>
        ActivateProtocolAsync(
            collectorInstanceId,
            package,
            collector,
            "inProcess",
            helloMessageId,
            cancellationToken);

    private async ValueTask<InProcessCollectorActivation> ActivateProtocolAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        IInProcessCollector collector,
        string executionDriver,
        Guid helloMessageId,
        CancellationToken cancellationToken,
        Func<CollectorActivationSession, ICollectorActivationLifetimeDriver>? lifetimeDriver = null,
        TimeSpan? drainBudget = null,
        Action<CollectorActivationLifetime>? lifetimeCreated = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(collector);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUuidV7(helloMessageId))
            throw ActivationError(
                "protocol_invalid_message",
                "activation.hello messageId must be a UUIDv7.");

        string artifactId;
        ProtocolSupport? protocolSupport;
        try
        {
            artifactId = collector.ArtifactId;
            protocolSupport = SnapshotProtocolSupport(collector.ProtocolSupport);
        }
        catch (Exception exception)
        {
            throw ActivationError(
                "protocol_invalid_message",
                "InProcess Collector failed to provide activation.hello fields.",
                exception);
        }

        string helloRequestHash;
        try
        {
            helloRequestHash = HelloRequestHash(
                collectorInstanceId,
                package,
                artifactId,
                protocolSupport);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            throw ActivationError(
                "protocol_invalid_message",
                "InProcess Collector returned malformed activation.hello fields.",
                exception);
        }
        HelloAttempt? helloAttempt = null;
        Task<InProcessCollectorActivation>? replayTask = null;
        lock (_helloAttemptGate)
        {
            if (_helloAttempts.TryGetValue((collectorInstanceId, helloMessageId), out var existing))
            {
                if (existing.RequestHash != helloRequestHash)
                    throw ActivationError(
                        "protocol_invalid_message",
                        "The same activation.hello messageId was reused with different content.");
                replayTask = existing.Completion.Task;
            }
            else
            {
                helloAttempt = new HelloAttempt(
                    helloRequestHash,
                    new TaskCompletionSource<InProcessCollectorActivation>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                _helloAttempts.Add((collectorInstanceId, helloMessageId), helloAttempt);
            }
        }
        if (replayTask is not null)
            return await replayTask.WaitAsync(cancellationToken);
        var ownedHelloAttempt = helloAttempt!;
        var registeredStartingInstance = false;
        InProcessCollectorActivation? activation = null;
        CollectorActivationSession? session = null;
        CollectorActivationLifetime? lifetime = null;

        try
        {
            CollectorInstance instance;
            VerifiedCollectorArtifact artifact;
            Guid activationId;
            lock (_gate)
            {
                ThrowIfDisposed();
                var instanceState = GetInstanceStateLocked(collectorInstanceId);
                ValidatePackageCandidate(instanceState, package);
                instance = ToPublic(instanceState) with
                {
                    PackageVersion = package.Manifest.Version,
                    PackageContentHash = package.PackageContentHash
                };
                artifact = ResolveProtocolArtifact(
                    package,
                    artifactId,
                    executionDriver);
                ValidateProtocolSupport(package, protocolSupport);
                activationId = NextUniqueId(
                    id => _activations.ContainsKey(id) ||
                          _state.ActivationAttemptTombstones.Any(attempt => attempt.ActivationId == id),
                    "Collector Activation");
                PersistActivationAttemptTombstoneLocked(
                    collectorInstanceId,
                    helloMessageId,
                    helloRequestHash,
                    activationId);
                if (_startingInstances.Contains(collectorInstanceId) ||
                    HasExternalHostActivationLocked(collectorInstanceId) ||
                    _activations.Values.Any(activation =>
                        activation.State != CollectorActivationState.Stopped &&
                        activation.Streams.Values.Any(stream =>
                            stream.Descriptor.CollectorInstanceId == collectorInstanceId)))
                    throw ActivationError(
                        "stream_writer_conflict",
                        "Stop the current Collector Activation before starting its replacement.");
                _startingInstances.Add(collectorInstanceId);
                registeredStartingInstance = true;
                session = CreateActivationSession(
                    activationId,
                    helloMessageId,
                    package,
                    ActivationDeliveryCapability.Complete);
                lifetime = new CollectorActivationLifetime(
                    lifetimeDriver?.Invoke(session) ??
                        new InProcessCollectorActivationLifetimeDriver(collector, session),
                    session.FenceDeliveryAfterDeadline,
                    () => CompleteActivationLifetime(
                        collectorInstanceId,
                        activationId,
                        helloMessageId,
                        activation),
                    _options.TimeProvider,
                    drainBudget ?? _options.InProcessDrainGracePeriod);
                _activationLifetimes.Add(activationId, lifetime);
                lifetimeCreated?.Invoke(lifetime);
            }

            using var activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime!.StopRequested);
            var activationToken = activationCancellation.Token;
            var initialization = new CollectorInitialization(
                activationId,
                instance,
                instance.Spec,
                artifact,
                new CollectorProtocolLimits(
                    _options.MaxFactsPerBatch,
                    _options.MaxBatchBytes),
                new CollectorResources(Path.Combine(
                    _instanceDataRoot,
                    collectorInstanceId.ToString("N")),
                    session!.DurableCommitFence));
            InProcessCollectorInitialization initialized;
            try
            {
                if (lifetime.HasStopIntent)
                    throw new OperationCanceledException(lifetime.StopRequested);
                initialized = await collector.InitializeAsync(initialization, activationToken);
                if (initialized is null)
                    throw ActivationError(
                        "protocol_invalid_message",
                        "InProcess Collector returned a null activation.initialized response.");
                initialized = SnapshotInitialization(initialized);
                session!.AcceptInitialized(initialized.AppliedSpecRevision, instance.Spec.SpecRevision);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw ActivationError(
                    "protocol_invalid_message",
                    "InProcess Collector rejected or failed activation.initialize.",
                    exception);
            }

            activationToken.ThrowIfCancellationRequested();
            InProcessCollectorStreamsOpened opened;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (lifetime.HasStopIntent)
                    throw ActivationError(
                        "activation_stopping",
                        "Collector Activation is stopping before streams.open.");
                if (initialized.AppliedSpecRevision != instance.Spec.SpecRevision)
                    throw ActivationError(
                        "spec_revision_stale",
                        "Collector did not apply the current SpecRevision.");

                var streamPlan = PlanStreams(
                    activationId,
                    instance,
                    package,
                    initialized.Bindings);
                var descriptors = streamPlan.Bindings.ToImmutableDictionary(
                    pair => pair.BindingId,
                    pair => ToDescriptor(pair.Stream),
                    StringComparer.Ordinal);
                session!.AcceptStreams(descriptors);
                var streams = streamPlan.Bindings.ToImmutableDictionary(
                    pair => pair.BindingId,
                    pair => new InProcessFactStream(
                        session,
                        ToDescriptor(pair.Stream)),
                    StringComparer.Ordinal);
                activation = new InProcessCollectorActivation(
                    session,
                    lifetime,
                    streams);
                _activations.Add(activationId, activation);
                _pendingActivationCommits.Add(activationId, streamPlan.Commit);
                opened = new InProcessCollectorStreamsOpened(
                    activationId,
                    streams.ToImmutableDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Descriptor,
                        StringComparer.Ordinal),
                    () => CompleteCollectorReadyAsync(activation, lifetime));
            }

            try
            {
                await Task.Factory.StartNew(
                    async () =>
                    {
                        activationToken.ThrowIfCancellationRequested();
                        await collector.OnStreamsOpenedAsync(opened, activationToken);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CollectorActivationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw ActivationError(
                    "protocol_invalid_message",
                    "InProcess Collector failed to complete streams.open/ready.",
                    exception);
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                if (activation.State != CollectorActivationState.Ready)
                    throw ActivationError(
                        "protocol_invalid_message",
                        "InProcess Collector returned before sending activation.ready.");
                _startingInstances.Remove(collectorInstanceId);
                registeredStartingInstance = false;
            }
            ownedHelloAttempt.Completion.SetResult(activation);
            return activation;
        }
        catch (Exception exception)
        {
            if (lifetime is not null)
            {
                var terminal = await lifetime.RequestStopAsync(
                    new CollectorActivationStopIntent(CollectorActivationStopCause.ActivationFailed));
                if (!terminal.OwnershipReleased)
                {
                    Log.Warning(
                        terminal.ReleaseError,
                        "停止失败的 Collector Activation 后仍保留 ownership");
                }
            }
            else if (registeredStartingInstance)
            {
                lock (_gate)
                    _startingInstances.Remove(collectorInstanceId);
            }
            ownedHelloAttempt.Completion.TrySetException(exception);
            _ = ownedHelloAttempt.Completion.Task.Exception;
            throw;
        }
    }

    private FactBatchAcknowledgement CommitFacts(
        Guid activationId,
        Guid streamId,
        IReadOnlyList<FactSubmission> facts,
        ActivationDeliveryFence deliveryFence)
    {
        var results = new List<FactDeliveryOutcome>(facts.Count);
        for (var index = 0; index < facts.Count; index++)
            results.Add(CommitFact(activationId, index, facts[index], deliveryFence));
        MarkAcknowledgedLiveTraffic(streamId, results);
        return new FactBatchAcknowledgement(results);
    }

    private void CompleteActivationLifetime(
        Guid collectorInstanceId,
        Guid activationId,
        Guid helloMessageId,
        InProcessCollectorActivation? activation)
    {
        lock (_gate)
        {
            if (activation is not null)
            {
                foreach (var streamId in activation.Streams.Values.Select(stream => stream.Descriptor.StreamId))
                {
                    if (_streamWriters.TryGetValue(streamId, out var writer) && writer == activationId)
                        _streamWriters.Remove(streamId);
                }
            }
            _activations.Remove(activationId);
            _activationLifetimes.Remove(activationId);
            _pendingActivationCommits.Remove(activationId);
            _startingInstances.Remove(collectorInstanceId);
        }
        if (activation is not null)
        {
            lock (_helloAttemptGate)
                _helloAttempts.Remove((collectorInstanceId, helloMessageId));
        }
    }

    private async ValueTask<InProcessCollectorActivation> CompleteCollectorReadyAsync(
        InProcessCollectorActivation activation,
        CollectorActivationLifetime lifetime)
    {
        long expectedSpecRevision;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var pendingCommit))
                throw ActivationError(
                    "protocol_invalid_message",
                    "Collector Activation has no pending Package and Stream state to commit.");
            expectedSpecRevision = pendingCommit.Instance.SpecRevision;
        }
        var ready = await lifetime.PublishReadyAsync(new CollectorReadyPublication(() =>
            ValueTask.FromResult(PrepareCollectorReady(
                activation,
                expectedSpecRevision))));
        if (ready == CollectorReadyOutcome.Stopping)
            throw ActivationError(
                "activation_stopping",
                "Collector Activation is stopping before activation.ready.");
        return activation;
    }

    private CollectorReadyPreparedCommit PrepareCollectorReady(
        InProcessCollectorActivation activation,
        long expectedSpecRevision)
    {
        CollectorRuntimeState baseState;
        CollectorRuntimeState next;
        PendingActivationCommit pendingCommit;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var currentPendingCommit))
                throw ActivationError(
                    "protocol_invalid_message",
                    "Collector Activation has no pending Package and Stream state to commit.");
            pendingCommit = currentPendingCommit;
            baseState = _state;
            next = baseState.WithInstanceAndStreams(
                pendingCommit.Instance,
                pendingCommit.Streams);
        }

        JsonCollectorRuntimeStore.PreparedReplacement replacement;
        try
        {
            replacement = _store.Prepare(next);
        }
        catch (CollectorRuntimeStateException exception)
        {
            throw ActivationError(
                "hub_backpressure",
                "Hub could not prepare the resolved Package and Fact Streams.",
                exception,
                retryable: true);
        }

        return new CollectorReadyPreparedCommit(
            () => TryCommitCollectorReady(
                activation,
                expectedSpecRevision,
                baseState,
                next,
                pendingCommit,
                replacement),
            replacement);
    }

    private bool TryCommitCollectorReady(
        InProcessCollectorActivation activation,
        long expectedSpecRevision,
        CollectorRuntimeState baseState,
        CollectorRuntimeState next,
        PendingActivationCommit pendingCommit,
        JsonCollectorRuntimeStore.PreparedReplacement replacement)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(_state, baseState) ||
                !_pendingActivationCommits.TryGetValue(activation.ActivationId, out var current) ||
                !ReferenceEquals(current, pendingCommit))
                return false;
            foreach (var stream in activation.Streams.Values)
            {
                if (_streamWriters.TryGetValue(stream.Descriptor.StreamId, out var writer) &&
                    writer != activation.ActivationId)
                    throw ActivationError(
                        "stream_writer_conflict",
                        "A previous Activation still holds the Fact Stream writer lease.");
            }
            activation.Session.AcceptReady(
                expectedSpecRevision,
                expectedSpecRevision,
                () =>
                {
                    try
                    {
                        replacement.Commit();
                    }
                    catch (CollectorRuntimeStateException exception)
                    {
                        throw ActivationError(
                            "hub_backpressure",
                            "Hub could not publish the resolved Package and Fact Streams.",
                            exception,
                            retryable: true);
                    }
                    _state = next;
                    _pendingActivationCommits.Remove(activation.ActivationId);
                    foreach (var schema in activation.Package.FactSchemas)
                        _factSchemasByHash[schema.ContentHash] = schema;
                    foreach (var stream in activation.Streams.Values)
                        _streamWriters[stream.Descriptor.StreamId] = activation.ActivationId;
                });
            return true;
        }
    }

    private GapDeliveryOutcome CommitGap(
        Guid activationId,
        Guid streamId,
        StreamGapReport gap,
        ActivationDeliveryFence deliveryFence)
    {
        while (true)
        {
            CollectorRuntimeState baseState;
            CollectorRuntimeState next;
            GapDeliveryOutcome outcome;
            string fencedMessage;
            lock (_gate)
            {
                ThrowIfDeliveryUnavailable(activationId);
                if (!_streamWriters.TryGetValue(streamId, out var activeWriter) || activeWriter != activationId)
                    return GapRejected(
                        streamId,
                        "stream_writer_conflict",
                        "Activation does not hold this Fact Stream writer lease.");

                if (_state.Gaps.FirstOrDefault(existing =>
                        existing.StreamId == streamId && existing.GapId == gap.GapId) is { } existing)
                {
                    return existing.Start == gap.Start && existing.End == gap.End &&
                           existing.Reason == gap.Reason &&
                           existing.EstimatedFactsLost == gap.EstimatedFactsLost
                        ? new GapDeliveryOutcome(streamId, GapDeliveryStatus.Duplicate)
                        : GapRejected(
                            streamId,
                            "gap_identity_conflict",
                            "GapId was already committed with different content.");
                }

                baseState = _state;
                // Runtime state schema v2 wrote committed Gaps without GapId. Bind an exact lost-ACK
                // replay once; removal follows the state inventory/window in compatibility-debt.md.
                if (_state.Gaps.FirstOrDefault(existing =>
                        existing.GapId == Guid.Empty &&
                        existing.StreamId == streamId && existing.Start == gap.Start &&
                        existing.End == gap.End && existing.Reason == gap.Reason &&
                        existing.EstimatedFactsLost == gap.EstimatedFactsLost) is { } currentFormatGap)
                {
                    next = baseState.WithBoundGapIdentity(currentFormatGap, gap.GapId);
                    outcome = new GapDeliveryOutcome(streamId, GapDeliveryStatus.Duplicate);
                    fencedMessage = "Hub drain deadline fenced the Stream Gap identity commit.";
                }
                else
                {
                    if (_state.Gaps.Count >= _options.MaxDurableFacts)
                        return GapRetry(streamId, "Hub durable Gap inbox is applying backpressure.");
                    next = baseState.WithGap(new CommittedGapState
                        {
                            StreamId = streamId,
                            GapId = gap.GapId,
                            Start = gap.Start,
                            End = gap.End,
                            Reason = gap.Reason,
                            EstimatedFactsLost = gap.EstimatedFactsLost
                        });
                    outcome = new GapDeliveryOutcome(streamId, GapDeliveryStatus.Committed);
                    fencedMessage = "Hub drain deadline fenced the Stream Gap commit.";
                }
            }

            JsonCollectorRuntimeStore.PreparedReplacement replacement;
            try
            {
                replacement = _store.Prepare(next);
            }
            catch (CollectorRuntimeStateException)
            {
                return GapRetry(streamId, "Hub could not persist the Stream Gap and is applying backpressure.");
            }
            using (replacement)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_state, baseState))
                        continue;
                    if (deliveryFence.IsFenced)
                        return GapRetry(streamId, fencedMessage);
                    if (!_streamWriters.TryGetValue(streamId, out var activeWriter) ||
                        activeWriter != activationId)
                        return GapRejected(
                            streamId,
                            "stream_writer_conflict",
                            "Activation does not hold this Fact Stream writer lease.");
                    try
                    {
                        if (!deliveryFence.TryCommitHost(() =>
                            {
                                replacement.Commit();
                                _state = next;
                            }))
                            return GapRetry(streamId, fencedMessage);
                    }
                    catch (CollectorRuntimeStateException)
                    {
                        return GapRetry(streamId, "Hub could not persist the Stream Gap and is applying backpressure.");
                    }
                }
            }
            return outcome;
        }
    }

    private FactDeliveryOutcome CommitFact(
        Guid activationId,
        int index,
        FactSubmission fact,
        ActivationDeliveryFence deliveryFence)
    {
        PreparedFactCommit prepared;
        lock (_gate)
        {
            ThrowIfDeliveryUnavailable(activationId);
            if (PrepareFactLocked(activationId, index, fact, out prepared) is { } immediate)
                return immediate;
        }

        if (prepared.Stream.FactKind == FactKind.Event &&
            !ProjectEvent(prepared.Stream, prepared.Committed, isReplay: false, deliveryFence))
            return Retry(index, "Hub durable Event projection is applying backpressure.");

        while (true)
        {
            CollectorRuntimeState baseState;
            CollectorRuntimeState next;
            lock (_gate)
            {
                if (PrepareFactLocked(activationId, index, fact, out prepared) is { } immediate)
                    return immediate;
                baseState = _state;
                next = baseState.WithFact(prepared.Committed, prepared.EvictedEvent);
            }

            JsonCollectorRuntimeStore.PreparedReplacement replacement;
            try
            {
                replacement = _store.Prepare(next);
            }
            catch (CollectorRuntimeStateException)
            {
                return Retry(index, "Hub could not persist the Fact and is applying backpressure.");
            }
            using (replacement)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_state, baseState))
                        continue;
                    if (!_streamWriters.TryGetValue(fact.StreamId, out var writer) || writer != activationId)
                        return Rejected(
                            index,
                            "stream_writer_conflict",
                            "Activation does not hold this Fact Stream writer lease.");
                    try
                    {
                        if (!deliveryFence.TryCommitHost(() =>
                            {
                                replacement.Commit();
                                _state = next;
                            }))
                            return Retry(index, "Hub drain deadline fenced the durable Fact commit.");
                    }
                    catch (CollectorRuntimeStateException)
                    {
                        return Retry(index, "Hub could not persist the Fact and is applying backpressure.");
                    }
                }
            }
            break;
        }

        if (prepared.Stream.FactKind != FactKind.Event)
            ProjectFact(prepared.Stream, prepared.Committed, isReplay: false);
        return new FactDeliveryOutcome(index, FactDeliveryStatus.Committed);
    }

    private FactDeliveryOutcome? PrepareFactLocked(
        Guid activationId,
        int index,
        FactSubmission fact,
        out PreparedFactCommit prepared)
    {
        prepared = default!;
        if (!_streamWriters.TryGetValue(fact.StreamId, out var writer) || writer != activationId)
            return Rejected(index, "stream_writer_conflict", "Activation does not hold this Fact Stream writer lease.");
        var activationState = _activations.TryGetValue(activationId, out var inProcessActivation)
            ? inProcessActivation.State
            : _externalHostActivations.TryGetValue(activationId, out var externalHostActivation)
                ? externalHostActivation.State
                : CollectorActivationState.Stopped;
        if (activationState is not (CollectorActivationState.Ready or CollectorActivationState.Draining))
            return Rejected(index, "activation_stopping", "Collector Activation cannot deliver Facts in its current state.");

        var stream = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == fact.StreamId);
        if (stream is null)
            return Rejected(index, "fact_schema_invalid", "Fact Stream does not exist.");

        var envelopeError = ValidateFactEnvelope(fact);
        if (envelopeError is not null)
            return Rejected(index, "fact_schema_invalid", envelopeError);
        var current = _state.Facts.SingleOrDefault(existing =>
            existing.StreamId == fact.StreamId && existing.FactId == fact.FactId);
        if (current is not null && fact.Revision < current.Revision)
            return new FactDeliveryOutcome(index, FactDeliveryStatus.Superseded);

        if (!stream.SchemaCatalog.TryGetValue(fact.SchemaRevision, out var expectedSchemaHash) ||
            !_factSchemasByHash.TryGetValue(expectedSchemaHash, out var schema) ||
            schema.SchemaId != stream.SchemaId ||
            schema.SchemaMajor != stream.SchemaMajor ||
            schema.SchemaRevision != fact.SchemaRevision ||
            schema.FactKind != stream.FactKind)
            return Rejected(index, "fact_schema_invalid", "Fact Schema revision is not available for this Stream.");

        var validationError = stream.FactKind switch
        {
            FactKind.Segment => ValidateSegmentContent(fact, schema),
            FactKind.Event => ValidateEventContent(fact, schema, current),
            _ => "FactKind is not supported by this Collector Runtime slice."
        };
        if (validationError is not null)
            return Rejected(index, "fact_schema_invalid", validationError);
        if (!CanProject(stream, fact))
            return Rejected(
                index,
                "fact_schema_invalid",
                "Fact payload is not compatible with the negotiated Hub projection shape.");
        if (stream.FactKind == FactKind.Segment &&
            _segmentSink is not IDurableSegmentProjectionSink and not ISubjectSegmentProjectionSink)
            return Rejected(
                index,
                "fact_schema_invalid",
                "The configured Segment projection cannot preserve durable Fact revisions.");
        if (stream.FactKind == FactKind.Event && _inputEventSink is null)
            return Rejected(
                index,
                "fact_schema_invalid",
                "The configured Event projection cannot preserve the existing InputEvent upload path.");

        var contentHash = FactCanonicalization.ContentHash(fact);
        if (current is not null)
        {
            if (fact.Revision == current.Revision)
            {
                return current.ContentHash == contentHash
                    ? new FactDeliveryOutcome(index, FactDeliveryStatus.Duplicate)
                    : Rejected(index, "fact_revision_conflict", "The same Fact Revision has different canonical content.");
            }

            if (stream.FactKind == FactKind.Segment &&
                (current.Start != fact.Time.Start ||
                 current.RecordState == FactRecordState.Retracted && fact.RecordState == FactRecordState.Present ||
                 current.IsFinal && fact.Time.IsFinal != true))
                return Rejected(index, "fact_schema_invalid", "Segment Revision violates its evolution rules.");
        }
        CommittedFactState? evictedEvent = null;
        if (current is null)
        {
            var sameKindStreamIds = _state.Streams
                .Where(candidate => candidate.FactKind == stream.FactKind)
                .Select(candidate => candidate.StreamId)
                .ToHashSet();
            var sameKindFacts = _state.Facts
                .Where(existing => sameKindStreamIds.Contains(existing.StreamId))
                .ToArray();
            if (sameKindFacts.Length >= _options.MaxDurableFacts)
            {
                if (stream.FactKind != FactKind.Event)
                    return Retry(index, "Hub durable Fact inbox is applying backpressure.");
                evictedEvent = sameKindFacts[0];
            }
        }

        var committed = new CommittedFactState
        {
            StreamId = fact.StreamId,
            FactId = fact.FactId,
            SchemaRevision = fact.SchemaRevision,
            Revision = fact.Revision,
            RecordState = fact.RecordState,
            ObservedAt = fact.ObservedAt,
            Start = fact.Time.Start ?? default,
            End = fact.Time.End ?? default,
            IsFinal = fact.Time.IsFinal ?? false,
            OccurredAt = fact.Time.OccurredAt,
            Payload = fact.RecordState == FactRecordState.Present ? fact.Payload.Clone() : null,
            ContentHash = contentHash
        };
        // Immutable Events use the durable inbox as a bounded replay/deduplication window. Their
        // projected InputEvent IDs remain stable downstream, so advancing this window keeps a raw
        // production stream flowing without weakening ACK-loss idempotency for retained entries.
        prepared = new PreparedFactCommit(stream, committed, evictedEvent);
        return null;
    }

    private sealed record PreparedFactCommit(
        FactStreamState Stream,
        CommittedFactState Committed,
        CommittedFactState? EvictedEvent);

    private static string? ValidateFactEnvelope(FactSubmission fact)
    {
        if (fact.StreamId == Guid.Empty || !IsUuidV7(fact.FactId) || fact.SchemaRevision <= 0 ||
            fact.Revision is <= 0 or > MaxSafeJsonInteger)
            return "Fact identity and revisions must be UUIDv7, positive, and JSON-safe.";
        if (!Enum.IsDefined(fact.RecordState))
            return "Fact recordState is not defined by Collector Protocol v1.";
        if (fact.ObservedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            return "Fact observedAt must be UTC.";
        return null;
    }

    private static string? ValidateSegmentContent(FactSubmission fact, FactSchemaDocument schema)
    {
        if (fact.Time.Start is not { } start || fact.Time.End is not { } end ||
            fact.Time.IsFinal is null || fact.Time.OccurredAt is not null)
            return "Segment time must contain exactly start, end, and isFinal.";
        if (start.Offset != TimeSpan.Zero || end.Offset != TimeSpan.Zero)
            return "Segment times must be UTC.";
        if (end < start)
            return "Segment end must not precede start.";
        if (fact.RecordState == FactRecordState.Retracted)
        {
            if (fact.Revision == 1)
                return "Retracted Fact must use a Revision higher than 1.";
            if (fact.Payload.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                return "Retracted Fact must omit payload.";
            return schema.AllowRetraction ? null : "Fact Schema does not allow retraction.";
        }
        if (fact.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined || !schema.IsPayloadValid(fact.Payload))
            return "Fact payload does not satisfy its Fact Schema Document.";
        return null;
    }

    private static string? ValidateEventContent(
        FactSubmission fact,
        FactSchemaDocument schema,
        CommittedFactState? current)
    {
        if (fact.Time.OccurredAt is not { } occurredAt ||
            fact.Time.Start is not null || fact.Time.End is not null || fact.Time.IsFinal is not null)
            return "Event time must contain exactly occurredAt.";
        if (occurredAt.Offset != TimeSpan.Zero)
            return "Event occurredAt must be UTC.";
        if (current is null && fact.Revision != 1)
            return "An Event must first be submitted at Revision 1.";
        if (fact.RecordState == FactRecordState.Retracted)
        {
            if (fact.Revision == 1)
                return "Retracted Fact must use a Revision higher than 1.";
            if (fact.Payload.ValueKind != JsonValueKind.Undefined)
                return "Retracted Fact must omit payload.";
            return schema.AllowRetraction ? null : "Fact Schema does not allow retraction.";
        }
        if (fact.Payload.ValueKind == JsonValueKind.Undefined || !schema.IsPayloadValid(fact.Payload))
            return "Fact payload does not satisfy its Fact Schema Document.";
        if (current is not null && current.OccurredAt != occurredAt)
            return "Event Revision cannot change occurredAt.";
        if (current is not null && fact.Revision > current.Revision &&
            schema.EvolutionMode != FactEvolutionMode.MutableEvent)
            return "Immutable Event Fact Schema does not allow a higher present Revision.";
        return null;
    }

    private bool CanProject(FactStreamState stream, FactSubmission fact)
    {
        if (fact.RecordState == FactRecordState.Retracted)
            return stream.FactKind == FactKind.Segment;
        return stream.FactKind switch
        {
            FactKind.Segment =>
                _segmentProjector.TryProject(
                    stream,
                    fact.FactId,
                    fact.Time.Start!.Value,
                    fact.Time.End!.Value,
                    fact.Payload,
                    out _),
            FactKind.Event =>
                ResolveEventProjector(stream.SchemaId, stream.SchemaMajor) is { } eventProjector &&
                eventProjector.TryProject(
                    fact.FactId,
                    fact.Time.OccurredAt!.Value,
                    fact.Payload,
                    out _),
            _ => false
        };
    }

    private StreamOpenPlan PlanStreams(
        Guid activationId,
        CollectorInstance instance,
        LocalCollectorPackage package,
        IReadOnlyList<OutputBinding> bindings)
    {
        if (bindings is null || bindings.Count == 0)
            throw ActivationError("output_not_declared", "Collector must open its declared Fact Streams before Ready.");
        if (bindings.Any(binding => binding is null))
            throw ActivationError("protocol_invalid_message", "streams.open bindings must not contain null.");
        if (bindings.Select(binding => binding.BindingId).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
            throw ActivationError("output_not_declared", "streams.open bindingId values must be unique.");

        var normalized = new List<(OutputBinding Binding, CollectorOutputTemplate Output, Dictionary<string, string> Dimensions)>();
        foreach (var binding in bindings)
        {
            if (binding is null || binding.Dimensions is null ||
                binding.Dimensions.Any(pair => pair.Value is null))
                throw ActivationError(
                    "protocol_invalid_message",
                    "streams.open bindings and dimension values must not be null.");
            if (string.IsNullOrWhiteSpace(binding.BindingId))
                throw ActivationError("output_not_declared", "streams.open bindingId must not be empty.");
            var output = package.Manifest.Outputs.SingleOrDefault(candidate => candidate.OutputId == binding.OutputId)
                ?? throw ActivationError("output_not_declared", $"Output '{binding.OutputId}' is not declared by the Package.");
            var hasProjector = output.FactKind switch
            {
                FactKind.Segment => true,
                FactKind.Event => ResolveEventProjector(output.Schema.Id, output.Schema.Major) is not null,
                _ => false
            };
            if (!hasProjector)
                throw ActivationError(
                    "output_not_declared",
                    $"Output '{binding.OutputId}' has no registered Fact projection adapter for " +
                    $"schema '{output.Schema.Id}/{output.Schema.Major}'.");
            if (!output.SubjectKinds.Contains(SubjectKindName(instance.Subject.Kind), StringComparer.Ordinal))
                throw ActivationError("output_not_declared", $"Output '{binding.OutputId}' does not support this SubjectKind.");
            if (binding.Dimensions.Keys.Any(key => !output.DimensionKeys.Contains(key, StringComparer.Ordinal)))
                throw ActivationError("output_not_declared", $"Output '{binding.OutputId}' received an undeclared dimension key.");
            var dimensions = binding.Dimensions
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Normalize(NormalizationForm.FormC),
                    StringComparer.Ordinal);
            normalized.Add((binding, output, dimensions));
        }
        if (package.Manifest.Outputs.Any(output => normalized.All(item => item.Output.OutputId != output.OutputId)))
            throw ActivationError("output_not_declared", "Collector did not open every declared Output before Ready.");
        ValidatePackageSchemaIdentities(package);

        var opened = new List<OpenedBinding>();
        var planned = new Dictionary<Guid, FactStreamState>();
        foreach (var item in normalized)
        {
            var stream = planned.Values.SingleOrDefault(candidate => StreamIdentityEquals(
                             candidate,
                             instance,
                             item.Output,
                             item.Dimensions)) ??
                         _state.Streams.SingleOrDefault(candidate => StreamIdentityEquals(
                             candidate,
                             instance,
                             item.Output,
                             item.Dimensions));
            if (stream is null)
            {
                var packageSchemas = PackageSchemas(package, item.Output).ToArray();
                var streamId = NextUniqueId(
                    id => _state.Streams.Any(existing => existing.StreamId == id) ||
                          planned.ContainsKey(id) ||
                          _pendingActivationCommits.Values.Any(commit =>
                              commit.Streams.Any(existing => existing.StreamId == id)),
                    "Fact Stream");
                stream = new FactStreamState
                {
                    StreamId = streamId,
                    CollectorInstanceId = instance.CollectorInstanceId,
                    SubjectId = instance.Subject.SubjectId,
                    SubjectKind = instance.Subject.Kind,
                    OutputId = item.Output.OutputId,
                    Source = item.Output.Source,
                    FactKind = item.Output.FactKind,
                    SchemaId = item.Output.Schema.Id,
                    SchemaMajor = item.Output.Schema.Major,
                    SchemaRevision = item.Output.Schema.Revision,
                    SchemaHash = item.Output.Schema.Hash,
                    SchemaCatalog = packageSchemas.ToDictionary(
                        schema => schema.SchemaRevision,
                        schema => schema.ContentHash),
                    SchemaDocuments = packageSchemas.ToDictionary(
                        schema => schema.SchemaRevision,
                        schema => schema.Content.ToArray()),
                    Dimensions = item.Dimensions
                };
            }
            else if (!planned.TryGetValue(stream.StreamId, out var alreadyPlanned))
            {
                stream = ResolveStreamForPackage(stream, package, item.Output);
            }
            else
            {
                stream = alreadyPlanned;
            }
            if (_streamWriters.TryGetValue(stream.StreamId, out var writer) && writer != activationId)
                throw ActivationError("stream_writer_conflict", "A previous Activation still holds the Fact Stream writer lease.");
            planned[stream.StreamId] = stream;
            opened.Add(new OpenedBinding(item.Binding.BindingId, stream));
        }

        var persistedInstance = _state.Instances.Single(existing =>
            existing.CollectorInstanceId == instance.CollectorInstanceId);
        var resolvedInstance = persistedInstance with
        {
            PackageVersion = instance.PackageVersion,
            PackageContentHash = instance.PackageContentHash
        };
        return new StreamOpenPlan(
            opened,
            new PendingActivationCommit(resolvedInstance, planned.Values.ToArray()));
    }

    private static FactStreamState ResolveStreamForPackage(
        FactStreamState stream,
        LocalCollectorPackage package,
        CollectorOutputTemplate output)
    {
        var schemaCatalog = new Dictionary<int, string>(stream.SchemaCatalog);
        var schemaDocuments = stream.SchemaDocuments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
        foreach (var schema in PackageSchemas(package, output))
        {
            if (schemaCatalog.TryGetValue(schema.SchemaRevision, out var existingHash) &&
                existingHash != schema.ContentHash &&
                (!schemaDocuments.TryGetValue(schema.SchemaRevision, out var existingDocument) ||
                 !FactSchemaContent.SemanticallyEquals(existingDocument, schema.Content.Span)))
                throw ActivationError(
                    "package_mismatch",
                    $"Fact Schema '{output.Schema.Id}/{output.Schema.Major}/{schema.SchemaRevision}' changed meaning across Package versions.");
            schemaCatalog[schema.SchemaRevision] = schema.ContentHash;
            schemaDocuments[schema.SchemaRevision] = schema.Content.ToArray();
        }
        return new FactStreamState
        {
            StreamId = stream.StreamId,
            CollectorInstanceId = stream.CollectorInstanceId,
            SubjectId = stream.SubjectId,
            SubjectKind = stream.SubjectKind,
            OutputId = stream.OutputId,
            Source = stream.Source,
            FactKind = stream.FactKind,
            SchemaId = stream.SchemaId,
            SchemaMajor = stream.SchemaMajor,
            SchemaRevision = output.Schema.Revision,
            SchemaHash = output.Schema.Hash,
            SchemaCatalog = schemaCatalog,
            SchemaDocuments = schemaDocuments,
            Dimensions = new Dictionary<string, string>(stream.Dimensions, StringComparer.Ordinal)
        };
    }

    private static IEnumerable<FactSchemaDocument> PackageSchemas(
        LocalCollectorPackage package,
        CollectorOutputTemplate output) =>
        package.FactSchemas
            .Where(schema =>
                schema.SchemaId == output.Schema.Id &&
                schema.SchemaMajor == output.Schema.Major &&
                schema.FactKind == output.FactKind);

    private void ValidatePackageSchemaIdentities(LocalCollectorPackage package)
    {
        var existingStreams = _state.Streams.Concat(
            _pendingActivationCommits.Values.SelectMany(commit => commit.Streams));
        foreach (var schema in package.FactSchemas)
        {
            if (existingStreams.Any(stream =>
                    stream.SchemaId == schema.SchemaId &&
                    stream.SchemaMajor == schema.SchemaMajor &&
                    stream.SchemaCatalog.TryGetValue(schema.SchemaRevision, out var existingHash) &&
                    existingHash != schema.ContentHash &&
                    (!stream.SchemaDocuments.TryGetValue(schema.SchemaRevision, out var existingDocument) ||
                     !FactSchemaContent.SemanticallyEquals(existingDocument, schema.Content.Span))))
                throw ActivationError(
                    "package_mismatch",
                    $"Fact Schema '{schema.SchemaId}/{schema.SchemaMajor}/{schema.SchemaRevision}' changed meaning across Package versions.");
        }
    }

    private static bool StreamIdentityEquals(
        FactStreamState stream,
        CollectorInstance instance,
        CollectorOutputTemplate output,
        IReadOnlyDictionary<string, string> dimensions) =>
        stream.CollectorInstanceId == instance.CollectorInstanceId &&
        stream.SubjectId == instance.Subject.SubjectId &&
        stream.SubjectKind == instance.Subject.Kind &&
        stream.OutputId == output.OutputId &&
        stream.Source == output.Source &&
        stream.FactKind == output.FactKind &&
        stream.SchemaId == output.Schema.Id &&
        stream.SchemaMajor == output.Schema.Major &&
        stream.Dimensions.Count == dimensions.Count &&
        stream.Dimensions.All(pair => dimensions.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static FactStreamDescriptor ToDescriptor(FactStreamState stream) => new(
        stream.StreamId,
        stream.CollectorInstanceId,
        new SubjectReference(stream.SubjectId, stream.SubjectKind),
        stream.OutputId,
        stream.Source,
        stream.FactKind,
        new FactStreamSchemaReference(
            stream.SchemaId,
            stream.SchemaMajor,
            stream.SchemaRevision,
            stream.SchemaHash),
        stream.Dimensions.ToImmutableDictionary(StringComparer.Ordinal));

    private CollectorInstanceState GetInstanceStateLocked(Guid collectorInstanceId)
    {
        return _state.Instances.SingleOrDefault(instance => instance.CollectorInstanceId == collectorInstanceId)
            ?? throw ActivationError("instance_not_found", $"Collector Instance '{collectorInstanceId}' was not found.");
    }

    private void ValidatePackageCandidate(
        CollectorInstanceState instance,
        LocalCollectorPackage package)
    {
        if (instance.PackageId != package.Manifest.PackageId)
            throw ActivationError(
                "package_mismatch",
                "Collector Instance is permanently bound to its PackageId.");
        if (!package.Manifest.Config.AcceptedVersions.Contains(instance.ConfigVersion))
            throw ActivationError(
                "config_version_unsupported",
                $"Collector Package '{package.Manifest.PackageId}/{package.Manifest.Version}' does not accept ConfigVersion {instance.ConfigVersion}.");
    }

    private static VerifiedCollectorArtifact ResolveProtocolArtifact(
        LocalCollectorPackage package,
        string artifactId,
        string executionDriver)
    {
        var artifact = ResolveProtocolArtifact(package, executionDriver);
        if (artifact.ArtifactId != artifactId)
            throw ActivationError("package_mismatch", $"Artifact '{artifactId}' is not the selected current {executionDriver} target.");
        return artifact;
    }

    private static VerifiedCollectorArtifact ResolveProtocolArtifact(
        LocalCollectorPackage package,
        string executionDriver)
    {
        var operatingSystem = CurrentOperatingSystem();
        var architecture = CurrentArchitecture();
        var candidates = package.Manifest.Artifacts.Where(artifact =>
            artifact.Driver == executionDriver &&
            artifact.OperatingSystems.Contains(operatingSystem, StringComparer.Ordinal) &&
            artifact.Architectures.Contains(architecture, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1)
            throw ActivationError(
                "package_mismatch",
                $"Collector Package must have exactly one Artifact for {executionDriver}/{operatingSystem}/{architecture}; found {candidates.Length}.");
        return package.Artifacts.Single(artifact => artifact.ArtifactId == candidates[0].ArtifactId);
    }

    private void ValidateProtocolSupport(LocalCollectorPackage package, ProtocolSupport? support)
    {
        if (support?.ProtocolMajors is null || support.Capabilities is null ||
            support.ProtocolMajors.Count == 0 ||
            support.ProtocolMajors.Any(major => major <= 0) ||
            support.ProtocolMajors.Distinct().Count() != support.ProtocolMajors.Count ||
            support.Capabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability.Key) || capability.Value is null ||
                capability.Value.Count == 0 || capability.Value.Any(version => version <= 0) ||
                capability.Value.Distinct().Count() != capability.Value.Count))
            throw ActivationError(
                "protocol_invalid_message",
                "activation.hello protocolMajors and supportedCapabilities are malformed.");
        if (!support.ProtocolMajors.Contains(1) || !package.Manifest.ProtocolMajors.Contains(1))
            throw ActivationError("protocol_no_common_major", "No common Collector Protocol major.");

        foreach (var capability in package.Manifest.Outputs.Select(output => output.FactKind switch
                 {
                     FactKind.Segment => "facts.segment",
                     FactKind.Event => "facts.event",
                     FactKind.Measurement => "facts.measurement.gauge",
                     _ => string.Empty
                 }).Append("diagnostics.stream-gap"))
        {
            if (capability == "facts.event" && _inputEventSink is null ||
                !HubProtocolCapabilities.TryGetValue(capability, out var hubVersions) ||
                !package.Manifest.SupportedCapabilities.TryGetValue(capability, out var packageVersions) ||
                !support.Capabilities.TryGetValue(capability, out var collectorVersions) ||
                !hubVersions.Intersect(packageVersions).Intersect(collectorVersions).Any())
                throw ActivationError(
                    "capability_no_common_version",
                    $"Required protocol capability '{capability}' has no common version.");
        }
    }

    private static ProtocolSupport? SnapshotProtocolSupport(ProtocolSupport? support)
    {
        if (support is null)
            return null;

        var protocolMajors = support.ProtocolMajors?.ToImmutableArray();
        if (support.Capabilities is null)
            return new ProtocolSupport(protocolMajors!, null!);

        var capabilities = ImmutableDictionary.CreateBuilder<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var capability in support.Capabilities)
            capabilities.Add(
                capability.Key,
                capability.Value is null ? null! : capability.Value.ToImmutableArray());
        return new ProtocolSupport(protocolMajors!, capabilities.ToImmutable());
    }

    private static InProcessCollectorInitialization SnapshotInitialization(
        InProcessCollectorInitialization initialized)
    {
        if (initialized.Bindings is null)
            throw new InvalidOperationException("activation.initialized bindings must be present.");

        var bindings = ImmutableArray.CreateBuilder<OutputBinding>();
        foreach (var binding in initialized.Bindings)
        {
            if (binding is null)
            {
                bindings.Add(null!);
                continue;
            }
            if (binding.Dimensions is null)
            {
                bindings.Add(binding with { Dimensions = null! });
                continue;
            }

            var dimensions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var dimension in binding.Dimensions)
            {
                if (!dimensions.TryAdd(dimension.Key, dimension.Value))
                    throw new InvalidOperationException(
                        $"activation.initialized binding '{binding.BindingId}' contains duplicate dimensions.");
            }
            bindings.Add(new OutputBinding(
                binding.BindingId,
                binding.OutputId,
                dimensions.ToImmutable()));
        }
        return new InProcessCollectorInitialization(
            initialized.AppliedSpecRevision,
            bindings.ToImmutable());
    }

    private Guid NextUniqueId(Func<Guid, bool> exists, string kind)
    {
        var id = _options.IdGenerator();
        if (!IsUuidV7(id) || exists(id))
            throw new InvalidOperationException($"Collector Runtime generated an invalid or duplicate UUIDv7 {kind} ID.");
        return id;
    }

    private CollectorActivationSession CreateActivationSession(
        Guid activationId,
        Guid helloMessageId,
        LocalCollectorPackage package,
        ActivationDeliveryCapability deliveryCapability)
    {
        var deliveryFence = new ActivationDeliveryFence();
        return new CollectorActivationSession(
            activationId,
            helloMessageId,
            package,
            new CollectorProtocolLimits(_options.MaxFactsPerBatch, _options.MaxBatchBytes),
            deliveryCapability,
            deliveryFence,
            (streamId, facts) => CommitFacts(activationId, streamId, facts, deliveryFence),
            (streamId, gap) => CommitGap(activationId, streamId, gap, deliveryFence),
            MarkAcknowledgedLiveTraffic);
    }

    private static string SubjectKindName(SubjectKind kind) => kind switch
    {
        SubjectKind.Machine => "machine",
        SubjectKind.Account => "account",
        SubjectKind.Person => "person",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string CurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        throw ActivationError("package_mismatch", "Current operating system is not supported by Collector Protocol v1.");

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw ActivationError("package_mismatch", "Current architecture is not supported by Collector Protocol v1.")
    };

    private static CollectorActivationException ActivationError(
        string code,
        string message,
        Exception? exception = null,
        bool retryable = false) => new(
        new CollectorProtocolError(code, message, retryable),
        exception);

    private static FactDeliveryOutcome Rejected(int index, string code, string message) => new(
        index,
        FactDeliveryStatus.Rejected,
        new CollectorProtocolError(code, message, false));

    private FactDeliveryOutcome Retry(int index, string message) => new(
        index,
        FactDeliveryStatus.Retry,
        new CollectorProtocolError("hub_backpressure", message, true),
        _options.RetryAfterMilliseconds);

    private static GapDeliveryOutcome GapRejected(Guid streamId, string code, string message) => new(
        streamId,
        GapDeliveryStatus.Rejected,
        new CollectorProtocolError(code, message, false));

    private GapDeliveryOutcome GapRetry(Guid streamId, string message) => new(
        streamId,
        GapDeliveryStatus.Retry,
        new CollectorProtocolError("hub_backpressure", message, true),
        _options.RetryAfterMilliseconds);

    private static FactBatchAcknowledgement MessageRejected(string code, string message) => new(
        [],
        new CollectorProtocolError(code, message, false));

    private static string HelloRequestHash(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        string? artifactId,
        ProtocolSupport? support)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("collectorInstanceId", collectorInstanceId);
            writer.WriteString("packageId", package.Manifest.PackageId);
            writer.WriteString("packageVersion", package.Manifest.Version);
            writer.WriteString("packageContentHash", package.PackageContentHash);
            writer.WriteString("artifactId", artifactId);
            writer.WritePropertyName("protocolSupport");
            if (support is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("protocolMajors");
                if (support.ProtocolMajors is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartArray();
                    foreach (var major in support.ProtocolMajors.Order())
                        writer.WriteNumberValue(major);
                    writer.WriteEndArray();
                }
                writer.WritePropertyName("capabilities");
                if (support.Capabilities is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    foreach (var capability in support.Capabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(capability.Key);
                        if (capability.Value is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            writer.WriteStartArray();
                            foreach (var version in capability.Value.Order())
                                writer.WriteNumberValue(version);
                            writer.WriteEndArray();
                        }
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private void PersistActivationAttemptTombstoneLocked(
        Guid collectorInstanceId,
        Guid helloMessageId,
        string requestHash,
        Guid activationId)
    {
        var previous = _state.ActivationAttemptTombstones.SingleOrDefault(attempt =>
            attempt.CollectorInstanceId == collectorInstanceId &&
            attempt.MessageId == helloMessageId);
        if (previous is not null)
        {
            if (previous.RequestHash != requestHash)
                throw ActivationError(
                    "protocol_invalid_message",
                    "The same activation.hello messageId was reused with different content.");
            throw ActivationError(
                "protocol_invalid_message",
                "The activation.hello attempt belongs to a previous Runtime session; send a new messageId.");
        }
        if (_state.ActivationAttemptTombstones.Any(attempt => attempt.ActivationId == activationId))
            throw ActivationError(
                "protocol_invalid_message",
                "The Collector Activation identifier belongs to a previous Runtime session.");

        var next = _state.WithActivationAttemptTombstone(new ActivationAttemptTombstoneState
        {
            CollectorInstanceId = collectorInstanceId,
            MessageId = helloMessageId,
            RequestHash = requestHash,
            ActivationId = activationId
        });
        try
        {
            _store.Save(next);
            _state = next;
        }
        catch (CollectorRuntimeStateException exception)
        {
            throw ActivationError(
                "hub_backpressure",
                "Hub could not persist activation.hello attempt identity.",
                exception,
                retryable: true);
        }
    }

    private void RestorePersistedFactSchemas()
    {
        try
        {
            foreach (var stream in _state.Streams)
            {
                foreach (var pair in stream.SchemaDocuments)
                {
                    var contentHash = stream.SchemaCatalog[pair.Key];
                    if (_factSchemasByHash.TryGetValue(contentHash, out var cached))
                    {
                        if (cached.SchemaId != stream.SchemaId ||
                            cached.SchemaMajor != stream.SchemaMajor ||
                            cached.SchemaRevision != pair.Key ||
                            cached.FactKind != stream.FactKind)
                            throw new PackageValidationException(
                                $"Durable Fact Schema hash '{contentHash}' is bound to conflicting identities.");
                        continue;
                    }
                    var restored = LocalCollectorPackage.RestoreFactSchema(
                        pair.Value,
                        stream.SchemaId,
                        stream.SchemaMajor,
                        pair.Key,
                        stream.FactKind,
                        contentHash);
                    _factSchemasByHash.Add(contentHash, restored);
                }
            }
        }
        catch (PackageValidationException exception)
        {
            throw new CollectorRuntimeStateException(
                "Collector Runtime state contains an invalid durable Fact Schema snapshot.",
                exception);
        }
    }

    private void ReplayCommittedFacts()
    {
        lock (_gate)
        {
            var replaySink = _inputEventSink as IInputEventFactReplaySink;
            List<InputEventItem>? replayEvents = replaySink is null ? null : [];
            foreach (var fact in _state.Facts)
            {
                var stream = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == fact.StreamId);
                if (stream is null)
                    continue;
                if (stream.FactKind == FactKind.Event && replayEvents is not null)
                {
                    if (TryCreateInputEventProjection(stream, fact, out var item))
                        replayEvents.Add(item!);
                    continue;
                }
                ProjectFact(stream, fact, isReplay: true);
            }

            if (replayEvents is { Count: > 0 })
            {
                try
                {
                    replaySink!.Replay(replayEvents);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "已持久接收 {Count} 条 Collector Event Fact，批量投影到 InputEvent 上传缓冲失败；重启时将重放",
                        replayEvents.Count);
                }
            }
        }
    }

    private void ProjectFact(FactStreamState stream, CommittedFactState fact, bool isReplay)
    {
        switch (stream.FactKind)
        {
            case FactKind.Segment:
                ProjectSegment(stream, fact, isReplay);
                break;
            case FactKind.Event:
                ProjectEvent(stream, fact, isReplay);
                break;
        }
    }

    private void ProjectSegment(FactStreamState stream, CommittedFactState fact, bool isReplay)
    {
        if (fact.RecordState == FactRecordState.Retracted)
        {
            if (_segmentSink is ISubjectSegmentProjectionSink subjectSink)
            {
                try
                {
                    subjectSink.RetractDurable(
                        ContextForStream(stream),
                        _segmentProjector.ProjectedId(stream.StreamId, fact.FactId),
                        fact.Revision);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "已持久接收 Collector Segment 撤回 {FactId}，从 Subject 缓冲移除失败；重启时将重放",
                        fact.FactId);
                }
            }
            else if (_segmentSink is IDurableSegmentProjectionSink durableSink)
            {
                try
                {
                    durableSink.RetractDurable(
                        _segmentProjector.ProjectedId(stream.StreamId, fact.FactId),
                        fact.Revision);
                }
                catch (Exception exception)
                {
                    Log.Error(
                        exception,
                        "已持久接收 Collector Segment 撤回 {FactId}，从 Hub 缓冲移除失败；重启时将重放",
                        fact.FactId);
                }
            }
            return;
        }
        if (fact.Payload is not { } payload ||
            !_segmentProjector.TryProject(
                stream,
                fact.FactId,
                fact.Start,
                fact.End,
                payload,
                out var item))
        {
            Log.Error(
                "已持久接收 Collector Segment Fact {FactId}，但其 payload 无法由 schema adapter 投影",
                fact.FactId);
            return;
        }
        try
        {
            if (_segmentSink is ISubjectSegmentProjectionSink subjectSink)
            {
                var context = ContextForStream(stream);
                if (isReplay)
                    subjectSink.ReplayDurable(context, item!, fact.Revision, fact.IsFinal);
                else
                    subjectSink.UpsertDurable(context, item!, fact.Revision, fact.IsFinal);
            }
            else if (_segmentSink is IDurableSegmentProjectionSink durableSink)
            {
                if (isReplay)
                    durableSink.ReplayDurable(item!, fact.Revision);
                else
                    durableSink.UpsertDurable(item!, fact.Revision);
            }
            else
                Log.Error(
                    "已持久接收 Collector Segment Fact {FactId}，但投影 sink 不支持 durable revision",
                    fact.FactId);
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "已持久接收 Collector Segment Fact {FactId}，投影到 Hub 缓冲失败；重启时将重放",
                fact.FactId);
        }
    }

    private CollectorProjectionContext ContextForStream(FactStreamState stream)
    {
        var instance = _state.Instances.Single(candidate =>
            candidate.CollectorInstanceId == stream.CollectorInstanceId);
        return new CollectorProjectionContext(
            stream.CollectorInstanceId,
            new SubjectReference(instance.SubjectId, instance.SubjectKind));
    }

    private bool ProjectEvent(
        FactStreamState stream,
        CommittedFactState fact,
        bool isReplay,
        ICollectorProjectionCommitFence? commitFence = null)
    {
        if (!TryCreateInputEventProjection(stream, fact, out var item))
            return false;
        if (_inputEventSink is null)
        {
            Log.Error(
                "已持久接收 Collector Event Fact {FactId}，但未配置 InputEvent 投影 sink",
                fact.FactId);
            return false;
        }
        try
        {
            return _inputEventSink.TryAccept(
                item!,
                isReplay,
                commitFence ?? UnfencedCollectorProjectionCommitFence.Instance);
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "已持久接收 Collector Event Fact {FactId}，投影到 InputEvent 上传缓冲失败；重启时将重放",
                fact.FactId);
            return false;
        }
    }

    private bool TryCreateInputEventProjection(
        FactStreamState stream,
        CommittedFactState fact,
        out InputEventItem? item)
    {
        item = null;
        if (fact.RecordState == FactRecordState.Present &&
            fact.OccurredAt is { } occurredAt &&
            fact.Payload is { } payload &&
            ResolveEventProjector(stream.SchemaId, stream.SchemaMajor) is { } projector &&
            projector.TryProject(fact.FactId, occurredAt, payload, out item))
            return true;

        Log.Error(
            "已持久接收 Collector Event Fact {FactId}，但其 payload 无法由 schema adapter 投影",
            fact.FactId);
        return false;
    }

    private void MarkAcknowledgedLiveTraffic(
        Guid streamId,
        IReadOnlyList<FactDeliveryOutcome> outcomes)
    {
        if (!outcomes.Any(outcome => outcome.IsAcknowledged) ||
            _segmentSink is not ICollectorTrafficSink trafficSink)
            return;
        string? source;
        lock (_gate)
            source = _state.Streams.SingleOrDefault(candidate => candidate.StreamId == streamId)?.Source;
        if (source is null)
            return;
        try
        {
            trafficSink.MarkSourceActive(source);
        }
        catch (Exception exception)
        {
            Log.Error(
                exception,
                "Collector Source {Source} 的实时流量盖戳失败；Fact ACK 与 durable inbox 保持有效",
                source);
        }
    }

    private IEventFactProjector? ResolveEventProjector(string schemaId, int schemaMajor) =>
        _eventProjectors.SingleOrDefault(projector => projector.Supports(schemaId, schemaMajor));

    private sealed record OpenedBinding(string BindingId, FactStreamState Stream);
    private sealed record StreamOpenPlan(
        IReadOnlyList<OpenedBinding> Bindings,
        PendingActivationCommit Commit);
    private sealed record PendingActivationCommit(
        CollectorInstanceState Instance,
        IReadOnlyList<FactStreamState> Streams);
    private sealed record HelloAttempt(
        string RequestHash,
        TaskCompletionSource<InProcessCollectorActivation> Completion);
}
