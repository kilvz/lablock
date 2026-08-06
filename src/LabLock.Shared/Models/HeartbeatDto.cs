namespace LabLock.Shared.Models;

public class HeartbeatDto
{
    public string ClientId { get; set; } = "";
    public string CurrentUser { get; set; } = "";
    public string InteractiveUser { get; set; } = "";
    public int InteractiveSessionId { get; set; }
    public int AgentSessionId { get; set; }
    public string ActiveWindow { get; set; } = "";
    public string ActiveProcess { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public long UptimeSeconds { get; set; }
    public DateTime Timestamp { get; set; }
}
