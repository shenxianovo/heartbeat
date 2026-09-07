using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Desktop.UI.Presentation;

namespace Heartbeat.Desktop.UI.Tests.Presentation;

public sealed class DesktopCollectorMarketplaceTests
{
    [Fact]
    public async Task Open_DoesNotRequireMachineIdentityUntilAnInstallNeedsItsBlueprint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-desktop-marketplace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var runtime = CollectorRuntime.Open(
                Path.Combine(root, "runtime.json"),
                new NullSegmentSink());
            var installations = new CollectorPackageInstallations(Path.Combine(root, "packages"));

            await using var marketplace = DesktopCollectorMarketplace.Open(
                "test",
                "identity-not-ready",
                installations,
                runtime,
                new Uri("https://registry.example.invalid/v1/"));

            Assert.NotNull(marketplace);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NullSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }
}
