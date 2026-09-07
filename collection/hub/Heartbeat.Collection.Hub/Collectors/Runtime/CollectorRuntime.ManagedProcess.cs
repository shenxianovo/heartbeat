using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Serilog;

namespace Heartbeat.Collection.Hub.Collectors.Runtime;

public enum CollectorRuntimePhase
{
    Starting,
    Negotiating,
    WaitingForAuthorization,
    OpeningStreams,
    Ready,
    Draining,
    Stopped,
    Failed
}

public sealed record CollectorRuntimeFailure(string Code, string Message, int? ProcessExitCode = null);

public sealed record CollectorRuntimeDiagnostic(string Code, string Message);

public enum CollectorAuthorizationChallengeKind
{
    Credentials,
    VerificationCode,
    Notice
}

public sealed record CollectorAuthorizationField(
    string Name,
    string Label,
    bool IsSecret = false,
    string? InputMode = null);

public sealed record CollectorAuthorizationChallenge(
    Guid InteractionId,
    CollectorAuthorizationChallengeKind Kind,
    string Title,
    string? Message,
    IReadOnlyList<CollectorAuthorizationField> Fields);

public sealed record CollectorRuntimeSnapshot(
    Guid CollectorInstanceId,
    Guid? ActivationId,
    CollectorRuntimePhase Phase,
    CollectorRuntimeFailure? Failure = null,
    int? PendingFacts = null,
    int? PendingGaps = null,
    bool ProcessTerminated = false,
    IReadOnlyList<CollectorRuntimeDiagnostic>? Diagnostics = null,
    CollectorAuthorizationChallenge? AuthorizationChallenge = null,
    InProcessCollectorDrainResult? DrainResult = null);

public sealed class ManagedProcessActivationOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DrainGracePeriod { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        ImmutableDictionary<string, string>.Empty;
    internal Func<TextWriter, TextWriter>? StandardInputDecorator { get; init; }

    internal void Validate()
    {
        CollectorRuntimeTimeout.Validate(StartupTimeout, nameof(StartupTimeout));
        CollectorRuntimeTimeout.Validate(DrainGracePeriod, nameof(DrainGracePeriod));
        ArgumentNullException.ThrowIfNull(EnvironmentVariables);
        if (EnvironmentVariables.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("ManagedProcess environment variables must have non-empty names and values.");
    }
}

public enum ManagedProcessUpdateOutcome
{
    Updated,
    RolledBack
}

public sealed record ManagedProcessUpdateResult(
    ManagedProcessUpdateOutcome Outcome,
    ManagedProcessCollectorActivation Activation,
    CollectorRuntimeFailure? CandidateFailure = null);

public sealed class ManagedProcessUpdateOptions
{
    public TimeSpan StabilityPeriod { get; init; } = TimeSpan.FromSeconds(30);
    public ManagedProcessActivationOptions CandidateActivation { get; init; } = new();
    public ManagedProcessActivationOptions RollbackActivation { get; init; } = new();

    internal void Validate()
    {
        CollectorRuntimeTimeout.Validate(StabilityPeriod, nameof(StabilityPeriod));
        ArgumentNullException.ThrowIfNull(CandidateActivation);
        ArgumentNullException.ThrowIfNull(RollbackActivation);
        CandidateActivation.Validate();
        RollbackActivation.Validate();
    }
}

public sealed class ManagedProcessUpdateException(
    string message,
    CollectorRuntimeFailure candidateFailure,
    CollectorRuntimeFailure rollbackFailure,
    Exception? innerException = null) : Exception(message, innerException)
{
    public CollectorRuntimeFailure CandidateFailure { get; } = candidateFailure;
    public CollectorRuntimeFailure RollbackFailure { get; } = rollbackFailure;
}

public sealed partial class CollectorRuntime
{
    private readonly Dictionary<Guid, ManagedProcessCollectorActivation> _managedProcessActivations = [];
    private readonly Dictionary<Guid, ManagedProcessProtocolClient> _managedProcessClients = [];
    private readonly Dictionary<Guid, CollectorRuntimeSnapshot> _managedProcessStates = [];
    private readonly List<ManagedProcessPhaseWaiter> _managedProcessPhaseWaiters = [];
    private readonly HashSet<Guid> _managedProcessUpdates = [];

    public CollectorRuntimeSnapshot GetManagedProcessRuntimeState(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _managedProcessStates.TryGetValue(collectorInstanceId, out var state)
                ? state
                : throw new KeyNotFoundException(
                    $"ManagedProcess Runtime State for Collector Instance '{collectorInstanceId}' was not found.");
        }
    }

    /// <summary>
    /// Completes as soon as the ManagedProcess Runtime State of the Collector Instance is published
    /// with the phase. Every publication signals the waiters, so observers never poll the state.
    /// </summary>
    internal async Task WaitForManagedProcessPhaseAsync(
        Guid collectorInstanceId,
        CollectorRuntimePhase phase,
        CancellationToken cancellationToken)
    {
        ManagedProcessPhaseWaiter waiter;
        lock (_gate)
        {
            if (_managedProcessStates.TryGetValue(collectorInstanceId, out var state) && state.Phase == phase)
                return;
            waiter = new ManagedProcessPhaseWaiter(collectorInstanceId, phase);
            _managedProcessPhaseWaiters.Add(waiter);
        }
        using var registration = cancellationToken.UnsafeRegister(
            static (state, token) => ((ManagedProcessPhaseWaiter)state!).Completion.TrySetCanceled(token),
            waiter);
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    public ValueTask SubmitManagedProcessAuthorizationAsync(
        Guid collectorInstanceId,
        Guid interactionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ManagedProcessProtocolClient client;
        lock (_gate)
        {
            ThrowIfDisposed();
            client = _managedProcessClients.TryGetValue(collectorInstanceId, out var current)
                ? current
                : throw new KeyNotFoundException(
                    $"ManagedProcess Collector Instance '{collectorInstanceId}' is not running.");
        }
        return client.SubmitAuthorizationAsync(interactionId, values, cancellationToken);
    }

    public ValueTask<ManagedProcessCollectorActivation> ActivateManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        CancellationToken cancellationToken = default) =>
        ActivateManagedProcessAsync(collectorInstanceId, package, new ManagedProcessActivationOptions(), cancellationToken);

