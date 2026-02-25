namespace server.DTOs
{
    public class UsageUploadRequest
    {
        public string DeviceName { get; set; } = string.Empty;

        public List<AppUsageItem> Usages { get; set; } = [];
    }

    public class AppUsageItem
    {
        public string AppName { get; set; } = string.Empty;

        public DateTimeOffset StartTime { get; set; }

        public DateTimeOffset EndTime { get; set; }
    }
}
