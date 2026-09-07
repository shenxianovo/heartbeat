using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Time;

// Collector-owned cross-language fixture. Production hosts never reference this executable.
var package = LocalCollectorPackage.Load(args[0]);
var dataDirectory = args[1];
var installations = new CollectorPackageInstallations(Path.Combine(dataDirectory, "packages"));
installations.Install(package.PackageDirectory);
var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine);
var sink = new SegmentIngestService(new SystemClock());
var runtime = OpenRuntime();
var blueprint = package.Manifest.DefaultInstance
                ?? throw new InvalidOperationException("Package must declare defaultInstance.");
var instance = runtime.CreateInstance(package, subject,
    new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()),
    CollectorRuntime.DefaultInstanceKey);
var handler = OpenHandler();
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.MapGet("/test/status", () => Results.Json(new
{
    instances = runtime.ListInstances().Count,
    status = runtime.ListInstances().Count == 0 ? null : runtime.DescribeExternalHostInstance(instance.CollectorInstanceId),
    facts = sink.GetAndClearSegments()
}));
app.MapPost("/test/restart", async () =>
{
    await handler.DisposeAsync();
    await runtime.DisposeAsync();
    runtime = OpenRuntime();
    handler = OpenHandler();
    return Results.Ok();
});
app.MapPost("/test/remove", async () =>
{
    await runtime.RemoveInstanceAsync(instance.CollectorInstanceId);
    installations.Uninstall(new CollectorPackageReference(
        package.Manifest.PackageId, package.Manifest.Version, package.PackageContentHash));
    return Results.Ok();
});
app.MapFallback(async context =>
{
    var response = await handler.HandleAsync(context.Request.Method,
        context.Request.Path.Value, context.Request.Body, context.RequestAborted);
    context.Response.StatusCode = response?.StatusCode ?? 404;
    if (response is not null)
    {
        context.Response.ContentType = response.IsJson ? "application/json" : "text/plain";
        await context.Response.WriteAsync(response.Body);
    }
});
await app.StartAsync();
Console.WriteLine(app.Urls.Single());
// Parent closes stdin even if a test fails; do not leave an orphan host or occupied port.
await Console.In.ReadLineAsync();
await app.StopAsync();
await handler.DisposeAsync();
await runtime.DisposeAsync();

CollectorRuntime OpenRuntime() => CollectorRuntime.Open(Path.Combine(dataDirectory, "runtime.json"), sink);
ExternalHostCollectorProtocolHandler OpenHandler() => new(runtime, new Declarations(), installations, () => subject);

sealed class Declarations : ICollectorDeclarationStore
{
    public IReadOnlyDictionary<string, CollectorRegistration> Snapshot { get; } =
        new Dictionary<string, CollectorRegistration>();
    public void StoreVerifiedPackageDeclaration(string source, string declarationJson, int version) { }
}
