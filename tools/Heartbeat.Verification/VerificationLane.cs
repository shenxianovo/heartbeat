using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Npgsql;

namespace Heartbeat.Verification;

internal sealed class VerificationLane(VerificationOptions options, RunReport report,
    string directory, SecretRedactor redactor)
{
    private readonly List<OwnedProcess> _services = [];
    private Socket? _disconnectedEndpoint;
    private bool _databaseAttempted;
    public string DirectoryPath => directory;
    public string WorkPath { get; } = Path.Combine(directory, "work");
    public string DatabaseName { get; } = "heartbeat-verify-" + report.RunId;
    public string ConnectionString { get; private set; } = "";
    public Uri AnalyticsUrl { get; private set; } = null!;
    public string HeadlessUrl { get; private set; } = "";

    public async Task<string> CommandAsync(string name, string executable, IEnumerable<string> args,
        CancellationToken token, string? workingDirectory = null)
    {
        await using var process = OwnedProcess.Start(name, executable, args,
            workingDirectory ?? options.Repository, Path.Combine(directory, name + ".log"), redactor);
        return await process.CompleteAsync(token);
    }

    public async Task BuildAsync(CancellationToken token)
    {
        foreach (var (name, project) in new[]
        {
            ("analytics", "server/Heartbeat.Server"),
            ("headless", "collection/hub/Heartbeat.Collection.Headless"),
            ("reference", "collection/collectors/Heartbeat.Collector.Reference.ManagedProcess")
        })
            await CommandAsync("build-" + name, "dotnet",
                ["publish", project, "-c", "Release", "-o", Path.Combine(WorkPath, name), "--nologo"], token);
    }

    public async Task StartDatabaseAsync(CancellationToken token)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        redactor.Add(password);
        var envPath = Path.Combine(WorkPath, "database.env");
        await File.WriteAllTextAsync(envPath,
            $"POSTGRES_USER=heartbeat\nPOSTGRES_DB=heartbeat\nPOSTGRES_PASSWORD={password}\n", token);
        RestrictFile(envPath);
        _databaseAttempted = true;
        await CommandAsync("database-create", "docker",
            ["create", "--name", DatabaseName, "--label", "heartbeat.verification=" + report.RunId,
             "--publish", "127.0.0.1::5432", "--env-file", envPath, "postgres:18.4-alpine"], token);
        await CommandAsync("database-start", "docker", ["start", DatabaseName], token);
        var port = await CommandAsync("database-port", "docker",
            ["inspect", "--format", "{{(index (index .NetworkSettings.Ports \"5432/tcp\") 0).HostPort}}", DatabaseName], token);
        ConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = int.Parse(port), Database = "heartbeat", Username = "heartbeat",
            Password = password, Timeout = 2, CommandTimeout = 10, Pooling = false
        }.ConnectionString;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync(token);
                break;
            }
            catch (NpgsqlException) { await Task.Delay(200, token); }
        }
        report.Evidence["databaseContainer"] = DatabaseName;
    }

    public async Task StartAnalyticsAsync(IReadOnlyDictionary<string, string?> auth, CancellationToken token)
    {
        AnalyticsUrl = new Uri($"http://127.0.0.1:{FreePort()}");
        var environment = new Dictionary<string, string?>(auth)
        {
            ["ASPNETCORE_URLS"] = AnalyticsUrl.ToString(),
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["ConnectionStrings__DefaultConnection"] = ConnectionString,
            ["Recap__ApiKey"] = "",
            ["Logging__LogLevel__Microsoft.EntityFrameworkCore"] = "Warning"
        };
        await StartServiceAsync("analytics", "Heartbeat.Server.dll", environment, [], token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (true)
        {
            ThrowIfExited();
            try
            {
                using var response = await http.GetAsync(new Uri(AnalyticsUrl, "/health"), token);
                if (response.IsSuccessStatusCode) break;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                && !token.IsCancellationRequested) { }
            await Task.Delay(100, token);
        }
        report.Evidence["analyticsUrl"] = AnalyticsUrl.ToString();
    }

    public async Task StartHeadlessAsync(string configPath, CancellationToken token)
    {
        var uploadUrl = AnalyticsUrl.ToString();
        if (options.DisconnectUpload)
        {
            // Reserve a local port without listening: connections fail and no unrelated service can bind it.
            _disconnectedEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _disconnectedEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            uploadUrl = "http://127.0.0.1:" + ((IPEndPoint)_disconnectedEndpoint.LocalEndPoint!).Port;
        }
        report.Evidence["uploadUrl"] = uploadUrl;
        report.Evidence["fault"] = options.DisconnectUpload ? "disconnect-upload" : null;
        await StartServiceAsync("headless", "Heartbeat.Collection.Headless.dll", new Dictionary<string, string?>
        {
            ["HEARTBEAT_API_BASE_URL"] = uploadUrl,
            ["HEARTBEAT_REFERENCE_BEHAVIOR"] = null,
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_ENVIRONMENT"] = "Production"
        }, [configPath], token);
    }

    public string AllocateHeadlessUrl() => HeadlessUrl = $"http://127.0.0.1:{FreePort()}";

    private async Task StartServiceAsync(string name, string assembly,
        IReadOnlyDictionary<string, string?> environment, string[] args, CancellationToken token)
    {
        var process = OwnedProcess.Start(name, "dotnet", new[] { assembly }.Concat(args),
            Path.Combine(WorkPath, name), Path.Combine(directory, name + ".log"), redactor,
            environment, Path.Combine(WorkPath, name + "-process"));
        _services.Add(process);
        report.Evidence[name + "SupervisorPid"] = process.Id;
        await process.WaitForServiceAsync(token);
    }

    public void ThrowIfExited() { foreach (var service in _services) service.ThrowIfExited(); }

    public async Task CleanupAsync()
    {
        report.Cleanup = "running";
        foreach (var process in _services.AsEnumerable().Reverse())
            await AttemptAsync(process.Name, async () => await process.DisposeAsync());
        _disconnectedEndpoint?.Dispose();
        if (_databaseAttempted)
        {
            // Collect logs independently: failure must not prevent removal.
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await CommandAsync("database", "docker", ["logs", DatabaseName], deadline.Token);
            }
            catch (Exception) { /* create may have failed before there was a container */ }
            await AttemptAsync("database", async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await CommandAsync("database-remove", "docker", ["rm", "--force", "--volumes", DatabaseName], deadline.Token); }
                catch (InvalidOperationException) when (File.ReadAllText(Path.Combine(directory, "database-remove.log"))
                    .Contains("No such container", StringComparison.OrdinalIgnoreCase)) { }
            });
        }
        // If a service could not be stopped, preserve its files instead of removing live state.
        if (report.CleanupErrors.Count == 0)
            await AttemptAsync("work directory", () => { if (Directory.Exists(WorkPath)) Directory.Delete(WorkPath, true); return Task.CompletedTask; });
        report.Cleanup = report.CleanupErrors.Count == 0 ? "passed" : "failed";

        async Task AttemptAsync(string resource, Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception exception) { report.CleanupErrors.Add(redactor.Redact(resource + ": " + exception.Message)); }
        }
    }

    internal static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
