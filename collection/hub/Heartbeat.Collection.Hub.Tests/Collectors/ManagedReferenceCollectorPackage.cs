using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Collection.Hub.Tests.Collectors;

internal sealed class ManagedReferenceCollectorPackage : IDisposable
{
    private ManagedReferenceCollectorPackage(string path) => Path = path;

    public string Path { get; }

    public static ManagedReferenceCollectorPackage Create(
        string version = "1.0.0",
        IReadOnlyList<int>? acceptedConfigVersions = null,
        int configVersion = 1)
    {
        acceptedConfigVersions ??= [1];
        var source = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "ManagedReferenceCollector");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Managed reference Collector build output was not copied: {source}");

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"heartbeat-managed-reference-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, System.IO.Path.Combine(path, System.IO.Path.GetFileName(file)));

        var executableName = OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
            : "Heartbeat.Collector.Reference.ManagedProcess";
        var executablePath = System.IO.Path.Combine(path, executableName);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var schemaDirectory = System.IO.Path.Combine(path, "schemas");
        Directory.CreateDirectory(schemaDirectory);
        var schemaPath = System.IO.Path.Combine(schemaDirectory, "reference-segment.schema.json");
        File.Copy(
            System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "ReferenceCollectorPackage",
                "schemas",
                "reference-segment.schema.json"),
            schemaPath,
            overwrite: true);

        var manifest = new
        {
            manifestVersion = 1,
            packageId = "heartbeat.collector.reference-managed",
            version,
            protocolMajors = new[] { 1 },
            supportedCapabilities = new Dictionary<string, int[]>
            {
                ["facts.segment"] = [1],
                ["auth.interactive"] = [1],
                ["secrets.instance"] = [1],
                ["diagnostics.stream-gap"] = [1]
            },
            config = new
            {
                version = configVersion,
                accepts = acceptedConfigVersions
            },
            presentation = new
            {
                displayName = "Reference Managed Collector",
                summary = "A generic ManagedProcess Collector used for host lifecycle tests."
            },
            defaultInstance = new
            {
                subjectKind = "account",
                configVersion,
                config = new { }
            },
            outputs = new[]
            {
                new
                {
                    outputId = "activity",
                    source = "reference.account",
                    factKind = "segment",
                    schema = new
                    {
                        id = "heartbeat.reference.segment",
                        major = 1,
                        revision = 1,
                        document = "schemas/reference-segment.schema.json",
                        hash = Hash(File.ReadAllBytes(schemaPath))
                    },
                    subjectKinds = new[] { "account" },
                    dimensionKeys = Array.Empty<string>()
                }
            },
            artifacts = new[]
            {
                new
                {
                    artifactId = "reference.managed",
                    selector = new
                    {
                        driver = "managedProcess",
                        os = new[] { CurrentOperatingSystem() },
                        arch = new[] { CurrentArchitecture() }
                    },
                    entrypoint = executableName,
                    size = new FileInfo(executablePath).Length,
                    contentHash = Hash(File.ReadAllBytes(executablePath))
                }
            }
        };
        File.WriteAllText(
            System.IO.Path.Combine(path, "collector-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }),
            new UTF8Encoding(false));
        return new ManagedReferenceCollectorPackage(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }

    private static string Hash(byte[] content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

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
