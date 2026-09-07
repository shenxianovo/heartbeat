using System.Collections.Immutable;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public enum CollectorInstanceExternalHostState
{
    WaitingForExternalHost,
    Negotiating,
    Connected
}

public sealed record CollectorInstanceExternalHostStatus(
    Guid CollectorInstanceId,
    CollectorInstanceExternalHostState State,
    int ConnectedExternalHosts,
    int NegotiatingExternalHosts,
    CollectorRuntimeFailure? Failure = null);

public sealed partial class CollectorRuntime
{
    private const string ExternalHostIdentityDimension = "externalHostIdentity";
    private const string AppIdentityKeyDimension = "appIdentityKey";
    private const int MaxExternalHostIdentityLength = 200;

    private readonly Dictionary<Guid, PendingExternalHostActivation> _pendingExternalHostActivations = [];
    private readonly Dictionary<Guid, ExternalHostCollectorActivation> _externalHostActivations = [];

    /// <summary>
    /// External Host 的并发所有权单位是 (Collector Instance, External Host Identity)，不是整个 Instance。
    /// 同一个 Instance 下不同 identity 可以并行协商、Ready 并各自持有自己 Fact Stream 的 writer lease；
    /// 同一 identity 的重连必须先让旧 Activation 走完生命周期（lease 真正释放）才能接管。
    /// </summary>
    private readonly Dictionary<Guid, ExternalHostActivationOwner> _externalHostOwners = [];

    /// <summary>
    /// 卸载围栏。进入这个集合的 Instance 不再接受新的 activation.hello，直到卸载成功（Instance 消失）
    /// 或失败回滚。
    /// </summary>
    private readonly HashSet<Guid> _instancesBeingRemoved = [];
    private readonly Dictionary<(Guid CollectorInstanceId, string ExternalHostIdentity), CollectorRuntimeFailure>
        _externalHostFailures = [];

    internal ExternalHostCollectorActivation FindExternalHostActivation(Guid activationId)
    {
        lock (_gate)
            return _externalHostActivations.TryGetValue(activationId, out var activation)
                ? activation
                : throw new KeyNotFoundException($"ExternalHost Activation '{activationId}' is not active.");
    }

    internal Task<CollectorActivationTerminalResult> FindExternalHostActivationTerminal(Guid activationId)
    {
        lock (_gate)
            return _activationLifetimes.TryGetValue(activationId, out var lifetime)
                ? lifetime.Terminal
                : throw new KeyNotFoundException($"ExternalHost Activation '{activationId}' has no lifetime owner.");
    }

    public ExternalHostCollectorInitialization BeginExternalHostActivation(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        string artifactId,
        string artifactHash,
        ProtocolSupport protocolSupport,
        string externalHostIdentity,
        string appIdentityKey,
        Guid helloMessageId)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(protocolSupport);
        if (!IsUuidV7(helloMessageId))
            throw ActivationError(
                "protocol_invalid_message",
                "activation.hello messageId must be a UUIDv7 value.");
        var identity = NormalizedExternalHostValue(externalHostIdentity, ExternalHostIdentityDimension);
        var appIdentity = NormalizedExternalHostValue(appIdentityKey, AppIdentityKeyDimension);

