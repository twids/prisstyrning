using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Data.Repositories;

public class UserScheduleEntryRepository
{
    private readonly PrisstyrningDbContext _db;

    public UserScheduleEntryRepository(PrisstyrningDbContext db) { _db = db; }

    public async Task<UserScheduleEntry> AddAsync(UserScheduleEntry entry)
    {
        var futureCount = await _db.UserScheduleEntries
            .CountAsync(e => e.UserId == entry.UserId && e.ScheduledTimeUtc > DateTimeOffset.UtcNow);

        if (futureCount >= 10)
            throw new InvalidOperationException("Maximum of 10 future schedule entries per user.");

        _db.UserScheduleEntries.Add(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    public async Task<List<UserScheduleEntry>> GetFutureEntriesAsync(string userId)
    {
        return await _db.UserScheduleEntries
            .Where(e => e.UserId == userId && e.ScheduledTimeUtc > DateTimeOffset.UtcNow)
            .OrderBy(e => e.ScheduledTimeUtc)
            .ToListAsync();
    }

    public async Task<bool> RemoveAsync(string userId, int id)
    {
        var entry = await _db.UserScheduleEntries
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (entry == null) return false;

        _db.UserScheduleEntries.Remove(entry);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task CleanupPastEntriesAsync(string userId)
    {
        var past = await _db.UserScheduleEntries
            .Where(e => e.UserId == userId && e.ScheduledTimeUtc <= DateTimeOffset.UtcNow)
            .ToListAsync();

        _db.UserScheduleEntries.RemoveRange(past);
        await _db.SaveChangesAsync();
    }
}
