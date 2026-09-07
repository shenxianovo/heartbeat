using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heartbeat.Collector.Reference.ManagedProcess;

internal static class ReferencePackageBuilder
{
    public static void Create(string packageDirectory) =>
        Create(AppContext.BaseDirectory, packageDirectory);

    internal static void Create(string sourceDirectory, string packageDirectory, string subjectKind = "account")
    {
        if (subjectKind is not ("account" or "machine")) throw new ArgumentException("Unsupported Reference Subject kind.");
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var root = Path.GetFullPath(packageDirectory);
        Directory.CreateDirectory(root);
        foreach (var source in Directory.EnumerateFiles(sourceRoot))
        {
            var name = Path.GetFileName(source);
            if (name is "collector-manifest.json")
                continue;
            File.Copy(source, Path.Combine(root, name), overwrite: true);
        }

        var executableName = OperatingSystem.IsWindows()
            ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
            : "Heartbeat.Collector.Reference.ManagedProcess";
        var executablePath = Path.Combine(root, executableName);
        if (!File.Exists(executablePath))
            throw new InvalidOperationException(
                "Run --create-package through the built apphost executable, not `dotnet <dll>`. ");

        File.WriteAllText(Path.Combine(root, "reference-subject-kind.txt"), subjectKind);

        var schemaDirectory = Path.Combine(root, "schemas");
        Directory.CreateDirectory(schemaDirectory);
        var schemaPath = Path.Combine(schemaDirectory, "reference-segment.schema.json");
        File.Copy(
            Path.Combine(sourceRoot, "contracts", "facts", "reference-segment.schema.json"),
            schemaPath,
            overwrite: true);
        var manifest = new
        {
            manifestVersion = 1,
            packageId = "heartbeat.collector.reference-managed",
            version = "1.0.0",
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
                version = 1,
                accepts = new[] { 1 }
            },
            presentation = new
            {
                displayName = "Reference Managed Collector",
                summary = "A generic ManagedProcess Collector used for host lifecycle tests."
            },
            defaultInstance = new
            {
                subjectKind,
                configVersion = 1,
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
                    subjectKinds = new[] { subjectKind },
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
            Path.Combine(root, "collector-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }),
            new UTF8Encoding(false));
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
