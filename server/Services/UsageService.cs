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
        /// 合并容差：新记录的 StartTime 与已有记录的 EndTime 相差不超过此值则合并
        /// </summary>
        private static readonly TimeSpan MergeTolerance = TimeSpan.FromMinutes(2);

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

            // 按 AppName 分组处理，减少数据库查询次数
            var groups = request.Usages
                .Where(u => !string.IsNullOrEmpty(u.AppName)
                         && u.StartTime != default
                         && u.EndTime > u.StartTime
                         && u.StartTime.Year >= 2020) // 过滤无效记录
                .GroupBy(u => u.AppName);

            foreach (var group in groups)
            {
                var appName = group.Key;

                // 查找该设备+应用的最新记录
                var existing = await _db.AppUsages
                    .Where(x => x.DeviceName == device.DeviceName && x.AppName == appName)
                    .OrderByDescending(x => x.EndTime)
                    .FirstOrDefaultAsync();

                // 将同一应用的记录按时间排序后逐条处理
                foreach (var u in group.OrderBy(u => u.StartTime))
                {
                    if (existing != null
                        && u.StartTime >= existing.StartTime
                        && u.StartTime <= existing.EndTime + MergeTolerance)
                    {
                        // 合并：扩展结束时间（只延长不缩短）
                        if (u.EndTime > existing.EndTime)
                        {
                            existing.EndTime = u.EndTime;
                            existing.DurationSeconds = (int)(existing.EndTime - existing.StartTime).TotalSeconds;
                        }
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

        public async Task<List<string>> GetDeviceNamesAsync()
        {
            return await _db.Devices.Select(x => x.DeviceName).ToListAsync();
        }

        public async Task<List<AppUsage>> GetUsageAsync(string? deviceName, DateTimeOffset? date)
        {
            var query = _db.AppUsages.AsQueryable();

            if (!string.IsNullOrEmpty(deviceName))
                query = query.Where(x => x.DeviceName == deviceName);

            if (date.HasValue)
            {
                var start = date.Value.Date;
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
