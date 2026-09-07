namespace Heartbeat.Verification;

internal static class VerificationCommand
{
    public static async Task<int> RunAsync(VerificationOptions options, CancellationToken cancellationToken)
    {
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss").ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..8];
        var directory = Path.Combine(options.Repository, ".local", "verification", runId);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var redactor = new SecretRedactor();
        var report = new RunReport(runId, directory, redactor);
        var lane = new VerificationLane(options, report, directory, redactor);
        Directory.CreateDirectory(lane.WorkPath);
        Console.WriteLine($"Run {runId}\nEvidence: {directory}");
        using var scenario = new HeadlessMainScenario(options, lane, report, redactor);
        try
        {
            await Stage("configuration", scenario.LoadConfigurationAsync);
            await Stage("docker", async token =>
            {
                try { await lane.CommandAsync("docker-check", "docker", ["info", "--format", "{{.ServerVersion}}"], token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { throw new DependencyBlockedException("Docker is unavailable; see docker-check.log."); }
            });
            await Stage("build", lane.BuildAsync, TimeSpan.FromMinutes(10));
            await Stage("auth", scenario.AuthenticateAsync, TimeSpan.FromSeconds(30));
            await Stage("database", lane.StartDatabaseAsync);
            await Stage("analytics-start", token => lane.StartAnalyticsAsync(scenario.AnalyticsAuth(), token));
            await Stage("prepare", scenario.PrepareAsync);
            await Stage("headless-start", scenario.StartAsync);
            await Stage("delivery", scenario.AssertDeliveryAsync);
            report.Status = "passed";
        }
        catch (Exception exception)
        {
            report.Status = exception switch
            {
                DependencyBlockedException => "blocked",
                OperationCanceledException when cancellationToken.IsCancellationRequested => "cancelled",
                _ => "failed"
            };
            report.Failure = redactor.Redact(exception.Message);
            Console.Error.WriteLine($"{report.Status}: {report.Failure}");
        }
        finally
        {
            if (options.Keep && !cancellationToken.IsCancellationRequested)
            {
                report.Cleanup = "retained";
                await report.SaveAsync();
                Console.WriteLine("Lane retained for inspection. Press Ctrl+C to stop it and clean up.");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { }
            }
            Console.WriteLine("[cleanup] stopping lane resources");
            await lane.CleanupAsync();
            report.FinishedAt = DateTimeOffset.UtcNow;
            await report.SaveAsync();
        }
        Console.WriteLine($"Result: {report.Status}; cleanup: {report.Cleanup}\nReport: {Path.Combine(directory, "report.json")}");
        if (report.Cleanup != "passed") return 1;
        return report.Status switch { "passed" => 0, "blocked" => 2, "cancelled" => 130, _ => 1 };

        Task Stage(string name, Func<CancellationToken, Task> action, TimeSpan? timeout = null) =>
            report.StageAsync(name, timeout ?? options.Timeout, action, cancellationToken);
    }
}
