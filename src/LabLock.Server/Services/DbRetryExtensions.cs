using LabLock.Server.Data;
using Microsoft.Data.Sqlite;

namespace LabLock.Server.Services;

public static class DbRetryExtensions
{
    public static async Task SaveChangesWithRetryAsync(this AppDbContext db, int maxAttempts = 10)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (Exception ex) when (IsLockError(ex) && attempt < maxAttempts)
            {
                await Task.Delay(Random.Shared.Next(15, 100) * attempt);
            }
        }
    }

    private static bool IsLockError(Exception ex) =>
        ex is SqliteException s && (s.SqliteErrorCode is 5 or 6);
}