    public ValueTask<ManagedProcessUpdateResult> UpdateManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage candidate,
        CancellationToken cancellationToken = default) =>
        UpdateManagedProcessAsync(
            collectorInstanceId,
            candidate,
            new ManagedProcessUpdateOptions(),
            cancellationToken);

    public async ValueTask<ManagedProcessUpdateResult> UpdateManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage candidate,
        ManagedProcessUpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        ManagedProcessCollectorActivation currentActivation;
        LocalCollectorPackage previousPackage;
        VerifiedCollectorArtifact candidateArtifact;
        VerifiedCollectorArtifact previousArtifact;
        LastKnownGoodCollectorPackage previousInstallation;
        lock (_gate)
        {
            ThrowIfDisposed();
            var instance = GetInstanceStateLocked(collectorInstanceId);
            ValidatePackageCandidate(instance, candidate);
            candidateArtifact = ResolveManagedProcessArtifact(candidate);
            ValidateManagedProcessArtifactSnapshot(candidate, candidateArtifact);
            currentActivation = _managedProcessActivations.Values.SingleOrDefault(activation =>
                    activation.CollectorInstanceId == collectorInstanceId &&
                    activation.State == CollectorActivationState.Ready)
                ?? throw ActivationError(
                    "activation_not_ready",
                    "ManagedProcess update requires a current Ready Activation.");
            previousPackage = currentActivation.Package;
            if (previousPackage.Manifest.Version != instance.PackageVersion ||
                previousPackage.PackageContentHash != instance.PackageContentHash)
                throw ActivationError(
                    "package_mismatch",
                    "The current ManagedProcess Activation does not match the resolved Collector Package.");
            previousArtifact = ResolveManagedProcessArtifact(previousPackage);
            previousInstallation = new LastKnownGoodCollectorPackage(
                previousPackage.Manifest.Version,
                previousPackage.PackageContentHash,
                previousArtifact.ArtifactId,
                previousArtifact.ContentHash,
                instance.ConfigVersion);
            if (!_managedProcessUpdates.Add(collectorInstanceId))
                throw ActivationError(
                    "stream_writer_conflict",
                    "A ManagedProcess update is already running for this Collector Instance.");
        }

        try
        {
            PersistLastKnownGood(collectorInstanceId, previousInstallation);
            _ = await currentActivation.RequestStopAsync(
                new CollectorActivationStopIntent(CollectorActivationStopCause.UpdateRequested),
                CancellationToken.None);
            ManagedProcessCollectorActivation? candidateActivation = null;
            CollectorRuntimeFailure? candidateFailure = null;
            try
            {
                candidateActivation = await ActivateManagedProcessAsync(
                    collectorInstanceId,
                    candidate,
                    options.CandidateActivation,
                    cancellationToken);
                using var stabilityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var stabilityElapsed = Task.Delay(options.StabilityPeriod, stabilityCancellation.Token);
                var completed = await Task.WhenAny(candidateActivation.Completion, stabilityElapsed);
                if (completed == stabilityElapsed)
                {
                    await stabilityElapsed;
                    if (!candidateActivation.Completion.IsCompleted)
                    {
                        PersistLastKnownGood(
                            collectorInstanceId,
                            new LastKnownGoodCollectorPackage(
                                candidate.Manifest.Version,
                                candidate.PackageContentHash,
                                candidateArtifact.ArtifactId,
                                candidateArtifact.ContentHash,
                                previousInstallation.ConfigVersion));
                        return new ManagedProcessUpdateResult(
                            ManagedProcessUpdateOutcome.Updated,
                            candidateActivation);
                    }
                }

                stabilityCancellation.Cancel();
                _ = await candidateActivation.RequestStopAsync(
                    new CollectorActivationStopIntent(CollectorActivationStopCause.UpdateRequested),
                    CancellationToken.None);
                candidateFailure = candidateActivation.RuntimeState.Failure ?? new CollectorRuntimeFailure(
                    "process_exited",
                    "Candidate ManagedProcess exited during its stability period.",
                    candidateActivation.Client.ExitCode);
            }
            catch (Exception exception) when (exception is not ManagedProcessUpdateException)
            {
                if (candidateActivation is not null && candidateActivation.State != CollectorActivationState.Stopped)
                {
                    _ = await candidateActivation.RequestStopAsync(
                        new CollectorActivationStopIntent(CollectorActivationStopCause.ActivationFailed),
                        CancellationToken.None);
                }
                candidateFailure = ManagedProcessFailureFrom(exception, collectorInstanceId);
            }

            return await RollBackManagedProcessAsync(
                collectorInstanceId,
                previousPackage,
                previousArtifact,
                options.RollbackActivation,
                candidateFailure!);
        }
        finally
        {
            lock (_gate)
                _managedProcessUpdates.Remove(collectorInstanceId);
        }
    }

    public async ValueTask<ManagedProcessCollectorActivation> ActivateManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage package,
        ManagedProcessActivationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        VerifiedCollectorArtifact artifact;
        lock (_gate)
        {
            ThrowIfDisposed();
            var instance = GetInstanceStateLocked(collectorInstanceId);
            ValidatePackageCandidate(instance, package);
            artifact = ResolveManagedProcessArtifact(package);
            PublishManagedProcessStateLocked(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, null, CollectorRuntimePhase.Starting));
        }

        ManagedProcessProtocolClient? client = null;
        CollectorActivationLifetime? activationLifetime = null;
        using var startupTimeout = new CancellationTokenSource(options.StartupTimeout, _options.TimeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, startupTimeout.Token);
        var startupStarted = _options.TimeProvider.GetTimestamp();
        try
        {
            client = await ManagedProcessProtocolClient.StartAsync(
                package,
                artifact,
                collectorInstanceId,
                options,
                _secretStore,
                Path.Combine(_instanceDataRoot, collectorInstanceId.ToString("N")),
                _options.TimeProvider,
                linkedCancellation.Token);
            var selectedCapabilities = SelectedCapabilities(package, client.ProtocolSupport);
            if (_secretStore is null && selectedCapabilities.ContainsKey("secrets.instance"))
                selectedCapabilities = selectedCapabilities
                    .Where(capability => capability.Key != "secrets.instance")
                    .ToImmutableDictionary(capability => capability.Key, capability => capability.Value, StringComparer.Ordinal);
            client.SetSelectedCapabilities(selectedCapabilities);
            client.AuthorizationChallengeChanged += challenge =>
                ManagedProcessAuthorizationChanged(collectorInstanceId, challenge);
            lock (_gate)
                _managedProcessClients[collectorInstanceId] = client;
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, null, CollectorRuntimePhase.Negotiating));

            var remainingStartup = options.StartupTimeout - _options.TimeProvider.GetElapsedTime(startupStarted);
            if (remainingStartup <= TimeSpan.Zero)
                startupTimeout.Cancel();
            else
                startupTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            var protocolActivationTask = ActivateProtocolAsync(
                    collectorInstanceId,
                    package,
                    client,
                    "managedProcess",
                    client.HelloMessageId,
                    linkedCancellation.Token,
                    session => new ManagedProcessCollectorActivationLifetimeDriver(
                        client!,
                        session,
                        () => ManagedProcessDraining(collectorInstanceId)),
                    options.DrainGracePeriod,
                    lifetime => activationLifetime = lifetime)
                .AsTask();
            var protocolActivation = await AwaitReadyWithAuthorizationPauseAsync(
                protocolActivationTask,
                client,
                remainingStartup,
                startupTimeout,
                _options.TimeProvider,
                cancellationToken);
            var activation = new ManagedProcessCollectorActivation(
                this, collectorInstanceId, client, protocolActivation);
            lock (_gate)
            {
                _managedProcessActivations[protocolActivation.ActivationId] = activation;
                PublishManagedProcessStateLocked(collectorInstanceId, new CollectorRuntimeSnapshot(
                    collectorInstanceId, protocolActivation.ActivationId, CollectorRuntimePhase.Ready,
                    AuthorizationChallenge: null));
            }
            activation.StartSupervision();
            return activation;
        }
        catch (OperationCanceledException exception) when (
            startupTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (client is not null && activationLifetime is null)
                await client.AbortAsync();
            lock (_gate)
                _managedProcessClients.Remove(collectorInstanceId);
            var failure = new CollectorRuntimeFailure(
                "activation_start_timeout",
                $"ManagedProcess Collector exceeded {options.StartupTimeout} in non-authorization startup phases.");
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, client?.ActivationId, CollectorRuntimePhase.Failed, failure,
                ProcessTerminated: client?.WasTerminated == true));
            throw ActivationError(failure.Code, failure.Message, exception, retryable: true);
        }
        catch (Exception exception)
        {
            if (client is not null && activationLifetime is null)
                await client.AbortAsync();
            lock (_gate)
                _managedProcessClients.Remove(collectorInstanceId);
            var failure = exception switch
            {
                CollectorActivationException activationException => new CollectorRuntimeFailure(
                    ContainsProcessExit(activationException)
                        ? "process_exited"
                        : activationException.Error.Code,
                    ContainsProcessExit(activationException)
                        ? "ManagedProcess Collector exited before reaching Ready."
                        : activationException.Error.Message,
                    client?.ExitCode ?? FindProcessExit(activationException)?.ExitCode),
                ManagedProcessExitedException exitedException => new CollectorRuntimeFailure(
                    "process_exited", exitedException.Message, exitedException.ExitCode),
                ManagedProcessProtocolException protocolException => new CollectorRuntimeFailure(
                    "protocol_invalid_message", protocolException.Message, client?.ExitCode),
                _ => new CollectorRuntimeFailure("process_start_failed", exception.Message, client?.ExitCode)
            };
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId, client?.ActivationId, CollectorRuntimePhase.Failed, failure,
                ProcessTerminated: client?.WasTerminated == true));
            if (exception is CollectorActivationException originalActivationException &&
                failure.Code == originalActivationException.Error.Code)
                throw;
            throw ActivationError(failure.Code, failure.Message, exception, retryable: true);
        }
    }

    private static async Task<InProcessCollectorActivation> AwaitReadyWithAuthorizationPauseAsync(
        Task<InProcessCollectorActivation> activationTask,
        ManagedProcessProtocolClient client,
        TimeSpan remaining,
        CancellationTokenSource startupTimeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var last = timeProvider.GetTimestamp();
        while (!activationTask.IsCompleted)
        {
            // The activation task owns startup cleanup and receives the same cancellation through
            // its linked token. Join it before returning, including when authorization was pending.
            if (cancellationToken.IsCancellationRequested)
                break;
            var waitingForAuthorization = client.IsWaitingForAuthorization;
            var slice = waitingForAuthorization
                ? TimeSpan.FromMilliseconds(50)
                : TimeSpan.FromMilliseconds(Math.Min(50, Math.Max(1, remaining.TotalMilliseconds)));
            await Task.WhenAny(activationTask, Task.Delay(slice, timeProvider, cancellationToken));
            var now = timeProvider.GetTimestamp();
            if (!waitingForAuthorization)
            {
                remaining -= timeProvider.GetElapsedTime(last, now);
                if (remaining <= TimeSpan.Zero && !activationTask.IsCompleted)
                {
                    startupTimeout.Cancel();
                    break;
                }
            }
            last = now;
        }
        return await activationTask;
    }

    private void ManagedProcessAuthorizationChanged(
        Guid collectorInstanceId,
        CollectorAuthorizationChallenge? challenge)
    {
        lock (_gate)
        {
            if (_disposed || !_managedProcessStates.TryGetValue(collectorInstanceId, out var current))
                return;
            var ready = _managedProcessActivations.Values.Any(activation =>
                activation.CollectorInstanceId == collectorInstanceId &&
                activation.State == CollectorActivationState.Ready);
            PublishManagedProcessStateLocked(collectorInstanceId, current with
            {
                Phase = challenge is not null
                    ? CollectorRuntimePhase.WaitingForAuthorization
                    : ready ? CollectorRuntimePhase.Ready : CollectorRuntimePhase.Negotiating,
                AuthorizationChallenge = challenge
            });
        }
    }

    private void ManagedProcessDraining(Guid collectorInstanceId)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_managedProcessStates.TryGetValue(collectorInstanceId, out var state) &&
                state.Phase is not CollectorRuntimePhase.Stopped and not CollectorRuntimePhase.Failed)
                PublishManagedProcessStateLocked(collectorInstanceId, state with
                {
                    Phase = CollectorRuntimePhase.Draining
                });
        }
    }

    internal void ManagedProcessStopped(
        ManagedProcessCollectorActivation activation,
        ManagedProcessDrainResult result,
        InProcessCollectorDrainResult drainResult)
    {
        lock (_gate)
        {
            _managedProcessActivations.Remove(activation.ActivationId);
            _managedProcessClients.Remove(activation.CollectorInstanceId);
            PublishManagedProcessStateLocked(activation.CollectorInstanceId, new CollectorRuntimeSnapshot(
                activation.CollectorInstanceId, activation.ActivationId, CollectorRuntimePhase.Stopped,
                PendingFacts: result.PendingFacts, PendingGaps: result.PendingGaps,
                ProcessTerminated: result.ProcessTerminated,
                DrainResult: drainResult));
        }
    }

    internal void ManagedProcessFailed(
        ManagedProcessCollectorActivation activation,
        ManagedProcessExit result,
        InProcessCollectorDrainResult drainResult)
    {
        var failure = new CollectorRuntimeFailure(
            result.ProtocolError is null ? "process_exited" : "protocol_invalid_message",
            result.ProtocolError?.Message ??
            $"ManagedProcess Collector exited before drain completed (exit code {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).",
            result.ExitCode);
        lock (_gate)
        {
            _managedProcessActivations.Remove(activation.ActivationId);
            _managedProcessClients.Remove(activation.CollectorInstanceId);
            PublishManagedProcessStateLocked(activation.CollectorInstanceId, new CollectorRuntimeSnapshot(
                activation.CollectorInstanceId, activation.ActivationId, CollectorRuntimePhase.Failed,
                failure, activation.Client.PendingFacts, activation.Client.PendingGaps,
                activation.Client.WasTerminated,
                DrainResult: drainResult));
        }
    }

    private void SetManagedProcessState(Guid collectorInstanceId, CollectorRuntimeSnapshot state)
    {
        lock (_gate)
            PublishManagedProcessStateLocked(collectorInstanceId, state);
    }

    private void PublishManagedProcessStateLocked(Guid collectorInstanceId, CollectorRuntimeSnapshot state)
    {
        _managedProcessStates[collectorInstanceId] = state;
        _managedProcessPhaseWaiters.RemoveAll(waiter => waiter.TryComplete(collectorInstanceId, state.Phase));
    }

    private async ValueTask<ManagedProcessUpdateResult> RollBackManagedProcessAsync(
        Guid collectorInstanceId,
        LocalCollectorPackage previousPackage,
        VerifiedCollectorArtifact previousArtifact,
        ManagedProcessActivationOptions options,
        CollectorRuntimeFailure candidateFailure)
    {
        try
        {
            var rollback = await ActivateManagedProcessAsync(
                collectorInstanceId,
                previousPackage,
                options,
                CancellationToken.None);
            SetManagedProcessState(collectorInstanceId, rollback.RuntimeState with
            {
                Diagnostics =
                [
                    new CollectorRuntimeDiagnostic(
                        "update_candidate_failed_rolled_back",
                        $"Candidate failed ({candidateFailure.Code}); restored Last-Known-Good Package '{previousPackage.Manifest.Version}'.")
                ]
            });
            return new ManagedProcessUpdateResult(
                ManagedProcessUpdateOutcome.RolledBack,
                rollback,
                candidateFailure);
        }
        catch (Exception exception)
        {
            var artifactPath = Path.GetFullPath(Path.Combine(
                previousPackage.PackageDirectory,
                previousArtifact.Entrypoint.Replace('/', Path.DirectorySeparatorChar)));
            var rollbackFailure = !Directory.Exists(previousPackage.PackageDirectory) || !File.Exists(artifactPath)
                ? new CollectorRuntimeFailure(
                    "last_known_good_package_missing",
                    $"Last-Known-Good Package '{previousPackage.Manifest.PackageId}/{previousPackage.Manifest.Version}' or Artifact '{previousArtifact.ArtifactId}' is missing.")
                : ManagedProcessFailureFrom(exception, collectorInstanceId);
            var failure = new CollectorRuntimeFailure(
                "update_rollback_failed",
                $"Candidate failed ({candidateFailure.Code}) and Last-Known-Good rollback failed ({rollbackFailure.Code}).");
            SetManagedProcessState(collectorInstanceId, new CollectorRuntimeSnapshot(
                collectorInstanceId,
                null,
                CollectorRuntimePhase.Failed,
                failure,
                Diagnostics:
                [
                    new CollectorRuntimeDiagnostic(candidateFailure.Code, candidateFailure.Message),
                    new CollectorRuntimeDiagnostic(rollbackFailure.Code, rollbackFailure.Message)
                ]));
            throw new ManagedProcessUpdateException(
                failure.Message,
                candidateFailure,
                rollbackFailure,
                exception);
        }
    }

    private CollectorRuntimeFailure ManagedProcessFailureFrom(Exception exception, Guid collectorInstanceId)
    {
        lock (_gate)
        {
            if (_managedProcessStates.TryGetValue(collectorInstanceId, out var state) &&
                state.Phase == CollectorRuntimePhase.Failed && state.Failure is not null)
                return state.Failure;
        }
        return exception is CollectorActivationException activationException
            ? new CollectorRuntimeFailure(activationException.Error.Code, activationException.Error.Message)
            : new CollectorRuntimeFailure("process_start_failed", exception.Message);
    }

    private void PersistLastKnownGood(
        Guid collectorInstanceId,
        LastKnownGoodCollectorPackage lastKnownGood)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var current = GetInstanceStateLocked(collectorInstanceId);
            var updated = current with { LastKnownGoodPackage = lastKnownGood };
            var next = _state.WithInstanceAndStreams(updated, []);
            try
            {
                _store.Save(next);
            }
            catch (CollectorRuntimeStateException exception)
            {
                throw ActivationError(
                    "hub_backpressure",
                    "Hub could not persist the Last-Known-Good Collector Package.",
                    exception,
                    retryable: true);
            }
            _state = next;
        }
    }

    private static bool ContainsProcessExit(Exception exception) =>
        FindProcessExit(exception) is not null;

    private static ManagedProcessExitedException? FindProcessExit(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ManagedProcessExitedException exited)
                return exited;
        }
        return null;
    }

    private static VerifiedCollectorArtifact ResolveManagedProcessArtifact(LocalCollectorPackage package) =>
        ResolveProtocolArtifact(package, "managedProcess");

    private static void ValidateManagedProcessArtifactSnapshot(
        LocalCollectorPackage package,
        VerifiedCollectorArtifact artifact)
    {
        var artifactPath = Path.GetFullPath(Path.Combine(
            package.PackageDirectory,
            artifact.Entrypoint.Replace('/', Path.DirectorySeparatorChar)));
        byte[] content;
        try
        {
            content = File.ReadAllBytes(artifactPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ActivationError(
                "package_mismatch",
                $"ManagedProcess Artifact '{artifact.ArtifactId}' is unavailable before update.",
                exception);
        }
        var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        if (actualHash != artifact.ContentHash || !content.AsSpan().SequenceEqual(artifact.Content.Span))
            throw ActivationError(
                "package_mismatch",
                $"ManagedProcess Artifact '{artifact.ArtifactId}' changed after Package verification.");
    }
}

