using Heartbeat.Desktop.UI.Diagnostics;

namespace Heartbeat.Desktop.UI.Hosting;

/// <summary>Resolves the data tree before any configuration, logging or Host writes.</summary>
public sealed class DesktopBootstrap : IDisposable
{
    private FileStream? _ownership;
    private Mutex? _legacy;
    private readonly DesktopStartupSmoke.Lifecycle? _smokeLifecycle;
    private readonly string _defaultDirectory;

    public string DataDirectory { get; }
    public DesktopStartupSmoke.Request? Smoke { get; }
    public bool AllowsInstallationBinding { get; }
    /// <summary>Determined after acquiring the directory lock, using the filesystem itself.</summary>
    public bool UsesDefaultDirectory { get; private set; }

    public DesktopBootstrap(string[] args, string defaultDirectory)
    {
        if (DesktopStartupSmoke.TryGetRequest(args, out var smoke)) Smoke = smoke;
        string? explicitDirectory = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--data-directory=", StringComparison.Ordinal))
                SetDirectory(args[i]["--data-directory=".Length..]);
            else if (args[i] == "--data-directory")
                SetDirectory(++i < args.Length ? args[i] : "");
        }
        if (Smoke is not null && explicitDirectory is not null)
            throw new ArgumentException("Use either --data-directory or --verify-startup-data-directory.");
        DataDirectory = ResolveDirectory(explicitDirectory ?? Smoke?.DataDirectory ?? defaultDirectory);
        _defaultDirectory = ResolveDirectory(defaultDirectory);
        AllowsInstallationBinding = explicitDirectory is null && Smoke is null;
        _smokeLifecycle = Smoke is null ? null : DesktopStartupSmoke.BeginLifecycle(Smoke);

        void SetDirectory(string value)
        {
            if (explicitDirectory is not null || string.IsNullOrWhiteSpace(value) || value.StartsWith("--"))
                throw new ArgumentException("--data-directory requires exactly one nonempty path.");
            explicitDirectory = value;
        }
    }

    public bool TryAcquire(string legacyMutexName)
    {
        if (_ownership is not null) throw new InvalidOperationException("Profile ownership was already acquired.");
        // FileShare.None locks the actual file, including symlink/case aliases, across processes.
        // Never unlink this file: replacing its inode would permit a second concurrent writer.
        Directory.CreateDirectory(DataDirectory);
        try { _ownership = new FileStream(Path.Combine(DataDirectory, ".desktop.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when (OperatingSystem.IsWindows()
            ? (exception.HResult & 0xffff) is 32 or 33
            : exception.HResult == (OperatingSystem.IsMacOS() ? 35 : 11)) { return false; }
        try
        {
            UsesDefaultDirectory = IsDefaultDirectory();
            if (!UsesDefaultDirectory) return true;
            bool created;
            _legacy = OperatingSystem.IsWindows()
                ? new Mutex(true, legacyMutexName, out created)
                : new Mutex(true, legacyMutexName,
                    new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out created);
            if (created) return true;
            _legacy.Dispose();
            _legacy = null;
            _ownership?.Dispose();
            _ownership = null;
            return false;
        }
        catch
        {
            _ownership?.Dispose();
            _ownership = null;
            throw; // Failure to establish ownership must never allow startup.
        }
    }

    private bool IsDefaultDirectory()
    {
        if (string.Equals(DataDirectory, _defaultDirectory, StringComparison.Ordinal)) return true;
        if (!Directory.Exists(_defaultDirectory)) return false;

        // A unique entry visible through both paths proves they name the same directory.
        // This respects per-volume/per-directory case rules, Unicode aliases and short paths
        // without writing into an unrelated default Profile or guessing from the OS name.
        // The probe is only an identity witness; .desktop.lock remains the ownership lock.
        var name = ".desktop-identity-" + Guid.NewGuid().ToString("N");
        using var probe = new FileStream(Path.Combine(DataDirectory, name), FileMode.CreateNew,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose);
        return File.Exists(Path.Combine(_defaultDirectory, name));
    }

    internal static string ResolveDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full)!;
        var resolved = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolved, component));
            var target = directory.Exists ? directory.ResolveLinkTarget(true) : null;
            resolved = target is null ? directory.FullName : ResolveDirectory(target.FullName);
        }
        return resolved;
    }

    public void Dispose()
    {
        if (_legacy is not null)
        {
            _legacy.ReleaseMutex();
            _legacy.Dispose();
            _legacy = null;
        }
        _ownership?.Dispose();
        _ownership = null;
        _smokeLifecycle?.Dispose();
    }
}
