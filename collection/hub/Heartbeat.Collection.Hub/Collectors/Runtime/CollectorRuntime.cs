using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Ingest;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public enum SubjectKind
{
    Machine,
    Account,
    Person
}

public readonly record struct SubjectReference
{
    public SubjectReference(Guid subjectId, SubjectKind kind)
    {
        if (subjectId == Guid.Empty)
            throw new ArgumentException("SubjectId must not be empty.", nameof(subjectId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        SubjectId = subjectId;
        Kind = kind;
    }

    public Guid SubjectId { get; }
    public SubjectKind Kind { get; }
}

public sealed record CollectorInstanceSpec(
    long SpecRevision,
    int ConfigVersion,
    JsonElement Config);

public sealed record LastKnownGoodCollectorPackage(
    string PackageVersion,
    string PackageContentHash,
    string ArtifactId,
    string ArtifactContentHash,
    int ConfigVersion);

public sealed record CollectorInstance(
    Guid CollectorInstanceId,
    string PackageId,
    string PackageVersion,
    string PackageContentHash,
    SubjectReference Subject,
    CollectorInstanceSpec Spec,
    LastKnownGoodCollectorPackage? LastKnownGoodPackage = null,
    string? InstanceKey = null);

public sealed class CollectorRuntimeOptions
{
    public Func<Guid> IdGenerator { get; init; } = Guid.CreateVersion7;
    public int MaxFactsPerBatch { get; init; } = 500;
    public int MaxBatchBytes { get; init; } = 1_048_576;
    /// <summary>Maximum durable replay-window entries retained for each Fact family.</summary>
    public int MaxDurableFacts { get; init; } = 20_000;
    public int RetryAfterMilliseconds { get; init; } = 1_000;
    public TimeSpan InProcessDrainGracePeriod { get; init; } = TimeSpan.FromSeconds(10);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    internal Action? BeforeStatePrepare { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(IdGenerator);
        if (MaxFactsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFactsPerBatch));
        if (MaxBatchBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchBytes));
        if (MaxDurableFacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDurableFacts));
        if (RetryAfterMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(RetryAfterMilliseconds));
        CollectorRuntimeTimeout.Validate(InProcessDrainGracePeriod, nameof(InProcessDrainGracePeriod));
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}

internal static class CollectorRuntimeTimeout
{
    internal static readonly TimeSpan MaximumSchedulable = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);

    internal static void Validate(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > MaximumSchedulable)
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Timeout must be positive and no greater than {MaximumSchedulable}.");
    }
}

