namespace LabLock.Shared.Models;

public class SystemInfoDto
{
    public string ClientId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string CpuName { get; set; } = "";
    public int CpuCores { get; set; }
    public long TotalMemoryMb { get; set; }
    public long FreeMemoryMb { get; set; }
    public long TotalDiskMb { get; set; }
    public long FreeDiskMb { get; set; }
    public string[] InstalledSoftware { get; set; } = [];
    public string[] RunningProcesses { get; set; } = [];
    public string CurrentUser { get; set; } = "";
    public string InteractiveUser { get; set; } = "";
    public int InteractiveSessionId { get; set; }
    public int AgentSessionId { get; set; }
    public string IpAddress { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public string ActiveProcess { get; set; } = "";
}
