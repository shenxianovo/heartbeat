using System.Text.Json;
using Heartbeat.Verification;

namespace Heartbeat.Verification.Tests;

public sealed class VerificationArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heartbeat-artifact-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuppliedArtifactsSkipBuildingAndHashDependencies()
    {
        Directory.CreateDirectory(_root);
        var binary = Path.Combine(_root, "app");
        await File.WriteAllTextAsync(binary, "native apphost");
        var options = new VerificationOptions(_root, "unused", TimeSpan.FromSeconds(5), false, false)
        {
            Artifacts = new() { ["analytics"] = binary, ["headless"] = binary, ["reference"] = binary }
        };
        var report = new RunReport("test", _root, new SecretRedactor());
        var lane = new VerificationLane(options, report, _root, new SecretRedactor());
        await lane.BuildAsync(CancellationToken.None);
        Assert.Equal(binary, lane.Artifact("headless").Path);
        Assert.False(Directory.Exists(lane.WorkPath));
        var before = JsonSerializer.Serialize(report.Evidence["headlessArtifact"]);
        await File.WriteAllTextAsync(Path.Combine(_root, "dependency.dll"), "changed dependency");
        var after = JsonSerializer.Serialize(await lane.Artifact("headless").DescribeAsync(CancellationToken.None));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task DottedNativeEntrypointReportsTheManagedBuildVersion()
    {
        Directory.CreateDirectory(_root);
        var binary = Path.Combine(_root, "Example.Desktop.Mac");
        await File.WriteAllTextAsync(binary, "apphost");
        File.Copy(typeof(VerificationArtifact).Assembly.Location, binary + ".dll");
        var description = JsonSerializer.SerializeToElement(await VerificationArtifact.Open(binary)
            .DescribeAsync(CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(description.GetProperty("version").GetString()));
    }

    [Fact]
    public void BundleResolvesTheExistingExecutableWithoutCopyingOrRebuilding()
    {
        var bundle = Path.Combine(_root, "Example.app");
        var macos = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
            "<plist><dict><key>CFBundleExecutable</key><string>desktop</string></dict></plist>");
        File.WriteAllText(Path.Combine(macos, "desktop"), "apphost");
        var artifact = VerificationArtifact.Open(bundle);
        Assert.Equal(Path.Combine(macos, "desktop"), artifact.Executable);
        Assert.Equal(bundle, artifact.Directory);
        Assert.Empty(artifact.Arguments);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