        var support = SnapshotProtocolSupport(protocolSupport);
        var helloRequestHash = HelloRequestHash(
            collectorInstanceId,
            package,
            artifactId,
            support);
        lock (_gate)
        {
            ThrowIfDisposed();
            var instanceState = GetInstanceStateLocked(collectorInstanceId);
            ValidatePackageCandidate(instanceState, package);
            var artifact = ResolveExternalHostArtifact(package, artifactId);
            if (!string.Equals(artifact.ContentHash, artifactHash, StringComparison.Ordinal))
                throw ActivationError("package_mismatch", "ExternalHost Artifact hash does not match the verified Package.");
            ValidateProtocolSupport(package, support);
            if (_instancesBeingRemoved.Contains(collectorInstanceId))
                throw ActivationError(
                    "activation_stopping",
                    "Collector Instance is being removed.");
            if (_startingInstances.Contains(collectorInstanceId) ||
                _activations.Values.Any(activation =>
                    activation.State != CollectorActivationState.Stopped &&
                    activation.Streams.Values.Any(stream =>
                        stream.Descriptor.CollectorInstanceId == collectorInstanceId)))
                throw ActivationError(
                    "stream_writer_conflict",
                    "Stop the current Collector Activation before starting its replacement.");
            if (_externalHostOwners.Values.Any(owner =>
                    owner.CollectorInstanceId == collectorInstanceId &&
                    string.Equals(owner.ExternalHostIdentity, identity, StringComparison.Ordinal)))
                throw ActivationError(
                    "stream_writer_conflict",
                    "Another Activation still owns this External Host Identity.");
            ValidateExternalHostIdentityBindingLocked(collectorInstanceId, identity, appIdentity);
            _externalHostFailures.Remove((collectorInstanceId, identity));
            var activationId = NextUniqueId(
                id => _pendingExternalHostActivations.ContainsKey(id) ||
                      _externalHostActivations.ContainsKey(id) ||
                      _activations.ContainsKey(id) ||
                      _state.ActivationAttemptTombstones.Any(attempt => attempt.ActivationId == id),
                "Collector Activation");
            PersistActivationAttemptTombstoneLocked(
                collectorInstanceId,
                helloMessageId,
                helloRequestHash,
                activationId);

            var instance = ToPublic(instanceState) with
            {
                PackageVersion = package.Manifest.Version,
                PackageContentHash = package.PackageContentHash
            };
            _externalHostOwners.Add(
                activationId,
                new ExternalHostActivationOwner(collectorInstanceId, identity, appIdentity));
            var session = CreateActivationSession(
                activationId,
                helloMessageId,
                package,
                ActivationDeliveryCapability.Complete);
            var lifetime = new CollectorActivationLifetime(
                new ExternalHostCollectorActivationLifetimeDriver(session),
                session.FenceDeliveryAfterDeadline,
                () => CompleteExternalHostActivationLifetime(activationId),
                _options.TimeProvider,
                _options.InProcessDrainGracePeriod);
            _activationLifetimes.Add(activationId, lifetime);
            _pendingExternalHostActivations.Add(
                activationId,
                new PendingExternalHostActivation(instance, package, session, lifetime, identity, appIdentity));

            return new ExternalHostCollectorInitialization(
                activationId,
                instance,
                instance.Spec,
                new CollectorProtocolLimits(
                    _options.MaxFactsPerBatch,
                    _options.MaxBatchBytes),
                new CollectorResources(null),
                SelectedCapabilities(package, support!)
                    .Where(capability => capability.Key != "resources.instance-data")
                    .ToImmutableDictionary(
                        capability => capability.Key,
                        capability => capability.Value,
                        StringComparer.Ordinal));
        }
    }

    public async ValueTask<ExternalHostCollectorActivation> ReadyExternalHostActivationAsync(
        Guid activationId,
        long appliedSpecRevision,
        IReadOnlyList<OutputBinding> bindings)
    {
        var activation = OpenExternalHostStreams(activationId, appliedSpecRevision, bindings);
        return await MarkExternalHostReadyAsync(activation, appliedSpecRevision);
    }

    public ExternalHostCollectorActivation OpenExternalHostStreams(
        Guid activationId,
        long appliedSpecRevision,
        IReadOnlyList<OutputBinding> bindings)
    {
        PendingExternalHostActivation pending;
        StreamOpenPlan streamPlan;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingExternalHostActivations.TryGetValue(activationId, out var candidate))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation is not awaiting ready.");
            pending = candidate;
            if (pending.Lifetime.HasStopIntent)
                throw ActivationError("activation_stopping", "ExternalHost Activation is stopping.");
            if (appliedSpecRevision != pending.Instance.Spec.SpecRevision)
                throw ActivationError("spec_revision_stale", "Collector did not apply the current SpecRevision.");
            streamPlan = PlanStreams(
                activationId,
                pending.Instance,
                pending.Package,
                WithExternalHostDimensions(pending, bindings));
        }

        pending.Session.AcceptInitialized(appliedSpecRevision, pending.Instance.Spec.SpecRevision);
        var descriptors = streamPlan.Bindings.ToImmutableDictionary(
            pair => pair.BindingId,
            pair => ToDescriptor(pair.Stream),
            StringComparer.Ordinal);
        pending.Session.AcceptStreams(descriptors);
        var activation = new ExternalHostCollectorActivation(pending.Session, pending.Lifetime);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingExternalHostActivations.TryGetValue(activationId, out var current) ||
                !ReferenceEquals(current, pending) || pending.Lifetime.HasStopIntent)
                throw ActivationError(
                    "activation_stopping",
                    "ExternalHost Activation ended while its Streams were opening.");
            _externalHostActivations.Add(activationId, activation);
            _pendingActivationCommits.Add(activationId, streamPlan.Commit);
            _pendingExternalHostActivations.Remove(activationId);
            return activation;
        }
    }

    public async ValueTask<ExternalHostCollectorActivation> MarkExternalHostReadyAsync(
        ExternalHostCollectorActivation activation,
        long appliedSpecRevision)
    {
        ArgumentNullException.ThrowIfNull(activation);
        long expectedSpecRevision;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (activation.State == CollectorActivationState.Ready)
                return activation;
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var pendingCommit))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation has no pending Stream commit.");
            expectedSpecRevision = pendingCommit.Instance.SpecRevision;
        }
        var outcome = await activation.Lifetime.PublishReadyAsync(new CollectorReadyPublication(() =>
            ValueTask.FromResult(PrepareExternalHostReady(
                activation,
                appliedSpecRevision,
                expectedSpecRevision))));
        if (outcome == CollectorReadyOutcome.Stopping)
            throw ActivationError("activation_stopping", "ExternalHost Activation stopped before Ready publication.");
        return activation;
    }

    private CollectorReadyPreparedCommit PrepareExternalHostReady(
        ExternalHostCollectorActivation activation,
        long appliedSpecRevision,
        long expectedSpecRevision)
    {
        CollectorRuntimeState baseState;
        CollectorRuntimeState next;
        PendingActivationCommit pendingCommit;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_pendingActivationCommits.TryGetValue(activation.ActivationId, out var currentPendingCommit))
                throw ActivationError("protocol_invalid_message", "ExternalHost Activation has no pending Stream commit.");
            pendingCommit = currentPendingCommit;
            baseState = _state;
            next = baseState.WithInstanceAndStreams(pendingCommit.Instance, pendingCommit.Streams);
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
            () => TryCommitExternalHostReady(
                activation,
                appliedSpecRevision,
                expectedSpecRevision,
                baseState,
                next,
                pendingCommit,
                replacement),
            replacement);
    }

    private bool TryCommitExternalHostReady(
        ExternalHostCollectorActivation activation,
        long appliedSpecRevision,
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
            activation.Session.AcceptReady(
                appliedSpecRevision,
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
                    foreach (var schema in activation.Package.FactSchemas)
                        _factSchemasByHash[schema.ContentHash] = schema;
                    foreach (var stream in activation.Streams.Values)
                        _streamWriters[stream.StreamId] = activation.ActivationId;
                    _pendingActivationCommits.Remove(activation.ActivationId);
                });
            return true;
        }
    }

    public async ValueTask AbandonExternalHostActivationAsync(
        Guid activationId,
        ExternalHostActivationStopReason reason)
    {
        CollectorActivationLifetime? lifetime;
        lock (_gate)
            lifetime = _pendingExternalHostActivations.TryGetValue(activationId, out var pending)
                ? pending.Lifetime
                : null;
        if (lifetime is not null)
            _ = await lifetime.RequestStopAsync(new ExternalHostCollectorActivationStopIntent(
                reason,
                new ExternalHostDrainEvidence.NotReported()));
    }

    public async ValueTask StopExternalHostActivationAsync(
        ExternalHostCollectorActivation activation,
        ExternalHostActivationStopReason reason,
        InProcessCollectorDrainResult? drainResult = null)
    {
        ArgumentNullException.ThrowIfNull(activation);
        _ = await activation.RequestStopAsync(reason, drainResult);
    }

    private void CompleteExternalHostActivationLifetime(Guid activationId)
    {
        lock (_gate)
        {
            _pendingExternalHostActivations.Remove(activationId);
            if (_externalHostActivations.Remove(activationId, out var activation))
            {
                foreach (var streamId in activation.Streams.Values.Select(stream => stream.StreamId))
                {
                    if (_streamWriters.TryGetValue(streamId, out var writer) && writer == activationId)
                        _streamWriters.Remove(streamId);
                }
            }
            _pendingActivationCommits.Remove(activationId);
            // Identity 所有权与 writer lease 同时释放，同 identity 的替代者只有在这之后才能接管。
            _externalHostOwners.Remove(activationId);
            _activationLifetimes.Remove(activationId);
        }
    }

    private static string NormalizedExternalHostValue(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > MaxExternalHostIdentityLength)
            throw ActivationError(
                "protocol_invalid_message",
                $"activation.hello {field} must be a trimmed, non-empty value of at most {MaxExternalHostIdentityLength} characters.");
        return value;
    }

    /// <summary>
    /// Identity 与 App 身份的绑定权威是持久 Fact Stream 的 identifying dimensions，不是第二份台账，
    /// 因此 Host 重启后同一 identity 仍然不能静默换绑到另一个 appIdentityKey。
    /// </summary>
    private void ValidateExternalHostIdentityBindingLocked(
        Guid collectorInstanceId,
        string externalHostIdentity,
        string appIdentityKey)
    {
        foreach (var stream in _state.Streams)
        {
            if (stream.CollectorInstanceId != collectorInstanceId ||
                !stream.Dimensions.TryGetValue(ExternalHostIdentityDimension, out var boundIdentity) ||
                !string.Equals(boundIdentity, externalHostIdentity, StringComparison.Ordinal) ||
                !stream.Dimensions.TryGetValue(AppIdentityKeyDimension, out var boundAppIdentityKey) ||
                string.Equals(boundAppIdentityKey, appIdentityKey, StringComparison.Ordinal))
                continue;
            RecordExternalHostIdentityConflictLocked(
                collectorInstanceId,
                externalHostIdentity,
                appIdentityKey);
            throw ActivationError(
                "external_host_identity_conflict",
                "External Host Identity is already bound to a different appIdentityKey.");
        }
    }

    private static IReadOnlyList<OutputBinding> WithExternalHostDimensions(
        PendingExternalHostActivation pending,
        IReadOnlyList<OutputBinding> bindings)
    {
        if (bindings is null || bindings.Count == 0 || bindings.Any(binding => binding is null))
            return bindings!;
        var resolved = new List<OutputBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (binding.Dimensions is null)
                throw ActivationError("protocol_invalid_message", "streams.open bindings must declare dimensions.");
            if (binding.Dimensions.ContainsKey(ExternalHostIdentityDimension) ||
                binding.Dimensions.ContainsKey(AppIdentityKeyDimension))
                throw ActivationError(
                    "protocol_invalid_message",
                    $"streams.open must not set the '{ExternalHostIdentityDimension}' or '{AppIdentityKeyDimension}' dimension; the Hub derives them from activation.hello.");
            var dimensions = new Dictionary<string, string>(binding.Dimensions, StringComparer.Ordinal)
            {
                [AppIdentityKeyDimension] = pending.AppIdentityKey,
                [ExternalHostIdentityDimension] = pending.ExternalHostIdentity
            };
            resolved.Add(binding with { Dimensions = dimensions });
        }
        return resolved;
    }

    internal bool HasExternalHostActivationLocked(Guid collectorInstanceId) =>
        _externalHostOwners.Values.Any(owner => owner.CollectorInstanceId == collectorInstanceId);

    public void RequireExternalHostIdentityBinding(
        Guid collectorInstanceId,
        string externalHostIdentity,
        string appIdentityKey)
    {
        var identity = NormalizedExternalHostValue(externalHostIdentity, ExternalHostIdentityDimension);
        var appIdentity = NormalizedExternalHostValue(appIdentityKey, AppIdentityKeyDimension);
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = GetInstanceStateLocked(collectorInstanceId);
            ValidateExternalHostIdentityBindingLocked(collectorInstanceId, identity, appIdentity);
        }
    }

    /// <summary>
    /// 通用的 Instance 级管理事实：已安装且 Instance 存在但没有任何 External Host 接进来时，读模型是
    /// WaitingForExternalHost，而不是伪装成某个 Activation 的 Starting/Failed。
    /// </summary>
    public CollectorInstanceExternalHostStatus DescribeExternalHostInstance(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = GetInstanceStateLocked(collectorInstanceId);
            var connected = 0;
            var negotiating = 0;
            foreach (var owner in _externalHostOwners)
            {
                if (owner.Value.CollectorInstanceId != collectorInstanceId)
                    continue;
                if (_externalHostActivations.TryGetValue(owner.Key, out var activation) &&
                    activation.State == CollectorActivationState.Ready)
                    connected++;
                else
                    negotiating++;
            }

            var state = connected > 0
                ? CollectorInstanceExternalHostState.Connected
                : negotiating > 0
                    ? CollectorInstanceExternalHostState.Negotiating
                    : CollectorInstanceExternalHostState.WaitingForExternalHost;
            return new CollectorInstanceExternalHostStatus(
                collectorInstanceId,
                state,
                connected,
                negotiating,
                _externalHostFailures
                    .Where(failure => failure.Key.CollectorInstanceId == collectorInstanceId)
                    .Select(failure => failure.Value)
                    .FirstOrDefault());
        }
    }

    internal void ReportExternalHostIdentityConflict(
        Guid collectorInstanceId,
        string externalHostIdentity,
        string appIdentityKey)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = GetInstanceStateLocked(collectorInstanceId);
            RecordExternalHostIdentityConflictLocked(
                collectorInstanceId,
                externalHostIdentity,
                appIdentityKey);
        }
    }

    private void RecordExternalHostIdentityConflictLocked(
        Guid collectorInstanceId,
        string externalHostIdentity,
        string appIdentityKey) =>
        _externalHostFailures[(collectorInstanceId, externalHostIdentity)] = new CollectorRuntimeFailure(
            "external_host_identity_conflict",
            $"External Host '{externalHostIdentity}' attempted to use appIdentityKey '{appIdentityKey}', " +
            "but that identity is already bound to a different appIdentityKey.");

    /// <summary>
    /// 停止某个 Instance 下所有 pending 与 active 的 External Host 生命周期，并等到它们真正走完终态
    /// （writer lease 已释放）。卸载在删除任何持久状态之前必须先做完这一步。
    /// </summary>
    private async ValueTask StopExternalHostActivationsForInstanceAsync(
        Guid collectorInstanceId,
        ExternalHostActivationStopReason reason)
    {
        while (true)
        {
            CollectorActivationLifetime lifetime;
            lock (_gate)
            {
                var owned = _externalHostOwners
                    .Where(owner => owner.Value.CollectorInstanceId == collectorInstanceId)
                    .Select(owner => owner.Key)
                    .Where(_activationLifetimes.ContainsKey)
                    .ToArray();
                if (owned.Length == 0)
                    return;
                lifetime = _activationLifetimes[owned[0]];
            }

            _ = await lifetime.RequestStopAsync(new ExternalHostCollectorActivationStopIntent(
                reason,
                new ExternalHostDrainEvidence.NotReported()));
            await lifetime.Terminal;
        }
    }

    /// <summary>
    /// 校验对端声明的 Artifact 就是当前平台被选中的那个 externalHost Artifact。
    /// 与 <see cref="BeginExternalHostActivation"/> 共用同一条规则，供协议入口在创建任何状态之前先行拒绝。
    /// </summary>
    internal static VerifiedCollectorArtifact RequireSelectedExternalHostArtifact(
        LocalCollectorPackage package,
        string artifactId) => ResolveExternalHostArtifact(package, artifactId);

    private static VerifiedCollectorArtifact ResolveExternalHostArtifact(
        LocalCollectorPackage package,
        string artifactId)
    {
        var operatingSystem = CurrentOperatingSystem();
        var architecture = CurrentArchitecture();
        var candidates = package.Manifest.Artifacts.Where(artifact =>
            artifact.Driver == "externalHost" &&
            artifact.OperatingSystems.Contains(operatingSystem, StringComparer.Ordinal) &&
            artifact.Architectures.Contains(architecture, StringComparer.Ordinal)).ToArray();
        if (candidates.Length != 1)
            throw ActivationError(
                "package_mismatch",
                $"Collector Package must have exactly one Artifact for externalHost/{operatingSystem}/{architecture}; found {candidates.Length}.");
        if (candidates[0].ArtifactId != artifactId)
            throw ActivationError("package_mismatch", $"Artifact '{artifactId}' is not the selected current ExternalHost target.");
        return package.Artifacts.Single(artifact => artifact.ArtifactId == candidates[0].ArtifactId);
    }

    private static IReadOnlyDictionary<string, int> SelectedCapabilities(
        LocalCollectorPackage package,
        ProtocolSupport support)
    {
        var selected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var capability in support.Capabilities)
        {
            if (!HubProtocolCapabilities.TryGetValue(capability.Key, out var hub) ||
                !package.Manifest.SupportedCapabilities.TryGetValue(capability.Key, out var declared))
                continue;
            var version = hub.Intersect(declared).Intersect(capability.Value).DefaultIfEmpty().Max();
            if (version > 0)
                selected[capability.Key] = version;
        }
        return selected.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private sealed record PendingExternalHostActivation(
        CollectorInstance Instance,
        LocalCollectorPackage Package,
        CollectorActivationSession Session,
        CollectorActivationLifetime Lifetime,
        string ExternalHostIdentity,
        string AppIdentityKey);

    private sealed record ExternalHostActivationOwner(
        Guid CollectorInstanceId,
        string ExternalHostIdentity,
        string AppIdentityKey);
}
