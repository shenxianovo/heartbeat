using Heartbeat.Core;
using Heartbeat.Server.Calendar;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;

namespace Heartbeat.Server.Tests.Services;

[Collection("postgres")]
public class ReportServiceTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    private long _deviceId;
    private long _appId;

    protected override async Task SeedAsync(AppDbContext db)
    {
        var device = new Device { OwnerId = "user-1", HardwareId = "hw-1", DeviceName = "Test PC" };
        var app = new App { Name = "VSCode" };
        db.Devices.Add(device);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        _deviceId = device.Id;
        _appId = app.Id;
    }

    private ActivitySegment SystemSegment(DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        Source = ActivitySources.System,
        IdentityKey = SystemIdentity.Key("VSCode", null),
        AppId = _appId,
        StartTime = start,
        EndTime = end
    };

    [Fact]
    public async Task Reports_OverlappingOptionalCollector_DoesNotInflateSystemDuration()
    {
        using var db = CreateDbContext();
        var day = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
        var system = SystemSegment(day.AddHours(9), day.AddHours(10));
        var collector = SystemSegment(day.AddHours(9), day.AddHours(10));
        collector.Source = "reference.optional";
        collector.IdentityKey = "reference:activity";
        db.ActivitySegments.AddRange(system, collector);
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var daily = await service.GetDailyReportAsync("user-1", null, DayWindow(day, day.AddDays(1)));
        var weekly = await service.GetWeeklyReportAsync(
            "user-1", null, WeekWindow("2026-09-07", "UTC", day, day.AddDays(7)));

        Assert.Equal(3600, Assert.Single(daily.Report!.Apps).DurationSeconds);
        Assert.Equal(3600, Assert.Single(weekly.Report!.Apps).DurationSeconds);
    }

    [Fact]
    public async Task DailyReport_MidnightCrossingSegment_ClippedPerDay_NoDoubleCount()
    {
        using var db = CreateDbContext();
        var svc = new ReportService(db);

        // 跨午夜段（ADR-018 后长段不再被 flush 截断，跨窗成为常态，如整夜 away）
        var day1 = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        db.ActivitySegments.Add(SystemSegment(day1.AddHours(23), day2.AddHours(1))); // 23:00–次日 01:00
        db.ActivitySegments.Add(SystemSegment(day2.AddHours(10), day2.AddHours(11)));
        await db.SaveChangesAsync();

        var report1 = (await svc.GetDailyReportAsync("user-1", null, DayWindow(day1, day2))).Report!;
        var item1 = Assert.Single(report1.Apps);
        Assert.Equal(3600, item1.DurationSeconds); // 只计 23:00–24:00

        var report2 = (await svc.GetDailyReportAsync(
            "user-1", null, DayWindow(day2, day2.AddDays(1)))).Report!;
        var item2 = Assert.Single(report2.Apps);
        Assert.Equal(3600 + 3600, item2.DurationSeconds); // 00:00–01:00 + 10:00–11:00
    }

    [Fact]
    public async Task DailyReport_SegmentOutsideWindow_NotCounted()
    {
        using var db = CreateDbContext();
        var svc = new ReportService(db);

        var day = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        // 恰好在窗口边界结束/开始的段：半开窗口 [day, day+1)，零重叠不计
        db.ActivitySegments.Add(SystemSegment(day.AddHours(-2), day));
        db.ActivitySegments.Add(SystemSegment(day.AddDays(1), day.AddDays(1).AddHours(2)));
        await db.SaveChangesAsync();

        var report = (await svc.GetDailyReportAsync(
            "user-1", null, DayWindow(day, day.AddDays(1)))).Report!;
        Assert.Empty(report.Apps);
    }

    [Theory]
    [InlineData("2026-03-08", "America/New_York", "2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z", 23)]
    [InlineData("2026-08-29", "Asia/Shanghai", "2026-08-28T16:00:00Z", "2026-08-29T16:00:00Z", 24)]
    [InlineData("2026-11-01", "America/New_York", "2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z", 25)]
    public async Task DailyReport_ClipsBothHalfOpenEdges_ForVariableLengthCivilDays(
        string localDate, string timeZone, string startText, string endText, int expectedHours)
    {
        using var db = CreateDbContext();
        var start = DateTimeOffset.Parse(startText);
        var end = DateTimeOffset.Parse(endText);
        db.ActivitySegments.AddRange(
            SystemSegment(start.AddHours(-1), start.AddHours(1)),
            SystemSegment(end.AddHours(-1), end.AddHours(1)),
            SystemSegment(start.AddHours(-2), start),
            SystemSegment(end, end.AddHours(2)));
        await db.SaveChangesAsync();

        var result = await new ReportService(db).GetDailyReportAsync(
            "user-1", null, DayWindow(localDate, timeZone, start, end));

        var app = Assert.Single(result.Report!.Apps);
        Assert.Equal(2 * 60 * 60, app.DurationSeconds);
        Assert.Equal(expectedHours, (end - start).TotalHours);
    }

    [Fact]
    public async Task DailyReport_AllAndSingleDevice_UseTheSameResolvedWindow()
    {
        using var db = CreateDbContext();
        var secondDevice = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-2",
            DeviceName = "Test Mac",
        };
        db.Devices.Add(secondDevice);
        await db.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-03-08T05:00:00Z");
        var end = DateTimeOffset.Parse("2026-03-09T04:00:00Z");
        db.ActivitySegments.Add(SystemSegment(start, start.AddHours(1)));
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = secondDevice.Id,
            Source = ActivitySources.System,
            IdentityKey = SystemIdentity.Key("VSCode", null),
            AppId = _appId,
            StartTime = end.AddHours(-1),
            EndTime = end.AddHours(1),
        });
        await db.SaveChangesAsync();

        var envelope = DayWindow("2026-03-08", "America/New_York", start, end);
        var all = await new ReportService(db).GetDailyReportAsync("user-1", null, envelope);
        var single = await new ReportService(db).GetDailyReportAsync("user-1", _deviceId, envelope);

        Assert.Equal(2 * 60 * 60, Assert.Single(all.Report!.Apps).DurationSeconds);
        Assert.Equal(60 * 60, Assert.Single(single.Report!.Apps).DurationSeconds);
        Assert.Equal(all.Report.Date, single.Report.Date);
    }

    [Fact]
    public async Task DailyReport_InvalidEnvelope_ReturnsBeforeQueryingFacts()
    {
        var db = CreateDbContext();
        var service = new ReportService(db);
        await db.DisposeAsync();

        var result = await service.GetDailyReportAsync(
            "user-1", null, DayWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)) with
            {
                LocalDate = "not-a-date",
            });

        Assert.Null(result.Report);
        Assert.Equal("invalid_local_date", result.Error!.Code);
    }

    [Theory]
    [InlineData("2026-08-29", "Asia/Shanghai", "2026-08-23T16:00:00Z", "2026-08-30T16:00:00Z", 168)]
    [InlineData("2026-03-08", "America/New_York", "2026-03-02T05:00:00Z", "2026-03-09T04:00:00Z", 167)]
    [InlineData("2026-11-01", "America/New_York", "2026-10-26T04:00:00Z", "2026-11-02T05:00:00Z", 169)]
    public async Task WeeklyReport_ClipsBothHalfOpenEdges_ForVariableLengthCivilWeeks(
        string localDate, string timeZone, string startText, string endText, int expectedHours)
    {
        using var db = CreateDbContext();
        var start = DateTimeOffset.Parse(startText);
        var end = DateTimeOffset.Parse(endText);
        db.ActivitySegments.AddRange(
            SystemSegment(start.AddHours(-1), start.AddHours(1)),
            SystemSegment(end.AddHours(-1), end.AddHours(1)),
            SystemSegment(start.AddHours(-2), start),
            SystemSegment(end, end.AddHours(2)));
        await db.SaveChangesAsync();

        var result = await new ReportService(db).GetWeeklyReportAsync(
            "user-1", null, WeekWindow(localDate, timeZone, start, end));

        var app = Assert.Single(result.Report!.Apps);
        Assert.Equal(2 * 60 * 60, app.DurationSeconds);
        Assert.Equal(expectedHours, (end - start).TotalHours);
    }

    [Fact]
    public async Task WeeklyReport_AllAndSingleDevice_UseTheSameResolvedWindow()
    {
        using var db = CreateDbContext();
        var secondDevice = new Device
        {
            OwnerId = "user-1",
            HardwareId = "hw-2",
            DeviceName = "Test Mac",
        };
        db.Devices.Add(secondDevice);
        await db.SaveChangesAsync();

        var start = DateTimeOffset.Parse("2026-10-26T04:00:00Z");
        var end = DateTimeOffset.Parse("2026-11-02T05:00:00Z");
        db.ActivitySegments.Add(SystemSegment(start, start.AddHours(1)));
        db.ActivitySegments.Add(new ActivitySegment
        {
            Id = Guid.CreateVersion7(),
            DeviceId = secondDevice.Id,
            Source = ActivitySources.System,
            IdentityKey = SystemIdentity.Key("VSCode", null),
            AppId = _appId,
            StartTime = end.AddHours(-1),
            EndTime = end.AddHours(1),
        });
        await db.SaveChangesAsync();

        var envelope = WeekWindow("2026-11-01", "America/New_York", start, end);
        var all = await new ReportService(db).GetWeeklyReportAsync("user-1", null, envelope);
        var single = await new ReportService(db).GetWeeklyReportAsync("user-1", _deviceId, envelope);

        Assert.Equal(2 * 60 * 60, Assert.Single(all.Report!.Apps).DurationSeconds);
        Assert.Equal(60 * 60, Assert.Single(single.Report!.Apps).DurationSeconds);
        Assert.Equal("2026-10-26", all.Report.WeekStart);
        Assert.Equal("2026-11-01", all.Report.WeekEnd);
        Assert.Equal(all.Report.WeekStart, single.Report!.WeekStart);
        Assert.Equal(all.Report.WeekEnd, single.Report.WeekEnd);
    }

    [Fact]
    public async Task WeeklyReport_InvalidEnvelope_ReturnsBeforeQueryingFacts()
    {
        var db = CreateDbContext();
        var service = new ReportService(db);
        await db.DisposeAsync();

        var result = await service.GetWeeklyReportAsync(
            "user-1", null, WeekWindow(
                "2026-03-08",
                "America/New_York",
                DateTimeOffset.Parse("2026-03-02T05:00:00Z"),
                DateTimeOffset.Parse("2026-03-09T04:00:01Z")));

        Assert.Null(result.Report);
        Assert.Equal("calendar_rules_mismatch", result.Error!.Code);
    }

    private static LocalCalendarWindowEnvelope DayWindow(DateTimeOffset start, DateTimeOffset end) =>
        DayWindow(start.ToString("yyyy-MM-dd"), "UTC", start, end);

    private static LocalCalendarWindowEnvelope DayWindow(
        string localDate,
        string timeZone,
        DateTimeOffset start,
        DateTimeOffset end) => new()
    {
        Version = 1,
        Kind = "day",
        LocalDate = localDate,
        TimeZone = timeZone,
        Start = start,
        EndExclusive = end,
    };

    private static LocalCalendarWindowEnvelope WeekWindow(
        string localDate,
        string timeZone,
        DateTimeOffset start,
        DateTimeOffset end) => new()
    {
        Version = 1,
        Kind = "week",
        LocalDate = localDate,
        TimeZone = timeZone,
        Start = start,
        EndExclusive = end,
    };
}
