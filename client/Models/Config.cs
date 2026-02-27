namespace client.Models
{
    public class Config
    {
        public string DeviceName { get; set; } = "DESKTOP-001";
        public string ApiUrl { get; set; } = "https://shenxianovo.com/heartbeat/api/v1/usage";
        public string ApiKey { get; set; } = string.Empty;
        public int UploadIntervalMinutes { get; set; } = 1;
    }
}