public sealed class ManagedProcessCollectorActivation : IAsyncDisposable
{
    private readonly CollectorRuntime _runtime;
    private readonly InProcessCollectorActivation _protocolActivation;
    private readonly CollectorActivationLifetime _lifetime;
    private Task? _terminalProjection;

    internal ManagedProcessCollectorActivation(
        CollectorRuntime runtime,
        Guid collectorInstanceId,
        ManagedProcessProtocolClient client,
        InProcessCollectorActivation protocolActivation)
    {
        _runtime = runtime;
        CollectorInstanceId = collectorInstanceId;
        Client = client;
        _protocolActivation = protocolActivation;
        _lifetime = protocolActivation.Lifetime;
    }

    public Guid CollectorInstanceId { get; }
    public Guid ActivationId => _protocolActivation.ActivationId;
    public int ProcessId => Client.ProcessId;
    public CollectorActivationState State => _protocolActivation.State;
    public ActivationDeliveryCapability DeliveryCapability => _protocolActivation.DeliveryCapability;
    public IReadOnlyList<CollectorHandshakeStep> HandshakeTranscript => _protocolActivation.HandshakeTranscript;
    public IReadOnlyDictionary<string, FactStreamDescriptor> Streams => _protocolActivation.Streams
        .ToDictionary(pair => pair.Key, pair => pair.Value.Descriptor, StringComparer.Ordinal);
    public CollectorRuntimeSnapshot RuntimeState => _runtime.GetManagedProcessRuntimeState(CollectorInstanceId);
    public Task Completion => _terminalProjection ?? Client.Completion;
    internal ManagedProcessProtocolClient Client { get; }
    internal LocalCollectorPackage Package => _protocolActivation.Package;
    internal InProcessCollectorDrainResult? ProtocolDrainResult => _protocolActivation.DrainResult;
    internal Task<CollectorActivationTerminalResult> Terminal => _lifetime.Terminal;
    internal int TerminationRequests => Client.TerminationRequests;

