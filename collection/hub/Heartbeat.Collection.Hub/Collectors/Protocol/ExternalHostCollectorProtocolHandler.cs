using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Protocol;

/// <summary>
/// 通用 ExternalHost loopback 绑定的宿主参数。这里只有协议会话自身的量，没有任何具体 Collector 的
/// 词汇：Package 从已验证 Installation 解析，Instance 由 Runtime 拥有。
/// </summary>
public sealed record ExternalHostProtocolBindingOptions
{
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// 通用 Collector Protocol 的 ExternalHost loopback 绑定。宿主只理解 Package、Instance、Activation、
/// Stream、Artifact、External Host Identity 与 AppIdentityKey；任何具体产品（浏览器、游戏客户端……）
/// 都是同一条路由上的一个 External Host。lease 只表示协议会话的所有权：过期只释放 Runtime 侧状态，
/// 从不假装能终止对端进程（ADR-046、ADR-051）。
/// </summary>
public sealed class ExternalHostCollectorProtocolHandler : IExternalHostProtocolHttpHandler, IDisposable, IAsyncDisposable
{
    public const string RoutePrefix = "/v1/collector-protocol/external-host";
    private readonly object _gate = new();
    private readonly CollectorRuntime _runtime;
    private readonly ICollectorDeclarationStore _declarations;
    private readonly CollectorPackageInstallations _installations;
    private readonly TimeProvider _timeProvider;
    private readonly ExternalHostProtocolBindingOptions _options;
    private readonly Func<SubjectReference> _subject;
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Dictionary<Guid, HelloAttempt> _helloAttempts = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ExternalHostCollectorProtocolHandler(
        CollectorRuntime runtime,
        ICollectorDeclarationStore declarations,
        CollectorPackageInstallations installations,
        Func<SubjectReference> subject,
        ExternalHostProtocolBindingOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(installations);
        ArgumentNullException.ThrowIfNull(subject);
        options ??= new ExternalHostProtocolBindingOptions();
        if (options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "LeaseDuration must be positive.");
        _runtime = runtime;
        _declarations = declarations;
        _installations = installations;
        _subject = subject;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ProtocolHttpResponse?> HandleAsync(
        string httpMethod,
        string? path,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        if (path is null || !path.StartsWith(RoutePrefix, StringComparison.Ordinal))
            return null;
        await ExpireLeasesAsync();
        Guid? replyTo = null;
        Guid? responseActivationId = null;
        string? rejectedType = null;
        var responseProtocol = "heartbeat.collector/1";

        async ValueTask<ProtocolMessage<T>> ReadRequest<T>(
            string protocol,
            string type,
            string failureType,
            Guid? activationId)
        {
            var message = await DeserializeAsync<ProtocolMessage<T>>(body, cancellationToken);
            replyTo = message.MessageId;
            responseActivationId = activationId;
            rejectedType = failureType;
            responseProtocol = protocol;
            if (message.Protocol != protocol || message.Type != type ||
                !IsUuidV7(message.MessageId) || message.Body is null ||
                message.ReplyTo is not null || message.ActivationId != activationId)
                throw new JsonException("Collector Protocol envelope is malformed or does not match the HTTP route.");
            return message;
        }

        try
        {
            if (httpMethod == "GET" && path == RoutePrefix)
                return Json(200, new { binding = "external-host", protocolMajors = new[] { 1 } });
            if (httpMethod == "POST" && path == $"{RoutePrefix}/hello")
                return await HandleHelloAsync(await ReadRequest<HelloRequest>(
                    "heartbeat.collector.bootstrap/1",
                    "activation.hello",
                    "activation.rejected",
                    null));
            if (!TryParseSessionPath(path, out var activationId, out var operation) || httpMethod != "POST")
                return Json(404, new { error = Error("protocol_invalid_message", "Unknown ExternalHost protocol route.") });
            return operation switch
            {
                "initialize" => HandleInitialize(activationId),
                "initialized" => HandleInitialized(
                    activationId,
                    await DeserializeAsync<ProtocolMessage<InitializedRequest>>(body, cancellationToken)),
                "streams" => await HandleStreamsAsync(activationId, await ReadRequest<StreamsOpenRequest>(
                    "heartbeat.collector/1", "streams.open", "streams.rejected", activationId)),
                "ready" => await HandleReadyAsync(activationId, await ReadRequest<ReadyRequest>(
                    "heartbeat.collector/1", "activation.ready", "activation.readyRejected", activationId)),
                "renew" => HandleRenew(activationId, await DeserializeAsync<RenewRequest>(body, cancellationToken)),
                "facts" => await HandleFactsAsync(
                    activationId,
                    await ReadRequest<PublishRequest>(
                        "heartbeat.collector/1", "facts.publish", "facts.rejected", activationId),
                    cancellationToken),
                "gap" => await HandleGapAsync(
                    activationId,
                    await ReadRequest<GapRequest>(
                        "heartbeat.collector/1", "stream.gap", "stream.gapRejected", activationId),
                    cancellationToken),
                "drained" => await HandleDrainedAsync(activationId, await ReadRequest<DrainedRequest>(
                    "heartbeat.collector/1", "activation.drained", "activation.drainRejected", activationId)),
                _ => Json(404, new { error = Error("protocol_invalid_message", "Unknown ExternalHost protocol operation.") })
            };
        }
        catch (JsonException exception)
        {
            var error = Error("protocol_invalid_message", exception.Message);
            return replyTo is { } requestId && rejectedType is not null
                ? ProtocolResponse(
                    400,
                    rejectedType,
                    responseActivationId,
                    requestId,
                    new { error },
                    protocol: responseProtocol)
                : Json(400, new { error });
        }
        catch (CollectorActivationException exception)
        {
            return replyTo is { } requestId && rejectedType is not null
                ? ProtocolResponse(
                    exception.Error.Retryable ? 503 : 409,
                    rejectedType,
                    responseActivationId,
                    requestId,
                    new { error = exception.Error },
                    protocol: responseProtocol)
                : Json(exception.Error.Retryable ? 503 : 409, new { error = exception.Error });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 交付围栏（Activation 已被停止或卸载）在握手各步上报的是 activation_stopping；
            // facts.publish 走的是另一条判定，必须给对端同一个稳定拒绝码，而不是让请求以未处理异常收场。
            var error = Error("activation_stopping", "ExternalHost Activation is stopping.");
            return replyTo is { } requestId && rejectedType is not null
                ? ProtocolResponse(
                    409,
                    rejectedType,
                    responseActivationId,
                    requestId,
                    new { error },
                    protocol: responseProtocol)
                : Json(409, new { error });
        }
    }

    public async ValueTask ExpireLeasesAsync()
    {
        var now = _timeProvider.GetUtcNow();
        Session[] expired;
        lock (_gate)
        {
            expired = _sessions.Values.Where(session => session.ExpiresAt <= now).ToArray();
            foreach (var session in expired)
            {
                _sessions.Remove(session.ActivationId);
            }
        }
        foreach (var session in expired)
        {
            if (session.Activation is null)
                await _runtime.AbandonExternalHostActivationAsync(
                    session.ActivationId,
                    ExternalHostActivationStopReason.LeaseExpired);
            else
                await _runtime.StopExternalHostActivationAsync(
                    session.Activation,
                    ExternalHostActivationStopReason.LeaseExpired);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _helloAttempts.Clear();
        }
        foreach (var session in sessions)
        {
            if (session.Activation is null)
                await _runtime.AbandonExternalHostActivationAsync(
                    session.ActivationId,
                    ExternalHostActivationStopReason.RuntimeStopping);
            else
                await _runtime.StopExternalHostActivationAsync(
                    session.Activation,
                    ExternalHostActivationStopReason.RuntimeStopping);
        }
        await ValueTask.CompletedTask;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask<ProtocolHttpResponse> HandleHelloAsync(ProtocolMessage<HelloRequest> message)
    {
        var request = message.Body;
        if (!TryResolveReference(request, out var reference, out var referenceError))
            return HelloRejected(message.MessageId, referenceError!, 400);
        // 精确引用解析：宿主不猜 Package，也不按产品名查找。装没装、内容对不对，只由 Installation 回答。
        if (!_installations.TryOpen(reference!, out var installation) || installation is null)
            return HelloRejected(
                message.MessageId,
                Error(
                    "package_not_installed",
                    "No verified Installation matches the declared Collector Package reference."),
                400);
        var package = installation.Package;
        var validationError = ValidateHello(request, package);
        if (validationError is not null)
            return HelloRejected(message.MessageId, validationError, 400);

        var requestHash = HelloRequestHash(request);
        lock (_gate)
        {
            if (_helloAttempts.TryGetValue(message.MessageId, out var attempt))
            {
                if (attempt.RequestHash != requestHash)
                    return HelloRejected(
                        message.MessageId,
                        Error(
                            "protocol_invalid_message",
                            "The same activation.hello messageId was reused with different content."),
                        400);
                if (attempt.ActivationId is { } replayId && _sessions.TryGetValue(replayId, out var replay))
                    return HelloResponse(replay, message.MessageId);
                return HelloRejected(
                    message.MessageId,
                    Error("activation_stopping", "The original ExternalHost Activation attempt has ended."),
                    409);
            }
        }

        // Subject 在这里才解析：机器身份尚未就绪只让这一条连接稍后重试，不会让宿主组合或启动失败
        // （ADR-048「宿主启动不依赖可选 Collector」）。
        SubjectReference subject;
        try
        {
            subject = _subject();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return HelloRejected(
                message.MessageId,
                new CollectorProtocolError(
                    "host_not_ready",
                    "The host machine identity is not established yet.",
                    true),
                503);
        }
        // Artifact 必须先过：被拒绝的 hello 不该留下任何持久状态，而 Instance 表达的是跨重启的用户意图。
        CollectorRuntime.RequireSelectedExternalHostArtifact(package, request.ArtifactId);

        // Instance 表达安装时创建的持久用户意图。External Host 只能接入现有默认 Instance，不能因为
        // 一次连接而创建或复活它。
        var instance = _runtime.FindInstance(
            package.Manifest.PackageId,
            subject,
            CollectorRuntime.DefaultInstanceKey);
        if (instance is null)
            return HelloRejected(
                message.MessageId,
                Error("instance_not_found", "The installed Collector Package has no enabled default Instance."),
                409);
        if (instance.PackageVersion != package.Manifest.Version ||
            instance.PackageContentHash != package.PackageContentHash)
            return HelloRejected(
                message.MessageId,
                Error("package_mismatch", "The declared Collector Package is not the default Instance's selected Package."),
                409);

        // 身份冲突必须在替换旧 owner 之前拒绝，否则一条无效 hello 会把健康连接踢下线。Runtime 再查
        // 持久 Stream，覆盖 Host 重启后内存 session 不存在的情况。
        Session[] replaced;
        lock (_gate)
        {
            replaced = _sessions.Values.Where(session =>
                session.CollectorInstanceId == instance.CollectorInstanceId &&
                string.Equals(
                    session.ExternalHostIdentity,
                    request.ExternalHostIdentity,
                    StringComparison.Ordinal)).ToArray();
            if (replaced.Any(session => !string.Equals(
                    session.AppIdentityKey,
                    request.AppIdentityKey,
                    StringComparison.Ordinal)))
                return HelloRejected(
                    message.MessageId,
                    Error(
                        "external_host_identity_conflict",
                        "External Host Identity is already bound to a different appIdentityKey."),
                    409);
        }
        _runtime.RequireExternalHostIdentityBinding(
            instance.CollectorInstanceId,
            request.ExternalHostIdentity,
            request.AppIdentityKey);

        // 同一个 identity 重连即替换：让旧 Activation 真正结束并交还 writer lease，新 Activation 才
        // 可能接管同一条持久 Stream。这里必须等待，否则两个 writer 会短暂重叠。
        foreach (var old in replaced)
            await StopAndRemoveAsync(old, ExternalHostActivationStopReason.LeaseReplaced);

        var initialization = _runtime.BeginExternalHostActivation(
            instance.CollectorInstanceId,
            package,
            request.ArtifactId,
            request.ArtifactHash,
            new ProtocolSupport(request.ProtocolMajors, request.SupportedCapabilities),
            request.ExternalHostIdentity,
            request.AppIdentityKey,
            message.MessageId);
        var session = new Session(
            initialization.ActivationId,
            message.MessageId,
            Guid.CreateVersion7(),
            instance.CollectorInstanceId,
            request.AppIdentityKey,
            request.ExternalHostIdentity,
            package,
            initialization,
            false,
            null,
            null,
            _timeProvider.GetUtcNow() + _options.LeaseDuration);
        lock (_gate)
        {
            _sessions.Add(session.ActivationId, session);
            _helloAttempts[message.MessageId] = new HelloAttempt(session.ActivationId, requestHash);
        }
        return HelloResponse(session, message.MessageId);
    }

    private ProtocolHttpResponse HandleInitialize(Guid activationId)
    {
        var session = GetSession(activationId);
        return ProtocolResponse(200, "activation.initialize", activationId, null, new
        {
            instance = new
            {
                collectorInstanceId = session.Initialization.Instance.CollectorInstanceId,
                subject = session.Initialization.Instance.Subject
            },
            spec = new
            {
                revision = session.Initialization.Spec.SpecRevision,
                config = new
                {
                    version = session.Initialization.Spec.ConfigVersion,
                    value = session.Initialization.Spec.Config
                }
            },
            limits = session.Initialization.Limits,
            hubTime = _timeProvider.GetUtcNow()
        }, session.InitializeMessageId);
    }

    private ProtocolHttpResponse HandleInitialized(
        Guid activationId,
        ProtocolMessage<InitializedRequest> message)
    {
        var session = GetSession(activationId);
        if (message.Protocol != "heartbeat.collector/1" || message.Type != "activation.initialized" ||
            !IsUuidV7(message.MessageId) || message.ActivationId != activationId ||
            message.ReplyTo != session.InitializeMessageId || message.Body is null ||
            message.Body.AppliedSpecRevision != session.Initialization.Spec.SpecRevision)
            return ProtocolResponse(
                409,
                "activation.initializeRejected",
                activationId,
                message.MessageId,
                new { error = Error("spec_revision_stale", "Collector did not apply the current SpecRevision.") });
        if (!TryReplaceSession(session, session with { Initialized = true }))
            return ProtocolResponse(
                409,
                "activation.initializeRejected",
                activationId,
                message.MessageId,
                new { error = Error("activation_stopping", "ExternalHost Activation has ended.") });
        return new ProtocolHttpResponse(204, string.Empty, false);
    }

    private async ValueTask<ProtocolHttpResponse> HandleStreamsAsync(
        Guid activationId,
        ProtocolMessage<StreamsOpenRequest> message)
    {
        var request = message.Body;
        var session = GetSession(activationId);
        if (!session.Initialized)
            return ProtocolResponse(
                409,
                "streams.rejected",
                activationId,
                message.MessageId,
                new { error = Error("protocol_invalid_message", "activation.initialized is required before streams.open.") });
        if (session.Activation is not null)
            return StreamsResponse(session, message.MessageId);
        if (request.Bindings is null ||
            request.Bindings.Any(binding => binding is null || binding.Dimensions is null))
            return Rejected(
                400,
                "streams.rejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "streams.open bindings are malformed."));
        // 身份 dimension 由 Runtime 从 activation.hello 派生，Collector 既不需要也不允许自己带。
        var bindings = request.Bindings.Select(binding => new OutputBinding(
            binding.BindingId,
            binding.OutputId,
            binding.Dimensions.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal))).ToArray();
        var activation = _runtime.OpenExternalHostStreams(
            activationId,
            request.SpecRevision,
            bindings);
        var opened = session with
        {
            Activation = activation,
            ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration
        };
        if (!TryReplaceSession(session, opened))
        {
            await _runtime.StopExternalHostActivationAsync(
                activation,
                ExternalHostActivationStopReason.LeaseExpired);
            return Rejected(
                409,
                "streams.rejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost Activation has ended."));
        }
        return StreamsResponse(opened, message.MessageId);
    }

    private async ValueTask<ProtocolHttpResponse> HandleReadyAsync(
        Guid activationId,
        ProtocolMessage<ReadyRequest> message)
    {
        var request = message.Body;
        var session = GetSession(activationId);
        if (session.Activation is null)
            return Rejected(
                400,
                "activation.readyRejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "streams.opened is required before activation.ready."));
        if (session.Activation.State == CollectorActivationState.Ready)
            return ReadyResponse(session, message.MessageId);
        var activation = await _runtime.MarkExternalHostReadyAsync(
            session.Activation,
            request.AppliedSpecRevision);
        var ready = session with
        {
            Activation = activation,
            LeaseToken = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
            ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration
        };
        if (!TryReplaceSession(session, ready))
        {
            await _runtime.StopExternalHostActivationAsync(
                activation,
                ExternalHostActivationStopReason.LeaseExpired);
            return Rejected(
                409,
                "activation.readyRejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost Activation has ended."));
        }
        RegisterPackageDeclaration(ready.Package);
        return ReadyResponse(ready, message.MessageId);
    }

    private ProtocolHttpResponse HandleRenew(Guid activationId, RenewRequest request)
    {
        _ = GetSession(activationId);
        if (!TryRenewLease(activationId, request.LeaseToken, out var renewed))
            return Json(409, new { error = Error("activation_stopping", "ExternalHost lease is not active.") });
        return Json(200, LeaseBody(renewed));
    }

    private async ValueTask<ProtocolHttpResponse> HandleFactsAsync(
        Guid activationId,
        ProtocolMessage<PublishRequest> message,
        CancellationToken cancellationToken)
    {
        var request = message.Body;
        if (!TryGetActiveLease(activationId, request.LeaseToken, out var session))
            return Rejected(
                409,
                "facts.rejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost lease is not active."));
        if (request.Facts is null || request.Facts.Count == 0)
            return Rejected(
                400,
                "facts.rejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "facts.publish must contain Facts."));
        var acknowledgement = await session.Activation!.PublishAsync(
            request.Facts[0].StreamId,
            message.MessageId,
            request.Facts,
            cancellationToken);
        if (acknowledgement.IsMessageRejected)
            return Rejected(400, "facts.rejected", activationId, message.MessageId, acknowledgement.MessageError!);
        return ProtocolResponse(
            200,
            "facts.ack",
            activationId,
            message.MessageId,
            new { results = acknowledgement.Results });
    }

    private async ValueTask<ProtocolHttpResponse> HandleGapAsync(
        Guid activationId,
        ProtocolMessage<GapRequest> message,
        CancellationToken cancellationToken)
    {
        var request = message.Body;
        if (!TryGetActiveLease(activationId, request.LeaseToken, out var session))
            return Rejected(
                409,
                "stream.gapRejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost lease is not active."));
        var outcome = await session.Activation!.ReportGapAsync(
            request.StreamId,
            message.MessageId,
            request.Gap,
            cancellationToken);
        return outcome.Status switch
        {
            GapDeliveryStatus.Rejected =>
                Rejected(400, "stream.gapRejected", activationId, message.MessageId, outcome.Error!),
            GapDeliveryStatus.Retry =>
                Rejected(503, "stream.gapRejected", activationId, message.MessageId, outcome.Error!),
            _ => ProtocolResponse(
                200,
                "stream.gapAck",
                activationId,
                message.MessageId,
                new { streamId = outcome.StreamId })
        };
    }

