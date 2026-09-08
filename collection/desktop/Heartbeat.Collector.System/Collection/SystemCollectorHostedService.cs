using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Configuration;
using Heartbeat.Collection.Hub.Segments;
using Microsoft.Extensions.Hosting;
using Heartbeat.Collection.Hub.Hosting;
using Heartbeat.Collection.Hub.Upload;

namespace Heartbeat.Collector.System.Collection;

public sealed record SystemCollectorBindingOptions(
    string DataDirectory,
    string? PackageDirectory = null);

/// <summary>
/// Owns the BuiltIn Package, stable Instance, Runtime, and one InProcess Activation for a desktop
/// Agent. Construction is side-effect free so composition tests never open production state.
/// </summary>
public sealed class SystemCollectorHostedService(
    SystemCollectorBindingOptions options,
    CollectorRuntime runtime,
    IDeviceIdentity deviceIdentity,
    SystemInProcessCollector collector,
    ICollectorDeclarationStore declarations) : IHostedService, IDisposable, IAsyncDisposable, IHostShutdownEvidence
{
    private InProcessCollectorActivation? _activation;
    public InProcessCollectorDrainResult? DrainResult => _activation?.DrainResult;

    public DeliveryRemainder ShutdownRemainder => DrainResult?.LogicalResult is
        { RemainderDurable: true, PendingFacts: { } facts, PendingGaps: { } gaps }
            ? new(facts + gaps, 0) : DeliveryRemainder.Unknown;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_activation is not null)
            throw new InvalidOperationException("The system Collector Binding is already started.");

        Directory.CreateDirectory(options.DataDirectory);
        var package = LocalCollectorPackage.Load(
            options.PackageDirectory ?? SystemCollectorPackage.Path);
        if (package.ObservationDeclaration is { } declaration)
            declarations.StoreVerifiedPackageDeclaration(
                declaration.Source,
                declaration.Json,
                declaration.Version);
        using var config = JsonDocument.Parse("{}");
        var subject = new SubjectReference(MachineSubjectId(deviceIdentity.HardwareId), SubjectKind.Machine);
        var instance = runtime.FindInstances(package.Manifest.PackageId, subject).FirstOrDefault()
            ?? runtime.CreateInstance(package, subject, new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
        _activation = await runtime.ActivateInProcessAsync(
            instance.CollectorInstanceId, package, collector, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        _activation?.StopAsync(cancellationToken).AsTask() ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    internal static Guid MachineSubjectId(string hardwareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        if (Guid.TryParse(hardwareId, out var parsed) && parsed != Guid.Empty)
            return parsed;
        throw new InvalidOperationException(
            "The desktop machine identity must be a non-empty UUID before the system Collector can activate.");
    }
}
