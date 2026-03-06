using Heartbeat.Core.DTOs.Reports;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    public class ReportService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        public async Task<DailyReportResponse> GetDailyReportAsync(long? deviceId, DateTimeOffset date)
        {
            var dayStart = new DateTimeOffset(date.Date, date.Offset).UtcDateTime;
            var dayEnd = dayStart.AddDays(1);

            var query = _db.AppUsages
                .Where(x => x.StartTime >= dayStart && x.StartTime < dayEnd);

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            var apps = await query
                .GroupBy(x => x.AppId)
                .Select(g => new AppDurationItem
                {
                    AppId = g.Key,
                    DurationSeconds = g.Sum(x => x.DurationSeconds)
                })
                .OrderByDescending(x => x.DurationSeconds)
                .ToListAsync();

            return new DailyReportResponse
            {
                Date = date.Date.ToString("yyyy-MM-dd"),
                TotalSeconds = apps.Sum(a => a.DurationSeconds),
                Apps = apps
            };
        }

        public async Task<WeeklyReportResponse> GetWeeklyReportAsync(long? deviceId, DateTimeOffset date)
        {
            var d = date.Date;
            var dayOfWeek = d.DayOfWeek;
            var mondayOffset = dayOfWeek == DayOfWeek.Sunday ? -6 : -(int)dayOfWeek + 1;
            var monday = d.AddDays(mondayOffset);
            var sundayEnd = monday.AddDays(7);

            var weekStart = new DateTimeOffset(monday, date.Offset).UtcDateTime;
            var weekEnd = new DateTimeOffset(sundayEnd, date.Offset).UtcDateTime;

            var query = _db.AppUsages
                .Where(x => x.StartTime >= weekStart && x.StartTime < weekEnd);

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            var apps = await query
                .GroupBy(x => x.AppId)
                .Select(g => new AppDurationItem
                {
                    AppId = g.Key,
                    DurationSeconds = g.Sum(x => x.DurationSeconds)
                })
                .OrderByDescending(x => x.DurationSeconds)
                .ToListAsync();

            return new WeeklyReportResponse
            {
                WeekStart = monday.ToString("yyyy-MM-dd"),
                WeekEnd = monday.AddDays(6).ToString("yyyy-MM-dd"),
                TotalSeconds = apps.Sum(a => a.DurationSeconds),
                Apps = apps
            };
        }
    }
}
