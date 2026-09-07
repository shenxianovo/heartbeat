using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heartbeat.Collection.Hub.Http;
using Heartbeat.Collection.Hub.Storage;
using Heartbeat.Collection.Hub.Upload;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Core.DTOs.Devices;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using Heartbeat.Server.Controllers;
using Heartbeat.Server.Data;
using Heartbeat.Server.Filters;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class StrictIngestProtocolTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void SegmentController_DelegatesAtomicIngestToOneApplicationService()
    {
        var parameters = Assert.Single(typeof(SegmentController).GetConstructors()).GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(AppDbContext));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(DeviceService));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(UsageService));
        Assert.Contains(parameters,
            parameter => parameter.ParameterType == typeof(ISegmentIngestApplicationService));
    }

    [Fact]
    public async Task AtomicIngest_ValidatesTheBatchAgainstOneCapturedNow()
    {
        await using var db = CreateDbContext();
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var time = new SequenceTimeProvider(now, now.AddDays(-2));
        var service = new SegmentIngestApplicationService(
            db,
            new DeviceService(db),
            new UsageService(db, timeProvider: time),
            time);
        var segment = StrictSystemSegment("win:code", now.AddMinutes(-1), now);

        await service.IngestAsync("owner", "hardware", "Device", [segment]);

        Assert.Equal(1, time.Reads);
        Assert.Equal(1, await db.ActivitySegments.CountAsync());
    }

    [Fact]
    public async Task AtomicIngest_AcceptsOfflineSegmentsOlderThanTheMaximumDuration()
    {
        await using var db = CreateDbContext();
        var service = new SegmentIngestApplicationService(db, new DeviceService(db), new UsageService(db));
        var start = DateTimeOffset.UtcNow.AddDays(-4);
        var segment = StrictSystemSegment("mac:com.apple.finder", start, start.AddMinutes(5));

        await service.IngestAsync("owner", "hardware", "Device", [segment]);

        var stored = await db.ActivitySegments.SingleAsync();
        Assert.Equal(segment.Id, stored.Id);
        Assert.Equal(segment.StartTime.ToUnixTimeSeconds(), stored.StartTime.ToUnixTimeSeconds());
        Assert.Equal(segment.EndTime.ToUnixTimeSeconds(), stored.EndTime.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AtomicIngest_EmptyBatchIsRejectedBeforeAnyProjectionSideEffects()
    {
        await using var db = CreateDbContext();
        var service = new SegmentIngestApplicationService(
            db,
            new DeviceService(db),
            new UsageService(db));

        var error = await Assert.ThrowsAsync<SegmentIngestContractException>(
            () => service.IngestAsync("owner", "hardware", "Device", []));

        Assert.Equal(SegmentIngestContractViolation.EmptyBatch, error.Violation);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_EmptyBatchDelegatesContractRejectionAndMaps400()
    {
        var ingest = new EmptyBatchRejectingIngestService();
        var controller = new SegmentController(ingest, new FakeCurrentUser("owner"));
        AttachHttpContext(controller, "hardware");

        var result = Assert.IsType<BadRequestObjectResult>(await controller.Upload(new SegmentUploadRequest()));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(1, ingest.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("garbage")]
    public async Task ProtocolFilter_MissingOrWrongVersion_ReturnsStable426BeforeEndpoint(string? version)
    {
        using var db = CreateDbContext();
        var httpContext = new DefaultHttpContext();
        if (version != null)
            httpContext.Request.Headers[HeartbeatProtocol.VersionHeader] = version;

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var executingContext = new ResourceExecutingContext(
            actionContext,
            [],
            []);
        var endpointCalled = false;

        await new RequireHeartbeatProtocolAttribute().OnResourceExecutionAsync(
            executingContext,
            () =>
            {
                endpointCalled = true;
                throw new InvalidOperationException("The endpoint must not run for an incompatible protocol.");
            });

        Assert.False(endpointCalled);
        var result = Assert.IsType<ObjectResult>(executingContext.Result);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", httpContext.Response.Headers.Upgrade);

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(HeartbeatProtocol.UpdateRequiredCode, payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(HeartbeatProtocol.RequiredVersion, payload.RootElement.GetProperty("requiredVersion").GetString());

        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task ProtocolFilter_RequiredVersion_AllowsEndpoint()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HeartbeatProtocol.VersionHeader] = HeartbeatProtocol.RequiredVersion;
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        var executingContext = new ResourceExecutingContext(actionContext, [], []);
        var endpointCalled = false;

        await new RequireHeartbeatProtocolAttribute().OnResourceExecutionAsync(
            executingContext,
            () =>
            {
                endpointCalled = true;
                return Task.FromResult(new ResourceExecutedContext(actionContext, []));
            });

        Assert.True(endpointCalled);
        Assert.Null(executingContext.Result);
    }

    [Fact]
    public async Task SegmentUpload_EmptyLegacyField_RejectsWholeMixedBatchWithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var request = new SegmentUploadRequest
        {
            Segments =
            [
                StrictSystemSegment("win:code", Now.AddMinutes(-4), Now.AddMinutes(-3)),
                new ActivitySegmentItem
                {
                    Id = Guid.CreateVersion7(),
                    Source = ActivitySources.System,
                    IdentityKey = "win:legacy\nLegacy",
                    AppIdentityKey = "win:legacy",
                    AppDisplayName = "Legacy",
                    AppName = "",
                    StartTime = Now.AddMinutes(-2),
                    EndTime = Now.AddMinutes(-1)
                }
            ]
        };

        var result = Assert.IsType<ObjectResult>(await controller.Upload(request));

        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", controller.Response.Headers.Upgrade);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task PresenceUpload_EmptyLegacyField_Returns426WithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateDeviceController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<ObjectResult>(await controller.Upload(new DeviceStatusRequest
        {
            CurrentAppIdentityKey = "win:code",
            CurrentAppDisplayName = "Visual Studio Code",
            CurrentApp = ""
        }));

        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", controller.Response.Headers.Upgrade);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task IconUpload_EmptyLegacyField_Returns426WithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateAppController(db, "user-1");

        var result = Assert.IsType<ObjectResult>(await controller.UploadIcon(new IconUploadRequest
        {
            AppIdentityKey = "win:code",
            AppDisplayName = "Visual Studio Code",
            AppName = "",
            IconData = [1, 2, 3]
        }));

        Assert.Equal(StatusCodes.Status426UpgradeRequired, result.StatusCode);
        Assert.Equal($"Heartbeat/{HeartbeatProtocol.RequiredVersion}", controller.Response.Headers.Upgrade);
        await AssertNoIngestFactsAsync(db);
        Assert.Empty(await db.AppIcons.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SegmentUpload_MalformedV2SystemIdentity_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var segment = StrictSystemSegment("sys:", Now.AddMinutes(-2), Now.AddMinutes(-1));

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(new SegmentUploadRequest
        {
            Segments = [segment]
        }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_MissingV2SystemIdentity_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var segment = StrictSystemSegment("win:code", Now.AddMinutes(-2), Now.AddMinutes(-1));
        segment.AppIdentityKey = null;

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(new SegmentUploadRequest
        {
            Segments = [segment]
        }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_InvalidTime_RejectsWholeMixedBatchWithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var invalid = StrictSystemSegment("win:poison", default, Now.AddMinutes(-1));

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new SegmentUploadRequest
            {
                Segments =
                [
                    StrictSystemSegment("win:code", Now.AddMinutes(-4), Now.AddMinutes(-3)),
                    invalid
                ]
            }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("source")]
    [InlineData("identity")]
    public async Task SegmentUpload_MissingCoreIdentity_Returns422WithoutSideEffects(string missing)
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var invalid = StrictSystemSegment("win:code", Now.AddMinutes(-2), Now.AddMinutes(-1));
        if (missing == "id") invalid.Id = Guid.Empty;
        if (missing == "source") invalid.Source = " ";
        if (missing == "identity") invalid.IdentityKey = " ";

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new SegmentUploadRequest { Segments = [invalid] }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_ExistingIdIdentityConflict_RejectsWholeBatchWithoutNewFacts()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var existing = StrictSystemSegment("win:code", Now.AddMinutes(-8), Now.AddMinutes(-6));
        Assert.IsType<OkResult>(await controller.Upload(new SegmentUploadRequest { Segments = [existing] }));

        var conflict = StrictSystemSegment("win:poison", Now.AddMinutes(-8), Now.AddMinutes(-5));
        conflict.Id = existing.Id;
        var otherwiseValid = StrictSystemSegment(
            "win:new-provisional", Now.AddMinutes(-4), Now.AddMinutes(-3));

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new SegmentUploadRequest { Segments = [otherwiseValid, conflict] }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal(existing.Id, Assert.Single(await db.ActivitySegments.AsNoTracking().ToListAsync()).Id);
        Assert.Equal("win:code", Assert.Single(await db.AppIdentities.AsNoTracking().ToListAsync()).Key);
        Assert.Single(await db.Apps.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SegmentUpload_ExistingIdDeviceConflict_DoesNotCreateRequestDevice()
    {
        using var db = CreateDbContext();
        var first = CreateSegmentController(db, "user-1", "first-hardware");
        var existing = StrictSystemSegment("win:code", Now.AddMinutes(-8), Now.AddMinutes(-6));
        Assert.IsType<OkResult>(await first.Upload(new SegmentUploadRequest { Segments = [existing] }));

        var second = CreateSegmentController(db, "user-1", "new-conflicting-hardware");
        var conflict = StrictSystemSegment("win:code", Now.AddMinutes(-8), Now.AddMinutes(-5));
        conflict.Id = existing.Id;

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await second.Upload(
            new SegmentUploadRequest { Segments = [conflict] }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal("first-hardware", Assert.Single(
            await db.Devices.AsNoTracking().ToListAsync()).HardwareId);
        Assert.Equal(existing.Id, Assert.Single(
            await db.ActivitySegments.AsNoTracking().ToListAsync()).Id);
    }

    [Fact]
    public async Task RealHttpUploadStream_SplitsStrictBatch_AndDurablyIsolatesPoisonFact()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), $"heartbeat-strict-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await using var application = CreateApplication();
            using var client = application.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
            client.DefaultRequestHeaders.Add(DeviceService.HardwareIdHeader, "strict-http-device");
            client.DefaultRequestHeaders.Add(DeviceService.DeviceNameHeader, "Strict HTTP Device");

            var validA = StrictSystemSegment(
                "win:code", Now.AddMinutes(-8), Now.AddMinutes(-7));
            var poison = StrictSystemSegment(
                "win:poison", Now.AddMinutes(-6), Now.AddMinutes(-5));
            poison.Id = Guid.Empty;
            var validB = StrictSystemSegment(
                "win:notepad", Now.AddMinutes(-4), Now.AddMinutes(-3));

            var cachePath = Path.Combine(tempDirectory, "segments.json");
            var deadLetterPath = Path.Combine(tempDirectory, "segments-dead-letter.json");
            var cache = new JsonFileCache<ActivitySegmentItem>(
                cachePath,
                20_000,
                HeartbeatCacheFormats.SegmentVersion2(),
                HeartbeatCacheFormats.SegmentMigrations());
            cache.Add([validA, poison, validB]);

            var api = new HeartbeatApiClient(client);
            var stream = new UploadStream<ActivitySegmentItem>(
                "segments",
                [new Heartbeat.Collection.Hub.Segments.SegmentIngestService(new Heartbeat.Collection.Hub.Time.SystemClock(), cache)],
                (batch, ct) => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }, ct),
                new JsonDeadLetterStore<ActivitySegmentItem>(deadLetterPath));

            await stream.DrainAsync();

            Assert.Empty(cache.Load());
            Assert.Equal(1, stream.Status.DeadLetterCount);
            using (var deadLetters = JsonDocument.Parse(await File.ReadAllTextAsync(deadLetterPath)))
            {
                var entry = Assert.Single(
                    deadLetters.RootElement.GetProperty("entries").EnumerateArray());
                Assert.Equal(StatusCodes.Status422UnprocessableEntity,
                    entry.GetProperty("statusCode").GetInt32());
                Assert.Equal(Guid.Empty, entry.GetProperty("item").GetProperty("id").GetGuid());
            }

            using var verification = CreateDbContext();
            var storedIds = await verification.ActivitySegments
                .AsNoTracking()
                .Select(segment => segment.Id)
                .ToListAsync();
            Assert.Equal(2, storedIds.Count);
            Assert.Contains(validA.Id, storedIds);
            Assert.Contains(validB.Id, storedIds);
            Assert.DoesNotContain(await verification.AppIdentities.AsNoTracking().ToListAsync(),
                identity => identity.Key == "win:poison");

            var restartedDeadLetters = new JsonDeadLetterStore<ActivitySegmentItem>(deadLetterPath);
            Assert.Equal(1, restartedDeadLetters.Count);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PresenceUpload_DisplayHintWithoutIdentity_Returns400BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateDeviceController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<BadRequestObjectResult>(await controller.Upload(new DeviceStatusRequest
        {
            CurrentAppDisplayName = "Visual Studio Code"
        }));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task IconUpload_MissingIdentity_Returns400WithoutSideEffects()
    {
        using var db = CreateDbContext();
        var controller = CreateAppController(db, "user-1");

        var result = Assert.IsType<BadRequestObjectResult>(await controller.UploadIcon(new IconUploadRequest
        {
            AppDisplayName = "Visual Studio Code",
            IconData = [1, 2, 3]
        }));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
        Assert.Empty(await db.AppIcons.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InputUpload_MissingCodeSet_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateInputController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new InputEventUploadRequest
            {
                Events =
                [
                    new InputEventItem
                    {
                        Id = Guid.CreateVersion7(),
                        EventType = InputEventType.KeyDown,
                        Code = 65,
                        Timestamp = Now
                    }
                ]
            }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task InputUpload_UnknownCodeSet_Returns422BeforeDeviceResolution()
    {
        using var db = CreateDbContext();
        var controller = CreateInputController(db, "user-1", "shared-hardware");

        var result = Assert.IsType<UnprocessableEntityObjectResult>(await controller.Upload(
            new InputEventUploadRequest
            {
                Events =
                [
                    new InputEventItem
                    {
                        Id = Guid.CreateVersion7(),
                        EventType = InputEventType.KeyDown,
                        CodeSet = "future-v9",
                        Code = 4,
                        Timestamp = Now
                    }
                ]
            }));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        await AssertNoIngestFactsAsync(db);
    }

    [Fact]
    public async Task SegmentUpload_UnknownValidIdentity_CreatesProvisionalAppForResolvedOwnerDevice()
    {
        using var db = CreateDbContext();
        var controller = CreateSegmentController(db, "user-1", "shared-hardware");
        var segment = StrictSystemSegment("mac:com.example.focus", Now.AddMinutes(-2), Now.AddMinutes(-1));
        segment.AppDisplayName = "Focus";

        var result = await controller.Upload(new SegmentUploadRequest { Segments = [segment] });

        Assert.IsType<OkResult>(result);
        var device = await db.Devices.AsNoTracking().SingleAsync();
        Assert.Equal("user-1", device.OwnerId);
        Assert.Equal("shared-hardware", device.HardwareId);

        var identity = await db.AppIdentities.Include(x => x.App).AsNoTracking().SingleAsync();
        Assert.Equal("mac:com.example.focus", identity.Key);
        Assert.True(identity.App.IsProvisional);
        Assert.Equal("Focus", identity.App.DisplayName);

        var stored = await db.ActivitySegments.AsNoTracking().SingleAsync();
        Assert.Equal(device.Id, stored.DeviceId);
        Assert.Equal(identity.Id, stored.AppIdentityId);
    }

    [Fact]
    public async Task SegmentUpload_SameHardwareForDifferentOwners_RemainsDeviceIsolated()
    {
        using var db = CreateDbContext();
        var first = CreateSegmentController(db, "user-1", "shared-hardware");
        var second = CreateSegmentController(db, "user-2", "shared-hardware");

        var firstSegment = StrictSystemSegment("win:code", Now.AddMinutes(-4), Now.AddMinutes(-3));
        var secondSegment = StrictSystemSegment("win:code", Now.AddMinutes(-2), Now.AddMinutes(-1));

        Assert.IsType<OkResult>(await first.Upload(new SegmentUploadRequest { Segments = [firstSegment] }));
        Assert.IsType<OkResult>(await second.Upload(new SegmentUploadRequest { Segments = [secondSegment] }));

        var devices = await db.Devices.AsNoTracking().OrderBy(x => x.OwnerId).ToListAsync();
        Assert.Equal(2, devices.Count);
        Assert.Equal(["user-1", "user-2"], devices.Select(x => x.OwnerId).ToArray());
        Assert.All(devices, x => Assert.Equal("shared-hardware", x.HardwareId));

        var segments = await db.ActivitySegments.AsNoTracking().ToListAsync();
        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, x => x.Id == firstSegment.Id && x.DeviceId == devices[0].Id);
        Assert.Contains(segments, x => x.Id == secondSegment.Id && x.DeviceId == devices[1].Id);
    }

    private static ActivitySegmentItem StrictSystemSegment(
        string identityKey,
        DateTimeOffset start,
        DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        Source = ActivitySources.System,
        IdentityKey = identityKey + "\nWindow",
        AppIdentityKey = identityKey,
        AppDisplayName = "Window",
        StartTime = start,
        EndTime = end
    };

    private static SegmentController CreateSegmentController(
        AppDbContext db,
        string userId,
        string hardwareId)
    {
        var ingestService = new SegmentIngestApplicationService(
            db,
            new DeviceService(db),
            new UsageService(db));
        var controller = new SegmentController(
            ingestService,
            new FakeCurrentUser(userId));
        AttachHttpContext(controller, hardwareId);
        return controller;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override DateTimeOffset GetUtcNow()
        {
            var index = Interlocked.Increment(ref _reads) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }

    private static DeviceController CreateDeviceController(
        AppDbContext db,
        string userId,
        string hardwareId)
    {
        var controller = new DeviceController(new DeviceService(db), new FakeCurrentUser(userId));
        AttachHttpContext(controller, hardwareId);
        return controller;
    }

    private static AppController CreateAppController(AppDbContext db, string userId)
    {
        var controller = new AppController(new AppService(db), new FakeCurrentUser(userId));
        AttachHttpContext(controller);
        return controller;
    }

    private static InputEventController CreateInputController(
        AppDbContext db,
        string userId,
        string hardwareId)
    {
        var controller = new InputEventController(
            new InputEventService(db),
            new DeviceService(db),
            new FakeCurrentUser(userId));
        AttachHttpContext(controller, hardwareId);
        return controller;
    }

    private static void AttachHttpContext(ControllerBase controller, string? hardwareId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[HeartbeatProtocol.VersionHeader] = HeartbeatProtocol.RequiredVersion;
        if (hardwareId != null)
            context.Request.Headers[DeviceService.HardwareIdHeader] = hardwareId;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private static async Task AssertNoIngestFactsAsync(AppDbContext db)
    {
        Assert.Empty(await db.Devices.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Apps.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AppIdentities.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ActivitySegments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.InputEvents.AsNoTracking().ToListAsync());
    }

    private WebApplicationFactory<SegmentController> CreateApplication() =>
        new WebApplicationFactory<SegmentController>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseNpgsql(TestConnectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                        options.DefaultScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            });
        });

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

    private sealed class FakeCurrentUser(string userId) : ICurrentUserService
    {
        public string GetUserId() => userId;
        public string? GetUserIdOrNull() => userId;
        public string? GetUsernameOrNull() => null;
    }

    private sealed class EmptyBatchRejectingIngestService : ISegmentIngestApplicationService
    {
        public int Calls { get; private set; }

        public Task IngestAsync(
            string ownerId,
            string hardwareId,
            string? deviceName,
            List<ActivitySegmentItem> segments)
        {
            Calls++;
            throw new SegmentIngestContractException(
                SegmentIngestContractViolation.EmptyBatch,
                "Segments cannot be empty.");
        }
    }

}
