using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

namespace Heartbeat.Collection.Hub.Tests.Collectors.Protocol;

/// <summary>
/// 通用 ExternalHost loopback 绑定的验收。这里刻意不出现任何具体产品名：被测的是「一个自己出现的
/// Collector 如何接入」，宿主对它的全部认知只有 Package 引用、External Host Identity 与 AppIdentityKey。
/// </summary>
public sealed class ExternalHostCollectorProtocolHandlerTests
{
    private const string SecondHostIdentity = "external-host-b";

    [Fact]
    public async Task Discovery_AnnouncesGenericBindingWithoutNamingAnyProduct()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        var response = await fixture.GetAsync(ExternalHostCollectorProtocolHandler.RoutePrefix);

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("external-host", body.RootElement.GetProperty("binding").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("protocolMajors")[0].GetInt32());
    }

    [Fact]
    public async Task Hello_WithoutInstallation_IsRejectedAsPackageNotInstalled()
    {
        // 未安装的 Package 不因为「有人来连」就变成已安装：宿主只承认 Installation 这一个权威。
        await using var fixture = await HandlerFixture.CreateAsync(install: false);

        var response = await fixture.HelloAsync();

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("package_not_installed", ErrorCode(response));
    }

    [Fact]
    public async Task Hello_WithInstallationButNoDefaultInstance_IsRejectedWithoutCreatingUserIntent()
    {
        await using var fixture = await HandlerFixture.CreateAsync(createDefaultInstance: false);

        var response = await fixture.HelloAsync();

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("instance_not_found", ErrorCode(response));
        Assert.Empty(fixture.Runtime.ListInstances());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("contentHash")]
    [InlineData("artifactId")]
    [InlineData("artifactHash")]
    public async Task Hello_WithDriftedIdentity_IsRejectedAndNeverResolvesToAnotherPackage(string drifted)
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        var response = await fixture.HelloAsync(mutate: hello =>
        {
            // 漂移值本身是合法形状的，测的才是「解析不到别的 Package」，而不是形状校验。
            (string Field, string Value) drift = drifted switch
            {
                "version" => ("packageVersion", "9.9.9"),
                "contentHash" => ("packageContentHash", "sha256:" + new string('b', 64)),
                "artifactId" => ("artifactId", "some-other-artifact"),
                _ => ("artifactHash", "sha256:" + new string('c', 64))
            };
            hello[drift.Field] = drift.Value;
        });

        // 版本/内容漂移根本找不到 Installation；Artifact 漂移能找到 Package 但对不上内容。
        Assert.Contains(ErrorCode(response), new[] { "package_not_installed", "package_mismatch" });
        Assert.Equal(400, response.StatusCode);
        Assert.Single(fixture.Runtime.ListInstances());
    }

    [Theory]
    [InlineData("packageContentHash", "drifted")]
    [InlineData("packageContentHash", "sha256:../../../../etc/passwd")]
    [InlineData("artifactHash", "SHA256:AABB")]
    [InlineData("packageId", "../escape")]
    [InlineData("packageVersion", "1.0.0/../..")]
    public async Task Hello_WithMalformedReference_IsRejectedInsteadOfFaultingTheRequest(
        string field,
        string value)
    {
        // 这条 route 的引用完全来自对端。畸形 hash 与路径穿越必须在宿主侧变成稳定拒绝码，
        // 而不是让 Installation 的目录解析抛出未处理异常。
        await using var fixture = await HandlerFixture.CreateAsync();

        var response = await fixture.HelloAsync(mutate: hello => hello[field] = value);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("protocol_invalid_message", ErrorCode(response));
        Assert.Single(fixture.Runtime.ListInstances());
    }

    [Fact]
    public async Task Hello_AgainstANonExternalHostArtifact_IsRejected()
    {
        // 这条 route 只承载 externalHost Driver 的 Artifact。同一个 Package 里的别的 Driver
        // 不能靠「自己连上来」换取一个 ExternalHost Activation。
        await using var fixture = await HandlerFixture.CreateAsync(driver: "inProcess");

        var response = await fixture.HelloAsync();

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("package_mismatch", ErrorCode(response));
        Assert.Single(fixture.Runtime.ListInstances());
    }

    [Fact]
    public async Task Hello_UsesTheExistingRuntimeOwnedInstanceSharedByEveryExternalHost()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        var first = await fixture.ReadyAsync(HandlerFixture.DefaultHostIdentity, "app.one");
        var second = await fixture.ReadyAsync(SecondHostIdentity, "app.two");

        // 两个 External Host 共享同一个 Runtime-owned 默认 Instance，而不是各造一个。
        Assert.NotEqual(first.ActivationId, second.ActivationId);
        var instance = Assert.Single(fixture.Runtime.ListInstances());
        Assert.Equal(CollectorRuntime.DefaultInstanceKey, instance.InstanceKey);

        var status = fixture.Runtime.DescribeExternalHostInstance(instance.CollectorInstanceId);
        Assert.Equal(CollectorInstanceExternalHostState.Connected, status.State);
        Assert.Equal(2, status.ConnectedExternalHosts);
    }

    [Fact]
    public async Task Hello_UsesCapabilitiesDeclaredByTheExternalHostPackage()
    {
        await using var fixture = await HandlerFixture.CreateAsync(factKind: "event");

        var response = await fixture.HelloAsync();

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task TwoIdentities_WriteToSeparateStreamsTaggedWithTheirOwnAppIdentityKey()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        var first = await fixture.ReadyAsync(HandlerFixture.DefaultHostIdentity, "app.one");
        var second = await fixture.ReadyAsync(SecondHostIdentity, "app.two");

        Assert.NotEqual(first.StreamId, second.StreamId);
        await fixture.PublishAsync(first, "one|work");
        await fixture.PublishAsync(second, "two|play");

        // Stream 的 identifying dimension 是宿主注入的，因此事实归属不依赖 Collector 自报 payload。
        var segments = fixture.Sink.GetAndClearSegments();
        Assert.Equal(2, segments.Count);
        var streams = fixture.Runtime.DescribeExternalHostInstance(
            fixture.Runtime.ListInstances().Single().CollectorInstanceId);
        Assert.Equal(2, streams.ConnectedExternalHosts);
    }

    [Fact]
    public async Task Hello_CannotOverrideTheHostInjectedIdentityDimensions()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var session = await fixture.HelloUntilStreamsAsync();

        var response = await fixture.PostAsync(
            $"{session.ActivationId}/streams",
            new
            {
                protocol = "heartbeat.collector/1",
                type = "streams.open",
                messageId = Guid.CreateVersion7(),
                activationId = session.ActivationId,
                body = new
                {
                    specRevision = session.SpecRevision,
                    bindings = new[]
                    {
                        new
                        {
                            bindingId = "activity",
                            outputId = "activity",
                            dimensions = new Dictionary<string, string>
                            {
                                ["appIdentityKey"] = "app.spoofed"
                            }
                        }
                    }
                }
            });

        // 宿主注入的 identity dimension 不接受对端覆盖，哪怕值和真实身份一致。
        Assert.Equal(409, response.StatusCode);
        Assert.Equal("protocol_invalid_message", ErrorCode(response));
    }

    [Fact]
    public async Task SameIdentityReconnect_ReplacesItsOwnActivationAndReusesTheDurableStream()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var first = await fixture.ReadyAsync();
        var other = await fixture.ReadyAsync(SecondHostIdentity, "app.two");

        var replacement = await fixture.ReadyAsync();

        // 同 identity 重连只替换自己：旧 Activation 结束、Stream 复用，别人的连接不受影响。
        Assert.NotEqual(first.ActivationId, replacement.ActivationId);
        Assert.Equal(first.StreamId, replacement.StreamId);

        var instanceId = fixture.Runtime.ListInstances().Single().CollectorInstanceId;
        var status = fixture.Runtime.DescribeExternalHostInstance(instanceId);
        Assert.Equal(2, status.ConnectedExternalHosts);
        Assert.Equal(0, status.NegotiatingExternalHosts);

        // 旧连接已经不是 writer 了，它的写入必须被拒。
        var stale = await fixture.TryPublishAsync(first, "one|stale");
        Assert.Equal(409, stale.StatusCode);
        Assert.Empty(fixture.Sink.GetAndClearSegments());

        // 被替换的一方交还了 writer lease，接管者能立刻写入；旁人的连接不受影响。
        await fixture.PublishAsync(replacement, "one|after-replacement");
        await fixture.PublishAsync(other, "two|untouched");
        Assert.Equal(2, fixture.Sink.GetAndClearSegments().Count);
    }

    [Fact]
    public async Task ConcurrentReconnectOfTheSameIdentity_LeavesExactlyOneOwner()
    {
        // 同一身份并发重连不能两个都成活，也不能互相挤掉后一个不剩：结果必须是确定的单 owner。
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.ReadyAsync();

        var responses = await Task.WhenAll(fixture.HelloAsync(), fixture.HelloAsync());

        Assert.Contains(responses, response => response.StatusCode == 200);

        var instanceId = Assert.Single(fixture.Runtime.ListInstances()).CollectorInstanceId;
        var status = fixture.Runtime.DescribeExternalHostInstance(instanceId);
        Assert.Equal(1, status.ConnectedExternalHosts + status.NegotiatingExternalHosts);
    }

    [Fact]
    public async Task SameIdentity_CannotSilentlyRebindToADifferentAppIdentityKey()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var current = await fixture.ReadyAsync(HandlerFixture.DefaultHostIdentity, "app.one");

        var response = await fixture.HelloAsync(
            HandlerFixture.DefaultHostIdentity,
            appIdentityKey: "app.two");

        Assert.Equal("external_host_identity_conflict", ErrorCode(response));
        var status = fixture.Runtime.DescribeExternalHostInstance(
            Assert.Single(fixture.Runtime.ListInstances()).CollectorInstanceId);
        Assert.Equal("external_host_identity_conflict", status.Failure?.Code);
        Assert.Contains(HandlerFixture.DefaultHostIdentity, status.Failure?.Message);
        await fixture.PublishAsync(current, "still-owned");
        Assert.Single(fixture.Sink.GetAndClearSegments());
    }

    [Fact]
    public async Task IdentityToAppBinding_SurvivesHostRestartBecauseStreamsAreTheAuthority()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.ReadyAsync(HandlerFixture.DefaultHostIdentity, "app.one");

        await fixture.RestartHostAsync();

        // 重启后没有内存台账，绑定权威只剩持久 Stream 的 dimension——它必须仍然挡住换绑。
        var conflict = await fixture.HelloAsync(
            HandlerFixture.DefaultHostIdentity,
            appIdentityKey: "app.two");
        Assert.Equal("external_host_identity_conflict", ErrorCode(conflict));

        var reconnect = await fixture.ReadyAsync(HandlerFixture.DefaultHostIdentity, "app.one");
        Assert.NotNull(reconnect);
        var status = fixture.Runtime.DescribeExternalHostInstance(
            Assert.Single(fixture.Runtime.ListInstances()).CollectorInstanceId);
        Assert.Null(status.Failure);
    }

    [Fact]
    public async Task Uninstall_StopsEveryConnectedExternalHostThenRemovesInstanceAndInstallation()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var first = await fixture.ReadyAsync(HandlerFixture.DefaultHostIdentity, "app.one");
        var second = await fixture.ReadyAsync(SecondHostIdentity, "app.two");
        var instanceId = fixture.Runtime.ListInstances().Single().CollectorInstanceId;

        await fixture.Runtime.RemoveInstanceAsync(instanceId);
        fixture.Installations.Uninstall(fixture.Reference);

        Assert.Empty(fixture.Runtime.ListInstances());

        // Instance 没了，挂在它上面的 External Host 一个都不剩，两条旧连接也都写不进来了。
        Assert.Equal(409, (await fixture.TryPublishAsync(first, "one|after-uninstall")).StatusCode);
        Assert.Equal(409, (await fixture.TryPublishAsync(second, "two|after-uninstall")).StatusCode);
        Assert.Empty(fixture.Sink.GetAndClearSegments());

        // 卸载成功之后，旧连接重连拿到的是「没装」，不是被悄悄重建的 Instance。
        var response = await fixture.HelloAsync();
        Assert.Equal("package_not_installed", ErrorCode(response));
        Assert.Empty(fixture.Runtime.ListInstances());
    }

    [Fact]
    public async Task HelloRacingUninstall_NeverLeavesAnActivationBehindAfterRemoval()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        await fixture.ReadyAsync();
        var instanceId = fixture.Runtime.ListInstances().Single().CollectorInstanceId;

        // 卸载与新连接并发：无论谁先，终态都必须是「Instance 没了，且没有孤立 Activation」。
        var removal = fixture.Runtime.RemoveInstanceAsync(instanceId).AsTask();
        var racing = fixture.HelloAsync(SecondHostIdentity, "app.two");
        await Task.WhenAll(removal, racing);

        // 被删掉的那个 Instance 必须真的消失，并且不留下任何挂在它上面的 Activation。
        Assert.DoesNotContain(
            instanceId,
            fixture.Runtime.ListInstances().Select(instance => instance.CollectorInstanceId));
        var racingResponse = await racing;
        Assert.Equal(409, racingResponse.StatusCode);
        Assert.Contains(ErrorCode(racingResponse), new[] { "activation_stopping", "instance_not_found" });
        Assert.Empty(fixture.Runtime.ListInstances());
    }

    [Fact]
    public async Task ManagementReadModel_DoesNotReportWaitingForAnUnknownInstance()
    {
        await using var fixture = await HandlerFixture.CreateAsync();

        var exception = Assert.Throws<CollectorActivationException>(() =>
            fixture.Runtime.DescribeExternalHostInstance(Guid.CreateVersion7()));

        Assert.Equal("instance_not_found", exception.Error.Code);
    }

    [Fact]
    public async Task ManagementReadModel_ReportsWaitingForExternalHostInsteadOfFakingStarting()
    {
        await using var fixture = await HandlerFixture.CreateAsync();
        var instance = Assert.Single(fixture.Runtime.ListInstances());

        // 已安装、Instance 在、但没人连上来——这既不是 Starting，也不是 Failed。
        var waiting = fixture.Runtime.DescribeExternalHostInstance(instance.CollectorInstanceId);
        Assert.Equal(CollectorInstanceExternalHostState.WaitingForExternalHost, waiting.State);
        Assert.Equal(0, waiting.ConnectedExternalHosts);

        await fixture.ReadyAsync();
        Assert.Equal(
            CollectorInstanceExternalHostState.Connected,
            fixture.Runtime.DescribeExternalHostInstance(instance.CollectorInstanceId).State);
    }

    private static string ErrorCode(ProtocolHttpResponse response)
    {
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var direct))
            return direct.GetProperty("code").GetString()!;
        return root.GetProperty("body").GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed record ReadySession(Guid ActivationId, Guid StreamId, string LeaseToken, long SpecRevision);

    private sealed class RecordingDeclarationStore : ICollectorDeclarationStore
    {
        private readonly Dictionary<string, CollectorRegistration> _entries = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CollectorRegistration> Snapshot => _entries;

        public void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version) =>
            _entries[source] = new CollectorRegistration(true, null, declarationJson, version);
    }

    private sealed class HandlerFixture : IAsyncDisposable
    {
        public const string DefaultHostIdentity = "external-host-a";
        public const string DefaultAppIdentityKey = "app.one";

        private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
        private readonly ReferenceCollectorPackageCopy _packageCopy;

        private HandlerFixture(
            ReferenceCollectorPackageCopy packageCopy,
            bool install,
            bool createDefaultInstance)
        {
            _packageCopy = packageCopy;
            Package = LocalCollectorPackage.Load(packageCopy.Path);
            Installations = new CollectorPackageInstallations(
                Path.Combine(_directory.Path, "collector-packages"));
            Reference = new CollectorPackageReference(
                Package.Manifest.PackageId,
                Package.Manifest.Version,
                Package.PackageContentHash);
            if (install)
                Installations.Install(packageCopy.Path);
            Sink = new SegmentIngestService(new FixedClock());
            Runtime = OpenRuntime();
            if (install && createDefaultInstance)
            {
                var blueprint = Package.Manifest.DefaultInstance
                                ?? throw new InvalidOperationException("Fixture Package must declare defaultInstance.");
                Runtime.CreateInstance(
                    Package,
                    Subject,
                    new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()),
                    CollectorRuntime.DefaultInstanceKey);
            }
            Handler = NewHandler();
        }

        public CollectorRuntime Runtime { get; private set; }
        public ExternalHostCollectorProtocolHandler Handler { get; private set; }
        public CollectorPackageInstallations Installations { get; }
        public CollectorPackageReference Reference { get; }
        public LocalCollectorPackage Package { get; }
        public SegmentIngestService Sink { get; }
        public SubjectReference Subject { get; } =
            new(Guid.CreateVersion7(), SubjectKind.Machine);

        public static Task<HandlerFixture> CreateAsync(
            bool install = true,
            string driver = "externalHost",
            bool createDefaultInstance = true,
            string factKind = "segment")
        {
            var copy = ReferenceCollectorPackageCopy.Create(Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "ReferenceCollectorPackage"));
            var manifest = copy.ReadManifest();
            manifest["artifacts"]![0]!["selector"]!["driver"] = driver;
            if (factKind == "event")
            {
                var schemaPath = Path.Combine(copy.Path, "schemas", "reference-segment.schema.json");
                File.WriteAllText(schemaPath, """
                    {
                      "documentVersion": 1,
                      "schemaId": "heartbeat.input",
                      "schemaMajor": 1,
                      "schemaRevision": 1,
                      "factKind": "event",
                      "evolution": { "mode": "immutableEvent", "allowRetraction": false },
                      "payloadSchemaDialect": "https://json-schema.org/draft/2020-12/schema",
                      "payloadSchema": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["eventType", "codeSet", "code"],
                        "properties": {
                          "eventType": { "enum": ["keyDown", "mouseButton", "mouseScroll"] },
                          "codeSet": { "const": "heartbeat-key-position-v1" },
                          "code": { "type": "integer", "minimum": -32768, "maximum": 32767 }
                        }
                      }
                    }
                    """);
                var capabilities = manifest["supportedCapabilities"]!.AsObject();
                capabilities.Remove("facts.segment");
                capabilities["facts.event"] = new JsonArray(1);
                var output = manifest["outputs"]![0]!;
                output["factKind"] = "event";
                output["schema"]!["id"] = "heartbeat.input";
                copy.WriteManifest(manifest);
                copy.UpdateSchemaHash(schemaPath);
                manifest = copy.ReadManifest();
            }
            copy.WriteManifest(manifest);
            return Task.FromResult(new HandlerFixture(copy, install, createDefaultInstance));
        }

        private CollectorRuntime OpenRuntime() => CollectorRuntime.Open(
            Path.Combine(_directory.Path, "runtime.json"),
            Sink,
            inputEventSink: new AcceptingInputEventSink());

        private ExternalHostCollectorProtocolHandler NewHandler() => new(
            Runtime,
            new RecordingDeclarationStore(),
            Installations,
            () => Subject);

        /// <summary>重启宿主：进程内状态全部丢弃，只留下磁盘上的 Runtime state 与 Installation。</summary>
        public async Task RestartHostAsync()
        {
            await Handler.DisposeAsync();
            await Runtime.DisposeAsync();
            Runtime = OpenRuntime();
            Handler = NewHandler();
        }

        public async Task<ProtocolHttpResponse> GetAsync(string path)
        {
            using var body = new MemoryStream();
            var response = await Handler.HandleAsync("GET", path, body);
            Assert.NotNull(response);
            return response!;
        }

        public async Task<ProtocolHttpResponse> PostAsync(string suffix, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var response = await Handler.HandleAsync(
                "POST",
                $"{ExternalHostCollectorProtocolHandler.RoutePrefix}/{suffix}",
                body);
            Assert.NotNull(response);
            return response!;
        }

        public Task<ProtocolHttpResponse> HelloAsync(
            string externalHostIdentity = DefaultHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey,
            Action<Dictionary<string, object?>>? mutate = null)
        {
            var hello = new Dictionary<string, object?>
            {
                ["packageId"] = Package.Manifest.PackageId,
                ["packageVersion"] = Package.Manifest.Version,
                ["packageContentHash"] = Package.PackageContentHash,
                ["artifactId"] = Package.Artifacts.Single().ArtifactId,
                ["artifactHash"] = Package.Artifacts.Single().ContentHash,
                ["protocolMajors"] = new[] { 1 },
                ["supportedCapabilities"] = Package.Manifest.SupportedCapabilities.ToDictionary(
                    capability => capability.Key,
                    capability => capability.Value.ToArray(),
                    StringComparer.Ordinal),
                ["appIdentityKey"] = appIdentityKey,
                ["externalHostIdentity"] = externalHostIdentity
            };
            mutate?.Invoke(hello);
            return PostAsync("hello", new
            {
                protocol = "heartbeat.collector.bootstrap/1",
                type = "activation.hello",
                messageId = Guid.CreateVersion7(),
                body = hello
            });
        }

        public async Task<(Guid ActivationId, long SpecRevision)> HelloUntilStreamsAsync(
            string externalHostIdentity = DefaultHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var hello = await HelloAsync(externalHostIdentity, appIdentityKey);
            Assert.Equal(200, hello.StatusCode);
            using var document = JsonDocument.Parse(hello.Body);
            var activationId = Guid.Parse(document.RootElement
                .GetProperty("body").GetProperty("activationId").GetString()!);

            var initialize = await PostAsync($"{activationId}/initialize", new { });
            Assert.Equal(200, initialize.StatusCode);
            using var initializeBody = JsonDocument.Parse(initialize.Body);
            // initialized 必须 replyTo initialize 的 messageId：握手是有序对话，不是无状态请求。
            var initializeMessageId = Guid.Parse(
                initializeBody.RootElement.GetProperty("messageId").GetString()!);
            var specRevision = initializeBody.RootElement
                .GetProperty("body").GetProperty("spec").GetProperty("revision").GetInt64();

            var initialized = await PostAsync($"{activationId}/initialized", new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.initialized",
                messageId = Guid.CreateVersion7(),
                activationId,
                replyTo = initializeMessageId,
                body = new { appliedSpecRevision = specRevision }
            });
            // initialized 是纯确认，没有 body：204。
            Assert.Equal(204, initialized.StatusCode);
            return (activationId, specRevision);
        }

        public async Task<ReadySession> ReadyAsync(
            string externalHostIdentity = DefaultHostIdentity,
            string appIdentityKey = DefaultAppIdentityKey)
        {
            var (activationId, specRevision) = await HelloUntilStreamsAsync(
                externalHostIdentity,
                appIdentityKey);
            var streams = await PostAsync($"{activationId}/streams", new
            {
                protocol = "heartbeat.collector/1",
                type = "streams.open",
                messageId = Guid.CreateVersion7(),
                activationId,
                body = new
                {
                    specRevision,
                    bindings = new[]
                    {
                        new
                        {
                            bindingId = "activity",
                            outputId = "activity",
                            dimensions = new Dictionary<string, string>()
                        }
                    }
                }
            });
            Assert.Equal(200, streams.StatusCode);
            using var opened = JsonDocument.Parse(streams.Body);
            // streams 是 bindingId -> Stream 的映射，不是数组。
            var streamId = Guid.Parse(opened.RootElement
                .GetProperty("body").GetProperty("streams")
                .GetProperty("activity").GetProperty("streamId").GetString()!);

            var ready = await PostAsync($"{activationId}/ready", new
            {
                protocol = "heartbeat.collector/1",
                type = "activation.ready",
                messageId = Guid.CreateVersion7(),
                activationId,
                body = new { appliedSpecRevision = specRevision }
            });
            Assert.Equal(200, ready.StatusCode);
            using var readyBody = JsonDocument.Parse(ready.Body);
            var leaseToken = readyBody.RootElement
                .GetProperty("body").GetProperty("lease").GetProperty("token").GetString()!;
            return new ReadySession(activationId, streamId, leaseToken, specRevision);
        }

        public async Task PublishAsync(ReadySession session, string identityKey)
        {
            var response = await TryPublishAsync(session, identityKey);
            Assert.Equal(200, response.StatusCode);
        }

        public async Task<ProtocolHttpResponse> TryPublishAsync(ReadySession session, string identityKey)
        {
            var now = DateTimeOffset.UtcNow;
            return await PostAsync($"{session.ActivationId}/facts", new
            {
                protocol = "heartbeat.collector/1",
                type = "facts.publish",
                messageId = Guid.CreateVersion7(),
                activationId = session.ActivationId,
                body = new
                {
                    leaseToken = session.LeaseToken,
                    facts = new[]
                    {
                        new
                        {
                            streamId = session.StreamId,
                            schemaRevision = 1,
                            factId = Guid.CreateVersion7(),
                            revision = 1L,
                            recordState = "present",
                            time = new
                            {
                                start = now.AddMinutes(-1),
                                end = now,
                                isFinal = true
                            },
                            payload = new { identityKey, title = "Work" }
                        }
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Handler.DisposeAsync();
            await Runtime.DisposeAsync();
            _packageCopy.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class AcceptingInputEventSink : IInputEventFactSink
    {
        public bool TryAccept(
            Heartbeat.Core.DTOs.Input.InputEventItem item,
            bool isReplay,
            ICollectorProjectionCommitFence commitFence) => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"heartbeat-external-host-handler-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
