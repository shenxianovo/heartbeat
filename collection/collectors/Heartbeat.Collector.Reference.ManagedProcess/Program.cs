using System.Text.Json;
using Heartbeat.Collection.CollectorProtocol;
using Heartbeat.Collector.Reference.ManagedProcess;

if (args is ["--create-package", var packageDirectory])
{
    ReferencePackageBuilder.Create(packageDirectory);
    return;
}

var behavior = Environment.GetEnvironmentVariable("HEARTBEAT_REFERENCE_BEHAVIOR");
if (RawReferenceProtocolProbe.Handles(behavior))
{
    await RawReferenceProtocolProbe.RunAsync(behavior!, Console.In, Console.Out);
    return;
}

var capabilities = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
{
    ["facts.segment"] = [1],
    ["auth.interactive"] = [1],
    ["secrets.instance"] = [1],
    ["diagnostics.stream-gap"] = [1]
};
if (behavior == "extra_capability")
    capabilities["reference.unsupported"] = [1];
var requiredCapabilities = new HashSet<string>(StringComparer.Ordinal)
{
    "facts.segment",
    "diagnostics.stream-gap"
};
var definition = new CollectorClientDefinition(
    "reference.managed",
    capabilities,
    "account",
    [new CollectorOutputBinding(
        "activity",
        "activity",
        new Dictionary<string, string>(StringComparer.Ordinal))],
    RequiredCapabilities: requiredCapabilities);
await using var binding = StdioCollectorProtocolBinding.FromEnvironment(Console.In, Console.Out);
await using var client = new CollectorProtocolClient(definition, binding);
try
{
    await client.RunAsync(new ReferenceFactCollector(behavior, Console.Out));
}
catch (ReferenceExitAfterReadyException)
{
    // Deliberate successful process exit used by the Hub-side lifecycle fixture.
}
if (behavior == "corrupt_after_drained")
{
    // activation.drained already carried truthful durable evidence. Corrupting the stream afterwards
    // makes the Hub terminate this process once that evidence exists.
    await Console.Out.WriteLineAsync("[broken-after-drained");
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.InfiniteTimeSpan);
}

