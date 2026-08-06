using System.ComponentModel.DataAnnotations;

namespace LabLock.Server.Models;

public class Client
{
    [Key]
    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string IpAddresses { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string CurrentUser { get; set; } = "";
    public string? InteractiveUser { get; set; }
    public int? InteractiveSessionId { get; set; }
    public int? AgentSessionId { get; set; }
    public string CpuName { get; set; } = "";
    public long? TotalMemoryMb { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public string ActiveProcess { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}