    internal void StartSupervision()
    {
        _terminalProjection = ObserveTerminalAsync();
        _ = SuperviseAsync();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _ = await RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.Deactivated),
            cancellationToken);

    public ValueTask DisposeAsync() => StopAsync();

    internal async ValueTask<CollectorActivationTerminalResult> RequestStopAsync(
        CollectorActivationStopIntent intent,
        CancellationToken cancellationToken = default)
    {
        var terminal = await _lifetime.RequestStopAsync(intent, cancellationToken);
        if (_terminalProjection is not null)
            await _terminalProjection.WaitAsync(cancellationToken);
        return terminal;
    }

    private async Task SuperviseAsync()
    {
        _ = await Client.ExitCompletion;
        _ = await _lifetime.RequestStopAsync(
            new CollectorActivationStopIntent(CollectorActivationStopCause.Supervision));
    }

    private async Task ObserveTerminalAsync()
    {
        var terminal = await _lifetime.Terminal;
        var process = Client.DrainResult;
        if (process.Failure is null && terminal.StopError is not null)
        {
            process = process with
            {
                ProcessTerminated = Client.WasTerminated,
                Failure = new ManagedProcessExit(Client.ExitCode, terminal.StopError)
            };
        }
        var drain = terminal.DrainOutcome.ToInProcess();
        if (process.Failure is not null)
            _runtime.ManagedProcessFailed(this, process.Failure, drain);
        else
            _runtime.ManagedProcessStopped(this, process, drain);
    }
}

/// <summary>
/// One registered observer of a ManagedProcess Runtime State phase. It is signalled by the state
/// publication itself, so observers do not have to sample the Runtime State on a timer.
/// </summary>
internal sealed class ManagedProcessPhaseWaiter(Guid collectorInstanceId, CollectorRuntimePhase phase)
{
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the waiter on a matching publication and reports whether it can be dropped.</summary>
    public bool TryComplete(Guid publishedInstanceId, CollectorRuntimePhase publishedPhase)
    {
        if (publishedInstanceId == collectorInstanceId && publishedPhase == phase)
            Completion.TrySetResult();
        return Completion.Task.IsCompleted;
    }
}

internal sealed record ManagedProcessDrainResult(
    int? PendingFacts,
    int? PendingGaps,
    bool ProcessTerminated,
    ManagedProcessExit? Failure = null);
internal sealed record ManagedProcessExit(int? ExitCode, Exception? ProtocolError);

internal sealed class ManagedProcessCollectorActivationLifetimeDriver(
    ManagedProcessProtocolClient client,
    CollectorActivationSession session,
    Action beginDraining) : ICollectorActivationLifetimeDriver
{
    public async ValueTask<CollectorActivationDriverStopResult> StopAsync(
        CollectorActivationStopIntent intent,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            beginDraining();
            session.BeginDrain();
            var drain = await client.StopOnceAsync(deadline).ConfigureAwait(false);
            return new CollectorActivationDriverStopResult(
                CollectorActivationDrainOutcome.FromInProcess(drain),
                Project(client.Termination));
        }
        catch (Exception exception)
        {
            var drain = await client.TerminateAfterStopFailureAsync(exception, deadline).ConfigureAwait(false);
            return new CollectorActivationDriverStopResult(
                CollectorActivationDrainOutcome.FromInProcess(drain),
                Project(client.Termination));
        }
    }

    /// <summary>
    /// Projects the terminal execution from the published termination state only. It runs after the
    /// fence wrote the cause, so it reports the first written cause instead of the stop reason guess.
    /// </summary>
    public CollectorActivationExecution ProjectFailedStop(CollectorDrainReason reason) =>
        Project(client.Termination);

    public void FenceAfterFailedStop(CollectorDrainReason reason) =>
        client.TerminateAfterLifetimeFailure(reason);

    private CollectorActivationExecution Project(ManagedProcessTermination? termination) =>
        termination is null
            ? new ManagedProcessExitedExecution(client.ExitCode)
            : new ManagedProcessTerminatedExecution(termination.Cause, client.ExitCode);
}

internal sealed class ManagedProcessProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class ManagedProcessExitedException(string message, int? exitCode = null)
    : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}

