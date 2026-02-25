namespace server.Entities
{
    public class Device
    {
        public long Id { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }
}
