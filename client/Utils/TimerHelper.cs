using Serilog;

namespace client.Utils
{
    public static class TimerHelper
    {
        // 保持对 Timer 的引用，防止 GC 回收
        private static readonly List<Timer> _timers = [];

        public static void RunEvery(TimeSpan interval, Action action)
        {
            var timer = new Timer(_ =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    Log.Error(ex, "定时任务执行异常");
                }
            }, null, TimeSpan.Zero, interval);
            _timers.Add(timer);
        }

        public static void RunEveryAsync(TimeSpan interval, Func<Task> action)
        {
            var timer = new Timer(async _ =>
            {
                try { await action(); }
                catch (Exception ex)
                {
                    Log.Error(ex, "异步定时任务执行异常");
                }
            }, null, TimeSpan.Zero, interval);
            _timers.Add(timer);
        }
    }
}
