using System.Text.Json;
using LabLock.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class WindowMonitorService : BackgroundService
{
    private readonly LogBufferService _logBuffer;
    private readonly IConfiguration _config;
    private readonly ILogger<WindowMonitorService> _logger;
    private readonly ActiveAppHelperService _activeAppHelper;

    private string _lastTitle = "";
    private string _lastProcess = "";
    private DateTime _lastChangeTime;

    public WindowMonitorService(
        LogBufferService logBuffer,
        IConfiguration config,
        ILogger<WindowMonitorService> logger,
        ActiveAppHelperService activeAppHelper)
    {
        _logBuffer = logBuffer;
        _config = config;
        _logger = logger;
        _activeAppHelper = activeAppHelper;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config["LabLock:EnableWindowMonitoring"];
        if (enabled != null && !bool.Parse(enabled))
        {
            _logger.LogInformation("Window monitoring is disabled");
            return;
        }

        var pollIntervalMs = int.TryParse(_config["LabLock:WindowPollIntervalMs"], out var interval) ? interval : 2000;

        _lastChangeTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (title, process) = _activeAppHelper.GetCurrent();

                if (title != _lastTitle || process != _lastProcess)
                {
                    if (!string.IsNullOrEmpty(_lastTitle))
                    {
                        var log = new ActivityLogDto
                        {
                            ClientId = _config["LabLock:ClientId"] ?? Environment.MachineName,
                            EventType = EventType.WindowFocus,
                            Details = JsonSerializer.Serialize(new
                            {
                                title = _lastTitle,
                                process = _lastProcess,
                                duration_sec = (DateTime.UtcNow - _lastChangeTime).TotalSeconds
                            }),
                            Timestamp = _lastChangeTime,
                            Username = Environment.UserName
                        };

                        _logBuffer.Add(log);
                    }

                    _lastTitle = title;
                    _lastProcess = process;
                    _lastChangeTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Window monitoring error");
            }

            await Task.Delay(pollIntervalMs, stoppingToken);
        }
    }
}
