using client.DTOs;
using client.Utils;
using Serilog;

namespace client.Services
{
    public class AppMonitorService : IDisposable
    {
        private readonly object _lock = new();
        private string? _currentApp;
        private DateTimeOffset _currentStart;
        private readonly List<AppUsageItem> _usages = [];
        private Thread? _hookThread;

        public AppMonitorService()
        {
            Log.Information("应用监测服务启动");
        }

        /// <summary>
        /// 启动前台窗口监听
        /// </summary>
        public void Start()
        {
            ActiveWindowHelper.ForegroundWindowChanged += OnForegroundChanged;

            // 记录启动时的前台窗口
            var initialApp = ActiveWindowHelper.GetForegroundProcessName();
            if (initialApp != null)
            {
                lock (_lock)
                {
                    _currentApp = initialApp;
                    _currentStart = DateTimeOffset.UtcNow;
                    Log.Information("初始前台应用: {App}", initialApp);
                }
            }

            // 在专用线程上运行消息循环
            _hookThread = new Thread(() =>
            {
                try
                {
                    ActiveWindowHelper.StartHook();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "WinEvent 钩子线程异常");
                }
            })
            {
                IsBackground = true,
                Name = "WinEventHookThread"
            };
            _hookThread.Start();
        }

        private void OnForegroundChanged(string? newApp)
        {
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                // 如果切换到的是同一个应用，忽略
                if (string.Equals(_currentApp, newApp, StringComparison.OrdinalIgnoreCase))
                    return;

                // 结束上一个应用的会话
                if (_currentApp != null && _currentStart != default)
                {
                    var duration = now - _currentStart;
                    // 只记录超过 1 秒的会话，过滤掉瞬间切换
                    if (duration.TotalSeconds >= 1)
                    {
                        _usages.Add(new AppUsageItem
                        {
                            AppName = _currentApp,
                            StartTime = _currentStart,
                            EndTime = now
                        });
                        Log.Information("应用结束: {App}，时长 {Duration:F1}s", _currentApp, duration.TotalSeconds);
                    }
                }

                // 开始新应用的会话
                _currentApp = newApp;
                _currentStart = now;

                if (newApp != null)
                {
                    Log.Information("应用切换: {App}", newApp);
                }
            }
        }

        /// <summary>
        /// 获取当前前台应用名称
        /// </summary>
        public string? GetCurrentApp()
        {
            lock (_lock)
            {
                return _currentApp;
            }
        }

        /// <summary>
        /// 获取并清空已记录的使用数据（将当前活跃会话截断）
        /// </summary>
        public List<AppUsageItem> GetAndClearUsages()
        {
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                // 截断当前活跃会话
                if (_currentApp != null && _currentStart != default)
                {
                    var duration = now - _currentStart;
                    if (duration.TotalSeconds >= 1)
                    {
                        _usages.Add(new AppUsageItem
                        {
                            AppName = _currentApp,
                            StartTime = _currentStart,
                            EndTime = now
                        });
                    }
                    _currentStart = now; // 重新开始新会话
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

        public void Dispose()
        {
            ActiveWindowHelper.ForegroundWindowChanged -= OnForegroundChanged;
            ActiveWindowHelper.StopHook();
            GC.SuppressFinalize(this);
        }
    }
}
