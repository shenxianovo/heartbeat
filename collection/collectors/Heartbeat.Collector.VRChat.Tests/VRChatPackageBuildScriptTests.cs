using System.Diagnostics;

namespace Heartbeat.Collector.VRChat.Tests;

public sealed class VRChatPackageBuildScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"heartbeat-vrchat-build-script-{Guid.NewGuid():N}");

    [Fact]
    public async Task ShellCli_ExistingUnownedNonEmptyOutput_IsRejectedWithoutDeletingIt()
    {
        if (OperatingSystem.IsWindows())
            return;
        var output = Path.Combine(_root, "user-content");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep-me.txt");
        await File.WriteAllTextAsync(sentinel, "user owned");
        var fakeBin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(fakeBin);
        var docker = Path.Combine(fakeBin, "docker");
        await File.WriteAllTextAsync(docker, "#!/usr/bin/env sh\nexit 0\n");
        File.SetUnixFileMode(
            docker,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await RunAsync(
            "/bin/bash",
            [Path.Combine(RepositoryRoot(), "scripts", "build-vrchat-package.sh"), "--output", output],
            new Dictionary<string, string?>
            {
                ["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(sentinel), result.Output);
        Assert.Contains("not owned", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShellCli_RootRepositoryAndRepositoryAncestorOutputs_AreRejectedBeforeBuild()
    {
        if (OperatingSystem.IsWindows())
            return;
        var repository = RepositoryRoot();
        var fakeBin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(fakeBin);
        var docker = Path.Combine(fakeBin, "docker");
        await File.WriteAllTextAsync(docker, "#!/usr/bin/env sh\nexit 0\n");
        File.SetUnixFileMode(
            docker,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string[] unsafeOutputs =
        [
            Path.GetPathRoot(repository)!,
            repository,
            Directory.GetParent(repository)!.FullName
        ];
        foreach (var output in unsafeOutputs)
        {
            var result = await RunAsync(
                "/bin/bash",
                [Path.Combine(repository, "scripts", "build-vrchat-package.sh"), "--output", output],
                new Dictionary<string, string?>
                {
                    ["PATH"] = fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
                });

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Refusing unsafe output path", result.Output, StringComparison.Ordinal);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var pair in environment)
            start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await standardOutput + await standardError);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            var gitEntry = Path.Combine(directory.FullName, ".git");
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "build-vrchat-package.sh")) &&
                (Directory.Exists(gitEntry) || File.Exists(gitEntry)))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Heartbeat repository root.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
