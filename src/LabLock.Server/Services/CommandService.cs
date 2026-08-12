using System.Collections.Concurrent;
using LabLock.Server.Data;
using LabLock.Server.Hubs;
using LabLock.Server.Models;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Services;

public class CommandService
{
    private sealed class PendingEntry
    {
        public required TaskCompletionSource<CommandResultDto> Tcs { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    private sealed class PendingInteractiveEntry
    {
        public required TaskCompletionSource<string> Tcs { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    private static readonly ConcurrentDictionary<int, PendingEntry> _pendingCommands = new();
    private static readonly ConcurrentDictionary<string, PendingInteractiveEntry> _pendingInteractive = new();

    private static void PutPendingCommand(int commandId, TaskCompletionSource<CommandResultDto> tcs, DateTime createdAt)
    {
        SweepStaleCommands();
        _pendingCommands[commandId] = new PendingEntry { Tcs = tcs, CreatedAt = createdAt };
    }

    private static void SweepStaleCommands()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (var kvp in _pendingCommands)
        {
            if (kvp.Value.CreatedAt < cutoff && _pendingCommands.TryRemove(kvp.Key, out var entry))
                entry.Tcs.TrySetCanceled();
        }
    }

    private readonly AppDbContext _db;
    private readonly IHubContext<ClientHub> _hubContext;
    private readonly ClientStateService _clientState;
    private readonly ILogger<CommandService> _logger;

    public CommandService(
        AppDbContext db,
        IHubContext<ClientHub> hubContext,
        ClientStateService clientState,
        ILogger<CommandService> logger)
    {
        _db = db;
        _hubContext = hubContext;
        _clientState = clientState;
        _logger = logger;
    }

    public async Task<int> SendCommandAsync(string clientId, string command, string sentBy, int timeoutSec = 60)
    {
        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null)
            throw new InvalidOperationException($"Client {clientId} is not online");

        var entry = new CommandHistory
        {
            ClientId = clientId,
            Command = command,
            Status = "pending",
            SentAt = DateTime.UtcNow,
            SentBy = sentBy
        };

        _db.CommandHistories.Add(entry);
        await _db.SaveChangesWithRetryAsync();

        var request = new CommandRequestDto { Command = command, TimeoutSeconds = timeoutSec };
        await _hubContext.Clients.Client(connId).SendAsync("ExecuteCommand", entry.Id, request);

        return entry.Id;
    }

    public async Task UpdateResultAsync(int commandId, CommandResultDto result)
    {
        var entry = await _db.CommandHistories.FindAsync(commandId);
        if (entry == null) return;

        entry.Output = result.Output;
        entry.Error = result.Error;
        entry.ExitCode = result.ExitCode;
        entry.Status = result.ExitCode == 0 ? "completed" : "failed";
        entry.CompletedAt = DateTime.UtcNow;
        entry.SessionId = result.SessionId;
        entry.InteractiveSessionId = result.InteractiveSessionId;
        entry.InteractiveUser = result.InteractiveUser;
        entry.RunAsUser = result.RunAsUser;
        entry.OsVersion = result.OsVersion;
        entry.WorkingDirectory = result.WorkingDirectory;
        entry.DurationMs = result.DurationMs;
        await _db.SaveChangesWithRetryAsync();

        if (_pendingCommands.TryRemove(commandId, out var pending))
            pending.Tcs.TrySetResult(result);
    }

    public async Task<string> ExecuteAndWaitAsync(string clientId, string command, int timeoutSec = 60)
    {
        var tcs = new TaskCompletionSource<CommandResultDto>();
        var commandId = await SendCommandAsync(clientId, command, "ai", timeoutSec);
        PutPendingCommand(commandId, tcs, DateTime.UtcNow);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec + 5));
        cts.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            var result = await tcs.Task;
            return FormatResult(result);
        }
        catch (TaskCanceledException)
        {
            _pendingCommands.TryRemove(commandId, out _);
            return "ERROR: Command timed out";
        }
    }

    public static string FormatResult(CommandResultDto result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Exit code   : {result.ExitCode}");
        sb.AppendLine($"Run-as      : {result.RunAsUser} (session {result.SessionId})");
        sb.AppendLine($"Interactive : session {result.InteractiveSessionId} ({result.InteractiveUser})");
        sb.AppendLine($"OS          : {result.OsVersion}");
        sb.AppendLine($"Working dir : {result.WorkingDirectory}");
        sb.AppendLine($"Duration    : {result.DurationMs} ms");
        sb.AppendLine($"Executed    : {result.ExecutedAt:yyyy-MM-dd HH:mm:ss} UTC");
        if (!string.IsNullOrEmpty(result.Error))
            sb.AppendLine($"STDERR      : {result.Error}");
        sb.AppendLine();
        sb.Append(result.Output);
        return sb.ToString();
    }

    public async Task<string> ExecuteInteractiveAndWaitAsync(string clientId, string action,
        Dictionary<string, object?>? parameters, int timeoutMs)
    {
        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null)
            throw new InvalidOperationException($"Client {clientId} is not online");

        var reqId = Guid.NewGuid().ToString("N");
        var payload = new Dictionary<string, object?>
        {
            ["requestId"] = reqId,
            ["action"] = action,
            ["params"] = parameters ?? new Dictionary<string, object?>(),
            ["timeoutMs"] = timeoutMs
        };

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = DateTime.UtcNow;
        _pendingInteractive[reqId] = new PendingInteractiveEntry { Tcs = tcs, CreatedAt = created };

        try
        {
            await _hubContext.Clients.Client(connId).SendAsync("ExecuteInteractive", (object)payload);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs + 5000));
            cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch (TaskCanceledException)
        {
            return "ERROR: Interactive command timed out";
        }
        finally
        {
            _pendingInteractive.TryRemove(reqId, out _);
        }
    }

    public void CompleteInteractive(string requestId, string result)
    {
        if (_pendingInteractive.TryGetValue(requestId, out var entry))
            entry.Tcs.TrySetResult(result);
    }

    public async Task<List<CommandHistory>> GetHistoryAsync(string? clientId, int limit = 50)
    {
        var query = _db.CommandHistories.AsQueryable();
        if (clientId != null)
            query = query.Where(c => c.ClientId == clientId);

        return await query.OrderByDescending(c => c.SentAt).Take(limit).ToListAsync();
    }

    public async Task<CommandHistory?> GetByIdAsync(int id)
    {
        return await _db.CommandHistories.FindAsync(id);
    }
}
