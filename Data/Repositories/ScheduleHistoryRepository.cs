using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;
using System.Text.Json.Nodes;

namespace Prisstyrning.Data.Repositories;

public class ScheduleHistoryRepository
{
    private readonly PrisstyrningDbContext _db;

    public ScheduleHistoryRepository(PrisstyrningDbContext db) { _db = db; }

    /// <summary>
    /// Save a new history entry.
    /// </summary>
    public async Task SaveAsync(string userId, JsonObject schedulePayload, DateTimeOffset timestamp)
    {
        var entry = new ScheduleHistoryEntry
        {
            UserId = userId.Trim(),
            Timestamp = timestamp,
            SchedulePayloadJson = schedulePayload.ToJsonString()
        };
        _db.ScheduleHistory.Add(entry);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Load all history entries for a user, ordered by timestamp descending (newest first).
    /// </summary>
    public async Task<List<ScheduleHistoryEntry>> LoadAsync(string userId)
    {
        return await _db.ScheduleHistory
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync();
    }

    /// <summary>
    /// Load history entries for a user within a date range (inclusive).
    /// </summary>
    public async Task<List<ScheduleHistoryEntry>> LoadByDateRangeAsync(string userId, DateTimeOffset from, DateTimeOffset to)
    {
        return await _db.ScheduleHistory
            .Where(e => e.UserId == userId && e.Timestamp >= from && e.Timestamp <= to)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync();
    }

    /// <summary>
    /// Delete entries older than cutoff for a specific user. Returns count deleted.
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(string userId, DateTimeOffset cutoff)
    {
        var old = await _db.ScheduleHistory
            .Where(e => e.UserId == userId && e.Timestamp < cutoff)
            .ToListAsync();
        _db.ScheduleHistory.RemoveRange(old);
        await _db.SaveChangesAsync();
        return old.Count;
    }

    /// <summary>
    /// Delete ALL entries older than cutoff across all users (for global cleanup jobs).
    /// Returns count deleted.
    /// </summary>
    public async Task<int> DeleteAllOlderThanAsync(DateTimeOffset cutoff)
    {
        var old = await _db.ScheduleHistory
            .Where(e => e.Timestamp < cutoff)
            .ToListAsync();
        _db.ScheduleHistory.RemoveRange(old);
        await _db.SaveChangesAsync();
        return old.Count;
    }

    /// <summary>
    /// Get count of entries for a user.
    /// </summary>
    public async Task<int> CountAsync(string userId)
    {
        return await _db.ScheduleHistory.CountAsync(e => e.UserId == userId);
    }
}
