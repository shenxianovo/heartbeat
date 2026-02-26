using client.DTOs;
using client.Utils;

namespace client.Services
{
    public class AppMonitorService
    {
        private readonly List<AppUsageItem> _usages = [];
        private string _lastApp = string.Empty;
        private DateTimeOffset _lastStart;

        public void RecordActiveApp()
        {
            var currentApp = ActiveWindowHelper.GetActiveProcessName();
            if (string.IsNullOrEmpty(currentApp))
                return;

            var now = DateTimeOffset.UtcNow;

            if (string.IsNullOrEmpty(_lastApp))
            {
                _lastApp = currentApp;
                _lastStart = now;
                return;
            }

            if (_lastApp == currentApp)
                return;

            _usages.Add(new AppUsageItem
            {
                AppName = _lastApp,
                StartTime = _lastStart,
                EndTime = now
            });

            _lastApp = currentApp;
            _lastStart = now;
        }

        public List<AppUsageItem> GetAndClearUsages()
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastApp != null)
            {
                _usages.Add(new AppUsageItem
                {
                    AppName = _lastApp,
                    StartTime = _lastStart,
                    EndTime = now
                });
                _lastStart = now;
            }

            var copy = new List<AppUsageItem>(_usages);
            _usages.Clear();
            return copy;
        }
    }
}
