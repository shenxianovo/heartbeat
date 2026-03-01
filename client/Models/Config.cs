namespace client.Models
{
    public class Config
    {
        public string DeviceName { get; set; } = "DESKTOP-001";
        public string ApiBaseUrl { get; set; } = "https://shenxianovo.com/heartbeat/api/v1";
        public string ApiKey { get; set; } = string.Empty;
        public int UploadIntervalMinutes { get; set; } = 1;
        public int StatusUploadIntervalSeconds { get; set; } = 30;
    }
}
