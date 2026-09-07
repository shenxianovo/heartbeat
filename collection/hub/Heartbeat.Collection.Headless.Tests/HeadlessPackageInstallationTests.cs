using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Core.DTOs.Segments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Heartbeat.Collection.Hub.Hosting;

namespace Heartbeat.Collection.Headless.Tests;

public sealed class HeadlessPackageInstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"heartbeat-headless-runtime-owner-{Guid.NewGuid():N}");

    [Fact]
    public async Task Host_ExposesSharedManagementAndClosesAdmissionBeforeStopping()
    {
        var options = Fleet(Path.Combine(_root, "host"));
        var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddHeadlessCollectors(options);
        }).Build();
        try
        {
            await host.StartAsync();
            var management = host.Services.GetRequiredService<ICollectorMarketplace>();
            Assert.False(management is IDisposable or IAsyncDisposable);
            await host.StopAsync();
            Assert.Throws<InvalidOperationException>(() => management.Install("reference"));
        }
        finally { await ((IAsyncDisposable)host).DisposeAsync(); }
    }

    [Fact]
    public async Task Restart_UsesRuntimeInstanceAndExactInstallationWithoutPackageSourceOrWeb()
    {
        var source = await CreateSourcePackageAsync();
        var data = Path.Combine(_root, "data");
        var installation = new CollectorPackageInstallations(Path.Combine(data, "collector-packages"))
            .Install(source);
        Guid instanceId;
        var subject = new SubjectReference(Guid.CreateVersion7(), SubjectKind.Account);
        using (var runtime = CollectorRuntime.Open(
                   Path.Combine(data, "collector-runtime.json"),
                   new NullSegmentSink()))
        {
            instanceId = runtime.CreateInstance(
                installation.Package,
                subject,
                new CollectorInstanceSpec(1, 1, JsonSerializer.SerializeToElement(new { })),
                "default").CollectorInstanceId;
        }
        Directory.Delete(source, recursive: true);

        var host = BuildHost(data);
        await host.StartAsync();
        var manager = host.Services.GetRequiredService<ICollectorMarketplace>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var status = Assert.Single(manager.InstalledSnapshot());
                if (status.Phase == CollectorMarketplaceRuntimePhase.Running)
                {
                    Assert.NotNull(status.InstalledVersion);
                    Assert.Equal("heartbeat.collector.reference-managed", status.PackageId);
                    Assert.Equal("Reference Managed Collector", status.DisplayName);
                    break;
                }
                Assert.NotEqual(CollectorMarketplaceRuntimePhase.Failed, status.Phase);
                await Task.Delay(20, timeout.Token);
            }
            Assert.False(File.Exists(Path.Combine(data, "headless-instance-map.json")));
            var restored = Assert.Single(host.Services.GetRequiredService<CollectorRuntime>().ListInstances());
            Assert.Equal(instanceId, restored.CollectorInstanceId);
            Assert.Equal(subject, restored.Subject);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await host.StopAsync(timeout.Token);
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    [Fact]
    public async Task InstallAndUninstall_GenericMarketplaceOwnsTheCompleteDefaultInstanceLifecycle()
    {
        var source = await CreateSourcePackageAsync();
        var data = Path.Combine(_root, "lifecycle-data");
        var sourcePackage = LocalCollectorPackage.Load(source);
        using var registry = new PackageRegistryHandler(source);
        var host = BuildHost(data, registry);
        await host.StartAsync();
        var manager = host.Services.GetRequiredService<ICollectorMarketplace>();
        try
        {
            await manager.WaitForSuccessAsync(manager.Install(sourcePackage.Manifest.PackageId));
            var installed = Assert.Single(manager.InstalledSnapshot());
            Assert.NotNull(installed.InstalledVersion);
            Assert.NotNull(installed.CollectorInstanceId);
            Assert.Single(manager.InstalledSnapshot());
            Assert.Single(new CollectorPackageInstallations(
                Path.Combine(data, "collector-packages")).List());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.WaitForSuccessAsync(manager.Uninstall(sourcePackage.Manifest.PackageId), timeout.Token);

            Assert.Empty(manager.InstalledSnapshot());
            Assert.Empty(new CollectorPackageInstallations(
                Path.Combine(data, "collector-packages")).List());
            Assert.False(Directory.Exists(Path.Combine(
                data, "instances", installed.CollectorInstanceId!.Value.ToString("D"))));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await host.StopAsync(timeout.Token);
            await ((IAsyncDisposable)host).DisposeAsync();
        }

        using var runtime = CollectorRuntime.Open(
            Path.Combine(data, "collector-runtime.json"),
            new NullSegmentSink());
        Assert.Empty(runtime.ListInstances());
    }

    [Fact]
    public async Task Http_ReconnectQueriesAndExplicitlyCancelsAnAcceptedDownload()
    {
        var source = await CreateSourcePackageAsync();
        using var registry = new PackageRegistryHandler(source) { HoldDownload = true };
        await using var app = BuildWebHost(registry);
        await app.StartAsync();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        var packageId = LocalCollectorPackage.Load(source).Manifest.PackageId;
        Guid operationId;
        using (var client = OwnerClient(app))
        {
            using var response = await client.PostAsync($"/hub/api/v1/collectors/{packageId}/installation", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var operation = (await response.Content.ReadFromJsonAsync<HostManagementOperation>(json))!;
            operationId = operation.OperationId;
            Assert.Equal($"/hub/api/v1/operations/{operationId:D}", response.Headers.Location!.OriginalString);
        }
        await registry.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var reconnected = OwnerClient(app);
        var snapshot = await reconnected.GetFromJsonAsync<HostManagementOperation[]>("/hub/api/v1/operations", json);
        Assert.Equal(operationId, Assert.Single(snapshot!).OperationId);
        using var cancellation = await reconnected.PostAsync($"/hub/api/v1/operations/{operationId:D}/cancellation", null);
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Equal(HostManagementOperationCancellation.Accepted,
            await cancellation.Content.ReadFromJsonAsync<HostManagementOperationCancellation>(json));
        var result = await app.Services.GetRequiredService<ICollectorMarketplace>()
            .WaitForOperationAsync(operationId).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var queried = await reconnected.GetFromJsonAsync<HostManagementOperation>(
            $"/hub/api/v1/operations/{operationId:D}", json);
        Assert.Equal(HostManagementOperationPhase.Cancelled, result.Phase);
        Assert.Equal(result, queried);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("another-owner", "heartbeat-web")]
    [InlineData("owner-1", "another-client")]
    public async Task Http_ManagementRequiresTheConfiguredOwnerAndClient(string? subject, string? clientId)
    {
        using var registry = new PackageRegistryHandler(await CreateSourcePackageAsync());
        await using var app = BuildWebHost(registry);
        await app.StartAsync();
        using var client = OwnerClient(app, subject ?? "owner-1", clientId ?? "heartbeat-web");
        if (subject is null) client.DefaultRequestHeaders.Authorization = null;
        var id = Guid.CreateVersion7();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "collectors"),
            (HttpMethod.Get, "operations"),
            (HttpMethod.Get, $"operations/{id:D}"),
            (HttpMethod.Post, $"operations/{id:D}/cancellation"),
            (HttpMethod.Post, "collectors/reference/installation"),
            (HttpMethod.Post, $"collector-instances/{id:D}/authorization/{id:D}")
        })
        {
            using var request = new HttpRequestMessage(method, "/hub/api/v1/" + path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Empty(app.Services.GetRequiredService<ICollectorMarketplace>().OperationsSnapshot());
        await app.StopAsync();
    }

    private static readonly SymmetricSecurityKey SigningKey = new(new byte[32]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32
    });

    private WebApplication BuildWebHost(PackageRegistryHandler registry)
    {
        var options = Fleet(Path.Combine(_root, "web"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHeadlessCollectors(options);
        builder.Services.AddHeadlessManagement(options.Management);
        builder.Services.AddHttpClient("CollectorMarketplace").ConfigurePrimaryHttpMessageHandler(() => registry);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, authentication =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = options.Management.Issuer };
            configuration.SigningKeys.Add(SigningKey);
            authentication.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHeadlessManagement();
        return app;
    }

    private static HttpClient OwnerClient(WebApplication app, string subject = "owner-1", string clientId = "heartbeat-web")
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://auth.example.test/", TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["client_id"] = clientId },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HeadlessFleetOptions Fleet(string dataDirectory) => new()
    {
        ApiKey = "test-key",
        DataDirectory = dataDirectory,
        UploadIntervalSeconds = 3600,
        CollectorRegistryUrl = "https://registry.example.invalid/v1/",
        CollectorDrainGraceSeconds = 2,
        Management = new HeadlessManagementOptions
        {
            OwnerSubject = "owner-1",
            Authority = "https://auth.example.test",
            Issuer = "https://auth.example.test/",
            ClientId = "heartbeat-web"
        }
    };

    private async Task<string> CreateSourcePackageAsync()
    {
        var packageDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(packageDirectory);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ManagedReferenceCollector");
        var executable = Path.Combine(
            fixture,
            OperatingSystem.IsWindows()
                ? "Heartbeat.Collector.Reference.ManagedProcess.exe"
                : "Heartbeat.Collector.Reference.ManagedProcess");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            ArgumentList = { "--create-package", packageDirectory }
        }) ?? throw new InvalidOperationException("Failed to start reference Package builder.");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return packageDirectory;
    }

    private sealed class NullSegmentSink : ISegmentSink
    {
        public void Push(List<ActivitySegmentItem> snapshots) { }
    }

    private IHost BuildHost(string data, HttpMessageHandler? registry = null) =>
        new HostBuilder().ConfigureServices(services =>
        {
            services.AddHeadlessCollectors(Fleet(data));
            if (registry is not null)
                services.AddHttpClient("CollectorMarketplace").ConfigurePrimaryHttpMessageHandler(() => registry);
        }).Build();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
