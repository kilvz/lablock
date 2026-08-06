using System.ComponentModel.DataAnnotations;

namespace LabLock.Server.Models;

public class CommandHistory
{
    [Key]
    public int Id { get; set; }
    public string ClientId { get; set; } = "";
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
    public string Status { get; set; } = "pending";
    public string SentBy { get; set; } = "";
    public DateTime SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? SessionId { get; set; }
    public int? InteractiveSessionId { get; set; }
    public string? InteractiveUser { get; set; }
    public string? RunAsUser { get; set; }
    public string? OsVersion { get; set; }
    public string? WorkingDirectory { get; set; }
    public long? DurationMs { get; set; }
}
