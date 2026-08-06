using System.Management;
using System.Reflection;
using System.Text.Json;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class ConnectionService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<ConnectionService> _logger;
    private readonly LogBufferService _logBuffer;
    private readonly PowerShellExecutorService _powerShell;
    private readonly SystemInfoService _systemInfo;
    private readonly ClientUpdateService _update;
    private readonly SessionContextService _sessionContext;
    private readonly ActiveAppHelperService _activeAppHelper;
    private readonly SessionAgentService _sessionAgent;

    private readonly ServerDiscoveryService _discovery;

    private HubConnection? _connection;
    private string _clientId = "";
    private string _serverUrl = "";
    private string _apiKey = "";
    private int _heartbeatInterval = 30;
    private int _logBatchInterval = 10;
    private System.Threading.Timer? _heartbeatTimer;
    private System.Threading.Timer? _logFlushTimer;
    private bool _isConnected;

    public HubConnection? Connection => _connection;
    public bool IsConnected => _isConnected;

    public ConnectionService(
        IConfiguration config,
        ILogger<ConnectionService> logger,
        LogBufferService logBuffer,
        PowerShellExecutorService powerShell,
        SystemInfoService systemInfo,
        ServerDiscoveryService discovery,
        ClientUpdateService update,
        SessionContextService sessionContext,
        ActiveAppHelperService activeAppHelper,
        SessionAgentService sessionAgent)
    {
        _config = config;
        _logger = logger;
        _logBuffer = logBuffer;
        _powerShell = powerShell;
        _systemInfo = systemInfo;
        _discovery = discovery;
        _update = update;
        _sessionContext = sessionContext;
        _activeAppHelper = activeAppHelper;
        _sessionAgent = sessionAgent;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _clientId = _config["LabLock:ClientId"] ?? "";
        if (string.IsNullOrEmpty(_clientId))
            _clientId = Environment.MachineName;

        _apiKey = _config["LabLock:ApiKey"] ?? "";
        if (string.IsNullOrEmpty(_apiKey))
            _apiKey = LabLockDefaults.ApiKey;

        _heartbeatInterval = int.TryParse(_config["LabLock:HeartbeatIntervalSeconds"], out var hb) ? hb : LabLockDefaults.HeartbeatIntervalSeconds;
        _logBatchInterval = int.TryParse(_config["LabLock:LogBatchIntervalSeconds"], out var lb) ? lb : LabLockDefaults.LogBatchIntervalSeconds;

        var reconnectDelay = int.TryParse(_config["LabLock:ReconnectDelaySeconds"], out var rd) ? rd : LabLockDefaults.ReconnectDelaySeconds;

        while (!stoppingToken.IsCancellationRequested)
        {
            HubConnection? oldConnection = null;

            try
            {
                if (string.IsNullOrEmpty(_serverUrl))
                {
                    _serverUrl = await _discovery.DiscoverAsync(stoppingToken);
                    _logger.LogInformation("Using server URL: {Url}", _serverUrl);
                }

                await ConnectAsync(stoppingToken);
                _logger.LogInformation("Connected. Waiting for disconnect...");

                if (_connection != null)
                {
                    var closedTcs = new TaskCompletionSource();
                    oldConnection = _connection;
                    oldConnection.Closed += ex =>
                    {
                        _logger.LogInformation(ex, "Connection lost, will reconnect");
                        closedTcs.TrySetResult();
                        return Task.CompletedTask;
                    };
                    await closedTcs.Task;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection failed, retrying in {Delay}s", reconnectDelay);
            }
            finally
            {
                _serverUrl = "";
                try { oldConnection?.DisposeAsync().AsTask().Wait(5000); } catch { }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Reconnecting in {Delay}s...", reconnectDelay);
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), stoppingToken);
            }
        }
    }

    private async Task ConnectAsync(CancellationToken stoppingToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hub/client?clientId={_clientId}&apiKey={_apiKey}")
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.FromSeconds(2)
            })
            .Build();

        _connection.On<int, CommandRequestDto>("ExecuteCommand", async (commandId, request) =>
        {
            var result = await _powerShell.ExecuteAsync(request.Command, request.TimeoutSeconds);
            result.CommandId = commandId;
            await _connection.InvokeAsync("ReportCommandResult", result);
        });

        _connection.On<JsonElement>("ExecuteInteractive", async (requestEl) =>
        {
            try
            {
                var requestId = requestEl.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "";
                var action = requestEl.GetProperty("action").GetString() ?? "";
                var paramsDict = new Dictionary<string, object?>();
                if (requestEl.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kvp in p.EnumerateObject())
                        paramsDict[kvp.Name] = kvp.Value;
                }
                var timeoutMs = requestEl.TryGetProperty("timeoutMs", out var t) ? t.GetInt32() : 30000;

                var result = await _sessionAgent.SendAsync(action, paramsDict, timeoutMs);
                var resultText = result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText();
                await _connection.InvokeAsync("ReportInteractiveResult", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["success"] = true,
                    ["result"] = resultText
                });
            }
            catch (Exception ex)
            {
                await _connection.InvokeAsync("ReportInteractiveResult", new Dictionary<string, object?>
                {
                    ["requestId"] = "",
                    ["success"] = false,
                    ["result"] = $"ERROR: {ex.Message}"
                });
            }
        });

        _sessionAgent.Start();

        _connection.On("GetSystemInfo", () =>
        {
            return _systemInfo.Collect();
        });

        _connection.On("RestartAgent", () =>
        {
            System.Diagnostics.Process.Start("cmd", "/c timeout 3 && net start LabLockAgent");
            Environment.Exit(0);
        });

        _connection.On<LabLock.Shared.Models.ClientUpdateDto>("UpdateAgent", (update) =>
        {
            _logger.LogInformation("Update request received for version {Version}", update.Version);
            _ = _update.ApplyAsync(update, _serverUrl, _apiKey);
        });

        _connection.On<Dictionary<string, string>>("UpdateConfig", (newConfig) =>
        {
            UpdateConfigFile(newConfig);
        });

        _connection.Reconnecting += (ex) =>
        {
            _isConnected = false;
            _logger.LogWarning("Reconnecting: {Message}", ex?.Message);
            return Task.CompletedTask;
        };

        _connection.Reconnected += async (connId) =>
        {
            _isConnected = true;
            _logger.LogInformation("Reconnected as {ConnectionId}", connId);
            await RegisterClient();
        };

        _connection.Closed += (ex) =>
        {
            _isConnected = false;
            _logger.LogInformation("Connection closed: {Message}", ex?.Message);
            return Task.CompletedTask;
        };

        await _connection.StartAsync(stoppingToken);
        _isConnected = true;
        _logger.LogInformation("Connected to server as {ClientId}", _clientId);

        await RegisterClient();

        _heartbeatTimer = new System.Threading.Timer(async _ => await SendHeartbeat(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(_heartbeatInterval));

        _logFlushTimer = new System.Threading.Timer(async _ => await FlushLogs(), null,
            TimeSpan.FromSeconds(_logBatchInterval), TimeSpan.FromSeconds(_logBatchInterval));
    }

    private async Task RegisterClient()
    {
        if (_connection == null) return;

        await _connection.InvokeAsync("RegisterClient", new ClientRegistrationDto
        {
            ClientId = _clientId,
            Hostname = Environment.MachineName,
            IpAddress = GetLocalIpAddress(),
            MacAddress = GetMacAddress(),
            OsVersion = _sessionContext.OsVersion,
            AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            ApiKey = _apiKey,
            InteractiveUser = _sessionContext.GetInteractiveUser(),
            InteractiveSessionId = _sessionContext.GetInteractiveSessionId(),
            AgentSessionId = _sessionContext.AgentSessionId
        });

        _logger.LogInformation("Registered client {ClientId}", _clientId);
    }

    private async Task SendHeartbeat()
    {
        if (_connection == null || !_isConnected) return;

        try
        {
            var (activeTitle, activeProcess) = _activeAppHelper.GetCurrent();

            var heartbeat = new HeartbeatDto
            {
                ClientId = _clientId,
                CurrentUser = Environment.UserName,
                InteractiveUser = _sessionContext.GetInteractiveUser(),
                InteractiveSessionId = _sessionContext.GetInteractiveSessionId(),
                AgentSessionId = _sessionContext.AgentSessionId,
                ActiveWindow = activeTitle,
                ActiveProcess = activeProcess,
                CpuPercent = GetCpuUsage(),
                MemoryPercent = GetMemoryUsage(),
                UptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                Timestamp = DateTime.UtcNow
            };

            await _connection.InvokeAsync("Heartbeat", heartbeat);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat");
        }
    }

    private async Task FlushLogs()
    {
        if (_connection == null || !_isConnected) return;

        try
        {
            var batch = _logBuffer.FlushBatch(500);
            if (batch.Length > 0)
                await _connection.InvokeAsync("SendActivityBatch", batch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush logs");
        }
    }

    private static double GetCpuUsage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LoadPercentage FROM Win32_Processor WHERE Name IS NOT NULL");
            foreach (var obj in searcher.Get())
            {
                if (obj["LoadPercentage"] != null)
                    return Convert.ToDouble(obj["LoadPercentage"]);
            }
        }
        catch { }

        return 0;
    }

    private static double GetMemoryUsage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var total = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                var free = Convert.ToDouble(obj["FreePhysicalMemory"]);
                if (total > 0)
                    return Math.Round((total - free) / total * 100, 1);
            }
        }
        catch { }

        return 0;
    }

    private static string GetLocalIpAddress()
    {
        var virtualKeywords = new[]
        {
            "virtualbox", "vbox", "vmware", "hyper-v", "hyperv",
            "tap-", "tapadapter", "tunnel", "npcap", "wintun", "loopback"
        };

        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (virtualKeywords.Any(k => desc.Contains(k))) continue;

            var props = ni.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;

            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                     && !System.Net.IPAddress.IsLoopback(a.Address));
            if (ipv4 != null)
                return ipv4.Address.ToString();
        }

        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            var ipv4 = ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                     && !System.Net.IPAddress.IsLoopback(a.Address));
            if (ipv4 != null)
                return ipv4.Address.ToString();
        }

        return "";
    }

    private static string GetMacAddress()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (var obj in searcher.Get())
                return obj["MACAddress"]?.ToString() ?? "";
        }
        catch { }

        return "";
    }

    private void UpdateConfigFile(Dictionary<string, string> newConfig)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
                System.IO.File.ReadAllText(path)) ?? new Dictionary<string, object>();

            if (json.TryGetValue("LabLock", out var labLockObj) && labLockObj is Dictionary<string, object> labLock)
            {
                foreach (var kvp in newConfig)
                    labLock[kvp.Key] = kvp.Value;
            }

            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));

            System.Diagnostics.Process.Start("cmd", "/c timeout 3 && net stop LabLockAgent && net start LabLockAgent");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update config");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _heartbeatTimer?.Dispose();
        _logFlushTimer?.Dispose();
        _sessionAgent.Stop();

        if (_connection != null)
            await _connection.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}