    private async ValueTask<ProtocolHttpResponse> HandleDrainedAsync(
        Guid activationId,
        ProtocolMessage<DrainedRequest> message)
    {
        var request = message.Body;
        if (!TryGetActiveLease(activationId, request.LeaseToken, out var session))
            return Rejected(
                409,
                "activation.drainRejected",
                activationId,
                message.MessageId,
                Error("activation_stopping", "ExternalHost lease is not active."));
        if (request.PendingFacts < 0 || request.PendingGaps < 0)
            return Rejected(
                400,
                "activation.drainRejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "Pending counts must not be negative."));
        if (!CollectorDrainVocabulary.TryParse(request.Reason, out var reason))
            return Rejected(
                400,
                "activation.drainRejected",
                activationId,
                message.MessageId,
                Error("protocol_invalid_message", "Drain reason is not supported."));
        var drainResult = new InProcessCollectorDrainResult(
            new InProcessCollectorLogicalDrainResult(
                request.PendingFacts,
                request.PendingGaps,
                reason,
                request.RemainderDurable));
        await StopAndRemoveAsync(
            session,
            ExternalHostActivationStopReason.CollectorDrained,
            drainResult);
        return new ProtocolHttpResponse(204, string.Empty, false);
    }

    private void RegisterPackageDeclaration(LocalCollectorPackage package)
    {
        if (package.ObservationDeclaration is not { } declaration)
            return;
        _declarations.StoreVerifiedPackageDeclaration(declaration.Source, declaration.Json, declaration.Version);
    }

    /// <summary>
    /// hello 必须自带精确 Package 引用。宿主不做「按 id 找最新版本」这类推断：连接方要说清自己正在
    /// 运行哪一份内容，宿主才可能判断它和本机 Installation 是不是同一份。
    /// </summary>
    private static bool TryResolveReference(
        HelloRequest request,
        out CollectorPackageReference? reference,
        out CollectorProtocolError? error)
    {
        reference = null;
        error = null;
        if (string.IsNullOrWhiteSpace(request.PackageId) ||
            string.IsNullOrWhiteSpace(request.PackageVersion) ||
            string.IsNullOrWhiteSpace(request.PackageContentHash) ||
            string.IsNullOrWhiteSpace(request.ArtifactId) ||
            string.IsNullOrWhiteSpace(request.ArtifactHash))
        {
            error = Error(
                "protocol_invalid_message",
                "activation.hello must declare packageId, packageVersion, packageContentHash, artifactId and artifactHash.");
            return false;
        }
        // 这条 route 上的引用整个来自对端，因此形状校验必须在宿主侧做完：Installation 的目录解析
        // 对畸形 hash 与路径穿越是抛 ArgumentException 的，不能让不可信输入走到那一步。
        if (!IsSha256Digest(request.PackageContentHash) || !IsSha256Digest(request.ArtifactHash))
        {
            error = Error(
                "protocol_invalid_message",
                "packageContentHash and artifactHash must be lowercase 'sha256:<64 hex>' digests.");
            return false;
        }
        if (HasPathSyntax(request.PackageId) || HasPathSyntax(request.PackageVersion))
        {
            error = Error(
                "protocol_invalid_message",
                "packageId and packageVersion must not contain path syntax.");
            return false;
        }
        reference = new CollectorPackageReference(
            request.PackageId,
            request.PackageVersion,
            request.PackageContentHash);
        return true;
    }

    private static bool IsSha256Digest(string value)
    {
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 64)
            return false;
        for (var index = prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            var isLowercaseHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowercaseHex)
                return false;
        }
        return true;
    }

