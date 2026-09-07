using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Headless;

public sealed record HeadlessCollectorStatusResponse(
    string PackageId,
    string DisplayName,
    string Summary,
    string? LatestVersion,
    bool IsInstalled,
    string? InstalledVersion,
    Guid? CollectorInstanceId,
    string Phase,
    CollectorAuthorizationChallenge? Authorization,
    HeadlessCurrentSubjectActivity? CurrentActivity,
    string? StatusDetail = null);

/// <summary>Projects shared Marketplace status with Headless per-Instance activity.</summary>
internal sealed class HeadlessCollectorReadModel(
    ICollectorMarketplace marketplace,
    HeadlessInstancePipelines pipelines)
{
    public async ValueTask<IReadOnlyList<HeadlessCollectorStatusResponse>> BrowseAsync(
        CancellationToken cancellationToken = default) =>
        (await marketplace.BrowseAsync(cancellationToken)).Select(ToResponse).ToArray();

    private HeadlessCollectorStatusResponse ToResponse(CollectorMarketplaceRuntimeItem item)
    {
        var installed = item.InstalledVersion is not null && item.CollectorInstanceId is not null;
        return new HeadlessCollectorStatusResponse(
            item.PackageId,
            item.DisplayName,
            item.Summary,
            item.LatestVersion,
            installed,
            item.InstalledVersion,
            item.CollectorInstanceId,
            item.Phase switch
            {
                CollectorMarketplaceRuntimePhase.Running => nameof(CollectorRuntimePhase.Ready),
                _ => item.Phase.ToString()
            },
            item.Authorization,
            installed && item.Phase is not CollectorMarketplaceRuntimePhase.Failed
                ? CurrentActivity(item.CollectorInstanceId!.Value)
                : null,
            DescribeFailure(item.Failure));
    }

    private HeadlessCurrentSubjectActivity? CurrentActivity(Guid collectorInstanceId)
    {
        try { return pipelines.CurrentActivity(collectorInstanceId); }
        catch (KeyNotFoundException) { return null; }
    }

    private static string? DescribeFailure(CollectorRuntimeFailure? failure) =>
        failure is null
            ? null
            : failure.ProcessExitCode is { } exitCode
                ? $"{failure.Code}: {failure.Message} (exit code {exitCode})"
                : $"{failure.Code}: {failure.Message}";

}
