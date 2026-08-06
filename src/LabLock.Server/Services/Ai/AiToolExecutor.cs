using LabLock.Server.Hubs;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace LabLock.Server.Services.Ai;

public class AiToolExecutor
{
    private readonly ClientStateService _clientState;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ClientHub> _hubContext;

    public AiToolExecutor(
        ClientStateService clientState,
        IServiceScopeFactory scopeFactory,
        IHubContext<ClientHub> hubContext)
    {
        _clientState = clientState;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
    }

    public async Task<string> ExecuteToolAsync(AiToolCall toolCall, CancellationToken ct)
    {
        switch (toolCall.Name)
        {
            case "execute_command":
                return await ExecuteCommand(toolCall, ct);

            case "execute_command_all":
                return await ExecuteCommandAll(toolCall, ct);

            case "list_clients":
                return ListClients(toolCall);

            case "get_system_info":
                return await GetSystemInfo(toolCall, ct);

            case "get_activity_logs":
                return await GetActivityLogs(toolCall, ct);

            case "get_command_history":
                return await GetCommandHistory(toolCall, ct);

            default:
                return $"Unknown tool: {toolCall.Name}";
        }
    }

    private async Task<string> ExecuteCommand(AiToolCall toolCall, CancellationToken ct)
    {
        var clientId = toolCall.Arguments["client_id"].ToString()!;
        var command = toolCall.Arguments["command"].ToString()!;
        var timeout = toolCall.Arguments.TryGetValue("timeout_seconds", out var t)
            ? Convert.ToInt32(t) : 60;

        using var scope = _scopeFactory.CreateScope();
        var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
        return await cmdService.ExecuteAndWaitAsync(clientId, command, timeout);
    }

    private async Task<string> ExecuteCommandAll(AiToolCall toolCall, CancellationToken ct)
    {
        var command = toolCall.Arguments["command"].ToString()!;
        var timeout = toolCall.Arguments.TryGetValue("timeout_seconds", out var t)
            ? Convert.ToInt32(t) : 60;

        var onlineClients = _clientState.GetAllClients().Where(c => c.IsOnline).ToList();
        if (onlineClients.Count == 0) return "No clients are currently online.";

        using var scope = _scopeFactory.CreateScope();
        var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
        var tasks = onlineClients.Select(async c =>
        {
            try
            {
                var output = await cmdService.ExecuteAndWaitAsync(c.ClientId, command, timeout);
                return $"=== {c.ClientId} ({c.Hostname}) ===\n{output}";
            }
            catch (Exception ex)
            {
                return $"=== {c.ClientId} ({c.Hostname}) ===\nERROR: {ex.Message}";
            }
        });

        var results = await Task.WhenAll(tasks);
        return string.Join("\n\n", results);
    }

    private string ListClients(AiToolCall toolCall)
    {
        var filter = toolCall.Arguments.TryGetValue("status_filter", out var f) ? f.ToString() : "all";
        var clients = _clientState.GetAllClients();

        clients = filter switch
        {
            "online" => clients.Where(c => c.IsOnline).ToList(),
            "offline" => clients.Where(c => !c.IsOnline).ToList(),
            "idle" => clients.Where(c => c.IsOnline && c.CpuPercent < 5).ToList(),
            _ => clients
        };

        if (clients.Count == 0) return "No clients match the filter.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"Client ID",-20} {"Hostname",-15} {"Status",-8} {"User",-15} {"Active App",-20} {"CPU",-6} {"Memory",-6}");
        sb.AppendLine(new string('-', 95));

        foreach (var c in clients)
        {
            var status = c.IsOnline ? "Online" : "Offline";
            sb.AppendLine($"{c.ClientId,-20} {c.Hostname,-15} {status,-8} {c.CurrentUser,-15} {c.ActiveProcess,-20} {c.CpuPercent,5:F1}% {c.MemoryPercent,5:F1}%");
        }

        return sb.ToString();
    }

    private async Task<string> GetSystemInfo(AiToolCall toolCall, CancellationToken ct)
    {
        var clientId = toolCall.Arguments["client_id"].ToString()!;
        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null) return $"ERROR: Client {clientId} is not online.";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var info = await _hubContext.Clients.Client(connId)
                .InvokeAsync<LabLock.Shared.Models.SystemInfoDto>("GetSystemInfo", cts.Token);

            return $"Hostname: {info.Hostname}\n" +
                   $"OS: {info.OsVersion}\n" +
                   $"CPU: {info.CpuName} ({info.CpuCores} cores)\n" +
                   $"Memory: {info.FreeMemoryMb}MB free / {info.TotalMemoryMb}MB total\n" +
                   $"Disk: {info.FreeDiskMb}MB free / {info.TotalDiskMb}MB total\n" +
                   $"User: {info.CurrentUser}\n" +
                   $"IP: {info.IpAddress}\n" +
                   $"Running Processes: {string.Join(", ", info.RunningProcesses.Take(30))}\n" +
                   $"Installed Software ({info.InstalledSoftware.Length} total): {string.Join(", ", info.InstalledSoftware.Take(20))}";
        }
        catch (Exception ex)
        {
            return $"ERROR: Failed to get system info: {ex.Message}";
        }
    }

    private async Task<string> GetActivityLogs(AiToolCall toolCall, CancellationToken ct)
    {
        var clientId = toolCall.Arguments["client_id"].ToString()!;
        var eventTypeStr = toolCall.Arguments.TryGetValue("event_type", out var et) ? et.ToString() : "all";
        var hoursBack = toolCall.Arguments.TryGetValue("hours_back", out var hb) ? Convert.ToInt32(hb) : 1;
        var limit = toolCall.Arguments.TryGetValue("limit", out var lm) ? Convert.ToInt32(lm) : 50;

        EventType? eventTypeFilter = eventTypeStr != "all"
            ? Enum.Parse<EventType>(eventTypeStr!, true)
            : null;

        var from = DateTime.UtcNow.AddHours(-hoursBack);

        using var scope = _scopeFactory.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<LogStorageService>();
        var (logs, total) = await logService.QueryAsync(clientId, eventTypeFilter, from, null, null, 1, limit);

        if (logs.Count == 0)
            return $"No activity logs found for {clientId} in the last {hoursBack} hour(s).";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Activity logs for {clientId} (showing {logs.Count} of {total}):\n");
        foreach (var log in logs)
            sb.AppendLine($"[{log.Timestamp:HH:mm:ss}] {log.EventType}: {log.Details}");

        return sb.ToString();
    }

    private async Task<string> GetCommandHistory(AiToolCall toolCall, CancellationToken ct)
    {
        var clientId = toolCall.Arguments["client_id"].ToString()!;
        var limit = toolCall.Arguments.TryGetValue("limit", out var lm) ? Convert.ToInt32(lm) : 20;

        using var scope = _scopeFactory.CreateScope();
        var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
        var history = await cmdService.GetHistoryAsync(clientId, limit);

        if (history.Count == 0) return $"No command history for {clientId}.";

        var sb = new System.Text.StringBuilder();
        foreach (var cmd in history)
        {
            sb.AppendLine($"[{cmd.SentAt:yyyy-MM-dd HH:mm:ss}] by {cmd.SentBy} | Status: {cmd.Status}");
            sb.AppendLine($"  Command: {cmd.Command}");
            if (!string.IsNullOrEmpty(cmd.Output))
                sb.AppendLine($"  Output: {(cmd.Output.Length > 200 ? cmd.Output[..200] + "..." : cmd.Output)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
