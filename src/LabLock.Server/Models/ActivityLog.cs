using System.ComponentModel.DataAnnotations;
using LabLock.Shared.Models;

namespace LabLock.Server.Models;

public class ActivityLog
{
    [Key]
    public long Id { get; set; }
    public string ClientId { get; set; } = "";
    public EventType EventType { get; set; }
    public string Details { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = "";
}
