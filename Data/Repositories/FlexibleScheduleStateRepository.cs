using Prisstyrning.Data.Entities;

namespace Prisstyrning.Data.Repositories;

public class FlexibleScheduleStateRepository
{
    private readonly PrisstyrningDbContext _db;

    public FlexibleScheduleStateRepository(PrisstyrningDbContext db) { _db = db; }

    public async Task<FlexibleScheduleState> GetOrCreateAsync(string userId)
    {
        var entity = await _db.FlexibleScheduleStates.FindAsync(userId);
        if (entity == null)
        {
            entity = new FlexibleScheduleState { UserId = userId };
            _db.FlexibleScheduleStates.Add(entity);
            await _db.SaveChangesAsync();
        }
        return entity;
    }

    public async Task UpdateEcoRunAsync(string userId, DateTimeOffset runTime)
    {
        var entity = await GetOrCreateAsync(userId);
        entity.LastEcoRunUtc = runTime;
        await _db.SaveChangesAsync();
    }

    public async Task UpdateComfortRunAsync(string userId, DateTimeOffset runTime)
    {
        var entity = await GetOrCreateAsync(userId);
        entity.LastComfortRunUtc = runTime;
        // Clear the scheduled comfort since it has now run
        entity.NextScheduledComfortUtc = null;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Schedule or reschedule a future comfort run.
    /// Called each batch cycle to set (or update) when comfort will run.
    /// If new price data reveals a cheaper hour, this is called again
    /// with the new time — effectively postponing to the better price.
    /// </summary>
    public async Task ScheduleComfortRunAsync(string userId, DateTimeOffset scheduledTime)
    {
        var entity = await GetOrCreateAsync(userId);
        entity.NextScheduledComfortUtc = scheduledTime;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Clear any pending scheduled comfort run (e.g., when switching modes).
    /// </summary>
    public async Task ClearScheduledComfortAsync(string userId)
    {
        var entity = await GetOrCreateAsync(userId);
        entity.NextScheduledComfortUtc = null;
        await _db.SaveChangesAsync();
    }
}
