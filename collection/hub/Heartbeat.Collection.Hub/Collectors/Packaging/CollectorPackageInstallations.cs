using System.Security.Cryptography;
using System.Text;

namespace Heartbeat.Collection.Hub.Collectors.Packages;

/// <summary>
/// 一个精确 Collector Package 在本机被完整持有的事实：稳定的 Installation 目录、它的精确引用，
/// 以及可直接使用的已安装 Package。
/// </summary>
public sealed record CollectorPackageInstallation(
    CollectorPackageReference Reference,
    string Directory,
    string TreeContentHash,
    LocalCollectorPackage Package);

/// <summary>
/// 精确 Collector Package 候选：声明版本与 content hash 共同确定内容，与是否已安装无关。
/// </summary>
public sealed record CollectorPackageReference(string PackageId, string Version, string PackageContentHash);

/// <summary>
/// 本机 Collector Installation 区域。Desktop Agent 与 Headless Hub 共用它把一个本地 Package 目录
/// 安装成稳定 Installation，并按精确引用重新打开。
///
/// 目录布局、staging 复制、tree hash 校验与失败清理都由本类独占：调用方只看到「精确引用 →
/// 已安装 Package」。Installation 事实的唯一权威是文件系统本身——目录只在内容通过校验后才被
/// rename 到最终路径，因此不存在额外的安装状态账本需要与之对账。Collector 专属校验（例如某个
/// ExternalHost Collector 的 sideload descriptor）属于该 Collector 自己的 adapter，本类只认
/// Collector Package 通用契约。
/// </summary>
public sealed class CollectorPackageInstallations
{
    private const string StagingPrefix = ".staging-";
    private readonly object _gate = new();

    public CollectorPackageInstallations(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        Root = Path.GetFullPath(installRoot);
    }

    /// <summary>Installation 区域根目录。同一 Host 数据目录下只应有一个。</summary>
    public string Root { get; }

    /// <summary>
    /// 从一个本地 Package 目录安装精确 Collector Package，并返回已安装副本。来源目录只被读取，
    /// 安装完成后不再参与运行。已经安装过同一个精确 Package 时不重建目录。
    /// </summary>
    /// <exception cref="PackageValidationException">
    /// 来源不是有效 Collector Package、复制后内容不一致，或同一精确引用下已存在不匹配的内容。
    /// </exception>
    public CollectorPackageInstallation Install(string sourcePackageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackageDirectory);
        var source = LocalCollectorPackage.Load(sourcePackageDirectory);
        var reference = ReferenceOf(source);
        var treeHash = ComputeTreeHash(source.PackageDirectory);
        var installDirectory = DirectoryFor(reference);

