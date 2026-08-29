using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Control;

public sealed class WriterLeaseService
{
    private readonly PrisstyrningDbContext _db;

    public WriterLeaseService(PrisstyrningDbContext db) => _db = db;

    public async Task<bool> TryAcquireOrRenewAsync(
        string userId,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await _db.ThermalControlStates
            .Where(x => x.UserId == userId &&
                        (x.LeaseOwner == null || x.LeaseOwner == owner || x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, owner)
                .SetProperty(x => x.LeaseExpiresUtc, now.Add(duration))
                .SetProperty(x => x.LastHeartbeatUtc, now), cancellationToken);
        if (updated == 1) return true;
        if (await _db.ThermalControlStates.AnyAsync(x => x.UserId == userId, cancellationToken)) return false;

        _db.ThermalControlStates.Add(new ThermalControlState
        {
            UserId = userId,
            LeaseOwner = owner,
            LeaseExpiresUtc = now.Add(duration),
            LastHeartbeatUtc = now
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }
}
