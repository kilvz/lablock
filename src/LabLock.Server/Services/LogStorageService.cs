using LabLock.Server.Data;
using LabLock.Server.Models;
using LabLock.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Services;

public class LogStorageService
{
    private readonly AppDbContext _db;

    public LogStorageService(AppDbContext db)
    {
        _db = db;
    }

    public async Task BulkInsertAsync(ActivityLogDto[] logs)
    {
        var entities = logs.Select(l => new ActivityLog
        {
            ClientId = l.ClientId,
            EventType = l.EventType,
            Details = l.Details,
            Timestamp = l.Timestamp,
            Username = l.Username
        });

        _db.ActivityLogs.AddRange(entities);
        await _db.SaveChangesWithRetryAsync();
    }

    public async Task<(List<ActivityLog> Logs, int Total)> QueryAsync(
        string? clientId, EventType? eventType, DateTime? from, DateTime? to,
        string? keyword, int page = 1, int pageSize = 50)
    {
        var query = _db.ActivityLogs.AsQueryable();

        if (clientId is not null and not "all")
            query = query.Where(l => l.ClientId == clientId);
        if (eventType != null)
            query = query.Where(l => l.EventType == eventType);
        if (from != null)
            query = query.Where(l => l.Timestamp >= from);
        if (to != null)
            query = query.Where(l => l.Timestamp <= to);
        if (keyword != null)
            query = query.Where(l => l.Details.Contains(keyword));

        var total = await query.CountAsync();
        var data = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (data, total);
    }

    public async Task<Dictionary<string, object>> GetStatsAsync(string? clientId, DateTime from)
    {
        var query = _db.ActivityLogs.Where(l => l.Timestamp >= from);
        if (clientId is not null and not "all")
            query = query.Where(l => l.ClientId == clientId);

        var eventCounts = await query
            .GroupBy(l => l.EventType)
            .ToDictionaryAsync(g => g.Key.ToString(), g => (long)g.Count());

        var totalLogs = eventCounts.Values.Sum();
        var activeClients = clientId is not null and not "all"
            ? 1
            : await query.Select(l => l.ClientId).Distinct().CountAsync();

        return new Dictionary<string, object>
        {
            ["totalLogs"] = totalLogs,
            ["activeClients"] = activeClients,
            ["eventCounts"] = eventCounts,
            ["from"] = from,
            ["to"] = DateTime.UtcNow
        };
    }

    public async Task<Dictionary<string, int>> GetEventCountsAsync(string? clientId, DateTime from, DateTime to)
    {
        var query = _db.ActivityLogs.Where(l => l.Timestamp >= from && l.Timestamp <= to);
        if (clientId is not null and not "all")
            query = query.Where(l => l.ClientId == clientId);

        return await query
            .GroupBy(l => l.EventType)
            .ToDictionaryAsync(g => g.Key.ToString(), g => g.Count());
    }

    public async Task PurgeOldLogsAsync(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        await _db.ActivityLogs.Where(l => l.Timestamp < cutoff).ExecuteDeleteAsync();
    }
}
