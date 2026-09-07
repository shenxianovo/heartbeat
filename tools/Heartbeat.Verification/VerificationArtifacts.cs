using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Heartbeat.Verification;

internal sealed record VerificationArtifact(string Path, string Directory, string Executable, string[] Arguments)
{
    public static VerificationArtifact Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetDirectoryName(path)!;
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(path))
        {
            root = path;
            var plist = XDocument.Load(System.IO.Path.Combine(path, "Contents", "Info.plist"));
            var executable = plist.Descendants("key").Single(element => element.Value == "CFBundleExecutable")
                .ElementsAfterSelf().First().Value;
            if (System.IO.Path.GetFileName(executable) != executable)
                throw new ArgumentException("Invalid bundle executable.");
            path = System.IO.Path.Combine(path, "Contents", "MacOS", executable);
        }
        if (!File.Exists(path)) throw new DependencyBlockedException($"Artifact does not exist: {path}");
        return new(path, root, path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path,
            path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? [path] : []);
    }

    public async Task<object> DescribeAsync(CancellationToken token)
    {
        // Hash the supplied tree, not just the native apphost (which is often identical between builds).
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => System.IO.Path.GetRelativePath(Directory, path), StringComparer.Ordinal))
        {
            digest.AppendData(Encoding.UTF8.GetBytes(System.IO.Path.GetRelativePath(Directory, file) + "\0"));
            await using var stream = File.OpenRead(file);
            digest.AppendData(await SHA256.HashDataAsync(stream, token));
            count++;
        }
        var assembly = Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Path
            : Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.ChangeExtension(Path, ".dll")
            : Path + ".dll";
        var version = FileVersionInfo.GetVersionInfo(File.Exists(assembly) ? assembly : Path).ProductVersion;
        return new { path = Path, version, files = count, treeHash = "sha256:" + Convert.ToHexStringLower(digest.GetHashAndReset()) };
    }
}
