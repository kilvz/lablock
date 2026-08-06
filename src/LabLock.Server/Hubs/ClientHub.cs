using System.Text.Json;
using LabLock.Server.Data;
using LabLock.Server.Models;
using LabLock.Server.Services;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace LabLock.Server.Hubs;

public class ClientHub : Hub
{
    private readonly ClientStateService _clientState;
    private readonly IHubContext<ClientHub> _hubContext;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string DashboardSecret = "lablock-dashboard-2026";

    public ClientHub(
        ClientStateService clientState,
        IHubContext<ClientHub> hubContext,
        IServiceScopeFactory scopeFactory)
    {
        _clientState = clientState;
        _hubContext = hubContext;
        _scopeFactory = scopeFactory;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext == null)
        {
            Context.Abort();
            return;
        }

        var clientId = httpContext.Request.Query["clientId"].FirstOrDefault();
        var apiKey = httpContext.Request.Query["apiKey"].FirstOrDefault();
        var role = httpContext.Request.Query["role"].FirstOrDefault();
        var token = httpContext.Request.Query["token"].FirstOrDefault();

        if (apiKey == DashboardSecret && role == "dashboard")
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
            await base.OnConnectedAsync();
            return;
        }

        if (!string.IsNullOrEmpty(token))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.DashboardUsers.FirstOrDefaultAsync(u => u.Token == token && u.IsActive);
            if (user != null)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
                await base.OnConnectedAsync();
                return;
            }
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(apiKey))
        {
            Context.Abort();
            return;
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
            if (client == null)
            {
                client = new Client
                {
                    ClientId = clientId,
                    ApiKey = apiKey,
                    Hostname = httpContext.Request.Query["hostname"].FirstOrDefault() ?? clientId,
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    IsActive = true
                };
                db.Clients.Add(client);
            }
            else
            {
                client.ApiKey = apiKey;
                client.LastSeen = DateTime.UtcNow;
                client.IsActive = true;
            }
            await db.SaveChangesWithRetryAsync();
        }

        _clientState.RegisterClient(clientId, Context.ConnectionId);

        await Groups.AddToGroupAsync(Context.ConnectionId, "clients");
        await base.OnConnectedAsync();

        await Clients.Group("dashboard").SendAsync("ClientConnected", new
        {
            clientId,
            timestamp = DateTime.UtcNow
        });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = _clientState.GetClientId(Context.ConnectionId);
        _clientState.UnregisterClient(Context.ConnectionId);

        if (clientId != null)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
                if (client != null)
                    client.LastSeen = DateTime.UtcNow;
                await db.SaveChangesWithRetryAsync();
            }

            await Clients.Group("dashboard").SendAsync("ClientDisconnected", new
            {
                clientId,
                timestamp = DateTime.UtcNow
            });
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task RegisterClient(ClientRegistrationDto reg)
    {
        _clientState.Register(reg, Context.ConnectionId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == reg.ClientId);
        if (client != null)
        {
            client.Hostname = reg.Hostname;
            client.IpAddress = reg.IpAddress;
            client.IpAddresses = reg.IpAddress;
            client.OsVersion = reg.OsVersion;
            client.AgentVersion = reg.AgentVersion;
            client.InteractiveUser = reg.InteractiveUser;
            client.InteractiveSessionId = reg.InteractiveSessionId;
            client.AgentSessionId = reg.AgentSessionId;
            client.LastSeen = DateTime.UtcNow;
            if (client.FirstSeen == default)
                client.FirstSeen = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    public async Task Heartbeat(HeartbeatDto dto)
    {
        var clientId = _clientState.GetClientId(Context.ConnectionId);
        if (clientId == null) return;

        _clientState.UpdateHeartbeat(dto);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client != null)
        {
            client.LastSeen = DateTime.UtcNow;
            client.CurrentUser = dto.CurrentUser;
            client.InteractiveUser = dto.InteractiveUser;
            client.InteractiveSessionId = dto.InteractiveSessionId;
            client.AgentSessionId = dto.AgentSessionId;
            client.CpuPercent = dto.CpuPercent;
            client.MemoryPercent = dto.MemoryPercent;
            client.ActiveProcess = dto.ActiveProcess;
        }
        await db.SaveChangesWithRetryAsync();
    }

    public async Task SendActivityBatch(List<ActivityLogDto> logs)
    {
        var clientId = _clientState.GetClientId(Context.ConnectionId);
        if (clientId == null || logs.Count == 0) return;

        const int MaxBatch = 2000;
        if (logs.Count > MaxBatch)
            logs = logs.GetRange(logs.Count - MaxBatch, MaxBatch);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entities = logs.Select(log => new ActivityLog
        {
            ClientId = clientId,
            EventType = log.EventType,
            Details = log.Details,
            ProcessName = log.ProcessName,
            WindowTitle = log.WindowTitle,
            Timestamp = log.Timestamp
        }).ToList();

        db.ActivityLogs.AddRange(entities);
        await db.SaveChangesWithRetryAsync();
    }

    public async Task<string> ExecuteCommand(JsonElement commandPayload)
    {
        var clientId = _clientState.GetClientId(Context.ConnectionId);
        if (clientId == null) return "ERROR: Not registered";

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var command = JsonSerializer.Deserialize<CommandRequestDto>(commandPayload.GetRawText());
        if (command == null) return "ERROR: Invalid command payload";

        var cmdHistory = new CommandHistory
        {
            ClientId = clientId,
            Command = command.Command,
            Args = "",
            SentBy = "system",
            SentAt = DateTime.UtcNow,
            Status = "executing"
        };
        db.CommandHistories.Add(cmdHistory);
        await db.SaveChangesAsync();

        var commandId = cmdHistory.Id;

        var output = command.Command;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(command.TimeoutSeconds > 0 ? command.TimeoutSeconds : 60));
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command.Command.Replace("\"", "\\\"")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\nSTDERR:\n{stderr}";
                cmdHistory.ExitCode = proc.ExitCode;
                cmdHistory.Status = proc.ExitCode == 0 ? "completed" : "failed";
            }
        }
        catch (OperationCanceledException)
        {
            output = "ERROR: Command timed out";
            cmdHistory.Status = "timed_out";
        }
        catch (Exception ex)
        {
            output = $"ERROR: {ex.Message}";
            cmdHistory.Status = "error";
        }

        cmdHistory.Output = output;
        cmdHistory.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await Clients.Group("dashboard").SendAsync("CommandExecuted", new
        {
            clientId,
            commandId = cmdHistory.Id,
            command = command.Command,
            status = cmdHistory.Status,
            timestamp = DateTime.UtcNow
        });

        return output;
    }

    public Task UpdateActiveProcess(string processName, string windowTitle)
    {
        var clientId = _clientState.GetClientId(Context.ConnectionId);
        if (clientId == null) return Task.CompletedTask;

        _clientState.UpdateActiveProcess(clientId, processName, windowTitle);
        return Task.CompletedTask;
    }

    public async Task ReportCommandResult(CommandResultDto result)
    {
        var clientId = _clientState.GetClientId(Context.ConnectionId);
        if (clientId == null) return;

        using var scope = _scopeFactory.CreateScope();
        var commandService = scope.ServiceProvider.GetRequiredService<CommandService>();
        await commandService.UpdateResultAsync(result.CommandId, result);
    }

    public Task ReportInteractiveResult(Dictionary<string, object?> data)
    {
        var reqId = data.TryGetValue("requestId", out var r) ? r?.ToString() ?? "" : "";
        var success = data.TryGetValue("success", out var s) && s is bool b && b;
        var result = data.TryGetValue("result", out var res) ? res?.ToString() ?? "" : "";

        var output = success ? result : $"ERROR: {result}";
        if (!string.IsNullOrEmpty(reqId))
        {
            using var scope = _scopeFactory.CreateScope();
            var cmdSvc = scope.ServiceProvider.GetRequiredService<CommandService>();
            cmdSvc.CompleteInteractive(reqId, output);
        }
        return Task.CompletedTask;
    }
}
