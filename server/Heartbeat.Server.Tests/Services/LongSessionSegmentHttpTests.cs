using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Segments;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Time;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Collector.System.Collection;
using Heartbeat.Collector.System.Configuration;
using Heartbeat.Collector.System.Input;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public sealed class LongSessionSegmentHttpTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SystemProtocolProjection_UploadsLongSessionAsContinuousValidChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"heartbeat-long-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new SharedClock(DateTimeOffset.UtcNow);
            await using var application = CreateApplication(clock);
            using var http = application.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
            http.DefaultRequestHeaders.Add(DeviceService.HardwareIdHeader, "long-session-device");
            http.DefaultRequestHeaders.Add(DeviceService.DeviceNameHeader, "Long Session Device");

            var sink = new SegmentIngestService(clock);
            var protocol = new SystemCollectorProtocolAdapter();
            var observations = new FakeObservations
            {
                CurrentActivity = new DesktopActivity("win:code", "Visual Studio Code", "main.cs")
            };
            var monitor = new AppMonitorService(
                clock,
                observations,
                new FakeInteractionSignal(),
                protocol,
                sink,
                new FakeSettings());
            using var collector = new SystemInProcessCollector(protocol, monitor);
            var package = LocalCollectorPackage.Load(SystemCollectorPackage.Path);
            using var config = JsonDocument.Parse("{}");
            await using var runtime = CollectorRuntime.Open(
                Path.Combine(root, "collector-runtime.json"),
                sink,
                inputEventSink: new DiscardingInputEventSink());
            var instance = runtime.CreateInstance(
                package,
                new SubjectReference(Guid.CreateVersion7(), SubjectKind.Machine),
                new CollectorInstanceSpec(1, 1, config.RootElement.Clone()));
            await using var activation = await runtime.ActivateInProcessAsync(
                instance.CollectorInstanceId,
                package,
                collector);

            var api = new HeartbeatApiClient(http);
            var upload = new UploadStream<ActivitySegmentItem>(
                "long system session",
                [sink],
                (batch, ct) => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct),
                new JsonDeadLetterStore<ActivitySegmentItem>(
                    Path.Combine(root, "segments-dead-letter.json")));

            clock.Advance(SegmentRotationPolicy.RotateAfter);
            monitor.PushCurrentSnapshot();
            await UploadProjectedAsync(sink, upload);

            clock.Advance(TimeSpan.FromHours(2));
            monitor.PushCurrentSnapshot();
            await UploadProjectedAsync(sink, upload);

            await activation.StopAsync();
            await UploadProjectedAsync(sink, upload);

            using var verification = CreateDbContext();
            var chunks = await verification.ActivitySegments
                .AsNoTracking()
                .OrderBy(segment => segment.StartTime)
                .ToListAsync();
            Assert.Equal(2, chunks.Count);
            Assert.Equal(chunks[0].EndTime, chunks[1].StartTime);
            Assert.All(chunks, chunk =>
                Assert.True(chunk.EndTime - chunk.StartTime <= SegmentValidationPolicy.MaxDuration));
            Assert.Equal(TimeSpan.FromHours(25), chunks.Aggregate(
                TimeSpan.Zero, (total, chunk) => total + (chunk.EndTime - chunk.StartTime)));
            Assert.Equal(0, upload.Status.DeadLetterCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task UploadProjectedAsync(
        SegmentIngestService sink,
        UploadStream<ActivitySegmentItem> upload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        List<ActivitySegmentItem> projected = [];
        while (projected.Count == 0)
        {
            projected = sink.ReadBatch();
            if (projected.Count == 0)
                await Task.Delay(10, timeout.Token);
        }
        await upload.DrainAsync();
    }

    private WebApplicationFactory<SegmentController> CreateApplication(TimeProvider clock) =>
        new WebApplicationFactory<SegmentController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
                services.AddSingleton(clock);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                        options.DefaultScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

    private sealed class SharedClock(DateTimeOffset now) : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeObservations : IDesktopObservationSource
    {
        public event Action<DesktopObservation>? Observation
        {
            add { }
            remove { }
        }
        public DesktopActivity CurrentActivity { get; set; } = DesktopActivity.None;
        public void Start() { }
        public void Stop() { }
    }

    private sealed class FakeInteractionSignal : IInputActivitySignal
    {
        public void MarkClick() { }
        public bool ClickedWithin(TimeSpan window) => false;
    }

    private sealed class FakeSettings : IDesktopSettings
    {
        public IReadOnlyList<string> AwayProcessNames => [];
        public bool SplitFocusedWindowChangesUnconditionally => true;
        public event Action<IReadOnlyList<string>>? AwayProcessNamesChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class DiscardingInputEventSink : IInputEventFactSink
    {
        public bool TryAccept(
            InputEventItem item,
            bool isReplay,
            ICollectorProjectionCommitFence commitFence) => !commitFence.IsFenced;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims =
            [
                new("sub", "user-1"),
                new("preferred_username", "alice")
            ];
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
