using client.DTOs;
using client.Models;
using client.Utils;
using Serilog;

namespace client.Services
{
    public class AppMonitorService
    {
        private readonly MonitorMode _mode;

        // 记录每个应用的当前会话开始时间
        private readonly Dictionary<string, DateTimeOffset> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AppUsageItem> _usages = [];

        public AppMonitorService(MonitorMode mode)
        {
            _mode = mode;
            Log.Information("应用监测服务启动，监测模式: {Mode}", _mode);
        }

        public void RecordActiveApps()
        {
            var currentApps = ActiveWindowHelper.GetActiveProcessNames(_mode);
            var now = DateTimeOffset.UtcNow;

            Log.Debug("采集到 {Count} 个活跃应用: {Apps}", currentApps.Count, string.Join(", ", currentApps));

            // 结束不再活跃的应用会话
            var ended = _activeSessions.Keys
                .Where(app => !currentApps.Contains(app))
                .ToList();

            foreach (var app in ended)
            {
                var start = _activeSessions[app];
                _activeSessions.Remove(app);

                if (start == default) continue; // 防御：跳过无效时间

                _usages.Add(new AppUsageItem
                {
                    AppName = app,
                    StartTime = start,
                    EndTime = now
                });
                Log.Information("应用结束: {App}，时长 {Duration:F1}s", app, (now - start).TotalSeconds);
            }

            // 开始新的应用会话
            foreach (var app in currentApps)
            {
                if (!_activeSessions.ContainsKey(app))
                {
                    _activeSessions[app] = now;
                    Log.Information("应用开始: {App}", app);
                }
            }
        }

        public List<AppUsageItem> GetAndClearUsages()
        {
            var now = DateTimeOffset.UtcNow;

            // 将所有当前活跃会话截断并重新开始
            foreach (var app in _activeSessions.Keys.ToList())
            {
                var start = _activeSessions[app];
                if (start == default) continue; // 防御：跳过无效时间

                _usages.Add(new AppUsageItem
                {
                    AppName = app,
                    StartTime = start,
                    EndTime = now
                });
                _activeSessions[app] = now; // 重新开始新会话
            }

            var copy = new List<AppUsageItem>(_usages);
            _usages.Clear();

            Log.Information("收集到 {Count} 条使用记录，准备上传", copy.Count);
            foreach (var item in copy)
            {
                Log.Information("  {App}: {Start:HH:mm:ss} - {End:HH:mm:ss} ({Duration:F1}s)",
                    item.AppName, item.StartTime.LocalDateTime, item.EndTime.LocalDateTime,
                    (item.EndTime - item.StartTime).TotalSeconds);
            }

            return copy;
        }
    }
}
