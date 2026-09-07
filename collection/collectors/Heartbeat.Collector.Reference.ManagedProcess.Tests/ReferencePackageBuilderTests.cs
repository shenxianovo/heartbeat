using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Heartbeat.Collector.Reference.ManagedProcess.Tests;

public sealed class ReferencePackageBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-reference-builder-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("account")]
    [InlineData("machine")]
    public void Create_ProducesConsistentManifestAndClientSubject(string subjectKind)
    {
        var source = Path.Combine(_root, "source");
        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(source);
        var executableName = OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
            : "Heartbeat.Collector.Reference.ManagedProcess";
        File.WriteAllText(Path.Combine(source, executableName), "reference executable");
        File.WriteAllText(Path.Combine(source, "Heartbeat.Collector.Reference.ManagedProcess.dll"), "reference assembly");
        var contractDirectory = Path.Combine(source, "contracts", "facts");
        Directory.CreateDirectory(contractDirectory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "contracts", "facts", "reference-segment.schema.json"),
            Path.Combine(contractDirectory, "reference-segment.schema.json"));

        ReferencePackageBuilder.Create(source, package, subjectKind);

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "collector-manifest.json")))!;
        Assert.Equal("managedProcess", manifest["artifacts"]![0]!["selector"]!["driver"]!.GetValue<string>());
        Assert.Equal(CurrentOperatingSystem(), manifest["artifacts"]![0]!["selector"]!["os"]![0]!.GetValue<string>());
        Assert.Equal(CurrentArchitecture(), manifest["artifacts"]![0]!["selector"]!["arch"]![0]!.GetValue<string>());
        Assert.Equal(subjectKind, manifest["outputs"]![0]!["subjectKinds"]![0]!.GetValue<string>());
        Assert.Equal(subjectKind, File.ReadAllText(Path.Combine(package, "reference-subject-kind.txt")));
        Assert.Equal(subjectKind, manifest["defaultInstance"]!["subjectKind"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(package, "schemas", "reference-segment.schema.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string CurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        throw new PlatformNotSupportedException();

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException()
    };
}