        lock (_gate)
        {
            if (!Directory.Exists(installDirectory))
                CopyIntoPlace(source.PackageDirectory, installDirectory, reference, treeHash);
            return OpenVerified(reference, installDirectory, treeHash);
        }
    }

    /// <summary>按精确引用打开已安装 Package。</summary>
    /// <exception cref="PackageValidationException">没有该 Installation，或其内容与引用不符。</exception>
    public CollectorPackageInstallation Open(CollectorPackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var installDirectory = DirectoryFor(reference);
        lock (_gate)
        {
            if (!Directory.Exists(installDirectory))
                throw new PackageValidationException(
                    $"Collector Package {reference.PackageId} {reference.Version} " +
                    $"({reference.PackageContentHash}) is not installed.");
            return OpenVerified(reference, installDirectory, expectedTreeHash: null);
        }
    }

    /// <summary>按精确引用打开已安装 Package；不存在或内容不符时返回 false。</summary>
    public bool TryOpen(
        CollectorPackageReference reference,
        out CollectorPackageInstallation? installation)
    {
        ArgumentNullException.ThrowIfNull(reference);
        try
        {
            installation = Open(reference);
            return true;
        }
        catch (PackageValidationException)
        {
            installation = null;
            return false;
        }
    }

    /// <summary>
    /// 列出当前有效的 Installation。未完成的 staging 目录、损坏内容与与自身引用不符的目录都被跳过，
    /// 不会作为 Installation 出现。
    /// </summary>
    public IReadOnlyList<CollectorPackageInstallation> List(string? packageId = null)
    {
        if (packageId is not null && !IsPackageId(packageId))
            throw new ArgumentException("Collector PackageId is invalid.", nameof(packageId));
        lock (_gate)
        {
            if (!Directory.Exists(Root))
                return [];
            var installations = new List<CollectorPackageInstallation>();
            var packageRoots = packageId is null
                ? Directory.EnumerateDirectories(Root).OrderBy(path => path, StringComparer.Ordinal)
                : Directory.Exists(Path.Combine(Root, packageId))
                    ? [Path.Combine(Root, packageId)]
                    : Enumerable.Empty<string>();
            foreach (var packageRoot in packageRoots)
            {
                if (IsLink(packageRoot))
                    continue;
                var id = Path.GetFileName(packageRoot);
                if (IsStaging(id))
                    continue;
                foreach (var versionDirectory in Directory.EnumerateDirectories(packageRoot)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (IsLink(versionDirectory))
                        continue;
                    var version = Path.GetFileName(versionDirectory);
                    if (IsStaging(version))
                        continue;
                    foreach (var hashDirectory in Directory.EnumerateDirectories(versionDirectory)
                                 .OrderBy(path => path, StringComparer.Ordinal))
                    {
                        if (IsLink(hashDirectory))
                            continue;
                        var hex = Path.GetFileName(hashDirectory);
                        if (IsStaging(hex))
                            continue;
                        var reference = new CollectorPackageReference(id, version, "sha256:" + hex);
                        try
                        {
                            installations.Add(OpenVerified(reference, hashDirectory, expectedTreeHash: null));
                        }
                        catch (PackageValidationException)
                        {
                            // 损坏或不匹配的目录不是 Installation。
                        }
                    }
                }
            }
            return installations;
        }
    }

    /// <summary>
    /// 删除一个精确 Installation。调用方必须先停止使用它的 Activation，并负责随后删除或修复仍引用它的
    /// durable Instance；Marketplace 卸载特意保留该 Instance 到 Runtime commit 成功，使失败保持可见、可重试。
    /// </summary>
    public void Uninstall(CollectorPackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var installDirectory = DirectoryFor(reference);
        lock (_gate)
        {
            if (!Directory.Exists(installDirectory))
                return;
            Directory.Delete(installDirectory, recursive: true);
            DeleteIfEmpty(Path.GetDirectoryName(installDirectory)!);
            DeleteIfEmpty(Path.GetDirectoryName(Path.GetDirectoryName(installDirectory)!)!);
        }
    }

    private static void DeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private void CopyIntoPlace(
        string sourceDirectory,
        string installDirectory,
        CollectorPackageReference reference,
        string treeHash)
    {
        var parent = Path.GetDirectoryName(installDirectory)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $"{StagingPrefix}{Guid.NewGuid():N}");
        try
        {
            CopyTree(sourceDirectory, staging);
            var staged = LocalCollectorPackage.Load(staging);
            if (ReferenceOf(staged) != reference || ComputeTreeHash(staging) != treeHash)
                throw new PackageValidationException(
                    "Staged Collector Package content changed during installation.");
            Directory.Move(staging, installDirectory);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private CollectorPackageInstallation OpenVerified(
        CollectorPackageReference reference,
        string installDirectory,
        string? expectedTreeHash)
    {
        var installed = LocalCollectorPackage.Load(installDirectory);
        var installedReference = ReferenceOf(installed);
        if (installedReference != reference)
            throw new PackageValidationException(
                $"Installed content at {installDirectory} does not match Collector Package " +
                $"{reference.PackageId} {reference.Version} ({reference.PackageContentHash}).");
        var treeHash = ComputeTreeHash(installDirectory);
        if (expectedTreeHash is not null && treeHash != expectedTreeHash)
            throw new PackageValidationException(
                $"Installed Collector Package {reference.PackageId} {reference.Version} " +
                "does not match the content being installed.");
        return new CollectorPackageInstallation(reference, installDirectory, treeHash, installed);
    }

    private string DirectoryFor(CollectorPackageReference reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.PackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Version);
        if (!IsSha256(reference.PackageContentHash))
            throw new ArgumentException(
                "Collector Package content hash must be a lowercase sha256 digest.",
                nameof(reference));
        var directory = Path.GetFullPath(Path.Combine(
            Root,
            reference.PackageId,
            reference.Version,
            reference.PackageContentHash["sha256:".Length..]));
        if (!directory.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException(
                "Collector Package reference must resolve inside the Installation root.",
                nameof(reference));
        return directory;
    }

    private static CollectorPackageReference ReferenceOf(LocalCollectorPackage package) =>
        new(package.Manifest.PackageId, package.Manifest.Version, package.PackageContentHash);

    private static bool IsStaging(string name) =>
        name.StartsWith(StagingPrefix, StringComparison.Ordinal);

    private static bool IsLink(string path)
    {
        var directory = new DirectoryInfo(path);
        return directory.LinkTarget is not null ||
               (directory.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsPackageId(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetterLower(value[0]))
            return false;
        var segmentStart = true;
        foreach (var character in value)
        {
            if (segmentStart)
            {
                if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))
                    return false;
                segmentStart = false;
                continue;
            }
            if (character is '.' or '-')
            {
                segmentStart = true;
                continue;
            }
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))
                return false;
        }
        return !segmentStart;
    }

    private static void CopyTree(string source, string destination)
    {
        var sourceRoot = new DirectoryInfo(source);
        if (sourceRoot.LinkTarget is not null)
            throw new PackageValidationException("Collector Package root must not be a symbolic link.");
        Directory.CreateDirectory(destination);
        CopyDirectory(sourceRoot, destination);
    }

    private static void CopyDirectory(DirectoryInfo source, string destination)
    {
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PackageValidationException("Collector Package must not contain symbolic links.");
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(directory, target);
            }
            else if (entry is FileInfo file)
            {
                if (IsIgnorableMetadataFile(file.Name))
                    continue;
                file.CopyTo(target);
                // ManagedProcess artifact 的可执行位属于 Package 内容的一部分：Installation 必须能直接
                // 启动，不能要求调用方在复制后补 chmod。
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, File.GetUnixFileMode(file.FullName));
            }
        }
    }

    private static string ComputeTreeHash(string root)
    {
        EnsureTreeHasNoLinks(new DirectoryInfo(root));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !IsIgnorableMetadataFile(path))
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var name = Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            var content = File.ReadAllBytes(path);
            hash.AppendData(BitConverter.GetBytes(content.LongLength));
            hash.AppendData(content);
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Package 内容之外的宿主文件系统噪声：既不进 tree hash，也不随 Installation 复制。宿主 adapter
    /// 校验声明与实际载荷时必须用同一条规则，否则会出现「安装成功但声明校验失败」。
    /// </summary>
    internal static bool IsIgnorableMetadataFile(string path) =>
        string.Equals(Path.GetFileName(path), ".DS_Store", StringComparison.Ordinal);

    private static void EnsureTreeHasNoLinks(DirectoryInfo directory)
    {
        if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new PackageValidationException("Collector Package must not contain symbolic links.");
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PackageValidationException("Collector Package must not contain symbolic links.");
            if (entry is DirectoryInfo child)
                EnsureTreeHasNoLinks(child);
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(char.IsAsciiHexDigitLower);
}
