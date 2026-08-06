using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LabLock.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace LabLock.Client.Services;

public class SystemInfoService
{
    private readonly IConfiguration _config;
    private readonly SessionContextService _sessionContext;

    public SystemInfoService(IConfiguration config, SessionContextService sessionContext)
    {
        _config = config;
        _sessionContext = sessionContext;
    }

    public SystemInfoDto Collect()
    {
        var clientId = _config["LabLock:ClientId"];
        if (string.IsNullOrEmpty(clientId))
            clientId = Environment.MachineName;

        return new SystemInfoDto
        {
            ClientId = clientId,
            Hostname = Environment.MachineName,
            OsVersion = GetFriendlyOsName(),
            CpuName = GetCpuName(),
            CpuCores = Environment.ProcessorCount,
            TotalMemoryMb = GetTotalMemory(),
            FreeMemoryMb = GetFreeMemory(),
            TotalDiskMb = GetDiskSpace(true),
            FreeDiskMb = GetDiskSpace(false),
            InstalledSoftware = GetInstalledSoftware(),
            RunningProcesses = GetRunningProcesses(),
            CurrentUser = GetCurrentUserName(),
            InteractiveUser = _sessionContext.GetInteractiveUser(),
            InteractiveSessionId = _sessionContext.GetInteractiveSessionId(),
            AgentSessionId = _sessionContext.AgentSessionId,
            IpAddress = GetLocalIpAddress()
        };
    }

    public static string GetFriendlyOsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var productName = key.GetValue("ProductName")?.ToString();
                var displayVersion = key.GetValue("DisplayVersion")?.ToString();
                var build = key.GetValue("CurrentBuildNumber")?.ToString();
                var ubr = key.GetValue("UBR")?.ToString();

                var buildFull = string.IsNullOrEmpty(ubr) ? build : $"{build}.{ubr}";
                if (!string.IsNullOrEmpty(productName))
                {
                    var parts = new[] { productName, displayVersion }.Where(s => !string.IsNullOrWhiteSpace(s));
                    var name = string.Join(" ", parts);
                    return string.IsNullOrEmpty(buildFull) ? name : $"{name} (Build {buildFull})";
                }
            }
        }
        catch { }

        var v = Environment.OSVersion.Version;
        var family = v.Build switch
        {
            >= 22000 => "Windows 11",
            >= 10240 => "Windows 10",
            >= 9600 => "Windows 8.1",
            >= 9200 => "Windows 8",
            >= 7601 => "Windows 7 SP1",
            >= 7600 => "Windows 7",
            _ => "Windows"
        };
        return $"{family} (Build {v.Build}.{v.Revision})";
    }

    private static string GetCurrentUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }

    private static string GetCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var obj in searcher.Get())
                return obj["Name"]?.ToString() ?? "Unknown";
        }
        catch { }

        return "Unknown";
    }

    private static long GetTotalMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
                if (obj["TotalVisibleMemorySize"] != null)
                    return Convert.ToInt64(obj["TotalVisibleMemorySize"]) / 1024;
        }
        catch { }

        return 0;
    }

    private static long GetFreeMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
                if (obj["FreePhysicalMemory"] != null)
                    return Convert.ToInt64(obj["FreePhysicalMemory"]) / 1024;
        }
        catch { }

        return 0;
    }

    private static long GetDiskSpace(bool total)
    {
        try
        {
            var drive = new DriveInfo("C:");
            return total ? drive.TotalSize / (1024 * 1024) : drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static string[] GetInstalledSoftware()
    {
        var list = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key != null)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName")?.ToString();
                    if (!string.IsNullOrEmpty(displayName))
                        list.Add(displayName);
                }
            }
        }
        catch { }

        return list.OrderBy(n => n).Take(200).ToArray();
    }

    private static string[] GetRunningProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Select(p => p.ProcessName)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string GetLocalIpAddress()
    {
        var virtualKeywords = new[]
        {
            "virtualbox", "vbox", "vmware", "hyper-v", "hyperv",
            "tap-", "tapadapter", "tunnel", "npcap", "wintun", "loopback"
        };

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (virtualKeywords.Any(k => desc.Contains(k))) continue;

            var props = ni.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;

            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                     && !IPAddress.IsLoopback(a.Address));
            if (ipv4 != null)
                return ipv4.Address.ToString();
        }

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipv4 = ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                     && !IPAddress.IsLoopback(a.Address));
            if (ipv4 != null)
                return ipv4.Address.ToString();
        }

        return "";
    }
}
