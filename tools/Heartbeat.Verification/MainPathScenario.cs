using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Heartbeat.Collection.Headless;
using Heartbeat.Collection.Hub.Auth;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Data;
using Heartbeat.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Verification;

internal static class ReferenceExpectation
{
    public const string Source = "reference.account";
    public const string IdentityKey = "reference.account|online";
    public const string Title = "Reference account online";
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-22T12:00:00Z");
    public static readonly DateTimeOffset End = Start.AddMinutes(5);

    public static void AssertMatches(IReadOnlyList<SegmentResponse> segments)
    {
        if (segments.Count != 1)
            throw new InvalidOperationException($"Expected exactly one Reference Segment; received {segments.Count}.");
        var item = segments[0];
        if (item.Id == Guid.Empty || item.DeviceId <= 0 || item.Source != Source || item.IdentityKey != IdentityKey
            || item.Title != Title || item.StartTime != Start || item.EndTime != End || item.DurationSeconds != 300)
            throw new InvalidOperationException("Reference Segment differs from the expected source, identity, title, time or duration; see expected.json and actual.json.");
    }
}

internal sealed class MainPathScenario(VerificationOptions options, VerificationLane lane,
    RunReport report, SecretRedactor redactor) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private HeadlessFleetOptions _sourceConfig = null!;
    private string _token = "";
    private string _owner = "";
    private string _username = "";
    private Guid _subjectId;
    private string _headlessConfig = "";

    public async Task LoadConfigurationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(options.ConfigPath))
            throw new DependencyBlockedException("Headless config is missing. Supply --config or prepare .local/heartbeat-headless.json as described in docs/development.md.");
        _sourceConfig = HeadlessFleetOptions.Load(options.ConfigPath);
        redactor.Add(_sourceConfig.ApiKey);
        if (string.IsNullOrWhiteSpace(_sourceConfig.ApiKey) || _sourceConfig.ApiKey.StartsWith("replace-"))
            throw new DependencyBlockedException("An online Auth API key is required in the Headless config.");
        _sourceConfig.Management.ValidateForVerification();
        _username = "verify-" + report.RunId;
        await report.WriteAsync("expected.json", new
        {
            ReferenceExpectation.Source, ReferenceExpectation.IdentityKey, ReferenceExpectation.Title,
            startTime = ReferenceExpectation.Start, endTime = ReferenceExpectation.End, durationSeconds = 300,
            count = 1
        });
    }

    public async Task AuthenticateAsync(CancellationToken token)
    {
        try
        {
            using var response = await new AuthServiceClient(_http)
                .ExchangeApiKeyAsync(new { apiKey = _sourceConfig.ApiKey }, token);
            if (!response.IsSuccessStatusCode)
                throw new DependencyBlockedException($"Online Auth API-key exchange returned HTTP {(int)response.StatusCode}.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            _token = json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new DependencyBlockedException("Online Auth returned no access token.");
            redactor.Add(_token);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(_token);
            _owner = jwt.Subject;
            if (string.IsNullOrWhiteSpace(_owner) || _owner != _sourceConfig.Management.OwnerSubject)
                throw new DependencyBlockedException("The API-key owner does not match management.ownerSubject in the supplied config.");
            // Parsing only supplies the fixture identity. Analytics validates the signed JWT normally.
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            && !token.IsCancellationRequested)
        {
            throw new DependencyBlockedException("Online Auth is unavailable or its request timed out.");
        }
    }

    public IReadOnlyDictionary<string, string?> AnalyticsAuth() => new Dictionary<string, string?>
    {
        ["AuthService__Authority"] = _sourceConfig.Management.Authority,
        ["AuthService__Issuer"] = Endpoints.AuthServiceBaseUrl,
        ["AuthService__Audience"] = Endpoints.AuthServiceBaseUrl,
        ["AuthService__OidcIssuer"] = _sourceConfig.Management.Issuer,
        ["AuthService__OidcClientId"] = _sourceConfig.Management.ClientId,
        ["AuthService__OidcAudience"] = _sourceConfig.Management.Audience ?? ""
    };

    public async Task PrepareAsync(CancellationToken token)
    {
        var reference = lane.Artifact("reference");
        var source = Path.Combine(lane.WorkPath, "package-source");
        await lane.CommandAsync("reference-package", reference.Executable,
            reference.Arguments.Concat(options.IsDesktop
                ? new[] { "--create-package", source, "--subject-kind", "machine" } : new[] { "--create-package", source }), token);
        var data = options.IsDesktop ? lane.DesktopData : Path.Combine(lane.WorkPath, "headless-data");
        _subjectId = options.IsDesktop ? await PrepareDesktopAsync(token) : Guid.CreateVersion7();
        var installation = new CollectorPackageInstallations(Path.Combine(data, "collector-packages")).Install(source);
        using (var runtime = CollectorRuntime.Open(Path.Combine(data, "collector-runtime.json"), new PreparationSink()))
        {
            var instance = runtime.CreateInstance(installation.Package,
                new SubjectReference(_subjectId, options.IsDesktop ? SubjectKind.Machine : SubjectKind.Account),
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })), "default");
            report.Evidence["collectorInstanceId"] = instance.CollectorInstanceId;
        }
        report.Evidence["subjectId"] = _subjectId;
        report.Evidence["packageHash"] = installation.Reference.PackageContentHash;
        Directory.Delete(source, true);

        await using var db = OpenDatabase();
        await new UserService(db).ProvisionAsync(_owner, _username);
        if (await db.ActivitySegments.AnyAsync(token))
            throw new InvalidOperationException("The new lane already contains Segments; refusing a possibly stale baseline.");

        if (!options.IsDesktop)
        {
            var config = new HeadlessFleetOptions
            {
                ApiKey = _sourceConfig.ApiKey, DataDirectory = data, Management = _sourceConfig.Management,
                UploadIntervalSeconds = 1, CollectorStartupTimeoutSeconds = 15, CollectorDrainGraceSeconds = 3,
                ListenUrl = lane.AllocateHeadlessUrl()
            };
            config.Validate();
            _headlessConfig = Path.Combine(lane.WorkPath, "headless.json");
            await File.WriteAllTextAsync(_headlessConfig, JsonSerializer.Serialize(config, RunReport.Json), token);
            VerificationLane.RestrictFile(_headlessConfig);
        }
        var baseline = await QueryAsync(token);
        if (baseline.Count != 0) throw new InvalidOperationException("The initial API query is not empty.");
        await report.WriteAsync("baseline.json", baseline);
    }

    private async Task<Guid> PrepareDesktopAsync(CancellationToken token)
    {
        Directory.CreateDirectory(lane.DesktopData);
        var configPath = Path.Combine(lane.DesktopData, "config.json");
        // Existing platform config fields; native optional permissions and shared loopback listener are off.
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            apiKey = "", deviceName = "Verification " + report.RunId, ingestPort = 0,
            uploadIntervalMinutes = 1, windowTitleObservationEnabled = false,
            windowActivityCollectionEnabled = false, interactionSignalEnabled = false, inputEventRecordingEnabled = false
        }, RunReport.Json), token);
        VerificationLane.RestrictFile(configPath);
        var smoke = Path.Combine(lane.WorkPath, "desktop-identity.json");
        var desktop = lane.Artifact("desktop");
        await lane.CommandAsync("desktop-identity", desktop.Executable, desktop.Arguments.Concat(new[]
        {
            "--verify-startup=" + smoke, "--verify-startup-data-directory=" + lane.DesktopData
        }), token, Path.GetDirectoryName(desktop.Path));
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(smoke, token));
        if (!identity.RootElement.GetProperty("hostStarted").GetBoolean()
            || !Guid.TryParse(identity.RootElement.GetProperty("hardwareId").GetString(), out var machineId)
            || machineId == Guid.Empty)
            throw new InvalidOperationException("Desktop did not report a valid real Machine identity.");
        var config = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(configPath, token))!;
        config["apiKey"] = _sourceConfig.ApiKey;
        await File.WriteAllTextAsync(configPath, config.ToJsonString(RunReport.Json), token);
        return machineId;
    }

    public Task StartAsync(CancellationToken token) => options.IsDesktop
        ? lane.StartDesktopAsync(token) : lane.StartHeadlessAsync(_headlessConfig, token);

    public async Task AssertDeliveryAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lane.ThrowIfExited();
            var actual = await QueryAsync(token);
            await report.WriteAsync("actual.json", actual);
            if (actual.Count != 0)
            {
                ReferenceExpectation.AssertMatches(actual);
                await using var db = OpenDatabase();
                var device = await db.Devices.AsNoTracking().SingleAsync(item => item.Id == actual[0].DeviceId, token);
                if (device.OwnerId != _owner || !(options.IsDesktop ? Guid.TryParse(device.HardwareId, out var machineId) && machineId == _subjectId
                    : device.HardwareId == $"subject:account:{_subjectId:D}"))
                    throw new InvalidOperationException("The delivered Segment belongs to an unexpected owner or Subject.");
                report.Evidence["segmentId"] = actual[0].Id;
                report.Evidence["deviceId"] = device.Id;
                report.Evidence["subjectMappingVerified"] = true;
                lane.ThrowIfExited();
                return;
            }
            await Task.Delay(250, token);
        }
    }

    private async Task<List<SegmentResponse>> QueryAsync(CancellationToken token)
    {
        lane.ThrowIfExited();
        var path = $"/api/v1/users/{Uri.EscapeDataString(_username)}/segments?source={ReferenceExpectation.Source}"
            + $"&start={Uri.EscapeDataString(ReferenceExpectation.Start.ToString("O"))}"
            + $"&end={Uri.EscapeDataString(ReferenceExpectation.End.ToString("O"))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(lane.AnalyticsUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, token);
        report.Evidence["lastQueryHttpStatus"] = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Analytics query returned HTTP {(int)response.StatusCode}; see analytics.log.");
        return await response.Content.ReadFromJsonAsync<List<SegmentResponse>>(cancellationToken: token)
            ?? throw new InvalidOperationException("Analytics returned null instead of a Segment list.");
    }

    private AppDbContext OpenDatabase() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(lane.ConnectionString).Options);

    public void Dispose() => _http.Dispose();

    private sealed class PreparationSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) =>
            throw new InvalidOperationException("Preparation must not produce or replay facts.");
    }
}

internal static class ManagementConfigurationValidation
{
    public static void ValidateForVerification(this HeadlessManagementOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OwnerSubject) || settings.OwnerSubject.StartsWith("replace-")
            || !Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https"
            || !Uri.TryCreate(settings.Issuer, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(settings.ClientId))
            throw new DependencyBlockedException("Headless management requires the online Auth authority, issuer, clientId and ownerSubject.");
    }
}
