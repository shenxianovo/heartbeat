using System.Diagnostics;
using Heartbeat.Desktop.UI.Hosting;
using Heartbeat.Verification;

namespace Heartbeat.Verification.Tests;

public sealed class DesktopProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("HEARTBEAT_PROFILE_TEST_ROOT") ?? Path.GetTempPath(),
        "heartbeat-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private readonly string _mutex = "heartbeat-profile-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SameDirectoryContendsDifferentDirectoryRunsAndCrashReleasesOwnership()
    {
        var first = await Start("a");
        Assert.Equal("acquired", await Read(first));
        Assert.Equal("occupied", await Read(await Start("a")));
        Assert.Equal("acquired", await Read(await Start("b")));
        first.Kill(true);
        await first.WaitForExitAsync();
        Assert.Equal("acquired", await Read(await Start("a")));
    }

    [UnixFact]
    public async Task SymlinkedParentCannotBypassOwnership()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));
        Assert.Equal("acquired", await Read(await Start("real/profile")));
        Assert.Equal("occupied", await Read(await Start("alias/profile")));
    }

    [Fact]
    public async Task DefaultDirectoryStillContendsWithLegacyMutexAndCleanExitReleasesIt()
    {
        // The probe takes both the directory lease and the legacy mutex on its main thread.
        var first = await Start("default", defaultName: "default");
        Assert.Equal("acquired", await Read(first));
        Assert.Equal("occupied", await Read(await Start("different-default", defaultName: "different-default")));
        await first.StandardInput.WriteLineAsync("stop");
        await first.WaitForExitAsync();
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("acquired", await Read(await Start("default", defaultName: "default")));
    }

    [Fact]
    public void ExplicitDefaultAliasNeverGrantsInstallationBinding()
    {
        using var bootstrap = new DesktopBootstrap(["--data-directory", Path.Combine(_root, "default", ".")],
            Path.Combine(_root, "default"));
        Assert.True(bootstrap.TryAcquire(_mutex));
        Assert.True(bootstrap.UsesDefaultDirectory);
        Assert.False(bootstrap.AllowsInstallationBinding);
        Assert.Throws<ArgumentException>(() => new DesktopBootstrap(["--data-directory"], _root));
    }

    [Fact]
    public async Task CaseAliasesFollowTheFilesystemOwnership()
    {
        Assert.Equal("acquired", await Read(await Start("MixedCase")));
        var aliasExists = Directory.Exists(Path.Combine(_root, "mixedcase"));
        Assert.Equal(aliasExists ? "occupied" : "acquired", await Read(await Start("mixedcase")));
    }

    [Fact]
    public async Task DefaultDirectoryCaseComparisonFollowsTheFilesystem()
    {
        Assert.Equal("acquired", await Read(await Start("Default", defaultName: "Default")));
        var sameDirectory = Directory.Exists(Path.Combine(_root, "default"));
        Assert.Equal(sameDirectory ? "occupied" : "acquired",
            await Read(await Start("default", defaultName: "Default")));
        // Whichever spelling is used, the actual default still retains the legacy mutex.
        Assert.Equal("occupied", await Read(await Start("other", defaultName: "other")));
    }

    [Fact]
    public async Task DefaultCaseAliasRetainsLegacyProtectionOnlyForTheSameDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Default"));
        var sameDirectory = Directory.Exists(Path.Combine(_root, "default"));
        // Hold only the shared legacy mutex relative to the directory under test.
        Assert.Equal("acquired", await Read(await Start("legacy-holder", defaultName: "legacy-holder")));
        Assert.Equal(sameDirectory ? "occupied" : "acquired",
            await Read(await Start("default", defaultName: "Default")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".desktop-identity-*", SearchOption.AllDirectories));
    }

    [UnixFact]
    public void DefaultDirectorySymlinkRetainsLegacyProtection()
    {
        Directory.CreateDirectory(Path.Combine(_root, "default"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Path.Combine(_root, "default"));
        using var bootstrap = new DesktopBootstrap(["--data-directory", Path.Combine(_root, "alias")],
            Path.Combine(_root, "default"));
        Assert.True(bootstrap.TryAcquire(_mutex));
        Assert.True(bootstrap.UsesDefaultDirectory);
        Assert.False(bootstrap.AllowsInstallationBinding);
    }

    [Fact]
    public void OwnershipFailureDoesNotAllowStartup()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "file");
        File.WriteAllText(file, "not a directory");
        using var bootstrap = new DesktopBootstrap(["--data-directory", file], _root);
        Assert.ThrowsAny<IOException>(() => bootstrap.TryAcquire(_mutex));
    }

    private Task<Process> Start(string name, string defaultName = "unused-default")
    {
        Directory.CreateDirectory(_root);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(VerificationOptions).Assembly.Location, "__profile-probe",
                     Path.Combine(_root, name), Path.Combine(_root, defaultName), _mutex }) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        _processes.Add(process);
        return Task.FromResult(process);
    }

    private static async Task<string?> Read(Process process)
    {
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (line is null) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
        return line;
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
            process.Dispose();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
