using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Tests.Collectors;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

public sealed class ExternalHostCollectorProtocolTranscriptTests
{
    private static string ReferencePackagePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "ReferenceCollectorPackage");

    [Fact]
    public async Task LeaseSession_AppliesFullSpecOpensStreamAndOnlyAckedFactIsDurable()
    {
        using var directory = TemporaryDirectory.Create();
        using var packageCopy = ExternalHostPackageCopy();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        var sink = new SegmentIngestService(new FixedClock());
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "runtime.json"),
            sink);
        using var config = JsonDocument.Parse("""{"enabled":true,"flushPeriodMs":30000}""");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(4, 1, config.RootElement.Clone()));
        var initialization = runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            package,
            "reference.inprocess",
            package.Artifacts.Single().ContentHash,
            Support(),
            "external-host-a",
            "app.reference",
            Guid.CreateVersion7());
        var activationId = initialization.ActivationId;

        Assert.Equal(4, initialization.Spec.SpecRevision);
        Assert.True(initialization.Spec.Config.GetProperty("enabled").GetBoolean());

        var activation = runtime.OpenExternalHostStreams(
            activationId,
            initialization.Spec.SpecRevision,
            [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        Assert.Equal(CollectorActivationState.OpeningStreams, activation.State);
        await runtime.MarkExternalHostReadyAsync(activation, initialization.Spec.SpecRevision);
        CollectorProtocolTranscriptContract.AssertHappyPath(
            activation.State,
            activation.DeliveryCapability,
            activation.HandshakeTranscript,
            activation.Streams,
            instance.Subject,
            "reference");
        var stream = activation.Streams["activity"];
        using var payload = JsonDocument.Parse("""{"identityKey":"reference|work","title":"Work"}""");
        var fact = new FactSubmission(
            stream.StreamId,
            1,
            Guid.CreateVersion7(),
            1,
            null,
            FactRecordState.Present,
            new SegmentFactTime(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, false),
            payload.RootElement.Clone());

        var acknowledgement = await activation.PublishAsync(
            stream.StreamId,
            Guid.CreateVersion7(),
            [fact]);

        var result = Assert.Single(acknowledgement.Results);
        Assert.Null(result.Error);
        Assert.Equal(FactDeliveryStatus.Committed, result.Status);
        Assert.Single(sink.GetAndClearSegments());
    }

    [Fact]
    public async Task LeaseExpiry_LeavesReadyReleasesWriterAndDoesNotClaimBrowserTermination()
    {
        using var directory = TemporaryDirectory.Create();
        using var packageCopy = ExternalHostPackageCopy();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        using var runtime = CollectorRuntime.Open(
            Path.Combine(directory.Path, "runtime.json"),
            new SegmentIngestService(new FixedClock()));
        using var config = JsonDocument.Parse("{}");
        var instance = runtime.CreateInstance(
            package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        var initialization = runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            package,
            "reference.inprocess",
            package.Artifacts.Single().ContentHash,
            Support(),
            "external-host-a",
            "app.reference",
            Guid.CreateVersion7());
        var activationId = initialization.ActivationId;
        var activation = await runtime.ReadyExternalHostActivationAsync(
            activationId,
            initialization.Spec.SpecRevision,
            [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        var stream = activation.Streams["activity"];

        await runtime.StopExternalHostActivationAsync(
            activation,
            ExternalHostActivationStopReason.LeaseExpired);

        Assert.Equal(CollectorActivationState.Stopped, activation.State);
        Assert.Equal(ExternalHostActivationStopReason.LeaseExpired, activation.StopReason);
        Assert.False(activation.ExternalHostWasTerminated);
        using var payload = JsonDocument.Parse("""{"identityKey":"late","title":"Late"}""");
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await activation.PublishAsync(
                stream.StreamId,
                Guid.CreateVersion7(),
                [new FactSubmission(
                    stream.StreamId,
                    1,
                    Guid.CreateVersion7(),
                    1,
                    null,
                    FactRecordState.Present,
                    new SegmentFactTime(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, false),
                    payload.RootElement.Clone())]));
    }

    [Fact]
    public async Task PendingReservation_RuntimeDisposeCompletesItsPersistentTerminal()
    {
        using var fixture = new ExternalHostFixture();
        var pending = fixture.BeginReplacement();
        var terminal = fixture.Runtime.FindExternalHostActivationTerminal(pending.ActivationId);

        await fixture.Runtime.DisposeAsync();

        var result = await terminal;
        var execution = Assert.IsType<ExternalHostLeaseRevokedExecution>(result.Execution);
        Assert.Equal(ExternalHostActivationStopReason.RuntimeStopping, execution.Reason);
        Assert.True(result.OwnershipReleased);
        Assert.IsType<ExternalHostDrainEvidence.NotReported>(execution.DrainEvidence);
    }

    [Fact]
    public async Task LeaseExpiryDuringReadyPreparationStopsWithoutDurableMutationOrWriterGrant()
    {
        var publication = new BlockingStatePrepare();
        using var fixture = new ExternalHostFixture(publication.Callback);
        var activation = fixture.OpenStreams();
        var durableBeforeReady = File.ReadAllText(fixture.StatePath);
        publication.Arm();

        var ready = Task.Run(async () => await fixture.Runtime.MarkExternalHostReadyAsync(
            activation,
            fixture.SpecRevision));
        await publication.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = Task.Run(async () =>
            await activation.RequestStopAsync(ExternalHostActivationStopReason.LeaseExpired));

        CollectorActivationTerminalResult terminal;
        try
        {
            terminal = await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (!stopping.IsCompletedSuccessfully)
                publication.Release();
        }
        Assert.Same(terminal, await activation.Terminal);
        Assert.DoesNotContain(CollectorHandshakeStep.Ready, activation.HandshakeTranscript);
        Assert.Equal(ExternalHostActivationStopReason.LeaseExpired, activation.StopReason);
        Assert.True(terminal.OwnershipReleased);
        Assert.False(activation.ExternalHostWasTerminated);

        publication.Release();
        var error = await Assert.ThrowsAsync<CollectorActivationException>(() => ready);
        Assert.Equal("activation_stopping", error.Error.Code);
        Assert.Equal(durableBeforeReady, File.ReadAllText(fixture.StatePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));

        var replacement = await fixture.ReadyReplacementAsync();
        Assert.Equal(CollectorActivationState.Ready, replacement.State);
    }

    [Fact]
    public async Task ReadyPreparationRetriesWhenConcurrentStateMutationMakesReplacementStale()
    {
        var mutation = new MutateStateOnceDuringPrepare();
        using var fixture = new ExternalHostFixture(mutation.Callback);
        var activation = fixture.OpenStreams();
        Guid concurrentInstanceId = Guid.Empty;
        mutation.Arm(() => concurrentInstanceId = fixture.CreateUnrelatedInstance().CollectorInstanceId);

        await fixture.Runtime.MarkExternalHostReadyAsync(activation, fixture.SpecRevision);

        Assert.Equal(CollectorActivationState.Ready, activation.State);
        Assert.NotEqual(Guid.Empty, concurrentInstanceId);
        Assert.Equal(concurrentInstanceId, fixture.Runtime.GetInstance(concurrentInstanceId).CollectorInstanceId);
        Assert.True(mutation.ArmedPrepareCalls >= 3);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentExternalStopIntentsChooseOnePersistentTerminalAndReleaseOnce()
    {
        using var fixture = new ExternalHostFixture();
        var activation = await fixture.ReadyAsync();
        var intents = new[]
        {
            ExternalHostActivationStopReason.LeaseExpired,
            ExternalHostActivationStopReason.LeaseReplaced,
            ExternalHostActivationStopReason.DesiredDisabled
        };

        var results = await Task.WhenAll(intents.Select(reason => Task.Run(async () =>
            await activation.RequestStopAsync(reason))));

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.True(activation.StopReason is { } winner && intents.Contains(winner));
        Assert.True(results[0].OwnershipReleased);
        Assert.Same(results[0], await activation.Terminal);
        Assert.False(activation.ExternalHostWasTerminated);

        var replacement = await fixture.ReadyReplacementAsync();
        // 同一个 External Host Identity 的所有权仍然是排他的：旧 Activation 还活着时，同 identity 的
        // 竞争者在 activation.hello 就被拒绝，而不是等到 streams.open 才发现 lease 冲突。
        var competingError = Assert.Throws<CollectorActivationException>(() =>
            fixture.BeginReplacement());
        Assert.Equal("stream_writer_conflict", competingError.Error.Code);
        Assert.Contains("External Host Identity", competingError.Message, StringComparison.Ordinal);
        Assert.Equal(CollectorActivationState.Ready, replacement.State);
    }

    [Fact]
    public async Task LeaseRevokeWithoutActivationDrainedNeverClaimsFullyDrainedOrTerminated()
    {
        using var fixture = new ExternalHostFixture();
        var activation = await fixture.ReadyAsync();

        var terminal = await activation.RequestStopAsync(
            ExternalHostActivationStopReason.LeaseExpired);

        Assert.Null(activation.DrainResult);
        Assert.False(activation.ExternalHostWasTerminated);
        Assert.False(terminal.DrainOutcome?.IsFullyDrained ?? false);
        Assert.Equal(ExternalHostActivationStopReason.LeaseExpired, activation.StopReason);
    }

    [Fact]
    public void Hello_SameAttemptAfterRuntimeRestart_IsRejectedInsteadOfAllocatingNewActivation()
    {
        using var directory = TemporaryDirectory.Create();
        using var packageCopy = ExternalHostPackageCopy();
        var package = LocalCollectorPackage.Load(packageCopy.Path);
        var statePath = Path.Combine(directory.Path, "runtime.json");
        using var config = JsonDocument.Parse("{}");
        Guid collectorInstanceId;
        var helloMessageId = Guid.CreateVersion7();
        using (var runtime = CollectorRuntime.Open(statePath, new SegmentIngestService(new FixedClock())))
        {
            var instance = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            collectorInstanceId = instance.CollectorInstanceId;
            runtime.BeginExternalHostActivation(
                collectorInstanceId,
                package,
                "reference.inprocess",
                package.Artifacts.Single().ContentHash,
                Support(),
                "external-host-a",
                "app.reference",
                helloMessageId);
        }

        using var reopened = CollectorRuntime.Open(statePath, new SegmentIngestService(new FixedClock()));
        var error = Assert.Throws<CollectorActivationException>(() =>
            reopened.BeginExternalHostActivation(
                collectorInstanceId,
                package,
                "reference.inprocess",
                package.Artifacts.Single().ContentHash,
                Support(),
                "external-host-a",
                "app.reference",
                helloMessageId));

        Assert.Equal("protocol_invalid_message", error.Error.Code);
        Assert.Contains("previous Runtime session", error.Message, StringComparison.Ordinal);
    }

    private static ProtocolSupport Support() => new(
        [1],
        new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["facts.segment"] = [1],
            ["diagnostics.stream-gap"] = [1]
        });

    private static ReferenceCollectorPackageCopy ExternalHostPackageCopy()
    {
        var copy = ReferenceCollectorPackageCopy.Create(ReferencePackagePath);
        var manifest = copy.ReadManifest();
        manifest["artifacts"]![0]!["selector"]!["driver"] = "externalHost";
        copy.WriteManifest(manifest);
        return copy;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class ExternalHostFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
        private readonly ReferenceCollectorPackageCopy _packageCopy = ExternalHostPackageCopy();
        private readonly JsonDocument _config = JsonDocument.Parse("{}");
        private readonly LocalCollectorPackage _package;
        private readonly CollectorInstance _instance;

        public ExternalHostFixture(Action? beforeStatePrepare = null)
        {
            _package = LocalCollectorPackage.Load(_packageCopy.Path);
            Runtime = CollectorRuntime.Open(
                Path.Combine(_directory.Path, "runtime.json"),
                new SegmentIngestService(new FixedClock()),
                new CollectorRuntimeOptions { BeforeStatePrepare = beforeStatePrepare });
            _instance = Runtime.CreateInstance(
                _package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, _config.RootElement.Clone()));
        }

        public CollectorRuntime Runtime { get; }
        public long SpecRevision => _instance.Spec.SpecRevision;
        public string DirectoryPath => _directory.Path;
        public string StatePath => Path.Combine(_directory.Path, "runtime.json");

        public const string DefaultExternalHostIdentity = "external-host-a";
        public const string DefaultAppIdentityKey = "app.reference";

        public Guid CollectorInstanceId => _instance.CollectorInstanceId;
        public LocalCollectorPackage Package => _package;

        public ExternalHostCollectorActivation OpenStreams(
            string externalHostIdentity = DefaultExternalHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var initialization = BeginReplacement(externalHostIdentity, appIdentityKey);
            return Runtime.OpenExternalHostStreams(
                initialization.ActivationId,
                initialization.Spec.SpecRevision,
                [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        }

        public async ValueTask<ExternalHostCollectorActivation> ReadyAsync(
            string externalHostIdentity = DefaultExternalHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var initialization = BeginReplacement(externalHostIdentity, appIdentityKey);
            return await ReadyAsync(initialization);
        }

        public async ValueTask<ExternalHostCollectorActivation> ReadyAsync(
            ExternalHostCollectorInitialization initialization)
        {
            return await Runtime.ReadyExternalHostActivationAsync(
                initialization.ActivationId,
                initialization.Spec.SpecRevision,
                [new OutputBinding("activity", "activity", new Dictionary<string, string>())]);
        }

        public ValueTask<ExternalHostCollectorActivation> ReadyReplacementAsync() => ReadyAsync();

        public ExternalHostCollectorInitialization BeginReplacement(
            string externalHostIdentity = DefaultExternalHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey) =>
            Runtime.BeginExternalHostActivation(
                _instance.CollectorInstanceId,
                _package,
                "reference.inprocess",
                _package.Artifacts.Single().ContentHash,
                Support(),
                externalHostIdentity,
                appIdentityKey,
                Guid.CreateVersion7());

        public CollectorInstance CreateUnrelatedInstance() => Runtime.CreateInstance(
            _package,
            new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
            new CollectorInstanceSpec(1, 1, _config.RootElement.Clone()));

        public void Dispose()
        {
            Runtime.Dispose();
            _config.Dispose();
            _packageCopy.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class BlockingStatePrepare
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Action Callback => () =>
        {
            if (Volatile.Read(ref _armed) == 0)
                return;
            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        };

        public Task Entered => _entered.Task;
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _release.TrySetResult();
    }

    private sealed class MutateStateOnceDuringPrepare
    {
        private Action? _mutation;
        private int _armed;
        private int _calls;

        public Action Callback => () =>
        {
            if (Volatile.Read(ref _armed) == 0)
                return;
            Interlocked.Increment(ref _calls);
            Interlocked.Exchange(ref _mutation, null)?.Invoke();
        };

        public int ArmedPrepareCalls => Volatile.Read(ref _calls);

        public void Arm(Action mutation)
        {
            _mutation = mutation;
            Volatile.Write(ref _armed, 1);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-external-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
