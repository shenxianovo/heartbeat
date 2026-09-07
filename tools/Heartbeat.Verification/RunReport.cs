using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heartbeat.Verification;

internal sealed class DependencyBlockedException(string message) : Exception(message);

internal sealed class SecretRedactor
{
    private readonly List<string> _secrets = [];
    public void Add(string value) { if (!string.IsNullOrEmpty(value)) lock (_secrets) _secrets.Add(value); }
    public string Redact(string text)
    {
        lock (_secrets)
            foreach (var secret in _secrets.OrderByDescending(value => value.Length))
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return Regex.Replace(text, @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[redacted-jwt]");
    }
}

internal sealed record StageResult(string Name, string Status, long ElapsedMilliseconds, string? Detail);

internal sealed class RunReport(string runId, string directory, SecretRedactor redactor)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string RunId { get; } = runId;
    public string Scenario { get; init; } = "headless-main";
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Failure { get; set; }
    public string Cleanup { get; set; } = "pending";
    public List<string> CleanupErrors { get; } = [];
    public List<StageResult> Stages { get; } = [];
    public Dictionary<string, object?> Evidence { get; } = [];

    public async Task StageAsync(string name, TimeSpan timeout, Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{name}] starting");
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await action(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            Stages.Add(new(name, "passed", stopwatch.ElapsedMilliseconds, null));
            Console.WriteLine($"[{name}] passed ({stopwatch.Elapsed.TotalSeconds:F1}s)");
        }
        catch (Exception exception)
        {
            var failure = exception is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? new TimeoutException($"Stage '{name}' exceeded {timeout.TotalSeconds:F0}s.", exception)
                : exception;
            Stages.Add(new(name, failure is DependencyBlockedException ? "blocked" : "failed",
                stopwatch.ElapsedMilliseconds, redactor.Redact(failure.Message)));
            if (ReferenceEquals(failure, exception)) throw;
            throw failure;
        }
        finally { await SaveAsync(); }
    }

    public Task SaveAsync() => WriteAsync("report.json", this);

    public async Task WriteAsync(string fileName, object value)
    {
        var path = Path.Combine(directory, fileName);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, redactor.Redact(JsonSerializer.Serialize(value, Json)));
        File.Move(temporary, path, overwrite: true);
    }
}