    private static bool HasPathSyntax(string value) =>
        value.Contains('/', StringComparison.Ordinal) ||
        value.Contains('\\', StringComparison.Ordinal) ||
        value.Contains("..", StringComparison.Ordinal);

    private static CollectorProtocolError? ValidateHello(HelloRequest request, LocalCollectorPackage package)
    {
        if (request.ProtocolMajors is null || request.SupportedCapabilities is null)
            return Error(
                "protocol_invalid_message",
                "activation.hello must declare protocolMajors and supportedCapabilities.");
        foreach (var (field, value) in new[]
                 {
                     ("appIdentityKey", request.AppIdentityKey),
                     ("externalHostIdentity", request.ExternalHostIdentity)
                 })
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.Length > 200)
                return Error(
                    "protocol_invalid_message",
                    $"{field} must be a stable, trimmed, non-empty value of at most 200 characters.");
        }
        var artifact = package.Artifacts.SingleOrDefault(candidate => candidate.ArtifactId == request.ArtifactId);
        if (artifact is null || !string.Equals(artifact.ContentHash, request.ArtifactHash, StringComparison.Ordinal))
            return Error(
                "package_mismatch",
                "The declared ExternalHost Artifact does not match the verified Collector Package.");
        return null;
    }

    private static string HelloRequestHash(HelloRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("packageId", request.PackageId);
            writer.WriteString("packageVersion", request.PackageVersion);
            writer.WriteString("packageContentHash", request.PackageContentHash);
            writer.WriteString("artifactId", request.ArtifactId);
            writer.WriteString("artifactHash", request.ArtifactHash);
            writer.WritePropertyName("protocolMajors");
            writer.WriteStartArray();
            foreach (var major in request.ProtocolMajors.Order())
                writer.WriteNumberValue(major);
            writer.WriteEndArray();
            writer.WritePropertyName("supportedCapabilities");
            writer.WriteStartObject();
            foreach (var capability in request.SupportedCapabilities.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WritePropertyName(capability.Key);
                writer.WriteStartArray();
                foreach (var version in capability.Value.Order())
                    writer.WriteNumberValue(version);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteString("appIdentityKey", request.AppIdentityKey);
            writer.WriteString("externalHostIdentity", request.ExternalHostIdentity);
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(buffer.ToArray()));
    }

    private Session GetSession(Guid activationId)
    {
        lock (_gate)
            return _sessions.TryGetValue(activationId, out var session)
                ? session
                : throw ActivationFailure("activation_stopping", "ExternalHost Activation was not found.");
    }

    private bool TryGetActiveLease(Guid activationId, string? leaseToken, out Session session)
    {
        session = GetSession(activationId);
        return session.Activation is not null && FixedTimeEquals(session.LeaseToken, leaseToken);
    }

    private bool TryReplaceSession(Session expected, Session replacement)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(expected.ActivationId, out var current) ||
                !ReferenceEquals(current, expected))
                return false;
            _sessions[replacement.ActivationId] = replacement;
            return true;
        }
    }

    private bool TryRenewLease(Guid activationId, string? leaseToken, out Session renewed)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(activationId, out var current) ||
                current.Activation is null || current.ExpiresAt <= _timeProvider.GetUtcNow() ||
                !FixedTimeEquals(current.LeaseToken, leaseToken))
            {
                renewed = null!;
                return false;
            }
            renewed = current with { ExpiresAt = _timeProvider.GetUtcNow() + _options.LeaseDuration };
            _sessions[activationId] = renewed;
            return true;
        }
    }

    private async ValueTask StopAndRemoveAsync(
        Session session,
        ExternalHostActivationStopReason reason,
        InProcessCollectorDrainResult? drainResult = null)
    {
        lock (_gate)
        {
            _sessions.Remove(session.ActivationId);
        }
        if (session.Activation is null)
            await _runtime.AbandonExternalHostActivationAsync(session.ActivationId, reason);
        else
            await _runtime.StopExternalHostActivationAsync(session.Activation, reason, drainResult);
    }

    private ProtocolHttpResponse HelloResponse(Session session, Guid replyTo) => ProtocolResponse(
        200,
        "activation.accepted",
        null,
        replyTo,
        new
        {
            activationId = session.ActivationId,
            selectedProtocolMajor = 1,
            selectedCapabilities = session.Initialization.SelectedCapabilities
        },
        protocol: "heartbeat.collector.bootstrap/1");

    private static ProtocolHttpResponse HelloRejected(
        Guid replyTo,
        CollectorProtocolError error,
        int statusCode) =>
        ProtocolResponse(
            statusCode,
            "activation.rejected",
            null,
            replyTo,
            new { error },
            protocol: "heartbeat.collector.bootstrap/1");

    private ProtocolHttpResponse ReadyResponse(Session session, Guid replyTo) => ProtocolResponse(
        200,
        "activation.readyAck",
        session.ActivationId,
        replyTo,
        new
        {
            appliedSpecRevision = session.Initialization.Spec.SpecRevision,
            lease = LeaseBody(session)
        });

    private ProtocolHttpResponse StreamsResponse(Session session, Guid replyTo) => ProtocolResponse(
        200,
        "streams.opened",
        session.ActivationId,
        replyTo,
        new { streams = session.Activation!.Streams });

    private object LeaseBody(Session session) => new
    {
        token = session.LeaseToken,
        durationMs = (int)_options.LeaseDuration.TotalMilliseconds,
        expiresAt = session.ExpiresAt
    };

    private static bool TryParseSessionPath(string path, out Guid activationId, out string operation)
    {
        activationId = Guid.Empty;
        operation = string.Empty;
        if (path.Length <= RoutePrefix.Length + 1 || path[RoutePrefix.Length] != '/')
            return false;
        var tail = path[(RoutePrefix.Length + 1)..].Split('/');
        return tail.Length == 2 && Guid.TryParse(tail[0], out activationId) &&
               (operation = tail[1]).Length > 0;
    }

    private static async ValueTask<T> DeserializeAsync<T>(Stream body, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(body, JsonOptions, cancellationToken)
        ?? throw new JsonException("Protocol request body is required.");

    private static ProtocolHttpResponse Json(int statusCode, object body) =>
        new(statusCode, JsonSerializer.Serialize(body, JsonOptions));

    private static ProtocolHttpResponse ProtocolResponse(
        int statusCode,
        string type,
        Guid? activationId,
        Guid? replyTo,
        object body,
        Guid? messageId = null,
        string protocol = "heartbeat.collector/1") =>
        Json(statusCode, new
        {
            protocol,
            type,
            messageId = messageId ?? Guid.CreateVersion7(),
            activationId,
            replyTo,
            body
        });

    private static ProtocolHttpResponse Rejected(
        int statusCode,
        string type,
        Guid activationId,
        Guid replyTo,
        CollectorProtocolError error) =>
        ProtocolResponse(statusCode, type, activationId, replyTo, new { error });

    private static CollectorProtocolError Error(string code, string message) => new(code, message, false);

    private static CollectorActivationException ActivationFailure(string code, string message) =>
        new(new CollectorProtocolError(code, message, false));

    private static bool FixedTimeEquals(string? left, string? right) =>
        left is not null && right is not null &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return value != Guid.Empty && text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private sealed record Session(
        Guid ActivationId,
        Guid HelloMessageId,
        Guid InitializeMessageId,
        Guid CollectorInstanceId,
        string AppIdentityKey,
        string ExternalHostIdentity,
        LocalCollectorPackage Package,
        ExternalHostCollectorInitialization Initialization,
        bool Initialized,
        ExternalHostCollectorActivation? Activation,
        string? LeaseToken,
        DateTimeOffset ExpiresAt);

    private sealed record HelloAttempt(
        Guid? ActivationId,
        string RequestHash);

    public sealed record ProtocolMessage<T>(
        string Protocol,
        string Type,
        Guid MessageId,
        Guid? ActivationId,
        Guid? ReplyTo,
        T Body);

    public sealed record HelloRequest(
        string PackageId,
        string PackageVersion,
        string PackageContentHash,
        string ArtifactId,
        string ArtifactHash,
        IReadOnlyList<int> ProtocolMajors,
        IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedCapabilities,
        string AppIdentityKey,
        string ExternalHostIdentity);

    public sealed record BindingRequest(
        string BindingId,
        string OutputId,
        IReadOnlyDictionary<string, string> Dimensions);

    public sealed record InitializedRequest(long AppliedSpecRevision);

    public sealed record StreamsOpenRequest(
        long SpecRevision,
        IReadOnlyList<BindingRequest> Bindings);

    public sealed record ReadyRequest(long AppliedSpecRevision);

    public sealed record RenewRequest(string LeaseToken);

    public sealed record PublishRequest(
        string LeaseToken,
        IReadOnlyList<FactSubmission> Facts);

    public sealed record GapRequest(
        string LeaseToken,
        Guid StreamId,
        StreamGapReport Gap);

    public sealed record DrainedRequest(
        string LeaseToken,
        long AppliedSpecRevision,
        int PendingFacts,
        int PendingGaps,
        string Reason,
        bool RemainderDurable);
}
