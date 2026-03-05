using Microsoft.EntityFrameworkCore;
using Heartbeat.Server.Data;
using Heartbeat.Server.Entities;
using Heartbeat.Core.DTOs;

namespace Heartbeat.Server.Services
{
    public class UsageService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

        /// <summary>
        /// 合并容差：同设备同应用首尾相连在此范围内的记录合并（处理客户端上传截断）
        /// </summary>
        private static readonly TimeSpan MergeTolerance = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 时间校验容差：客户端时间与服务端时间偏差不得超过此值
        /// </summary>
        private static readonly TimeSpan TimeSkewTolerance = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 单条记录最大时长限制
        /// </summary>
        private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

        public async Task SaveUsageAsync(long deviceId, UsageUploadRequest request)
        {
            var now = DateTimeOffset.UtcNow;

            var validUsages = request.Usages
                .Where(u => !string.IsNullOrEmpty(u.AppName)
                         && u.StartTime != default
                         && u.EndTime > u.StartTime
                         && u.StartTime.Year >= 2020
                         && u.EndTime <= now + TimeSkewTolerance
                         && u.StartTime >= now - TimeSkewTolerance - MaxDuration
                         && (u.EndTime - u.StartTime) <= MaxDuration)
                .OrderBy(u => u.StartTime)
                .ToList();

            // 获取或创建 App 记录
            var appNames = validUsages.Select(u => u.AppName).Distinct().ToList();
            var existingApps = await _db.Apps
                .Where(a => appNames.Contains(a.Name))
                .ToDictionaryAsync(a => a.Name);

            foreach (var name in appNames)
            {
                if (!existingApps.ContainsKey(name))
                {
                    var app = new App { Name = name };
                    _db.Apps.Add(app);
                    existingApps[name] = app;
                }
            }
            await _db.SaveChangesAsync(); // 保存以获取新 App 的 Id

            // 按 AppName 分组，对每组尝试与数据库中最新记录合并
            foreach (var group in validUsages.GroupBy(u => u.AppName))
            {
                var appId = existingApps[group.Key].Id;

                // 查找该设备+应用的最新记录
                var existing = await _db.AppUsages
                    .Where(x => x.DeviceId == deviceId && x.AppId == appId)
                    .OrderByDescending(x => x.EndTime)
                    .FirstOrDefaultAsync();

                foreach (var u in group)
                {
                    if (existing != null
                        && u.StartTime >= existing.EndTime
                        && u.StartTime <= existing.EndTime + MergeTolerance)
                    {
                        // 首尾相连，合并（扩展结束时间）
                        existing.EndTime = u.EndTime;
                        existing.DurationSeconds = (int)(existing.EndTime - existing.StartTime).TotalSeconds;
                    }
                    else
                    {
                        // 插入新记录
                        existing = new AppUsage
                        {
                            DeviceId = deviceId,
                            AppId = appId,
                            StartTime = u.StartTime,
                            EndTime = u.EndTime,
                            DurationSeconds = (int)(u.EndTime - u.StartTime).TotalSeconds
                        };
                        _db.AppUsages.Add(existing);
                    }
                }
            }

            await _db.SaveChangesAsync();
        }

        public async Task<List<AppUsageResponse>> GetUsageAsync(long? deviceId, DateTimeOffset? start, DateTimeOffset? end)
        {
            var query = _db.AppUsages
                .Include(x => x.App)
                .AsQueryable();

            if (deviceId.HasValue)
                query = query.Where(x => x.DeviceId == deviceId.Value);

            if (start.HasValue)
                query = query.Where(x => x.StartTime >= start.Value);

            if (end.HasValue)
                query = query.Where(x => x.StartTime < end.Value);

            return await query
                .OrderByDescending(x => x.StartTime)
                .Select(x => new AppUsageResponse
                {
                    Id = x.Id,
                    AppId = x.AppId,
                    AppName = x.App.Name,
                    StartTime = x.StartTime,
                    EndTime = x.EndTime,
                    DurationSeconds = x.DurationSeconds
                })
                .ToListAsync();
        }
    }
}
