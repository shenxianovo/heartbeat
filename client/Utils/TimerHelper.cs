namespace client.Utils
{
    public static class TimerHelper
    {
        public static void RunEvery(TimeSpan interval, Action action)
        {
            Timer timer = new(_ =>
            {
                try { action(); } catch { }
            }, null, TimeSpan.Zero, interval);
        }
    }
}
