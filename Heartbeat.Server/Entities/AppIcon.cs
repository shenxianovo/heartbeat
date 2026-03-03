namespace server.Entities
{
    public class AppIcon
    {
        public long Id { get; set; }
        public string AppName { get; set; } = string.Empty;
        public byte[] IconData { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
