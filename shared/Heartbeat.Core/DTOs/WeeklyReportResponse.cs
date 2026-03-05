namespace Heartbeat.Core.DTOs
{
    public class WeeklyReportResponse
    {
        public string WeekStart { get; set; } = string.Empty;
        public string WeekEnd { get; set; } = string.Empty;
        public int TotalSeconds { get; set; }
        public List<AppDurationItem> Apps { get; set; } = [];
    }
}
