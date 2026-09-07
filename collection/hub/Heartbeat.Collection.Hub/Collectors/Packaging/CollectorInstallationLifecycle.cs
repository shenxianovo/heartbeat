using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Packages;

public sealed record InstalledCollectorPackage(
    CollectorPackageInstallation Installation,
    CollectorInstance Instance)
{
    public LocalCollectorPackage Package => Installation.Package;
    public CollectorPackagePresentation Presentation =>
        Package.Manifest.Presentation
        ?? throw new PackageValidationException("Marketplace Package does not declare presentation.");
}

public sealed record CollectorInstallationInspection(
    CollectorInstance Instance,
    CollectorPackageInstallation? Installation,
    string? Failure)
{
    public bool IsValid => Installation is not null && Failure is null;
}

/// <summary>
/// Owns the generic Marketplace transition from a Catalog choice to an Installation and one
/// Runtime-owned default Instance. Callers do not parse blueprints or coordinate rollback.
/// </summary>
public sealed class CollectorInstallationLifecycle(
    ICollectorPackageMarketplace marketplace,
    CollectorPackageInstallations installations,
    CollectorRuntime runtime,
    Func<SubjectKind, SubjectReference> createSubject) : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);

    public IReadOnlyList<InstalledCollectorPackage> ListInstalled() =>
        InspectInstalled()
            .Where(item => item.IsValid)
            .Select(item => new InstalledCollectorPackage(item.Installation!, item.Instance))
            .OrderBy(item => item.Presentation.DisplayName, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<CollectorInstallationInspection> InspectInstalled() =>
        runtime.ListInstances()
            .Where(instance => instance.InstanceKey == CollectorRuntime.DefaultInstanceKey)
            .Select(Inspect)
            .OrderBy(item => item.Instance.PackageId, StringComparer.Ordinal)
            .ToArray();

    private CollectorInstallationInspection Inspect(CollectorInstance instance)
    {
        try
        {
            return new CollectorInstallationInspection(
                instance,
                installations.Open(ReferenceOf(instance)),
                null);
        }
        catch (Exception exception) when (exception is
                   PackageValidationException or IOException or UnauthorizedAccessException)
        {
            return new CollectorInstallationInspection(instance, null, exception.Message);
        }
    }

    public async ValueTask<InstalledCollectorPackage> InstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        return await InstallAsync(packageId, beginCommit: null, cancellationToken);
    }

    internal async ValueTask<InstalledCollectorPackage> InstallAsync(
        string packageId,
        Action? beginCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var existing = runtime.ListInstances()
                .Where(instance =>
                    instance.PackageId == packageId &&
                    instance.InstanceKey == CollectorRuntime.DefaultInstanceKey)
                .ToArray();
            if (existing.Length > 1)
                throw new CollectorRuntimeStateException(
                    $"Collector Package '{packageId}' has more than one default Instance.");
            if (existing.Length == 1)
            {
                beginCommit?.Invoke();
                return new InstalledCollectorPackage(
                    installations.Open(ReferenceOf(existing[0])),
                    existing[0]);
            }

            CollectorPackageInstallation? installation = null;
            CollectorInstance? instance = null;
            try
            {
                installation = await marketplace.InstallLatestAsync(packageId, cancellationToken);
                var package = installation.Package;
                _ = package.Manifest.Presentation
                    ?? throw new PackageValidationException("Marketplace Package does not declare presentation.");
                var blueprint = package.Manifest.DefaultInstance
                                ?? throw new PackageValidationException(
                                    "Marketplace Package does not declare defaultInstance.");
                beginCommit?.Invoke();
                var subject = createSubject(ParseSubjectKind(blueprint.SubjectKind));
                instance = runtime.CreateInstance(
                    package,
                    subject,
                    new CollectorInstanceSpec(1, blueprint.ConfigVersion, blueprint.Config.Clone()),
                    CollectorRuntime.DefaultInstanceKey);
                return new InstalledCollectorPackage(installation, instance);
            }
            catch
            {
                if (instance is not null)
                    await runtime.RemoveInstanceAsync(instance.CollectorInstanceId, CancellationToken.None);
                if (installation is not null && runtime.ListInstances().All(candidate =>
                        candidate.PackageId != installation.Reference.PackageId ||
                        candidate.PackageVersion != installation.Reference.Version ||
                        candidate.PackageContentHash != installation.Reference.PackageContentHash))
                    installations.Uninstall(installation.Reference);
                throw;
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask UninstallAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        await UninstallCoreAsync(packageId, null, cancellationToken);
    }

    public async ValueTask UninstallAsync(
        string packageId,
        Func<CollectorInstance, CancellationToken, ValueTask> afterActivationsStopped,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(afterActivationsStopped);
        await UninstallCoreAsync(packageId, afterActivationsStopped, cancellationToken);
    }

    private async ValueTask UninstallCoreAsync(
        string packageId,
        Func<CollectorInstance, CancellationToken, ValueTask>? afterActivationsStopped,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var installed = runtime.ListInstances()
                .Where(instance =>
                    instance.PackageId == packageId &&
                    instance.InstanceKey == CollectorRuntime.DefaultInstanceKey)
                .ToArray();
            if (installed.Length == 0)
                return;
            if (installed.Length > 1)
                throw new CollectorRuntimeStateException(
                    $"Collector Package '{packageId}' has more than one default Instance.");

            var instance = installed[0];
            var reference = ReferenceOf(instance);
            await runtime.RemoveInstanceAsync(
                instance.CollectorInstanceId,
                async commitToken =>
                {
                    if (afterActivationsStopped is not null)
                        await afterActivationsStopped(instance, commitToken);
                    // Delete the recoverable copy before the durable Runtime commit. If that later
                    // commit fails, the Instance remains visible as invalid and retry can finish.
                    if (runtime.ListInstances().All(candidate =>
                            candidate.CollectorInstanceId == instance.CollectorInstanceId ||
                            candidate.PackageId != reference.PackageId ||
                            candidate.PackageVersion != reference.Version ||
                            candidate.PackageContentHash != reference.PackageContentHash))
                        installations.Uninstall(reference);
                },
                cancellationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    private static CollectorPackageReference ReferenceOf(CollectorInstance instance) => new(
        instance.PackageId,
        instance.PackageVersion,
        instance.PackageContentHash);

    private static SubjectKind ParseSubjectKind(string value) => value switch
    {
        "machine" => SubjectKind.Machine,
        "account" => SubjectKind.Account,
        "person" => SubjectKind.Person,
        _ => throw new PackageValidationException($"Unknown default Instance subject kind '{value}'.")
    };

    public void Dispose() => _operations.Dispose();
}
