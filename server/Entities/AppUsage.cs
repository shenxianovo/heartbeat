namespace server.Entities
{
    public class AppUsage
    {
        public long Id { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;
        public DateTimeOffset StartTime { get; set; }
        public DateTimeOffset EndTime { get; set; }
        public int DurationSeconds { get; set; }
    }
}
