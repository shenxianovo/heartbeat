using Microsoft.EntityFrameworkCore;
using server.Data;
using server.DTOs;
using server.Entities;

namespace server.Services
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

        public async Task SaveUsageAsync(UsageUploadRequest request)
        {
            var device = await _db.Devices.FirstOrDefaultAsync(x => x.DeviceName == request.DeviceName);

            if (device == null)
            {
                device = new Device
                {
                    DeviceName = request.DeviceName,
                    ApiKey = request.ApiKey,
                };

                _db.Devices.Add(device);
            }

            var now = DateTimeOffset.UtcNow;

            var validUsages = request.Usages
                .Where(u => !string.IsNullOrEmpty(u.AppName)
                         && u.StartTime != default
                         && u.EndTime > u.StartTime
                         && u.StartTime.Year >= 2020
                         && u.EndTime <= now + TimeSkewTolerance       // 不能超过服务端当前时间太多
                         && u.StartTime >= now - TimeSkewTolerance - MaxDuration // 不能太久远
                         && (u.EndTime - u.StartTime) <= MaxDuration)  // 单条时长不超过 24 小时
                .OrderBy(u => u.StartTime)
                .ToList();

            // 按 AppName 分组，对每组尝试与数据库中最新记录合并
            foreach (var group in validUsages.GroupBy(u => u.AppName))
            {
                var appName = group.Key;

                // 查找该设备+应用的最新记录
                var existing = await _db.AppUsages
                    .Where(x => x.DeviceName == device.DeviceName && x.AppName == appName)
                    .OrderByDescending(x => x.EndTime)
                    .FirstOrDefaultAsync();

                foreach (var u in group)
                {
                    if (existing != null
                        && u.AppName == existing.AppName
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
                            DeviceName = device.DeviceName,
                            AppName = appName,
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

        public async Task<bool> ValidateApiKeyAsync(string deviceName, string apiKey)
        {
            var device = await _db.Devices.FirstOrDefaultAsync(x => x.DeviceName == deviceName);
            // 新设备允许注册，已有设备必须匹配 ApiKey
            return device == null || device.ApiKey == apiKey;
        }

        public async Task<List<AppUsage>> GetUsageAsync(string? deviceName, DateTimeOffset? date)
        {
            var query = _db.AppUsages.AsQueryable();

            if (!string.IsNullOrEmpty(deviceName))
                query = query.Where(x => x.DeviceName == deviceName);

            if (date.HasValue)
            {
                var start = new DateTimeOffset(date.Value.Date, TimeSpan.Zero);
                var end = start.AddDays(1);

                query = query.Where(x =>
                    x.StartTime >= start &&
                    x.StartTime < end);
            }

            return await query
                .OrderByDescending(x => x.StartTime)
                .ToListAsync();
        }
    }
}
