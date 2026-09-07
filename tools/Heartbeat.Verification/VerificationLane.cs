using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Npgsql;

namespace Heartbeat.Verification;

internal sealed class VerificationLane(VerificationOptions options, RunReport report,
    string directory, SecretRedactor redactor)
{
    private readonly Dictionary<string, VerificationArtifact> _artifacts = [];
    public VerificationArtifact Artifact(string service) => _artifacts[service];
    public string DesktopData => Path.Combine(WorkPath, "desktop-data");

    private readonly List<OwnedProcess> _services = [];
    private TcpListener? _disconnectedEndpoint;
    private Task? _rejections;
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
        if (options.IsDesktop && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new DependencyBlockedException("Desktop requires a logged-in macOS or Windows desktop session.");
        var desktop = OperatingSystem.IsMacOS() ? "Mac" : "Windows";
        foreach (var (name, project, entrypoint) in new[]
        {
            ("analytics", "server/Heartbeat.Server", "Heartbeat.Server.dll"),
            options.IsDesktop
                ? ("desktop", $"collection/desktop/Heartbeat.Desktop.{desktop}", $"Heartbeat.Desktop.{desktop}" + (OperatingSystem.IsWindows() ? ".exe" : ""))
                : ("headless", "collection/hub/Heartbeat.Collection.Headless", "Heartbeat.Collection.Headless.dll"),
            ("reference", "collection/collectors/Heartbeat.Collector.Reference.ManagedProcess", "Heartbeat.Collector.Reference.ManagedProcess" + (OperatingSystem.IsWindows() ? ".exe" : ""))
        })
        {
            if (!options.Artifacts.TryGetValue(name, out var path))
            {
                var output = Path.Combine(WorkPath, name);
                await CommandAsync("build-" + name, "dotnet",
                    ["publish", project, "-c", "Release", "-o", output, "--nologo"], token);
                path = Path.Combine(output, entrypoint);
            }
            var artifact = VerificationArtifact.Open(path);
            _artifacts.Add(name, artifact);
            report.Evidence[name + "Artifact"] = await artifact.DescribeAsync(token);
        }
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
        await StartServiceAsync("analytics", environment, [], token);
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
        var uploadUrl = AllocateUploadUrl();
        await StartServiceAsync("headless", RuntimeEnvironment(uploadUrl), [configPath], token);
    }

    private string AllocateUploadUrl()
    {
        var uploadUrl = AnalyticsUrl.ToString();
        if (options.DisconnectUpload)
        {
            // A bound, non-listening socket blackholes SYN on macOS. Accept then reset instead,
            // so this fault consistently means disconnected, not a platform-dependent network timeout.
            _disconnectedEndpoint = new TcpListener(IPAddress.Loopback, 0);
            _disconnectedEndpoint.Start();
            uploadUrl = "http://127.0.0.1:" + ((IPEndPoint)_disconnectedEndpoint.LocalEndpoint).Port;
            _rejections = RejectConnectionsAsync(_disconnectedEndpoint);
        }
        report.Evidence["uploadUrl"] = uploadUrl;
        report.Evidence["fault"] = options.DisconnectUpload ? "disconnect-upload" : null;
        return uploadUrl;
    }

    private static Dictionary<string, string?> RuntimeEnvironment(string uploadUrl) => new()
    {
        ["HEARTBEAT_API_BASE_URL"] = uploadUrl,
        ["HEARTBEAT_REFERENCE_BEHAVIOR"] = null,
        ["DOTNET_ENVIRONMENT"] = "Production",
        ["ASPNETCORE_ENVIRONMENT"] = "Production"
    };

    public async Task StartDesktopAsync(CancellationToken token)
    {
        await StartServiceAsync("desktop", RuntimeEnvironment(AllocateUploadUrl()),
            ["--data-directory", DesktopData], token);
        var status = Path.Combine(DesktopData, "desktop-status.json");
        while (!File.Exists(status)) { ThrowIfExited(); await Task.Delay(100, token); }
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(status, token));
        if (!json.RootElement.GetProperty("uiReady").GetBoolean())
            throw new InvalidOperationException("The native Desktop UI is not ready.");
        if (json.RootElement.GetProperty("loginStartSupported").GetBoolean()
            || json.RootElement.GetProperty("updatesSupported").GetBoolean())
            throw new InvalidOperationException("An isolated Desktop unexpectedly acquired installation capabilities.");
        report.Evidence["desktopInstallationDetached"] = true;
        report.Evidence["desktopUiReady"] = true;
        report.Evidence["desktopPid"] = json.RootElement.GetProperty("pid").GetInt32();
    }

    public string AllocateHeadlessUrl() => HeadlessUrl = $"http://127.0.0.1:{FreePort()}";

    private async Task StartServiceAsync(string name,
        IReadOnlyDictionary<string, string?> environment, string[] args, CancellationToken token)
    {
        var artifact = Artifact(name);
        var process = OwnedProcess.Start(name, artifact.Executable, artifact.Arguments.Concat(args),
            Path.GetDirectoryName(artifact.Path)!, Path.Combine(directory, name + ".log"), redactor,
            environment, Path.Combine(WorkPath, name + "-process"));
        _services.Add(process);
        report.Evidence[name + "SupervisorPid"] = process.Id;
        await process.WaitForServiceAsync(token);
    }

    public void ThrowIfExited() { foreach (var service in _services) service.ThrowIfExited(); }

    public async Task CleanupAsync()
    {
        report.Cleanup = "running";
        if (options.IsDesktop && File.Exists(Path.Combine(DesktopData, "desktop-status.json")))
        {
            await AttemptAsync("desktop graceful exit", async () =>
            {
                await File.WriteAllTextAsync(Path.Combine(DesktopData, "desktop-stop"), "stop");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var exit = Path.Combine(WorkPath, "desktop-process.exit");
                while (!File.Exists(exit)) await Task.Delay(100, deadline.Token);
                var code = (await File.ReadAllTextAsync(exit, deadline.Token)).Trim();
                report.Evidence["desktopExitCode"] = code;
                if (code != "0") throw new InvalidOperationException("Desktop did not exit cleanly: " + code);
            });
        }
        await AttemptAsync("desktop logs", async () =>
        {
            if (Directory.Exists(Path.Combine(DesktopData, "logs")))
                foreach (var log in System.IO.Directory.EnumerateFiles(Path.Combine(DesktopData, "logs")))
                    await File.WriteAllTextAsync(Path.Combine(directory, "desktop-" + Path.GetFileName(log)),
                        redactor.Redact(await File.ReadAllTextAsync(log)));
        });
        foreach (var process in _services.AsEnumerable().Reverse())
            await AttemptAsync(process.Name, async () => await process.DisposeAsync());
        if (_disconnectedEndpoint is not null)
        {
            _disconnectedEndpoint.Stop();
            if (_rejections is not null) await _rejections;
        }
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

    private static async Task RejectConnectionsAsync(TcpListener listener)
    {
        while (true)
        {
            try
            {
                using var connection = await listener.AcceptSocketAsync();
                connection.LingerState = new LingerOption(true, 0);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException) { return; }
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
