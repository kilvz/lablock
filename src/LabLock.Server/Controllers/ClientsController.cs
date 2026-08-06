using System.Diagnostics;
using System.Text.Json;
using LabLock.Server.Data;
using LabLock.Server.Hubs;
using LabLock.Server.Services;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/clients")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly ClientStateService _clientState;
    private readonly AppDbContext _db;
    private readonly IHubContext<ClientHub> _hubContext;
    private readonly CommandService _commandService;
    private readonly ClientUpdateService _update;

    public ClientsController(ClientStateService clientState, AppDbContext db, IHubContext<ClientHub> hubContext, CommandService commandService, ClientUpdateService update)
    {
        _clientState = clientState;
        _db = db;
        _hubContext = hubContext;
        _commandService = commandService;
        _update = update;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var dbClients = await _db.Clients.Where(c => c.IsActive).ToListAsync();
        var now = DateTime.UtcNow;

        var result = dbClients.Select(c =>
        {
            return new
            {
                c.ClientId,
                c.Hostname,
                c.IpAddresses,
                c.AgentVersion,
                c.CurrentUser,
                c.InteractiveUser,
                c.InteractiveSessionId,
                c.AgentSessionId,
                c.OsVersion,
                c.CpuName,
                c.CpuPercent,
                c.MemoryPercent,
                c.TotalMemoryMb,
                c.ActiveProcess,
                c.FirstSeen,
                c.LastSeen,
                IsOnline = ClientStateService.IsOnline(c.LastSeen, now)
            };
        }).ToList();

        var serverUptimeSeconds = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;

        return Ok(new
        {
            clients = result,
            total = result.Count,
            online = result.Count(r => r.IsOnline),
            serverUptimeSeconds
        });
    }

    [HttpGet("{clientId}")]
    public async Task<IActionResult> GetById(string clientId)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client == null) return NotFound();

        return Ok(new
        {
            client.ClientId,
            client.Hostname,
            client.IpAddresses,
            client.AgentVersion,
            client.CurrentUser,
            client.InteractiveUser,
            client.InteractiveSessionId,
            client.AgentSessionId,
            client.OsVersion,
            client.CpuName,
            client.CpuPercent,
            client.MemoryPercent,
            client.TotalMemoryMb,
            client.ActiveProcess,
            client.FirstSeen,
            client.LastSeen,
            IsOnline = ClientStateService.IsOnline(client.LastSeen, DateTime.UtcNow)
        });
    }

    [HttpDelete("{clientId}")]
    public async Task<IActionResult> Delete(string clientId)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client == null) return NotFound();

        client.IsActive = false;
        await _db.SaveChangesWithRetryAsync();

        var connId = _clientState.GetConnectionId(clientId);
        if (connId != null)
            await _hubContext.Clients.Client(connId).SendAsync("Disconnect");

        return NoContent();
    }

    [HttpPost("{clientId}/command")]
    public async Task<IActionResult> SendCommand(string clientId, [FromBody] CommandRequestDto command)
    {
        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null)
            return BadRequest(new { error = "Client is not online" });

        var cmdId = await _commandService.SendCommandAsync(clientId, command.Command, "api", command.TimeoutSeconds);

        return Ok(new { message = "Command sent to client", commandId = cmdId });
    }

    [HttpPost("{clientId}/command/wait")]
    public async Task<IActionResult> SendCommandAndWait(string clientId, [FromBody] CommandRequestDto command)
    {
        try
        {
            var output = await _commandService.ExecuteAndWaitAsync(clientId, command.Command, command.TimeoutSeconds);
            return Ok(new { output });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{clientId}/update")]
    public async Task<IActionResult> PushUpdate(string clientId)
    {
        try
        {
            var version = await _update.PushUpdateAsync(clientId);
            return Ok(new { message = "Update pushed to client", version });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{clientId}/system-info")]
    public async Task<IActionResult> GetSystemInfo(string clientId)
    {
        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null)
            return BadRequest(new { error = "Client is not online" });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await _hubContext.Clients
                .Client(connId)
                .InvokeAsync<SystemInfoDto>("GetSystemInfo", cts.Token);

            return Ok(info);
        }
        catch
        {
            return StatusCode(504, new { error = "Client did not respond in time" });
        }
    }

    [HttpPost("{clientId}/interactive")]
    public async Task<IActionResult> SendInteractive(string clientId, [FromBody] InteractiveRequest request)
    {
        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null)
            return BadRequest(new { error = "Client is not online" });

        try
        {
            var parameters = new Dictionary<string, object?>();
            if (!string.IsNullOrEmpty(request.Parameters))
            {
                using var doc = JsonDocument.Parse(request.Parameters);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kvp in doc.RootElement.EnumerateObject())
                        parameters[kvp.Name] = CloneJsonElement(kvp.Value);
                }
            }

            var output = await _commandService.ExecuteInteractiveAndWaitAsync(
                clientId, request.Action, request.Parameters != null ? parameters : new Dictionary<string, object?>(), request.TimeoutMs);
            return Ok(new { output });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (TimeoutException)
        {
            return StatusCode(504, new { error = "Interactive command timed out" });
        }
    }

    private static object? CloneJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(CloneJsonElement).ToList(),
        _ => el.ToString()
    };
}

public class InteractiveRequest
{
    public string Action { get; set; } = "";
    public string? Parameters { get; set; }
    public int TimeoutMs { get; set; } = 30000;
}
