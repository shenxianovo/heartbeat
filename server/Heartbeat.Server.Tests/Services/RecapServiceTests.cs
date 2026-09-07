using System.Runtime.CompilerServices;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Core.DTOs.Recaps;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class RecapServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _appId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "vscode" };
        db.Devices.Add(device);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _appId = app.Id;
    }

    /// <summary>
    /// 假生成器（ADR-042 §8 的流式接口）：一次生成吐若干块，拼起来才是完整叙事。默认分块拼出
    /// "narrative-N"，让"拼接全文"与"第几次生成"同时可断言。
    ///
    /// 失败分两种形状，因为它们要防的东西不同：首块之前失败（什么都没吐）与若干块之后失败
    /// （半截叙事已经在路上）——后者才是"失败不覆盖上次成功正文"真正的考题。
    ///
    /// 时间维度也得能摆，因为判死线的语义就在时间上（ADR-042 §9）：<see cref="Gap"/> 把块拉开，
    /// <see cref="SilentForever"/> 一块都不吐，<see cref="ReasoningForever"/> 只思考不落笔。
    /// </summary>
    private sealed class FakeGenerator : IRecapGenerator
    {
        public int Calls;
        public string? LastDigest;

        /// <summary>吐满这么多块后抛失败；0 = 首块之前就失败，null = 不失败。</summary>
        public int? FailAfterChunks;

        /// <summary>覆盖默认分块（全是正文块）。</summary>
        public string[]? Chunks;

        /// <summary>显式脚本：分型与顺序都由测试指定，优先于 <see cref="Chunks"/>。</summary>
        public LlmChunk[]? Script;

        /// <summary>每块之前的等待。默认 0——多数测试要的正是"MoveNextAsync 同步落地"。</summary>
        public TimeSpan Gap;

        /// <summary>一块都不吐，挂到被取消为止：真静默。</summary>
        public bool SilentForever;

        /// <summary>只吐思考、永远不落笔：思考失控，用来撞整段上限。</summary>
        public bool ReasoningForever;

        public string Model => "fake-model";
        public string PromptHash => "deadbeef";

        public async IAsyncEnumerable<LlmChunk> GenerateStreamAsync(
            string digest, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var call = ++Calls;
            LastDigest = digest;

            // Gap = 0 时唯一的 await 已完成，MoveNextAsync 因此同步落地。那些测试要断言的是取消与
            // 落库的先后，不是线程调度——真流的异步性由 ChatCompletionClient 那层负责。
            await Task.CompletedTask;

            if (FailAfterChunks == 0) throw new RecapGenerationException("upstream down");

            if (SilentForever)
            {
                // 真流静默时也正是这样：卡在读流上，直到调用方的时限取消它。
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield break;
            }

            // while 而不是 if + 无限循环：后者会把下面的正文脚本变成不可达代码。
            while (ReasoningForever)
            {
                if (Gap > TimeSpan.Zero) await Task.Delay(Gap, ct);
                yield return LlmChunk.OfReasoning("想…");
            }

            var contents = Chunks ?? ["narrative-", $"{call}"];
            LlmChunk[] script = Script ?? [.. contents.Select(LlmChunk.OfContent)];
            for (var i = 0; i < script.Length; i++)
            {
                if (Gap > TimeSpan.Zero) await Task.Delay(Gap, ct);
                yield return script[i];
                if (FailAfterChunks == i + 1) throw new RecapGenerationException("upstream down");
            }
        }
    }

    private sealed class GatedCancellationGenerator : IRecapGenerator
    {
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CleanupFinished { get; private set; }
        public string Model => "fake-model";
        public string PromptHash => "deadbeef";

        public async IAsyncEnumerable<LlmChunk> GenerateStreamAsync(
            string digest, [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                CancellationObserved.TrySetResult();
                await AllowCleanup.Task;
                CleanupFinished = true;
            }
            yield break;
        }
    }

    private ActivitySegment SystemSegment(DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = "vscode|",
        AppId = _appId,
        StartTime = start,
        EndTime = end
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>已结束的历史日窗口（UTC 昨天再往前一天，避开"今天"的水位路径）。</summary>
    private static readonly DateTimeOffset PastDay =
        new(DateTimeOffset.UtcNow.Date.AddDays(-2), TimeSpan.Zero);

    /// <summary>"今天"场景的固定时钟：窗口 2026-07-08（UTC），now 定在正午——窗口未结束，走水位路径。</summary>
    private static readonly DateTimeOffset FixedDay = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixedNoon = FixedDay.AddHours(12);

    private static ResolvedCalendarWindow DayWindow(DateTimeOffset day) => new(
        1,
        "day",
        day.ToString("yyyy-MM-dd"),
        "Etc/UTC",
        new DateTimeOffset(day.UtcDateTime.Date, TimeSpan.Zero),
        new DateTimeOffset(day.UtcDateTime.Date.AddDays(1), TimeSpan.Zero),
        NodaTime.LocalDate.FromDateTime(day.UtcDateTime.Date),
        NodaTime.LocalDate.FromDateTime(day.UtcDateTime.Date.AddDays(1)));

    private static ResolvedCalendarWindow DayWindow(
        string localDate, string timeZone, string start, string endExclusive)
    {
        var validation = LocalCalendarWindowValidator.ResolveDay(new LocalCalendarWindowEnvelope
        {
            Version = 1,
            Kind = "day",
            LocalDate = localDate,
            TimeZone = timeZone,
            Start = DateTimeOffset.Parse(start),
            EndExclusive = DateTimeOffset.Parse(endExclusive),
        });
        Assert.Null(validation.Error);
        return validation.Window!;
    }

    /// <summary>抽干一条生成流，事件按到达顺序返回。</summary>
    private static async Task<List<RecapStreamEvent>> DrainAsync(
        RecapService svc, DateTimeOffset date, CancellationToken ct = default)
    {
        var events = new List<RecapStreamEvent>();
        await foreach (var e in svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(date), ct))
            events.Add(e);
        return events;
    }

    /// <summary>生成一次并要求成功，返回 done 里的 DTO（旧测试的 force:true 在新形状下的等价物）。</summary>
    private static async Task<DailyRecapResponse> GenerateAsync(RecapService svc, DateTimeOffset date)
    {
        var events = await DrainAsync(svc, date);
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.ErrorType);
        return events.Single(e => e.Type == RecapStreamEvent.DoneType).Recap!;
    }

    private static async Task<DailyRecapResponse> GenerateAsync(
        RecapService svc, ResolvedCalendarWindow window)
    {
        var events = new List<RecapStreamEvent>();
        await foreach (var e in svc.GenerateDailyRecapStreamAsync("user-1", window))
            events.Add(e);
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.ErrorType);
        return events.Single(e => e.Type == RecapStreamEvent.DoneType).Recap!;
    }

    private static List<string> DeltasOf(List<RecapStreamEvent> events) =>
        [.. events.Where(e => e.Type == RecapStreamEvent.DeltaType).Select(e => e.Delta!)];

    private static List<string> ThinkingsOf(List<RecapStreamEvent> events) =>
        [.. events.Where(e => e.Type == RecapStreamEvent.ThinkingType).Select(e => e.Thinking!)];

    /// <summary>毫秒级时限：把分钟级的真实时限压到测试跑得动的尺度，语义一模一样。</summary>
    private static RecapStreamTimeouts Timeouts(int silenceMs, int overallMs, int heartbeatMs) =>
        new(TimeSpan.FromMilliseconds(silenceMs),
            TimeSpan.FromMilliseconds(overallMs),
            TimeSpan.FromMilliseconds(heartbeatMs));

    // ===== 读路径：零 LLM、零写库（ADR-042 §2）=====

    [Fact]
    public async Task LegacyFixedOffsetRow_MissesWindowKey_ThenGenerationPersistsFullWindowIdentity()
    {
        using var db = CreateDbContext();
        var window = DayWindow(PastDay);
        db.ActivitySegments.Add(SystemSegment(window.Start.AddHours(9), window.Start.AddHours(11)));
        db.Recaps.Add(new Recap
        {
            OwnerId = "user-1",
            WindowStart = window.Start,
            Narrative = "legacy narrative",
            GeneratedAt = window.Start,
        });
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var miss = await svc.GetDailyRecapAsync("user-1", window);
        Assert.Null(miss.Narrative);

        await GenerateAsync(svc, window);
        var sameBoundsOtherZone = DayWindow(
            window.LocalDate,
            "Africa/Abidjan",
            window.Start.ToString("O"),
            window.EndExclusive.ToString("O"));
        await GenerateAsync(svc, sameBoundsOtherZone);

        var rows = await db.Recaps.OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].WindowKey);
        Assert.Equal(window.WindowKey.Value, rows[1].WindowKey);
        Assert.Equal(window.Version, rows[1].WindowVersion);
        Assert.Equal(window.Kind, rows[1].WindowKind);
        Assert.Equal(window.LocalDate, rows[1].LocalDate);
        Assert.Equal(window.TimeZone, rows[1].TimeZone);
        Assert.Equal(window.Start, rows[1].WindowStart);
        Assert.Equal(window.EndExclusive, rows[1].WindowEndExclusive);
        Assert.Equal(sameBoundsOtherZone.WindowKey.Value, rows[2].WindowKey);
        Assert.NotEqual(rows[1].WindowKey, rows[2].WindowKey);
    }

    [Theory]
    [InlineData("2026-02-15", "Asia/Shanghai", "2026-02-14T16:00:00Z", "2026-02-15T16:00:00Z", 24)]
    [InlineData("2026-03-08", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z", 23)]
    [InlineData("2026-11-01", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z", 25)]
    public async Task GenerationProjectionAndWatermarkUseTheExactResolvedDayBounds(
        string localDate, string timeZone, string start, string endExclusive, int durationHours)
    {
        using var db = CreateDbContext();
        var window = DayWindow(localDate, timeZone, start, endExclusive);
        db.ActivitySegments.Add(SystemSegment(window.Start.AddHours(-1), window.EndExclusive.AddHours(1)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        await GenerateAsync(svc, window);

        Assert.Contains($"{durationHours}小时00分", fake.LastDigest);
        Assert.Equal(window.EndExclusive, (await db.Recaps.SingleAsync()).SegmentWatermark);
    }

    [Fact]
    public async Task SpringForwardProjectionFormatsPostTransitionActivityWithCivilTimezoneRules()
    {
        using var db = CreateDbContext();
        var window = DayWindow(
            "2026-03-08",
            "America/New_York",
            "2026-03-08T05:00:00Z",
            "2026-03-09T04:00:00Z");
        db.ActivitySegments.Add(SystemSegment(
            DateTimeOffset.Parse("2026-03-08T13:00:00Z"),
            DateTimeOffset.Parse("2026-03-08T14:00:00Z")));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        await GenerateAsync(svc, window);

        Assert.Contains("09:00–10:00 vscode", fake.LastDigest);
        Assert.DoesNotContain("08:00–09:00 vscode", fake.LastDigest);
    }

    [Fact]
    public async Task EmptyDay_Read_IsEmpty_NoLlmCall_NothingCached()
    {
        using var db = CreateDbContext();
        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.True(result.IsEmpty);
        Assert.Null(result.Narrative);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await db.Recaps.ToListAsync());
    }

    [Fact]
    public async Task NeverGenerated_NonEmptyDay_ReadsAsNotGenerated_NoLlmCall()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        // 三态的中间那一态：有数据、没叙事。读取不再顺手生成（旧行为的反向断言）。
        Assert.False(result.IsEmpty);
        Assert.Null(result.Narrative);
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await db.Recaps.ToListAsync());
    }

    [Fact]
    public async Task Generated_ThenRead_ServedFromCache()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var generated = await GenerateAsync(svc, PastDay);
        var read = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.Equal("narrative-1", generated.Narrative);
        Assert.Equal("narrative-1", read.Narrative);
        Assert.Equal(1, fake.Calls);

        var row = Assert.Single(await db.Recaps.ToListAsync());
        Assert.Equal("fake-model", row.Model);
        Assert.Equal("deadbeef", row.PromptHash);
        Assert.Equal(PastDay.AddHours(11), row.SegmentWatermark);
    }

    [Fact]
    public async Task Regenerate_OverwritesCache_NotASecondRow()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        await GenerateAsync(svc, PastDay);
        var regenerated = await GenerateAsync(svc, PastDay);

        Assert.Equal("narrative-2", regenerated.Narrative);
        Assert.Equal(2, fake.Calls);
        Assert.Single(await db.Recaps.ToListAsync()); // upsert，不是第二行
    }

    [Fact]
    public async Task Today_FreshWatermark_NotSegmentStale()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(8), FixedDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db), new FixedClock(FixedNoon));

        await GenerateAsync(svc, FixedNoon);
        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(FixedNoon));

        Assert.False(result.SegmentStale); // 无新数据，水位未落后
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Today_StaleWatermark_HintsOnly_DoesNotRegenerate()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(8), FixedDay.AddHours(9)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db), new FixedClock(FixedNoon));
        await GenerateAsync(svc, FixedNoon); // 水位 = 09:00

        // 新段到达：最新段尾 11:30 距缓存水位 09:00 有 2.5h > 1h 阈值
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(10.5), FixedDay.AddHours(11.5)));
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(FixedNoon));

        // 旧行为（水位落后即自动重生成）的反向断言：只提示，不烧 token，正文原样。
        Assert.True(result.SegmentStale);
        Assert.Equal("narrative-1", result.Narrative);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task HistoricalDay_CacheHit_NeverSegmentStale_KnowledgeStaleStillComputed()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        var strand = HittingStrand();
        db.Strands.Add(strand);
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        // 迟到的段落进已结束的窗口：段层面永不过期，判脏只剩知识那把尺。
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(20), PastDay.AddHours(22)));
        strand.Gloss = "改名后的项目";
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.False(result.SegmentStale);
        Assert.True(result.KnowledgeStale);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task CachedOnly_MissingRecap_ReturnsNullWithoutGeneration()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var result = await svc.GetCachedDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.Null(result);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task CachedOnly_ExistingRecap_ReturnsItWithoutRegeneration()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        var result = await svc.GetCachedDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.NotNull(result);
        Assert.Equal("narrative-1", result.Narrative);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task OtherOwnersSegments_NotVisible()
    {
        using var db = CreateDbContext();
        var otherDevice = new Device { OwnerId = "user-2", HardwareId = "hw-2", DeviceName = "Other PC" };
        db.Devices.Add(otherDevice);
        await db.SaveChangesAsync();
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = otherDevice.Id,
            Source = ActivitySources.System,
            IdentityKey = "vscode|",
            AppId = _appId,
            StartTime = PastDay.AddHours(9),
            EndTime = PastDay.AddHours(11)
        });
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.True(result.IsEmpty); // user-2 的数据对 user-1 不可见
        Assert.Equal(0, fake.Calls);
    }

    // ===== 生成路径：事件流、落库与取消（ADR-042 §4/§6）=====

    [Fact]
    public async Task Generate_ThreeChunks_DeltasThenDone_CachesJoinedNarrative()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator { Chunks = ["第一段。", "第二段。", "第三段。"] };
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var events = await DrainAsync(svc, PastDay);

        Assert.Equal(["第一段。", "第二段。", "第三段。"], DeltasOf(events));
        var done = Assert.Single(events, e => e.Type == RecapStreamEvent.DoneType);
        Assert.Equal("第一段。第二段。第三段。", done.Recap!.Narrative);
        Assert.Equal("第一段。第二段。第三段。", (await db.Recaps.SingleAsync()).Narrative);
    }

    [Fact]
    public async Task Generate_FailsBeforeFirstChunk_ErrorEvent_CacheUntouched()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        fake.FailAfterChunks = 0;
        var events = await DrainAsync(svc, PastDay);

        Assert.Empty(DeltasOf(events));
        Assert.Equal("upstream down", Assert.Single(events, e => e.Type == RecapStreamEvent.ErrorType).Message);
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.DoneType);

        // EF 变更追踪可能残留失败尝试的状态，重开上下文验证持久化事实。
        using var verify = CreateDbContext();
        Assert.Equal("narrative-1", (await verify.Recaps.SingleAsync()).Narrative);
    }

    [Fact]
    public async Task Generate_FailsAfterSecondChunk_ErrorEvent_CacheUntouched()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        // 半截叙事已经吐给了前端，落库仍然一票不投：上次成功的正文比半成品值钱。
        fake.Chunks = ["半截", "叙事", "永远到不了"];
        fake.FailAfterChunks = 2;
        var events = await DrainAsync(svc, PastDay);

        Assert.Equal(["半截", "叙事"], DeltasOf(events));
        Assert.Single(events, e => e.Type == RecapStreamEvent.ErrorType);
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.DoneType);

        using var verify = CreateDbContext();
        Assert.Equal("narrative-1", (await verify.Recaps.SingleAsync()).Narrative);
    }

    [Fact]
    public async Task Generate_ConsumerBreaksMidStream_NothingCached()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator { Chunks = ["一", "二", "三"] };
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var seen = new List<string>();
        await foreach (var e in svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(PastDay)))
        {
            if (e.Type == RecapStreamEvent.DeltaType) seen.Add(e.Delta!);
            if (seen.Count == 2) break; // 客户端关页面
        }

        Assert.Equal(["一", "二"], seen);

        // 选迭代器的决定性理由：消费者 break 之后，落库那一步在结构上根本执行不到。
        using var verify = CreateDbContext();
        Assert.Empty(await verify.Recaps.ToListAsync());
    }

    [Fact]
    public async Task Generate_CompletedThenRequestCancelled_StillPersists()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator { Chunks = ["钱", "花完了"] };
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        using var cts = new CancellationTokenSource();
        var events = new List<RecapStreamEvent>();
        var deltas = 0;
        await foreach (var e in svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(PastDay), cts.Token))
        {
            events.Add(e);
            // 最后一块已经到手之后请求才断：ADR-042 §6 的核心——货已经买到了就存下来，
            // 落库走独立 CancellationToken，不透传请求的 ct。
            if (e.Type == RecapStreamEvent.DeltaType && ++deltas == 2) await cts.CancelAsync();
        }

        Assert.Single(events, e => e.Type == RecapStreamEvent.DoneType);

        using var verify = CreateDbContext();
        Assert.Equal("钱花完了", (await verify.Recaps.SingleAsync()).Narrative);
    }

    [Fact]
    public async Task Generate_EmptyDay_NoLlmCall_DoneSaysEmpty_NothingCached()
    {
        using var db = CreateDbContext();
        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var events = await DrainAsync(svc, PastDay);

        var done = Assert.Single(events, e => e.Type == RecapStreamEvent.DoneType);
        Assert.True(done.Recap!.IsEmpty);
        Assert.Empty(DeltasOf(events));
        Assert.Equal(0, fake.Calls);
        Assert.Empty(await db.Recaps.ToListAsync());
    }

    // ---- 知识投影惰性判脏（ADR-031 §7）----

    /// <summary>命中当日观测的 Strand（vscode 段 → app equal vscode）。</summary>
    private Strand HittingStrand(string name = "Heartbeat", string gloss = "自部署监控") => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerId = "user-1",
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        Gloss = gloss,
        Version = 1,
        CreatedAt = PastDay,
        UpdatedAt = PastDay,
        Members =
        [
            new StrandMatcher
            {
                Id = Guid.CreateVersion7(),
                Source = ActivitySources.System,
                StepsJson = """[{"Reading":"app","Op":"equals","Value":"vscode"}]""",
            }
        ],
    };

    [Fact]
    public async Task HistoricalRead_RelevantKnowledgeChanged_StaleHintWithoutLlm()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        var strand = HittingStrand();
        db.Strands.Add(strand);
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);
        Assert.False((await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay))).KnowledgeStale);

        // 相关知识变化：编辑 Gloss（叙事相关字段）
        strand.Gloss = "改名后的项目";
        await db.SaveChangesAsync();

        var stale = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.True(stale.KnowledgeStale); // 只提示
        Assert.Equal("narrative-1", stale.Narrative); // 正文仍是缓存
        Assert.Equal(1, fake.Calls); // 读取不调 LLM
    }

    [Fact]
    public async Task HistoricalRead_UnrelatedKnowledgeChange_NotStale()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        // 新增一条当日观测命不中的 Strand：与该日无关
        db.Strands.Add(new Strand
        {
            Id = Guid.CreateVersion7(),
            OwnerId = "user-1",
            Name = "无关项目",
            NormalizedName = "无关项目",
            Gloss = "",
            Version = 1,
            CreatedAt = PastDay,
            UpdatedAt = PastDay,
            Members =
            [
                new StrandMatcher
                {
                    Id = Guid.CreateVersion7(),
                    Source = ActivitySources.System,
                    StepsJson = """[{"Reading":"app","Op":"equals","Value":"never.exe"}]""",
                }
            ],
        });
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.False(result.KnowledgeStale); // 精确到相关知识，不是全局版本
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task PreKnowledgeHashRecap_WithWindowKey_LazilyStale_NoBackfill()
    {
        using var db = CreateDbContext();
        var window = DayWindow(PastDay);
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        db.Recaps.Add(new Recap
        {
            OwnerId = "user-1",
            WindowKey = window.WindowKey.Value,
            WindowVersion = window.Version,
            WindowKind = window.Kind,
            LocalDate = window.LocalDate,
            TimeZone = window.TimeZone,
            WindowStart = window.Start,
            WindowEndExclusive = window.EndExclusive,
            Narrative = "旧配方写的",
            GeneratedAt = PastDay,
            KnowledgeHash = null, // 投影引入前的旧行
        });
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var result = await svc.GetDailyRecapAsync("user-1", window);

        Assert.True(result.KnowledgeStale); // 惰性视为可重新生成
        Assert.Equal("旧配方写的", result.Narrative);
        Assert.Equal(0, fake.Calls); // 不自动回填、不调 LLM
        Assert.Null((await db.Recaps.SingleAsync()).KnowledgeHash); // 读取不写库
    }

    [Fact]
    public async Task Regenerate_WritesNewHash_ClearsStale()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        var strand = HittingStrand();
        db.Strands.Add(strand);
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        strand.Gloss = "新的理解";
        await db.SaveChangesAsync();
        Assert.True((await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay))).KnowledgeStale);

        var regenerated = await GenerateAsync(svc, PastDay);

        Assert.False(regenerated.KnowledgeStale);
        Assert.Equal("narrative-2", regenerated.Narrative);
        Assert.False((await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay))).KnowledgeStale);
    }

    [Fact]
    public async Task RegenerateFailure_KeepsLastGoodNarrativeAndHash()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);
        var goodHash = (await db.Recaps.AsNoTracking().SingleAsync()).KnowledgeHash;

        fake.FailAfterChunks = 0;
        var events = await DrainAsync(svc, PastDay);
        Assert.Single(events, e => e.Type == RecapStreamEvent.ErrorType);

        // EF 变更追踪可能残留失败尝试的状态，重开上下文验证持久化事实。
        using var verify = CreateDbContext();
        var row = await verify.Recaps.SingleAsync();
        Assert.Equal("narrative-1", row.Narrative); // 失败不覆盖上次成功正文
        Assert.Equal(goodHash, row.KnowledgeHash); // 也不覆盖投影标识
    }

    [Fact]
    public async Task RecurrenceProbe_NeverEntersProjection_NoStale()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            OwnerId = "user-1",
            LocalDate = DateOnly.FromDateTime(PastDay.Date),
            Text = "第一次出现的行为",
            Version = 1,
            CreatedAt = PastDay,
            UpdatedAt = PastDay,
        };
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        // Probe 新增（谓词命中当日观测也一样）：知识投影结构上不含 Probe——不判脏
        db.RecurrenceProbes.Add(new RecurrenceProbe
        {
            Id = Guid.CreateVersion7(),
            OwnerId = "user-1",
            EpisodeId = episode.Id,
            Source = ActivitySources.System,
            StepsJson = """[{"Reading":"app","Op":"equals","Value":"vscode"}]""",
            Status = ProbeStatuses.Active,
            CreatedAt = PastDay,
        });
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.False(result.KnowledgeStale);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task DayEpisode_EntersDigest_AndChangesHash()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        // 用户确认当天事实（经 EpisodeService 之外直接落库模拟已确认状态）
        db.Episodes.Add(new Episode
        {
            Id = Guid.CreateVersion7(),
            OwnerId = "user-1",
            LocalDate = DateOnly.FromDateTime(PastDay.Date),
            Text = "面了一场模拟面试",
            Version = 1,
            CreatedAt = PastDay,
            UpdatedAt = PastDay,
        });
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.True(result.KnowledgeStale); // Episode 新增 → 相关知识变化
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Today_SegmentStaleHint_IndependentOfKnowledge()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(8), FixedDay.AddHours(9)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db), new FixedClock(FixedNoon));
        await GenerateAsync(svc, FixedNoon);

        // 段水位落后与知识判脏是两把独立的尺，且现在形状一致：都只提示。
        db.ActivitySegments.Add(SystemSegment(FixedDay.AddHours(10.5), FixedDay.AddHours(11.5)));
        await db.SaveChangesAsync();

        var result = await svc.GetDailyRecapAsync("user-1", DayWindow(FixedNoon));

        Assert.True(result.SegmentStale);
        Assert.False(result.KnowledgeStale);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task PublicRead_CacheOnly_NeverStaleHint_NoKnowledgeAccess()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        var strand = HittingStrand();
        db.Strands.Add(strand);
        await db.SaveChangesAsync();

        var fake = new FakeGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db));
        await GenerateAsync(svc, PastDay);

        // 知识已变化，但公开路径不重算投影
        strand.Gloss = "变了";
        await db.SaveChangesAsync();

        var result = await svc.GetCachedDailyRecapAsync("user-1", DayWindow(PastDay));

        Assert.NotNull(result);
        Assert.False(result.KnowledgeStale); // 纯缓存：不暴露知识投影细节
        Assert.False(result.SegmentStale); // 两个判脏位在公开路径恒 false
        Assert.Equal(1, fake.Calls); // 不触发生成
    }

    // ===== 判死线：上游静默，而不是首个正文 token（ADR-042 §9）=====

    [Fact]
    public async Task Generate_LongReasoningBeforeFirstContent_NotJudgedSilent()
    {
        // 本次线上 bug 的回归测试：8/7 的 digest 让思考期长达 175s，那段时间每秒好几帧，但
        // delta.content 全是空串。旧的"首个正文 token 判死线"因此把一条活得很好的流判成超时。
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        // 每块间隔 80ms < 静默线 250ms，但思考总时长 400ms > 静默线：只有"整段思考"超线，
        // 帧间隔一次都没超——这正是真实那次调用的形状。
        var fake = new FakeGenerator
        {
            Gap = TimeSpan.FromMilliseconds(80),
            Script =
            [
                LlmChunk.OfReasoning("想一"),
                LlmChunk.OfReasoning("想二"),
                LlmChunk.OfReasoning("想三"),
                LlmChunk.OfReasoning("想四"),
                LlmChunk.OfReasoning("想五"),
                LlmChunk.OfContent("那一天，"),
                LlmChunk.OfContent("你写了代码。")
            ]
        };
        var svc = new RecapService(db, fake, new DigestAssembler(db), null, Timeouts(250, 20_000, 10_000));

        var events = await DrainAsync(svc, PastDay);

        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.ErrorType);
        Assert.Equal(5, ThinkingsOf(events).Count);
        Assert.Equal(["那一天，", "你写了代码。"], DeltasOf(events));
        var done = Assert.Single(events, e => e.Type == RecapStreamEvent.DoneType);
        Assert.Equal("那一天，你写了代码。", done.Recap!.Narrative);

        // 思考不进叙事：落库的正文里一个"想"字都不该有。
        using var verify = CreateDbContext();
        Assert.Equal("那一天，你写了代码。", (await verify.Recaps.SingleAsync()).Narrative);
    }

    [Fact]
    public async Task Generate_UpstreamTotallySilent_SilenceError_NothingCached()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator { SilentForever = true };
        var svc = new RecapService(db, fake, new DigestAssembler(db), null, Timeouts(200, 20_000, 10_000));

        var events = await DrainAsync(svc, PastDay);

        var error = Assert.Single(events, e => e.Type == RecapStreamEvent.ErrorType);
        Assert.Contains("没有任何响应", error.Message!);
        Assert.Contains("0.2 秒", error.Message!); // 秒数由注入的时限算出，不是硬编码
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.DoneType);

        using var verify = CreateDbContext();
        Assert.Empty(await verify.Recaps.ToListAsync());
    }

    [Fact]
    public async Task Generate_ThinksForeverWithoutContent_OverallTimeoutError_NothingCached()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        // 帧一直来（静默线永远不到期），但整段上限必须兜住：思考也是钱，不能无限思考。
        var fake = new FakeGenerator { ReasoningForever = true, Gap = TimeSpan.FromMilliseconds(20) };
        var svc = new RecapService(db, fake, new DigestAssembler(db), null, Timeouts(5_000, 300, 10_000));

        var events = await DrainAsync(svc, PastDay);

        var error = Assert.Single(events, e => e.Type == RecapStreamEvent.ErrorType);
        Assert.Contains("超过", error.Message!);
        Assert.Contains("完整叙事", error.Message!);
        Assert.DoesNotContain("没有任何响应", error.Message!); // 两种病症两句话（Q5）
        Assert.DoesNotContain(events, e => e.Type == RecapStreamEvent.DoneType);

        using var verify = CreateDbContext();
        Assert.Empty(await verify.Recaps.ToListAsync());
    }

    [Fact]
    public async Task Generate_ReasoningChunks_EmitThinkingEvents_NeverEnterNarrative()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator
        {
            Script =
            [
                LlmChunk.OfReasoning("先盘一下这天"),
                LlmChunk.OfContent("那一天，"),
                LlmChunk.OfReasoning("再想想收尾"),
                LlmChunk.OfContent("你写了代码。")
            ]
        };
        var svc = new RecapService(db, fake, new DigestAssembler(db));

        var events = await DrainAsync(svc, PastDay);

        Assert.Equal(["先盘一下这天", "再想想收尾"], ThinkingsOf(events));
        Assert.Equal(["那一天，", "你写了代码。"], DeltasOf(events));
        // thinking 事件只带 thinking 字段：前端按 event 名分派，Delta 必须为 null 才不会被误拼进正文。
        Assert.All(events.Where(e => e.Type == RecapStreamEvent.ThinkingType), e => Assert.Null(e.Delta));

        using var verify = CreateDbContext();
        Assert.Equal("那一天，你写了代码。", (await verify.Recaps.SingleAsync()).Narrative);
    }

    [Fact]
    public async Task Generate_ClientDisconnect_BeatsBothTimeouts_NoErrorEvent_NothingCached()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new GatedCancellationGenerator();
        var svc = new RecapService(db, fake, new DigestAssembler(db), null,
            new RecapStreamTimeouts(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(1)));
        using var cts = new CancellationTokenSource();
        await using var stream = svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(PastDay), cts.Token)
            .GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(RecapStreamEvent.PingType, stream.Current.Type);

        // 心跳证明 MoveNext 已在途；取消后的清理由 barrier 控制，不与线程调度或 EF 冷启动赛跑。
        await cts.CancelAsync();
        await fake.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var next = stream.MoveNextAsync().AsTask();
        try
        {
            Assert.False(next.IsCompleted, "Stream must await upstream cleanup before disposing its iterator.");
        }
        finally
        {
            fake.AllowCleanup.TrySetResult();
        }

        // 客户端断开会连带取消 overall（它 link 在 ct 上），于是"谁被取消了"同时成立两条。判定
        // 顺序错了就会给一条已经没人听的流发 error，还把断开写成一句超时。
        Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(fake.CleanupFinished);

        using var verify = CreateDbContext();
        Assert.Empty(await verify.Recaps.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Generate_Timeout_WaitsForUpstreamCleanupBeforeDisposing(bool silenceTimeout)
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();
        var fake = new GatedCancellationGenerator();
        var deadline = TimeSpan.FromMilliseconds(1);
        var svc = new RecapService(db, fake, new DigestAssembler(db), null, new RecapStreamTimeouts(
            silenceTimeout ? deadline : Timeout.InfiniteTimeSpan,
            silenceTimeout ? Timeout.InfiniteTimeSpan : deadline,
            Timeout.InfiniteTimeSpan));
        await using var stream = svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(PastDay)).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(RecapStreamEvent.ErrorType, stream.Current.Type);
        Assert.Contains(silenceTimeout ? "没有任何响应" : "完整叙事", stream.Current.Message!);
        await fake.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var dispose = stream.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted, "Timeout must retain ownership of the in-flight MoveNext.");
        }
        finally
        {
            fake.AllowCleanup.TrySetResult();
        }
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fake.CleanupFinished);
        using var verify = CreateDbContext();
        Assert.Empty(await verify.Recaps.ToListAsync());
    }

    [Fact]
    public void Di_ResolvesRecapService_DespiteOptionalConstructorParameters()
    {
        // 时钟与三个时限都是可选构造参数，DI 一个都不注册。AddScoped 走 ActivatorUtilities，它对
        // "解析不出来但有默认值"的参数是宽容的——可这份宽容是运行期的事，编译期看不出来，所以拿
        // 一条断言当"真实启动不炸"的最小复现（生成端点是 scoped 解析，炸也只炸在用户点下去那一刻）。
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Database=never-connected"));
        services.AddScoped<DigestAssembler>();
        services.AddScoped<IRecapGenerator>(_ => new FakeGenerator());
        services.AddScoped<RecapService>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RecapService>());
    }

    [Fact]
    public async Task Generate_WhileWaitingForChunks_KeepsPinging()
    {
        using var db = CreateDbContext();
        db.ActivitySegments.Add(SystemSegment(PastDay.AddHours(9), PastDay.AddHours(11)));
        await db.SaveChangesAsync();

        var fake = new FakeGenerator { SilentForever = true };
        var svc = new RecapService(db, fake, new DigestAssembler(db), null, Timeouts(20_000, 30_000, 60));

        var pings = 0;
        await foreach (var e in svc.GenerateDailyRecapStreamAsync("user-1", DayWindow(PastDay)))
        {
            Assert.NotEqual(RecapStreamEvent.ErrorType, e.Type);
            // 等到第二个心跳才罢手：心跳必须是周期性的，而不是"只发一次然后忙循环/静默"。
            if (e.Type == RecapStreamEvent.PingType && ++pings == 2) break;
        }

        Assert.Equal(2, pings);

        using var verify = CreateDbContext();
        Assert.Empty(await verify.Recaps.ToListAsync());
    }
}
