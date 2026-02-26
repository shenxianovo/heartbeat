using Microsoft.EntityFrameworkCore;
using server.Data;
using server.DTOs;
using server.Entities;

namespace server.Services
{
    public class UsageService(AppDbContext db)
    {
        private readonly AppDbContext _db = db;

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

            foreach (var u in request.Usages)
            {
                var usage = new AppUsage
                {
                    DeviceName = device.DeviceName,
                    AppName = u.AppName,
                    StartTime = u.StartTime,
                    EndTime = u.EndTime,
                    DurationSeconds = (int)(u.EndTime - u.StartTime).TotalSeconds
                };

                _db.AppUsages.Add(usage);
            }

            await _db.SaveChangesAsync();
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
