namespace server.DTOs
{
    public class DeviceStatusResponse
    {
        public string? CurrentApp { get; set; } = string.Empty;
        public DateTimeOffset? LastSeen { get; set; }
        public bool IsOnline => LastSeen != null &&
                               DateTimeOffset.UtcNow - LastSeen < TimeSpan.FromSeconds(30);
    }
}
