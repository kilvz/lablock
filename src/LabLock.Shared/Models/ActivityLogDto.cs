namespace LabLock.Shared.Models;

public class ActivityLogDto
{
    public string ClientId { get; set; } = "";
    public EventType EventType { get; set; }
    public string Details { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = "";
}
