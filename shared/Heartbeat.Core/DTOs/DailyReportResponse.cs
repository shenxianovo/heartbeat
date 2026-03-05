namespace Heartbeat.Core.DTOs
{
    public class DailyReportResponse
    {
        public string Date { get; set; } = string.Empty;
        public int TotalSeconds { get; set; }
        public List<AppDurationItem> Apps { get; set; } = [];
    }

    public class AppDurationItem
    {
        public long AppId { get; set; }
        public int DurationSeconds { get; set; }
    }
}
