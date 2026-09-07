using System.Diagnostics;
using System.Text.Json;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Verification;

namespace Heartbeat.Verification.Tests;

public sealed class VerificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "heartbeat-verification-tests-" + Guid.NewGuid().ToString("N"));

    public VerificationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void EmptyOrDuplicateResultsCannotPass()
    {
        Assert.Throws<InvalidOperationException>(() => ReferenceExpectation.AssertMatches([]));
        Assert.Throws<InvalidOperationException>(() => ReferenceExpectation.AssertMatches([ValidSegment(), ValidSegment()]));
        ReferenceExpectation.AssertMatches([ValidSegment()]);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("time")]
    [InlineData("duration")]
    [InlineData("source")]
    public void ARecordWithWrongMeaningCannotPass(string difference)
    {
        var segment = ValidSegment();
        switch (difference)
        {
            case "identity": segment.IdentityKey = "another-activity"; break;
            case "time": segment.StartTime = segment.StartTime.AddDays(-1); break;
            case "duration": segment.DurationSeconds = 0; break;
            case "source": segment.Source = "system"; break;
        }
        Assert.Throws<InvalidOperationException>(() => ReferenceExpectation.AssertMatches([segment]));
    }

    [Fact]
    public async Task DeadlineProducesFailedStageAndKeepsEarlierEvidence()
    {
        var report = new RunReport("test", _directory, new SecretRedactor());
        report.Evidence["baselineCount"] = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => report.StageAsync("delivery", TimeSpan.FromMilliseconds(30),
            token => Task.Delay(Timeout.Infinite, token), CancellationToken.None));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "report.json")));
        Assert.Equal("failed", saved.RootElement.GetProperty("stages")[0].GetProperty("status").GetString());
        Assert.Equal(0, saved.RootElement.GetProperty("evidence").GetProperty("baselineCount").GetInt32());
    }

    [Fact]
    public async Task EvidenceAndFailureDetailsRedactKeysAndTokens()
    {
        var redactor = new SecretRedactor();
        redactor.Add("private-key");
        var report = new RunReport("test", _directory, redactor);
        report.Evidence["message"] = "private-key eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl";
        await report.SaveAsync();
        var text = await File.ReadAllTextAsync(Path.Combine(_directory, "report.json"));
        Assert.DoesNotContain("private-key", text);
        Assert.DoesNotContain("eyJ", text);
    }

    [UnixFact]
    public async Task CancellationStopsAnOwnedCommand()
    {
        var command = OwnedProcess.Start("waiting", "/bin/sleep", ["60"], _directory,
            Path.Combine(_directory, "command.log"), new SecretRedactor());
        var pid = command.Id;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.CompleteAsync(cancellation.Token));
        await command.DisposeAsync();
        Assert.False(await IsRunningAsync(pid));
    }

    [UnixFact]
    public async Task ServiceExitIsDetectedAndOrphanedChildrenAreCleaned()
    {
        var childPidFile = Path.Combine(_directory, "child.pid");
        var state = Path.Combine(_directory, "service");
        // Positional argument avoids interpolating paths into shell code.
        var service = OwnedProcess.Start("early-exit", "/bin/sh",
            ["-c", "echo service-started; sleep 60 & echo $! > \"$1\"; exit 7", "test", childPidFile], _directory,
            Path.Combine(_directory, "service.log"), new SecretRedactor(), serviceState: state);
        int childPid;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(state + ".exit")) await Task.Delay(20, deadline.Token);
            Assert.Contains("code 7", Assert.Throws<InvalidOperationException>(service.ThrowIfExited).Message);
            childPid = int.Parse(await File.ReadAllTextAsync(childPidFile, deadline.Token));
            Assert.True(await IsRunningAsync(childPid));
        }
        finally { await service.DisposeAsync(); }
        for (var attempt = 0; attempt < 50 && await IsRunningAsync(childPid); attempt++) await Task.Delay(20);
        Assert.False(await IsRunningAsync(childPid));
        Assert.Contains("service-started", await File.ReadAllTextAsync(Path.Combine(_directory, "service.log")));
    }

    private static async Task<bool> IsRunningAsync(int pid)
    {
        var start = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "-p", pid.ToString(), "-o", "stat=" }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        var status = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 && status.Length > 0 && !status.StartsWith('Z');
    }

    private static SegmentResponse ValidSegment() => new()
    {
        Id = Guid.NewGuid(), DeviceId = 1, Source = "reference.account", IdentityKey = "reference.account|online",
        Title = "Reference account online", StartTime = DateTimeOffset.Parse("2026-08-22T12:00:00Z"),
        EndTime = DateTimeOffset.Parse("2026-08-22T12:05:00Z"), DurationSeconds = 300
    };

    public void Dispose() => Directory.Delete(_directory, true);
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "POSIX process-group regression."; }
}
