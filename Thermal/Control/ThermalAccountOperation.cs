using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;

namespace Prisstyrning.Thermal.Control;

/// <summary>Serializes settings, mode changes and LWT evaluations across application instances.</summary>
internal static class ThermalAccountOperation
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TestLocks = new();

    internal static async Task<IAsyncDisposable> EnterAsync(PrisstyrningDbContext db, string userId, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            // Only the test provider is non-relational. Production always uses PostgreSQL.
            var semaphore = TestLocks.GetOrAdd(userId, _ => new(1, 1));
            await semaphore.WaitAsync(ct);
            return new Release(() => { semaphore.Release(); return ValueTask.CompletedTask; });
        }
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed) await db.Database.OpenConnectionAsync(ct);
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("thermal-control:" + userId)));
        try
        {
            await using var command = Command(connection, "SELECT pg_advisory_lock(@key)", key);
            command.CommandTimeout = 30;
            await command.ExecuteScalarAsync(ct);
        }
        catch
        {
            // Closing also releases a lock acquired just as cancellation occurred.
            await db.Database.CloseConnectionAsync();
            throw;
        }
        return new Release(async () =>
        {
            try
            {
                await using var command = Command(connection, "SELECT pg_advisory_unlock(@key)", key);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            finally { if (wasClosed) await db.Database.CloseConnectionAsync(); }
        });
    }

    private static DbCommand Command(DbConnection connection, string sql, long key)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        return command;
    }

    private sealed class Release(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }
}
