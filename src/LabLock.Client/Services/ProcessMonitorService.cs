using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using LabLock.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class ProcessMonitorService : BackgroundService
{
    private readonly LogBufferService _logBuffer;
    private readonly IConfiguration _config;
    private readonly ILogger<ProcessMonitorService> _logger;

    private readonly ConcurrentDictionary<int, (string name, string path, DateTime startTime)> _trackedProcesses = new();

    public ProcessMonitorService(
        LogBufferService logBuffer,
        IConfiguration config,
        ILogger<ProcessMonitorService> logger)
    {
        _logBuffer = logBuffer;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config["LabLock:EnableProcessMonitoring"];
        if (enabled != null && !bool.Parse(enabled))
        {
            _logger.LogInformation("Process monitoring is disabled");
            return;
        }

        // Snapshot current processes
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                _trackedProcesses.TryAdd(p.Id, (p.ProcessName, p.MainModule?.FileName ?? "", p.StartTime));
            }
            catch { }
        }

        // Try WMI watchers
        var wmiOk = false;
        try
        {
            var startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"));
            startWatcher.EventArrived += OnProcessStarted;
            startWatcher.Start();

            var stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"));
            stopWatcher.EventArrived += OnProcessStopped;
            stopWatcher.Start();

            wmiOk = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI event watchers failed, falling back to polling");
        }

        if (!wmiOk)
        {
            // Fallback: poll every 3 seconds
            await PollingLoop(stoppingToken);
        }
        else
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }

    private async Task PollingLoop(CancellationToken stoppingToken)
    {
        var previous = new Dictionary<int, (string name, DateTime startTime)>();

        foreach (var kvp in _trackedProcesses)
            previous[kvp.Key] = (kvp.Value.name, kvp.Value.startTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(3000, stoppingToken);

            try
            {
                var current = Process.GetProcesses();
                var currentDict = new Dictionary<int, (string name, DateTime startTime)>();
                var currentIds = new HashSet<int>();

                foreach (var p in current)
                {
                    currentIds.Add(p.Id);
                    try
                    {
                        currentDict[p.Id] = (p.ProcessName, p.StartTime);
                    }
                    catch { }
                }

                // New processes
                foreach (var kvp in currentDict)
                {
                    if (!previous.ContainsKey(kvp.Key))
                        LogProcessStart(kvp.Value.name, kvp.Key, "", kvp.Value.startTime);
                }

                // Stopped processes
                foreach (var kvp in previous)
                {
                    if (!currentIds.Contains(kvp.Key))
                        LogProcessStop(kvp.Key, kvp.Value.name);
                }

                previous = currentDict;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Process polling error");
            }
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var name = target["Name"]?.ToString() ?? "";
            var pid = Convert.ToInt32(target["ProcessId"]);
            var path = target["ExecutablePath"]?.ToString() ?? "";
            var startTime = DateTime.Now;

            _trackedProcesses.TryAdd(pid, (name, path, startTime));
            LogProcessStart(name, pid, path, startTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing process start event");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid = Convert.ToInt32(target["ProcessId"]);

            if (_trackedProcesses.TryRemove(pid, out var info))
                LogProcessStop(pid, info.name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing process stop event");
        }
    }

    private void LogProcessStart(string name, int pid, string path, DateTime startTime)
    {
        var log = new ActivityLogDto
        {
            ClientId = _config["LabLock:ClientId"] ?? Environment.MachineName,
            EventType = EventType.ProcessStart,
            Details = JsonSerializer.Serialize(new
            {
                name,
                pid,
                path
            }),
            Timestamp = DateTime.UtcNow,
            Username = Environment.UserName
        };

        _logBuffer.Add(log);
    }

    private void LogProcessStop(int pid, string name)
    {
        var log = new ActivityLogDto
        {
            ClientId = _config["LabLock:ClientId"] ?? Environment.MachineName,
            EventType = EventType.ProcessStop,
            Details = JsonSerializer.Serialize(new
            {
                name,
                pid
            }),
            Timestamp = DateTime.UtcNow,
            Username = Environment.UserName
        };

        _logBuffer.Add(log);
    }
}
