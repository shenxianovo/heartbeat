using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Verification;

namespace Heartbeat.Verification.Tests;

public sealed class VerificationEntryTests
{
    [UnixFact]
    public async Task PrebuiltEntrySelectsArtifactsEvenWhenServiceProjectsCannotBuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "heartbeat-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Heartbeat.slnx"), "<Solution />");
            foreach (var project in new[] { "server/Heartbeat.Server", "collection/hub/Heartbeat.Collection.Headless" })
            {
                var path = Path.Combine(root, project);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, Path.GetFileName(path) + ".csproj"), "not valid project XML");
            }
            var brokenBuild = await Run("dotnet", ["build", "server/Heartbeat.Server/Heartbeat.Server.csproj", "--no-restore"], root);
            Assert.NotEqual(0, brokenBuild.Code);
            Assert.Contains("MSB4025", brokenBuild.Output);

            // Docker is an external prerequisite. Stop at artifact selection, before online Auth,
            // so this command-entry regression never needs Docker, credentials or service processes.
            var commands = Path.Combine(root, "commands");
            Directory.CreateDirectory(commands);
            var docker = Path.Combine(commands, "docker");
            File.WriteAllText(docker, "#!/bin/sh\n[ \"$1\" = info ] || exit 99\necho fixture-docker\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var config = Path.Combine(root, "config.json");
            File.WriteAllText(config, """
                {"apiKey":"offline-fixture","dataDirectory":"unused","management":{
                "ownerSubject":"fixture","authority":"https://example.invalid",
                "issuer":"https://example.invalid","clientId":"fixture"}}
                """);
            var artifacts = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(artifacts);
            var binary = Path.Combine(artifacts, "app.dll");
            File.Copy(typeof(VerificationOptions).Assembly.Location, binary);
            var result = await Run("dotnet", [typeof(VerificationOptions).Assembly.Location,
                "run", "headless-main", "--config", config,
                "--artifact", "analytics=" + binary, "--artifact", "headless=" + binary,
                "--artifact", "reference=" + Path.Combine(artifacts, "missing-reference")], root, commands);
            Assert.Equal(2, result.Code);
            var run = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".local", "verification")));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "report.json")));
            var evidence = report.RootElement.GetProperty("evidence");
            Assert.Equal(binary, evidence.GetProperty("analyticsArtifact").GetProperty("path").GetString());
            Assert.Equal(binary, evidence.GetProperty("headlessArtifact").GetProperty("path").GetString());
            Assert.Contains("missing-reference", report.RootElement.GetProperty("failure").GetString());
            Assert.Equal("passed", report.RootElement.GetProperty("cleanup").GetString());
            Assert.Empty(Directory.GetFiles(run, "build-*.log"));
            Assert.DoesNotContain("MSB4025", result.Output);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(int Code, string Output)> Run(string executable, string[] arguments,
        string directory, string? commands = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (commands is not null) start.Environment["PATH"] = commands + Path.PathSeparator + start.Environment["PATH"];
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await stdout + await stderr);
    }
}