internal sealed class ManagedProcessProtocolClient : IInProcessCollector
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Process _process;
    private readonly StreamReader _reader;
    private readonly TextWriter _writer;
    private readonly ManagedProcessActivationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Guid _collectorInstanceId;
    private readonly ICollectorSecretStore? _secretStore;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource<ManagedProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ManagedProcessDrainResult> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _authorizationGate = new();
    private InProcessCollectorActivation? _activation;
    private IReadOnlyDictionary<string, int> _selectedCapabilities = ImmutableDictionary<string, int>.Empty;
    private long _specRevision;
    private Guid? _drainMessageId;
    private ManagedProcessDrainResult? _drainResult;
    private CollectorAuthorizationChallenge? _authorizationChallenge;
    private ManagedProcessTermination? _termination;

    private ManagedProcessProtocolClient(
        Process process,
        ManagedProcessActivationOptions options,
        Guid helloMessageId,
        string artifactId,
        ProtocolSupport protocolSupport,
        Guid collectorInstanceId,
        ICollectorSecretStore? secretStore,
        string dataDirectory,
        TimeProvider timeProvider)
    {
        _process = process;
        _reader = process.StandardOutput;
        _writer = options.StandardInputDecorator?.Invoke(process.StandardInput) ?? process.StandardInput;
        _options = options;
        _timeProvider = timeProvider;
        _collectorInstanceId = collectorInstanceId;
        _secretStore = secretStore;
        _dataDirectory = dataDirectory;
        HelloMessageId = helloMessageId;
        ArtifactId = artifactId;
        ProtocolSupport = protocolSupport;
    }

    public Guid HelloMessageId { get; }
    public Guid? ActivationId { get; private set; }
    public string ArtifactId { get; }
    public ProtocolSupport ProtocolSupport { get; }
    public int ProcessId => _process.Id;
    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
    /// <summary>
    /// The single published termination fact, or <see langword="null"/> while the process was never
    /// terminated by the Hub. Read it once and derive every projection from that snapshot.
    /// </summary>
    public ManagedProcessTermination? Termination => Volatile.Read(ref _termination);
    public bool WasTerminated => Termination is not null;
    public ManagedProcessTerminationCause? TerminationCause => Termination?.Cause;
    internal int TerminationRequests => Termination is null ? 0 : 1;
    public int? PendingFacts { get; private set; }
    public int? PendingGaps { get; private set; }
    public CollectorDrainReason DrainReason { get; private set; } = CollectorDrainReason.FlushCancelled;
    public bool RemainderDurable { get; private set; }

    /// <summary>
    /// Whether the Collector-reported remainder evidence is complete enough to be trusted as durable.
    /// Terminating the process later cannot make already reported durable evidence untrue.
    /// </summary>
    public bool LogicalRemainderDurable => RemainderDurable && PendingFacts is not null && PendingGaps is not null;
    public Task Completion => _exit.Task;
    public Task<ManagedProcessExit> ExitCompletion => _exit.Task;
    public ManagedProcessDrainResult DrainResult => _drainResult ?? (_drained.Task.IsCompletedSuccessfully
        ? _drained.Task.Result
        : new ManagedProcessDrainResult(PendingFacts, PendingGaps, WasTerminated));
    public event Action<CollectorAuthorizationChallenge?>? AuthorizationChallengeChanged;

    public bool IsWaitingForAuthorization
    {
        get { lock (_authorizationGate) return _authorizationChallenge is not null; }
    }

    public void SetSelectedCapabilities(IReadOnlyDictionary<string, int> selectedCapabilities) =>
        _selectedCapabilities = selectedCapabilities;

    public async ValueTask SubmitAuthorizationAsync(
        Guid interactionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        CollectorAuthorizationChallenge challenge;
        lock (_authorizationGate)
        {
            challenge = _authorizationChallenge
                ?? throw new InvalidOperationException("Collector is not waiting for authorization.");
            if (challenge.InteractionId != interactionId)
                throw new InvalidOperationException("Authorization interaction is no longer current.");
            var expected = challenge.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            if (!expected.SetEquals(values.Keys) || values.Any(pair => string.IsNullOrEmpty(pair.Value)))
                throw new ArgumentException(
                    "Authorization response must contain every declared non-empty field.",
                    nameof(values));
        }
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "auth.response",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            body = new { interactionId, values }
        }, cancellationToken);
    }

    public static async Task<ManagedProcessProtocolClient> StartAsync(
        LocalCollectorPackage package,
        VerifiedCollectorArtifact artifact,
        Guid collectorInstanceId,
        ManagedProcessActivationOptions options,
        ICollectorSecretStore? secretStore,
        string dataDirectory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var artifactPath = Path.GetFullPath(Path.Combine(
            package.PackageDirectory,
            artifact.Entrypoint.Replace('/', Path.DirectorySeparatorChar)));
        var content = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        if (actualHash != artifact.ContentHash || !content.AsSpan().SequenceEqual(artifact.Content.Span))
            throw new ManagedProcessProtocolException("ManagedProcess Artifact changed after Package verification.");
        Directory.CreateDirectory(dataDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = artifactPath,
            WorkingDirectory = package.PackageDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["HEARTBEAT_COLLECTOR_INSTANCE_ID"] = collectorInstanceId.ToString("D");
        startInfo.Environment["HEARTBEAT_COLLECTOR_PACKAGE_ID"] = package.Manifest.PackageId;
        startInfo.Environment["HEARTBEAT_COLLECTOR_PACKAGE_VERSION"] = package.Manifest.Version;
        startInfo.Environment["HEARTBEAT_COLLECTOR_ARTIFACT_ID"] = artifact.ArtifactId;
        startInfo.Environment["HEARTBEAT_COLLECTOR_ARTIFACT_HASH"] = artifact.ContentHash;
        foreach (var pair in options.EnvironmentVariables)
        {
            if (pair.Key.StartsWith("HEARTBEAT_COLLECTOR_", StringComparison.Ordinal))
                throw new ArgumentException($"ManagedProcess environment variable '{pair.Key}' is reserved by the binding.");
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        _ = DrainStandardErrorAsync(process.StandardError);

        try
        {
            using var hello = await ReadRequiredMessageAsync(process.StandardOutput, cancellationToken);
            RequireEnvelope(hello.RootElement, "heartbeat.collector.bootstrap/1", "activation.hello");
            var messageId = ReadUuidV7(hello.RootElement, "messageId");
            var body = RequireObject(hello.RootElement, "body");
            RequireExactProperties(body, "collectorInstanceId", "runtimeArtifact", "protocolMajors", "supportedCapabilities");
            if (ReadGuid(body, "collectorInstanceId") != collectorInstanceId)
                throw new ManagedProcessProtocolException("activation.hello collectorInstanceId does not match the configured Instance.");
            var runtimeArtifact = RequireObject(body, "runtimeArtifact");
            RequireExactProperties(runtimeArtifact, "packageId", "packageVersion", "artifactId", "artifactHash");
            if (ReadString(runtimeArtifact, "packageId") != package.Manifest.PackageId ||
                ReadString(runtimeArtifact, "packageVersion") != package.Manifest.Version ||
                ReadString(runtimeArtifact, "artifactId") != artifact.ArtifactId ||
                ReadString(runtimeArtifact, "artifactHash") != artifact.ContentHash)
                throw new ManagedProcessProtocolException("activation.hello runtimeArtifact does not match the selected verified Artifact.");
            var support = new ProtocolSupport(
                ReadPositiveIntArray(body, "protocolMajors"),
                ReadCapabilities(body, "supportedCapabilities"));
            return new ManagedProcessProtocolClient(
                process,
                options,
                messageId,
                artifact.ArtifactId,
                support,
                collectorInstanceId,
                secretStore,
                dataDirectory,
                timeProvider);
        }
        catch (ManagedProcessExitedException exception)
        {
            await WaitForExitAsync(process);
            var exitCode = process.HasExited ? process.ExitCode : exception.ExitCode;
            process.Dispose();
            throw new ManagedProcessExitedException(
                "ManagedProcess Collector exited before activation.hello completed.",
                exitCode);
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask<InProcessCollectorInitialization> InitializeAsync(
        CollectorInitialization initialization,
        CancellationToken cancellationToken)
    {
        ActivationId = initialization.ActivationId;
        await WriteAsync(new
        {
            protocol = "heartbeat.collector.bootstrap/1",
            type = "activation.accepted",
            messageId = Guid.CreateVersion7(),
            replyTo = HelloMessageId,
            body = new
            {
                activationId = initialization.ActivationId,
                selectedProtocolMajor = 1,
                selectedCapabilities = _selectedCapabilities
            }
        }, cancellationToken);

        var initializeMessageId = Guid.CreateVersion7();
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.initialize",
            messageId = initializeMessageId,
            activationId = initialization.ActivationId,
            body = new
            {
                instance = new
                {
                    collectorInstanceId = initialization.Instance.CollectorInstanceId,
                    subject = new
                    {
                        subjectId = initialization.Instance.Subject.SubjectId,
                        kind = SubjectKindName(initialization.Instance.Subject.Kind)
                    }
                },
                spec = new
                {
                    revision = initialization.Spec.SpecRevision,
                    config = new { version = initialization.Spec.ConfigVersion, value = initialization.Spec.Config }
                },
                limits = initialization.Limits,
                resources = initialization.Resources,
                hubTime = ProtocolTimestamp(DateTimeOffset.UtcNow)
            }
        }, cancellationToken);

        using var initialized = await ReadInitializationResponseAsync(initializeMessageId, cancellationToken);
        RequireResponse(initialized.RootElement, "activation.initialized", initializeMessageId, initialization.ActivationId);
        var initializedBody = RequireObject(initialized.RootElement, "body");
        RequireExactProperties(initializedBody, "appliedSpecRevision");
        var appliedSpecRevision = ReadPositiveLong(initializedBody, "appliedSpecRevision");
        _specRevision = initialization.Spec.SpecRevision;

        using var open = await ReadRequiredMessageAsync(_reader, cancellationToken);
        RequireEnvelope(open.RootElement, "heartbeat.collector/1", "streams.open", initialization.ActivationId);
        var openBody = RequireObject(open.RootElement, "body");
        RequireExactProperties(openBody, "specRevision", "bindings");
        if (ReadPositiveLong(openBody, "specRevision") != appliedSpecRevision)
            throw new ManagedProcessProtocolException("streams.open specRevision does not match activation.initialized.");
        var bindings = RequireArray(openBody, "bindings").EnumerateArray().Select(binding =>
        {
            RequireExactProperties(binding, "bindingId", "outputId", "dimensions");
            var dimensions = RequireObject(binding, "dimensions").EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : throw new ManagedProcessProtocolException("Stream dimension values must be strings."),
                StringComparer.Ordinal);
            return new OutputBinding(ReadString(binding, "bindingId"), ReadString(binding, "outputId"), dimensions);
        }).ToArray();
        OpenMessageId = ReadUuidV7(open.RootElement, "messageId");
        return new InProcessCollectorInitialization(appliedSpecRevision, bindings);
    }

    private async Task<JsonDocument> ReadInitializationResponseAsync(
        Guid initializeMessageId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReadRequiredMessageAsync(_reader, cancellationToken);
            var type = ReadString(message.RootElement, "type");
            if (type == "activation.initialized")
                return message;
            try
            {
                switch (type)
                {
                    case "auth.challenge":
                        HandleAuthorizationChallenge(message.RootElement);
                        break;
                    case "auth.completed":
                        HandleAuthorizationCompleted(message.RootElement);
                        break;
                    case "secret.read":
                        await HandleSecretReadAsync(message.RootElement, cancellationToken);
                        break;
                    case "secret.write":
                        await HandleSecretWriteAsync(message.RootElement, cancellationToken);
                        break;
                    case "secret.delete":
                        await HandleSecretDeleteAsync(message.RootElement, cancellationToken);
                        break;
                    default:
                        throw new ManagedProcessProtocolException(
                            $"Expected activation.initialized or authorization message for request '{initializeMessageId:D}'.");
                }
            }
            finally
            {
                message.Dispose();
            }
        }
    }

    private void HandleAuthorizationChallenge(JsonElement root)
    {
        RequireAuthorizationCapability();
        RequireEnvelope(root, "heartbeat.collector/1", "auth.challenge", ActivationId!.Value);
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "interactionId", "kind", "title", "message", "fields");
        var kind = ReadString(body, "kind") switch
        {
            "credentials" => CollectorAuthorizationChallengeKind.Credentials,
            "verificationCode" => CollectorAuthorizationChallengeKind.VerificationCode,
            "notice" => CollectorAuthorizationChallengeKind.Notice,
            var value => throw new ManagedProcessProtocolException(
                $"Unknown authorization challenge kind '{value}'.")
        };
        var fields = RequireArray(body, "fields").EnumerateArray().Select(field =>
        {
            RequireExactProperties(field, "name", "label", "isSecret", "inputMode");
            return new CollectorAuthorizationField(
                ReadString(field, "name"),
                ReadString(field, "label"),
                ReadBoolean(field, "isSecret"),
                field.TryGetProperty("inputMode", out var inputMode) && inputMode.ValueKind == JsonValueKind.String
                    ? inputMode.GetString()
                    : null);
        }).ToArray();
        if ((kind != CollectorAuthorizationChallengeKind.Notice && fields.Length == 0) ||
            fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new ManagedProcessProtocolException(
                "Authorization challenge fields must be non-empty and unique.");
        var challenge = new CollectorAuthorizationChallenge(
            ReadUuidV7(body, "interactionId"),
            kind,
            ReadString(body, "title"),
            body.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null,
            fields);
        lock (_authorizationGate)
            _authorizationChallenge = challenge;
        AuthorizationChallengeChanged?.Invoke(challenge);
    }

    private void HandleAuthorizationCompleted(JsonElement root)
    {
        RequireAuthorizationCapability();
        RequireEnvelope(root, "heartbeat.collector/1", "auth.completed", ActivationId!.Value);
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "interactionId");
        var interactionId = ReadGuid(body, "interactionId");
        lock (_authorizationGate)
        {
            if (_authorizationChallenge?.InteractionId != interactionId)
                throw new ManagedProcessProtocolException(
                    "auth.completed does not match the current interaction.");
            _authorizationChallenge = null;
        }
        AuthorizationChallengeChanged?.Invoke(null);
    }

    private async Task HandleSecretReadAsync(JsonElement root, CancellationToken cancellationToken)
    {
        RequireSecretCapability();
        RequireEnvelope(root, "heartbeat.collector/1", "secret.read", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "key");
        var key = ReadString(body, "key");
        var value = await _secretStore!.ReadAsync(_collectorInstanceId, key, cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "secret.value",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = new { key, found = value is not null, value }
        }, cancellationToken);
    }

    private async Task HandleSecretWriteAsync(JsonElement root, CancellationToken cancellationToken)
    {
        RequireSecretCapability();
        RequireEnvelope(root, "heartbeat.collector/1", "secret.write", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "key", "value");
        var key = ReadString(body, "key");
        var value = ReadString(body, "value");
        await _secretStore!.WriteAsync(_collectorInstanceId, key, value, cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "secret.stored",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = new { key }
        }, cancellationToken);
    }

    private async Task HandleSecretDeleteAsync(JsonElement root, CancellationToken cancellationToken)
    {
        RequireSecretCapability();
        RequireEnvelope(root, "heartbeat.collector/1", "secret.delete", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "key");
        var key = ReadString(body, "key");
        await _secretStore!.DeleteAsync(_collectorInstanceId, key, cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "secret.deleted",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = new { key }
        }, cancellationToken);
    }

    private void RequireSecretCapability()
    {
        if (!_selectedCapabilities.ContainsKey("secrets.instance") || _secretStore is null)
            throw new ManagedProcessProtocolException(
                "Collector attempted to use unavailable secrets.instance capability.");
    }

    private void RequireAuthorizationCapability()
    {
        if (!_selectedCapabilities.ContainsKey("auth.interactive"))
            throw new ManagedProcessProtocolException(
                "Collector attempted to use unavailable auth.interactive capability.");
    }

    private Guid OpenMessageId { get; set; }

    public async ValueTask OnStreamsOpenedAsync(InProcessCollectorStreamsOpened opened, CancellationToken cancellationToken)
    {
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "streams.opened",
            messageId = Guid.CreateVersion7(),
            activationId = opened.ActivationId,
            replyTo = OpenMessageId,
            body = new
            {
                streams = opened.Streams.Select(pair => new { bindingId = pair.Key, stream = StreamDescriptor(pair.Value) })
            }
        }, cancellationToken);

        using var ready = await ReadRequiredMessageAsync(_reader, cancellationToken);
        RequireEnvelope(ready.RootElement, "heartbeat.collector/1", "activation.ready", opened.ActivationId);
        var readyMessageId = ReadUuidV7(ready.RootElement, "messageId");
        var readyBody = RequireObject(ready.RootElement, "body");
        RequireExactProperties(readyBody, "appliedSpecRevision");
        var appliedSpecRevision = ReadPositiveLong(readyBody, "appliedSpecRevision");
        if (appliedSpecRevision != _specRevision)
            throw new ManagedProcessProtocolException("activation.ready appliedSpecRevision does not match the initialized Spec.");
        _activation = await opened.ReadyAsync(cancellationToken);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.readyAck",
            messageId = Guid.CreateVersion7(),
            activationId = opened.ActivationId,
            replyTo = readyMessageId,
            body = new { appliedSpecRevision }
        }, cancellationToken);
        _ = PumpAsync();
    }

    public async ValueTask<InProcessCollectorDrainResult> StopAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await StopOnceAsync(deadline).ConfigureAwait(false);
    }

    internal async ValueTask<InProcessCollectorDrainResult> StopOnceAsync(
        DateTimeOffset deadline)
    {
        await StopCoreAsync(deadline).ConfigureAwait(false);
        if (Termination is { } termination)
        {
            var projection = ManagedProcessTerminationProjector.Project(termination.Cause);
            return new InProcessCollectorDrainResult(
                new InProcessCollectorLogicalDrainResult(
                    PendingFacts,
                    PendingGaps,
                    projection.Reason,
                    LogicalRemainderDurable),
                projection.CompletionReason,
                DrainResult.Failure?.ProtocolError?.Message);
        }
        var reason = DrainResult.Failure is null ? DrainReason : CollectorDrainReason.FlushCancelled;
        return new InProcessCollectorDrainResult(
            new InProcessCollectorLogicalDrainResult(
                PendingFacts,
                PendingGaps,
                reason,
                LogicalRemainderDurable),
            DrainResult.Failure is null
                ? CollectorDrainCompletionReason.Completed
                : CollectorDrainCompletionReason.CompletionFailed,
            DrainResult.Failure?.ProtocolError?.Message);
    }

    public async Task AbortAsync()
    {
        if (!_process.HasExited)
            Terminate(ManagedProcessTerminationCause.StartupAborted);
        await WaitForExitAsync(_process);
        _exit.TrySetResult(new ManagedProcessExit(ExitCode, null));
    }

    internal void TerminateAfterStopFailure()
    {
        if (_process.HasExited)
            return;
        Terminate(ManagedProcessTerminationCause.StopFailed);
    }

    internal void TerminateAfterLifetimeFailure(CollectorDrainReason reason)
    {
        if (_process.HasExited)
            return;
        Terminate(ManagedProcessTerminationProjector.FromFailedStopReason(reason));
    }

    internal async Task<InProcessCollectorDrainResult> TerminateAfterStopFailureAsync(
        Exception exception,
        DateTimeOffset deadline)
    {
        TerminateAfterStopFailure();
        _ = await TryWaitForExitBeforeAsync(deadline).ConfigureAwait(false);
        var failure = new ManagedProcessExit(ExitCode, exception);
        _exit.TrySetResult(failure);
        _drainResult = new ManagedProcessDrainResult(
            PendingFacts,
            PendingGaps,
            WasTerminated,
            failure);
        _drained.TrySetResult(_drainResult);
        var projection = ManagedProcessTerminationProjector.Project(
            Termination?.Cause ?? ManagedProcessTerminationCause.StopFailed);
        return new InProcessCollectorDrainResult(
            new InProcessCollectorLogicalDrainResult(
                PendingFacts,
                PendingGaps,
                projection.Reason,
                LogicalRemainderDurable),
            projection.CompletionReason,
            exception.Message);
    }

    private async Task StopCoreAsync(DateTimeOffset deadline)
    {
        if (_process.HasExited)
        {
            var exited = await _exit.Task;
            _drainResult = new ManagedProcessDrainResult(
                PendingFacts,
                PendingGaps,
                false,
                exited);
            _drained.TrySetResult(_drainResult);
            return;
        }
        if (_activation is null)
        {
            Terminate(ManagedProcessTerminationCause.BeforeReady);
            await TryWaitForExitBeforeAsync(deadline);
            _drainResult = new ManagedProcessDrainResult(null, null, true);
            _drained.TrySetResult(_drainResult);
            return;
        }

        using var deadlineCancellation = CreateDeadlineCancellation(deadline);
        _drainMessageId = Guid.CreateVersion7();
        try
        {
            await WriteAsync(new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.drain",
                messageId = _drainMessageId,
                activationId = ActivationId,
                body = new { deadline = ProtocolTimestamp(deadline) }
            }, deadlineCancellation.Token);
        }
        catch (Exception exception) when (
            exception is ManagedProcessProtocolException ||
            exception is OperationCanceledException && deadlineCancellation.IsCancellationRequested)
        {
            if (!_process.HasExited)
                Terminate(exception is OperationCanceledException
                    ? ManagedProcessTerminationCause.DeadlineExceeded
                    : ManagedProcessTerminationCause.DrainWriteFailed);
            await TryWaitForExitBeforeAsync(deadline);
            var drainWriteFailure = new ManagedProcessExit(ExitCode, exception);
            _exit.TrySetResult(drainWriteFailure);
            _drainResult = new ManagedProcessDrainResult(
                PendingFacts,
                PendingGaps,
                WasTerminated,
                drainWriteFailure);
            _drained.TrySetResult(_drainResult);
            return;
        }

        var drainAcknowledged = false;
        try
        {
            var drain = await _drained.Task.WaitAsync(deadlineCancellation.Token);
            drainAcknowledged = drain.Failure is null;
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
                await WaitForExitAsync(_process).WaitAsync(remaining);
        }
        catch (Exception exception) when (
            exception is TimeoutException ||
            exception is OperationCanceledException && deadlineCancellation.IsCancellationRequested)
        {
            if (!_process.HasExited)
                Terminate(ManagedProcessTerminationCause.DeadlineExceeded);
        }
        var processExited = await TryWaitForExitBeforeAsync(deadline);
        var exit = processExited && _exit.Task.IsCompletedSuccessfully
            ? _exit.Task.Result
            : new ManagedProcessExit(ExitCode, null);
        var failure = exit.ProtocolError is not null ||
                      (!drainAcknowledged && !WasTerminated) ||
                      (!WasTerminated && exit.ExitCode is not null and not 0)
            ? exit
            : null;
        _drainResult = new ManagedProcessDrainResult(
            PendingFacts,
            PendingGaps,
            WasTerminated,
            failure);
        _drained.TrySetResult(_drainResult);
    }

    private CancellationTokenSource CreateDeadlineCancellation(DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero
            ? new CancellationTokenSource(remaining, _timeProvider)
            : new CancellationTokenSource(TimeSpan.Zero, _timeProvider);
    }

    private async Task<bool> TryWaitForExitBeforeAsync(DateTimeOffset deadline)
    {
        var wait = WaitForExitAsync(_process);
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            Observe(wait);
            return wait.IsCompletedSuccessfully;
        }
        try
        {
            await wait.WaitAsync(remaining);
            return true;
        }
        catch (TimeoutException)
        {
            Observe(wait);
            return false;
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);

    private async Task PumpAsync()
    {
        Exception? protocolError = null;
        try
        {
            while (await ReadOptionalMessageAsync(_reader, CancellationToken.None) is { } message)
            {
                using (message)
                {
                    var root = message.RootElement;
                    switch (ReadString(root, "type"))
                    {
                        case "facts.publish":
                            await HandleFactsPublishAsync(root);
                            break;
                        case "stream.gap":
                            await HandleStreamGapAsync(root);
                            break;
                        case "auth.challenge":
                            HandleAuthorizationChallenge(root);
                            break;
                        case "auth.completed":
                            HandleAuthorizationCompleted(root);
                            break;
                        case "secret.read":
                            await HandleSecretReadAsync(root, CancellationToken.None);
                            break;
                        case "secret.write":
                            await HandleSecretWriteAsync(root, CancellationToken.None);
                            break;
                        case "secret.delete":
                            await HandleSecretDeleteAsync(root, CancellationToken.None);
                            break;
                        case "activation.drained":
                            HandleDrained(root);
                            break;
                        default:
                            throw new ManagedProcessProtocolException($"ManagedProcess sent unexpected message type '{ReadString(root, "type")}'.");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            protocolError = exception is ManagedProcessProtocolException
                ? exception
                : new ManagedProcessProtocolException("ManagedProcess protocol stream is invalid.", exception);
            if (!_process.HasExited)
                Terminate(ManagedProcessTerminationCause.ProtocolFailure);
        }
        finally
        {
            await WaitForExitAsync(_process);
            var exit = new ManagedProcessExit(ExitCode, protocolError);
            _exit.TrySetResult(exit);
            if (_drainMessageId is not null && !_drained.Task.IsCompleted)
            {
                var failure = protocolError is not null || !WasTerminated
                    ? exit
                    : null;
                _drainResult = new ManagedProcessDrainResult(
                    PendingFacts,
                    PendingGaps,
                    WasTerminated,
                    failure);
                _drained.TrySetResult(_drainResult);
            }
        }
    }

    private async Task HandleFactsPublishAsync(JsonElement root)
    {
        RequireEnvelope(root, "heartbeat.collector/1", "facts.publish", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "facts");
        var facts = RequireArray(body, "facts").EnumerateArray().Select(ReadFact).ToArray();
        var streamIds = facts.Select(fact => fact.StreamId).Distinct().ToArray();
        if (streamIds.Length != 1 || _activation is null ||
            _activation.Streams.Values.All(stream => stream.Descriptor.StreamId != streamIds[0]))
            throw new ManagedProcessProtocolException("facts.publish references an unopened Stream.");
        var stream = _activation.Streams.Values.Single(item => item.Descriptor.StreamId == streamIds[0]);
        var acknowledgement = await stream.PublishAsync(messageId, facts);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = acknowledgement.IsMessageRejected ? "facts.rejected" : "facts.ack",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = acknowledgement.IsMessageRejected
                ? (object)new { error = acknowledgement.MessageError }
                : new
                {
                    results = acknowledgement.Results.Select(result => new
                    {
                        index = result.Index,
                        status = EnumName(result.Status),
                        error = result.Error,
                        retryAfterMs = result.RetryAfterMilliseconds
                    })
                }
        }, CancellationToken.None);
    }

    private async Task HandleStreamGapAsync(JsonElement root)
    {
        RequireEnvelope(root, "heartbeat.collector/1", "stream.gap", ActivationId!.Value);
        var messageId = ReadUuidV7(root, "messageId");
        var body = RequireObject(root, "body");
        RequireExactProperties(body, "streamId", "gapId", "factTime", "reason", "estimatedFactsLost");
        var streamId = ReadGuid(body, "streamId");
        var gapId = ReadUuidV7(body, "gapId");
        var time = RequireObject(body, "factTime");
        RequireExactProperties(time, "start", "end");
        var gap = new StreamGapReport(
            gapId, ReadUtcTimestamp(time, "start"), ReadUtcTimestamp(time, "end"), ReadString(body, "reason"),
            body.TryGetProperty("estimatedFactsLost", out var estimate) ? estimate.GetInt32() : null);
        var stream = _activation?.Streams.Values.SingleOrDefault(item => item.Descriptor.StreamId == streamId)
            ?? throw new ManagedProcessProtocolException("stream.gap references an unopened Stream.");
        var outcome = await stream.ReportGapAsync(messageId, gap);
        await WriteAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = outcome.IsAcknowledged ? "stream.gapAck" : "stream.gapRejected",
            messageId = Guid.CreateVersion7(),
            activationId = ActivationId,
            replyTo = messageId,
            body = outcome.IsAcknowledged ? (object)new { streamId } : new { error = outcome.Error }
        }, CancellationToken.None);
    }

    private void HandleDrained(JsonElement root)
    {
        RequireEnvelope(root, "heartbeat.collector/1", "activation.drained", ActivationId!.Value, hasReplyTo: true);
        var body = RequireObject(root, "body");
        RequireExactProperties(
            body,
            "appliedSpecRevision",
            "pendingFacts",
            "pendingGaps",
            "reason",
            "remainderDurable");
        if (_drainMessageId is null || ReadGuid(root, "replyTo") != _drainMessageId)
            throw new ManagedProcessProtocolException("activation.drained replyTo does not match activation.drain.");
        if (ReadPositiveLong(body, "appliedSpecRevision") != _specRevision)
            throw new ManagedProcessProtocolException("activation.drained appliedSpecRevision does not match the initialized Spec.");
        PendingFacts = ReadNonNegativeInt(body, "pendingFacts");
        PendingGaps = ReadNonNegativeInt(body, "pendingGaps");
        var reason = ReadString(body, "reason");
        if (!CollectorDrainVocabulary.TryParse(reason, out var drainReason))
            throw new ManagedProcessProtocolException($"activation.drained reason '{reason}' is not supported.");
        DrainReason = drainReason;
        RemainderDurable = ReadBoolean(body, "remainderDurable");
        _drainResult = new ManagedProcessDrainResult(PendingFacts, PendingGaps, false);
        _drained.TrySetResult(_drainResult);
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new ManagedProcessProtocolException("ManagedProcess protocol connection closed while writing.", exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<JsonDocument> ReadRequiredMessageAsync(StreamReader reader, CancellationToken cancellationToken) =>
        await ReadOptionalMessageAsync(reader, cancellationToken)
        ?? throw new ManagedProcessExitedException("ManagedProcess protocol connection closed unexpectedly.");

    private static async Task<JsonDocument?> ReadOptionalMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
            return null;
        if (line.Length > 1_048_576)
            throw new ManagedProcessProtocolException("ManagedProcess protocol message exceeds 1 MiB.");
        try
        {
            var document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            RejectDuplicateKeys(document.RootElement);
            return document;
        }
        catch (JsonException exception)
        {
            throw new ManagedProcessProtocolException("ManagedProcess emitted malformed JSON.", exception);
        }
    }

    private static void RequireResponse(JsonElement root, string type, Guid replyTo, Guid activationId)
    {
        RequireEnvelope(root, "heartbeat.collector/1", type, activationId, hasReplyTo: true);
        if (ReadGuid(root, "replyTo") != replyTo)
            throw new ManagedProcessProtocolException($"{type} replyTo does not match its request.");
    }

    private static void RequireEnvelope(
        JsonElement root,
        string protocol,
        string type,
        Guid? activationId = null,
        bool hasReplyTo = false)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ManagedProcessProtocolException($"Expected {type} protocol envelope.");
        RequireExactProperties(
            root,
            activationId is null
                ? ["protocol", "type", "messageId", "body"]
                : hasReplyTo
                    ? ["protocol", "type", "messageId", "activationId", "replyTo", "body"]
                    : ["protocol", "type", "messageId", "activationId", "body"]);
        if (ReadString(root, "protocol") != protocol || ReadString(root, "type") != type)
            throw new ManagedProcessProtocolException($"Expected {type} protocol envelope.");
        _ = ReadUuidV7(root, "messageId");
        if (activationId is not null && ReadGuid(root, "activationId") != activationId)
            throw new ManagedProcessProtocolException($"{type} activationId does not match the live Activation.");
        _ = RequireObject(root, "body");
    }

    private static object StreamDescriptor(FactStreamDescriptor descriptor) => new
    {
        streamId = descriptor.StreamId,
        collectorInstanceId = descriptor.CollectorInstanceId,
        subject = new { subjectId = descriptor.Subject.SubjectId, kind = SubjectKindName(descriptor.Subject.Kind) },
        outputId = descriptor.OutputId,
        source = descriptor.Source,
        factKind = EnumName(descriptor.FactKind),
        schema = new { id = descriptor.Schema.Id, major = descriptor.Schema.Major, revision = descriptor.Schema.Revision, hash = descriptor.Schema.Hash },
        dimensions = descriptor.Dimensions
    };

    private static FactSubmission ReadFact(JsonElement fact)
    {
        RequireExactProperties(
            fact,
            "streamId",
            "schemaRevision",
            "factId",
            "revision",
            "observedAt",
            "recordState",
            "time",
            "payload");
        var recordState = ReadString(fact, "recordState") switch
        {
            "present" => FactRecordState.Present,
            "retracted" => FactRecordState.Retracted,
            var value => throw new ManagedProcessProtocolException($"Unknown Fact recordState '{value}'.")
        };
        var time = RequireObject(fact, "time");
        FactTime factTime;
        if (time.TryGetProperty("occurredAt", out _))
        {
            RequireExactProperties(time, "occurredAt");
            factTime = new EventFactTime(ReadUtcTimestamp(time, "occurredAt"));
        }
        else
        {
            RequireExactProperties(time, "start", "end", "isFinal");
            factTime = new SegmentFactTime(
                ReadUtcTimestamp(time, "start"),
                ReadUtcTimestamp(time, "end"),
                ReadBoolean(time, "isFinal"));
        }
        return new FactSubmission(
            ReadGuid(fact, "streamId"), ReadPositiveInt(fact, "schemaRevision"), ReadUuidV7(fact, "factId"),
            ReadPositiveLong(fact, "revision"),
            fact.TryGetProperty("observedAt", out _) ? ReadUtcTimestamp(fact, "observedAt") : null,
            recordState,
            factTime,
            recordState == FactRecordState.Present ? fact.GetProperty("payload").Clone() : default);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> ReadCapabilities(JsonElement parent, string name) =>
        RequireObject(parent, name).EnumerateObject().ToDictionary(
            property => property.Name,
            property => (IReadOnlyList<int>)ReadPositiveIntArrayValue(property.Value, $"{name}.{property.Name}"),
            StringComparer.Ordinal);

    private static IReadOnlyList<int> ReadPositiveIntArray(JsonElement parent, string name)
    {
        return ReadPositiveIntArrayValue(RequireArray(parent, name), name);
    }

    private static int[] ReadPositiveIntArrayValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new ManagedProcessProtocolException($"{name} must be an array.");
        var values = value.EnumerateArray().Select(item =>
            item.TryGetInt32(out var result)
                ? result
                : throw new ManagedProcessProtocolException($"{name} must contain integers.")).ToArray();
        if (values.Length == 0 || values.Any(value => value <= 0))
            throw new ManagedProcessProtocolException($"{name} must contain positive integers.");
        return values;
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new ManagedProcessProtocolException($"{name} must be an object.");
        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ManagedProcessProtocolException($"{name} must be an array.");
        return value;
    }

    private static string ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ManagedProcessProtocolException($"{name} must be a non-empty string.");
        return value.GetString()!;
    }

    private static Guid ReadGuid(JsonElement parent, string name) =>
        ReadString(parent, name) is { } text &&
        Guid.TryParseExact(text, "D", out var value) &&
        value != Guid.Empty &&
        string.Equals(text, value.ToString("D"), StringComparison.Ordinal)
            ? value
            : throw new ManagedProcessProtocolException($"{name} must be a canonical UUID.");

    private static void RequireExactProperties(JsonElement element, params string[] allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new ManagedProcessProtocolException("Protocol message field must be an object.");
        foreach (var property in element.EnumerateObject())
        {
            if (allowedProperties.IndexOf(property.Name) < 0)
                throw new ManagedProcessProtocolException($"Unknown protocol field '{property.Name}'.");
        }
    }

    private static Guid ReadUuidV7(JsonElement parent, string name)
    {
        var value = ReadGuid(parent, name);
        return value.Version == 7 ? value : throw new ManagedProcessProtocolException($"{name} must be a UUIDv7.");
    }

    private static int ReadPositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result <= 0)
            throw new ManagedProcessProtocolException($"{name} must be a positive integer.");
        return result;
    }

    private static long ReadPositiveLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result) || result <= 0)
            throw new ManagedProcessProtocolException($"{name} must be a positive integer.");
        return result;
    }

    private static int ReadNonNegativeInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result < 0)
            throw new ManagedProcessProtocolException($"{name} must be a non-negative integer.");
        return result;
    }

    private static bool ReadBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ManagedProcessProtocolException($"{name} must be a boolean.");
        return value.GetBoolean();
    }

    private static DateTimeOffset ReadUtcTimestamp(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || !text.EndsWith('Z') ||
            !value.TryGetDateTimeOffset(out var result) || result.Offset != TimeSpan.Zero)
            throw new ManagedProcessProtocolException($"{name} must be an RFC 3339 UTC timestamp.");
        return result;
    }

    private static string ProtocolTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ManagedProcessProtocolException($"Duplicate JSON field '{property.Name}'.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateKeys(item);
        }
    }

    private static string SubjectKindName(SubjectKind kind) => kind switch
    {
        SubjectKind.Machine => "machine",
        SubjectKind.Account => "account",
        SubjectKind.Person => "person",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string EnumName<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static async Task DrainStandardErrorAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                Log.Debug("ManagedProcess stderr: {Line}", line);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Task WaitForExitAsync(Process process) => process.HasExited ? Task.CompletedTask : process.WaitForExitAsync();

    private void Terminate(ManagedProcessTerminationCause cause)
    {
        if (Interlocked.CompareExchange(ref _termination, new ManagedProcessTermination(cause), null) is not null)
            return;
        Kill(_process);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
