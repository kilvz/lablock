using System.Collections.Concurrent;
using LabLock.Shared.Models;
using Microsoft.Data.Sqlite;

namespace LabLock.Client.Services;

public class LogBufferService : IDisposable
{
    private readonly ConcurrentQueue<ActivityLogDto> _buffer = new();
    private readonly string _dbPath = Path.Combine(AppContext.BaseDirectory, "buffer.db");
    private SqliteConnection? _db;

    public LogBufferService()
    {
        InitializeDb();
    }

    private void InitializeDb()
    {
        _db = new SqliteConnection($"Data Source={_dbPath}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS buffered_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id TEXT NOT NULL,
                event_type INTEGER NOT NULL,
                details TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                username TEXT NOT NULL
            )
        """;
        cmd.ExecuteNonQuery();
    }

    public void Add(ActivityLogDto log)
    {
        _buffer.Enqueue(log);
        if (_buffer.Count > 5000)
            OverflowToDb();
    }

    public ActivityLogDto[] FlushBatch(int maxCount = 500)
    {
        var results = new List<ActivityLogDto>();
        while (results.Count < maxCount && _buffer.TryDequeue(out var log))
            results.Add(log);

        if (results.Count < maxCount)
            results.AddRange(ReadFromDb(maxCount - results.Count));

        return results.ToArray();
    }

    private void OverflowToDb()
    {
        var toStore = new List<ActivityLogDto>();
        int count = _buffer.Count / 2;
        for (int i = 0; i < count && _buffer.TryDequeue(out var log); i++)
            toStore.Add(log);
        BulkInsertToDb(toStore);
    }

    private void BulkInsertToDb(List<ActivityLogDto> logs)
    {
        if (_db == null || logs.Count == 0) return;

        using var transaction = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO buffered_logs (client_id, event_type, details, timestamp, username)
            VALUES (@client_id, @event_type, @details, @timestamp, @username)
        """;

        var pClientId = cmd.Parameters.Add("@client_id", SqliteType.Text);
        var pEventType = cmd.Parameters.Add("@event_type", SqliteType.Integer);
        var pDetails = cmd.Parameters.Add("@details", SqliteType.Text);
        var pTimestamp = cmd.Parameters.Add("@timestamp", SqliteType.Text);
        var pUsername = cmd.Parameters.Add("@username", SqliteType.Text);

        foreach (var log in logs)
        {
            pClientId.Value = log.ClientId;
            pEventType.Value = (int)log.EventType;
            pDetails.Value = log.Details;
            pTimestamp.Value = log.Timestamp.ToString("O");
            pUsername.Value = log.Username;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private List<ActivityLogDto> ReadFromDb(int maxCount)
    {
        var results = new List<ActivityLogDto>();
        if (_db == null || maxCount <= 0) return results;

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT client_id, event_type, details, timestamp, username FROM buffered_logs ORDER BY id LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", maxCount);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityLogDto
            {
                ClientId = reader.GetString(0),
                EventType = (EventType)reader.GetInt32(1),
                Details = reader.GetString(2),
                Timestamp = DateTime.Parse(reader.GetString(3)),
                Username = reader.GetString(4)
            });
        }

        DeleteReadFromDb(results.Count);
        return results;
    }

    private void DeleteReadFromDb(int count)
    {
        if (_db == null || count <= 0) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM buffered_logs WHERE id IN (SELECT id FROM buffered_logs ORDER BY id LIMIT @limit)";
        cmd.Parameters.AddWithValue("@limit", count);
        cmd.ExecuteNonQuery();
    }

    public void PersistAll()
    {
        var all = new List<ActivityLogDto>();
        while (_buffer.TryDequeue(out var log))
            all.Add(log);
        BulkInsertToDb(all);
    }

    public void PurgeOldLogs(int maxDays)
    {
        if (_db == null) return;
        var cutoff = DateTime.UtcNow.AddDays(-maxDays);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM buffered_logs WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        PersistAll();
        _db?.Dispose();
    }
}