internal sealed class ReferenceFactCollector(string? behavior, TextWriter rawOutput)
    : ICollectorProtocolApplication
{
    public async ValueTask InitializeAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        if (behavior is "authorization_required" or "authorization_then_hang")
        {
            var response = await activation.ChallengeAsync(
                "credentials",
                "Reference account login",
                "Authorization is required.",
                [
                    new CollectorAuthorizationField("username", "Username", false, "text"),
                    new CollectorAuthorizationField("password", "Password", true, "password")
                ],
                cancellationToken);
            if (response.Values.GetValueOrDefault("username") != "collector-user" ||
                response.Values.GetValueOrDefault("password") != "collector-password")
                throw new InvalidOperationException("Reference authorization response was invalid.");
            await activation.CompleteAuthorizationAsync(response.InteractionId, cancellationToken);
        }
        if (behavior == "authorization_then_hang")
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (behavior == "secret_roundtrip")
        {
            await activation.WriteSecretAsync("session", "reference-secret-value", cancellationToken);
            var value = await activation.ReadSecretAsync("session", cancellationToken);
            if (value != "reference-secret-value")
                throw new InvalidOperationException("Reference Collector Secret round-trip failed.");
        }
    }

    public async ValueTask StartAsync(
        CollectorActivation activation,
        CancellationToken cancellationToken)
    {
        if (behavior == "exit_after_ready")
        {
            Environment.Exit(0);
            throw new ReferenceExitAfterReadyException();
        }
        if (behavior == "corrupt_after_ready")
        {
            await rawOutput.WriteLineAsync("[broken");
            await rawOutput.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        await activation.PublishAsync(new CollectorFact(
            "activity",
            1,
            Guid.Parse("0198d5eb-fc31-7d7b-8bf0-c2d009ec8999"),
            1,
            DateTimeOffset.Parse("2026-08-22T12:05:00Z"),
            CollectorFactRecordState.Present,
            new CollectorSegmentFactTime(
                DateTimeOffset.Parse("2026-08-22T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-22T12:05:00Z"),
                false),
            JsonSerializer.SerializeToElement(new
            {
                identityKey = "reference.account|online",
                title = "Reference account online"
            })), cancellationToken);
    }

    public async ValueTask StopAsync(
        CollectorDrainContext drain,
        CancellationToken cancellationToken)
    {
        if (behavior == "corrupt_on_drain")
        {
            await rawOutput.WriteLineAsync("{broken-drain");
            await rawOutput.FlushAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        if (behavior == "ignore_drain")
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

internal sealed class ReferenceExitAfterReadyException : Exception;

internal static class RawReferenceProtocolProbe
{
    private static readonly HashSet<string> Behaviors =
    [
        "startup_timeout",
        "malformed",
        "exit_before_hello",
        "invalid_capability_type",
        "uppercase_uuid",
        "unknown_hello_field",
        "wrong_ready_spec_revision"
    ];

    public static bool Handles(string? behavior) => behavior is not null && Behaviors.Contains(behavior);

    public static async Task RunAsync(string behavior, TextReader input, TextWriter output)
    {
        if (behavior == "startup_timeout")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        if (behavior == "exit_before_hello")
            return;
        if (behavior == "malformed")
        {
            await output.WriteLineAsync("{not-json");
            await output.FlushAsync();
            return;
        }

        var messageId = Guid.CreateVersion7();
        object messageIdValue = behavior == "uppercase_uuid"
            ? messageId.ToString("D").ToUpperInvariant()
            : messageId;
        object capabilities = behavior == "invalid_capability_type"
            ? new Dictionary<string, object> { ["facts.segment"] = 1 }
            : new Dictionary<string, int[]>
            {
                ["facts.segment"] = [1],
                ["diagnostics.stream-gap"] = [1]
            };
        var hello = JsonSerializer.SerializeToNode(new
        {
            protocol = "heartbeat.collector.bootstrap/1",
            type = "activation.hello",
            messageId = messageIdValue,
            body = new
            {
                collectorInstanceId = Environment.GetEnvironmentVariable("HEARTBEAT_COLLECTOR_INSTANCE_ID"),
                runtimeArtifact = new
                {
                    packageId = Environment.GetEnvironmentVariable("HEARTBEAT_COLLECTOR_PACKAGE_ID"),
                    packageVersion = Environment.GetEnvironmentVariable("HEARTBEAT_COLLECTOR_PACKAGE_VERSION"),
                    artifactId = Environment.GetEnvironmentVariable("HEARTBEAT_COLLECTOR_ARTIFACT_ID"),
                    artifactHash = Environment.GetEnvironmentVariable("HEARTBEAT_COLLECTOR_ARTIFACT_HASH")
                },
                protocolMajors = new[] { 1 },
                supportedCapabilities = capabilities
            }
        })!.AsObject();
        if (behavior == "unknown_hello_field")
            hello["body"]!.AsObject()["unexpected"] = true;
        await output.WriteLineAsync(hello.ToJsonString());
        await output.FlushAsync();

        if (behavior == "wrong_ready_spec_revision")
            await SendWrongReadyRevisionAsync(input, output);
    }

    private static async Task SendWrongReadyRevisionAsync(TextReader input, TextWriter output)
    {
        using var accepted = await ReadMessageAsync("activation.accepted");
        var activationId = accepted.RootElement.GetProperty("body").GetProperty("activationId").GetGuid();
        using var initialize = await ReadMessageAsync("activation.initialize");
        var specRevision = initialize.RootElement.GetProperty("body").GetProperty("spec")
            .GetProperty("revision").GetInt64();
        await SendAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.initialized",
            messageId = Guid.CreateVersion7(),
            activationId,
            replyTo = initialize.RootElement.GetProperty("messageId").GetGuid(),
            body = new { appliedSpecRevision = specRevision }
        });
        await SendAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "streams.open",
            messageId = Guid.CreateVersion7(),
            activationId,
            body = new
            {
                specRevision,
                bindings = new[] { new { bindingId = "activity", outputId = "activity", dimensions = new { } } }
            }
        });
        using var opened = await ReadMessageAsync("streams.opened");
        // The shared client rejects an incorrect revision itself, so this hostile fixture writes raw wire messages.
        await SendAsync(new
        {
            protocol = "heartbeat.collector/1",
            type = "activation.ready",
            messageId = Guid.CreateVersion7(),
            activationId,
            body = new { appliedSpecRevision = specRevision + 1 }
        });
        await Task.Delay(Timeout.InfiniteTimeSpan);

        async Task<JsonDocument> ReadMessageAsync(string expectedType)
        {
            var document = JsonDocument.Parse(await input.ReadLineAsync() ?? throw new EndOfStreamException());
            if (document.RootElement.GetProperty("type").GetString() == expectedType)
                return document;
            document.Dispose();
            throw new InvalidDataException($"Expected {expectedType} in the reference handshake.");
        }

        async Task SendAsync(object message)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(message));
            await output.FlushAsync();
        }
    }
}