/// <summary>
/// Collector Package/Instance/Activation runtime. Durable Instance and Fact Stream identity,
/// protocol convergence, and legacy Hub projection stay behind this module.
/// </summary>
public sealed partial class CollectorRuntime : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Runtime 拥有的默认 Instance 的保留 InstanceKey。它是 Runtime 的内部约定，不是任何 Collector
    /// 可以自己声明的名字，因此宿主侧不会因为某个具体产品而多出一个 Instance 命名规则。
    /// </summary>
    public const string DefaultInstanceKey = "default";

    private const long MaxSafeJsonInteger = 9_007_199_254_740_991;
    private readonly object _gate = new();
    private readonly JsonCollectorRuntimeStore _store;
    private readonly ISegmentSink _segmentSink;
    private readonly IInputEventFactSink? _inputEventSink;
    private readonly ICollectorSecretStore? _secretStore;
    private readonly string _instanceDataRoot;
    private readonly CollectorRuntimeOptions _options;
    private readonly object _disposeGate = new();
    private CollectorRuntimeState _state;
    private Task? _disposeTask;
    private bool _disposing;
    private bool _disposed;

    private CollectorRuntime(
        JsonCollectorRuntimeStore store,
        ISegmentSink segmentSink,
        CollectorRuntimeOptions options,
        CollectorRuntimeState state,
        IInputEventFactSink? inputEventSink,
        ICollectorSecretStore? secretStore,
        string instanceDataRoot)
    {
        _store = store;
        _segmentSink = segmentSink;
        _inputEventSink = inputEventSink;
        _secretStore = secretStore;
        _instanceDataRoot = instanceDataRoot;
        _options = options;
        _state = state;
        _segmentProjector = new ActivitySegmentFactProjector();
        _eventProjectors = [new InputEventFactProjector()];
    }

    public static CollectorRuntime Open(
        string stateFilePath,
        ISegmentSink segmentSink,
        CollectorRuntimeOptions? options = null,
        IInputEventFactSink? inputEventSink = null,
        ICollectorSecretStore? secretStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        ArgumentNullException.ThrowIfNull(segmentSink);
        options ??= new CollectorRuntimeOptions();
        options.Validate();

        var store = new JsonCollectorRuntimeStore(stateFilePath, options.BeforeStatePrepare);
        try
        {
            var state = store.Load();
            var runtime = new CollectorRuntime(
                store,
                segmentSink,
                options,
                state,
                inputEventSink,
                secretStore,
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stateFilePath))!, "collector-data"));
            runtime.RestorePersistedFactSchemas();
            runtime.ReplayCommittedFacts();
            return runtime;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public CollectorInstance CreateInstance(
        LocalCollectorPackage package,
        SubjectReference subject,
        CollectorInstanceSpec spec,
        string? instanceKey = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateSpec(spec);
        ValidateInstanceKey(instanceKey);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (instanceKey is not null && _state.Instances.Any(instance =>
                    instance.PackageId == package.Manifest.PackageId &&
                    instance.SubjectId == subject.SubjectId &&
                    instance.SubjectKind == subject.Kind &&
                    instance.InstanceKey == instanceKey))
                throw new InvalidOperationException(
                    $"Collector Instance key '{instanceKey}' already exists for this Package and Subject.");
            return CreateInstanceLocked(package, subject, spec, instanceKey);
        }
    }

    private CollectorInstance CreateInstanceLocked(
        LocalCollectorPackage package,
        SubjectReference subject,
        CollectorInstanceSpec spec,
        string? instanceKey)
    {
            var instanceId = _options.IdGenerator();
            if (!IsUuidV7(instanceId))
                throw new InvalidOperationException("Collector Runtime ID generator must return a UUIDv7.");
            if (_state.Instances.Any(instance => instance.CollectorInstanceId == instanceId))
                throw new InvalidOperationException($"Collector Instance '{instanceId}' already exists.");

            var instanceState = new CollectorInstanceState
            {
                CollectorInstanceId = instanceId,
                InstanceKey = instanceKey,
                PackageId = package.Manifest.PackageId,
                PackageVersion = package.Manifest.Version,
                PackageContentHash = package.PackageContentHash,
                SubjectId = subject.SubjectId,
                SubjectKind = subject.Kind,
                SpecRevision = spec.SpecRevision,
                ConfigVersion = spec.ConfigVersion,
                Config = spec.Config.Clone(),
                LastKnownGoodPackage = null
            };
            var next = _state.WithInstance(instanceState);
            _store.Save(next);
            _state = next;
            return ToPublic(instanceState);
    }

    public CollectorInstance GetInstance(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var state = _state.Instances.SingleOrDefault(
                instance => instance.CollectorInstanceId == collectorInstanceId)
                ?? throw new KeyNotFoundException($"Collector Instance '{collectorInstanceId}' was not found.");
            return ToPublic(state);
        }
    }

    public IReadOnlyList<CollectorInstance> ListInstances()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _state.Instances.Select(ToPublic).ToArray();
        }
    }

    /// <summary>
    /// Stops the current ManagedProcess activation, removes the durable Instance and all of its
    /// Fact state, then deletes its secret namespace and per-Instance data directory.
    /// </summary>
    public async ValueTask RemoveInstanceAsync(
        Guid collectorInstanceId,
        CancellationToken cancellationToken = default)
    {
        ManagedProcessCollectorActivation? activation;
        lock (_gate)
        {
            ThrowIfDisposed();
            _ = GetInstanceStateLocked(collectorInstanceId);
            if (_managedProcessUpdates.Contains(collectorInstanceId))
                throw new InvalidOperationException(
                    "Collector Instance cannot be removed during a ManagedProcess update.");
            if (_startingInstances.Contains(collectorInstanceId))
                throw new InvalidOperationException(
                    "Collector Instance cannot be removed while an Activation is starting.");
            activation = _managedProcessActivations.Values.SingleOrDefault(
                candidate => candidate.CollectorInstanceId == collectorInstanceId);
            if (activation is null && HasInProcessActivationLocked(collectorInstanceId))
                throw new InvalidOperationException(
                    "Collector Instance has an active InProcess Activation.");
            // 先立围栏：卸载过程中不再接受新的 activation.hello，避免刚停掉一个 External Host 又接进来
            // 一个新的。围栏在卸载成功（Instance 消失）或失败回滚时撤掉。
            _instancesBeingRemoved.Add(collectorInstanceId);
        }

        try
        {
            if (activation is not null)
                await activation.StopAsync(cancellationToken);
            // External Host 不是 Host 能杀死的进程，但它的 Activation 生命周期归 Host 所有：撤掉 lease、
            // 等到终态，Fact Stream 的 writer 才真正没人持有。
            await StopExternalHostActivationsForInstanceAsync(
                collectorInstanceId,
                ExternalHostActivationStopReason.DesiredDisabled);

            if (_secretStore is not null)
                await _secretStore.DeleteInstanceAsync(collectorInstanceId, cancellationToken);
            var dataDirectory = Path.Combine(_instanceDataRoot, collectorInstanceId.ToString("N"));
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);

            lock (_gate)
            {
                ThrowIfDisposed();
                if (_startingInstances.Contains(collectorInstanceId) ||
                    HasInProcessActivationLocked(collectorInstanceId) ||
                    HasExternalHostActivationLocked(collectorInstanceId))
                    throw new InvalidOperationException("Collector Instance still has an active Activation.");
                var next = _state.WithoutInstance(collectorInstanceId);
                _store.Save(next);
                _state = next;
                _managedProcessStates.Remove(collectorInstanceId);
                _managedProcessClients.Remove(collectorInstanceId);
            }
        }
        finally
        {
            lock (_gate)
                _instancesBeingRemoved.Remove(collectorInstanceId);
        }
    }

    private bool HasInProcessActivationLocked(Guid collectorInstanceId) =>
        _activations.Values.Any(activation =>
            activation.Streams.Values.Any(stream =>
                stream.Descriptor.CollectorInstanceId == collectorInstanceId));

    public IReadOnlyList<CollectorInstance> FindInstances(
        string packageId,
        SubjectReference subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _state.Instances
                .Where(instance =>
                    instance.PackageId == packageId &&
                    instance.SubjectId == subject.SubjectId &&
                    instance.SubjectKind == subject.Kind)
                .Select(ToPublic)
                .ToArray();
        }
    }

    public CollectorInstance? FindInstance(
        string packageId,
        SubjectReference subject,
        string instanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ValidateInstanceKey(instanceKey);
        lock (_gate)
        {
            ThrowIfDisposed();
            var state = _state.Instances.SingleOrDefault(instance =>
                instance.PackageId == packageId &&
                instance.SubjectId == subject.SubjectId &&
                instance.SubjectKind == subject.Kind &&
                instance.InstanceKey == instanceKey);
            return state is null ? null : ToPublic(state);
        }
    }

    public CollectorInstance UpdateInstanceSpec(
        Guid collectorInstanceId,
        int configVersion,
        JsonElement config)
    {
        if (configVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(configVersion));
        if (config.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Config must contain a JSON value.", nameof(config));

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_managedProcessUpdates.Contains(collectorInstanceId))
                throw new InvalidOperationException(
                    "Collector Instance config cannot change during a ManagedProcess update.");
            var current = GetInstanceStateLocked(collectorInstanceId);
            if (current.SpecRevision >= MaxSafeJsonInteger)
                throw new InvalidOperationException("Collector Instance SpecRevision cannot be incremented safely.");
            var updated = current with
            {
                SpecRevision = current.SpecRevision + 1,
                ConfigVersion = configVersion,
                Config = config.Clone()
            };
            var next = _state.WithInstanceAndStreams(updated, []);
            _store.Save(next);
            _state = next;
            return ToPublic(updated);
        }
    }

    private static void ValidateSpec(CollectorInstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.SpecRevision is <= 0 or > MaxSafeJsonInteger)
            throw new ArgumentOutOfRangeException(nameof(spec), "SpecRevision must be a positive JSON-safe integer.");
        if (spec.ConfigVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(spec), "ConfigVersion must be positive.");
        if (spec.Config.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Config must contain a JSON value.", nameof(spec));
    }

    private static void ValidateInstanceKey(string? instanceKey)
    {
        if (instanceKey is null)
            return;
        if (string.IsNullOrWhiteSpace(instanceKey) || instanceKey.Length > 200 || instanceKey != instanceKey.Trim())
            throw new ArgumentException(
                "InstanceKey must be a non-empty, trimmed string of at most 200 characters.",
                nameof(instanceKey));
    }

    private static CollectorInstance ToPublic(CollectorInstanceState state) => new(
        state.CollectorInstanceId,
        state.PackageId,
        state.PackageVersion,
        state.PackageContentHash,
        new SubjectReference(state.SubjectId, state.SubjectKind),
        new CollectorInstanceSpec(
            state.SpecRevision,
            state.ConfigVersion,
            state.Config.Clone()),
        state.LastKnownGoodPackage,
        state.InstanceKey);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null;
        Task disposeTask;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
            }
            disposeTask = _disposeTask;
        }
        if (owner is not null)
            _ = CompleteDisposeAsync(owner);
        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync();
            completion.SetResult();
        }
        catch (Exception exception)
        {
            lock (_disposeGate)
            {
                if (ReferenceEquals(_disposeTask, completion.Task))
                    _disposeTask = null;
            }
            completion.SetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        CollectorActivationLifetime[] activationLifetimes;
        ManagedProcessCollectorActivation[] managedProcessActivations;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposing = true;
            activationLifetimes = _activationLifetimes
                .Where(pair => !_managedProcessActivations.ContainsKey(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
            managedProcessActivations = _managedProcessActivations.Values.ToArray();
        }

        var stopTargets = activationLifetimes
            .Select((lifetime, index) => new CollectorRuntimeStopTarget(
                $"InProcess/ExternalHost Activation snapshot entry {index}",
                () => lifetime.RequestStopAsync(
                    new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping))))
            .Concat(managedProcessActivations.Select(activation => new CollectorRuntimeStopTarget(
                $"ManagedProcess Activation {activation.ActivationId}",
                () => activation.RequestStopAsync(
                    new CollectorActivationStopIntent(CollectorActivationStopCause.RuntimeStopping),
                    CancellationToken.None))))
            .ToArray();
        await CollectorRuntimeTerminationBatch.StopAllAsync(stopTargets).ConfigureAwait(false);

        lock (_gate)
        {
            _streamWriters.Clear();
            _store.Dispose();
            _disposed = true;
            _disposing = false;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _disposing, this);

    private void ThrowIfDeliveryUnavailable(Guid activationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_disposing &&
            (!_activations.TryGetValue(activationId, out var activation) ||
             activation.State != CollectorActivationState.Draining))
            throw new ObjectDisposedException(nameof(CollectorRuntime));
    }

    private static bool IsUuidV7(Guid id)
    {
        var text = id.ToString("D");
        return id != Guid.Empty && text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }
}
