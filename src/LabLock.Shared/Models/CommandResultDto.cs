namespace LabLock.Shared.Models;

public class CommandResultDto
{
    public int CommandId { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
    public bool IsPartial { get; set; }
    public int SessionId { get; set; }
    public int InteractiveSessionId { get; set; }
    public string InteractiveUser { get; set; } = "";
    public string RunAsUser { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public DateTime ExecutedAt { get; set; }
    public long DurationMs { get; set; }
}
