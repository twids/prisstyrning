using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;
using System.Text.Json.Nodes;

namespace Prisstyrning.Data.Repositories;

public class PriceRepository
{
    private readonly PrisstyrningDbContext _db;

    public PriceRepository(PrisstyrningDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Save a price snapshot (upsert by zone+date).
    /// </summary>
    public async Task SaveSnapshotAsync(string zone, DateOnly date, JsonArray today, JsonArray tomorrow)
    {
        zone = zone.Trim().ToUpperInvariant();

        var existing = await _db.PriceSnapshots
            .FirstOrDefaultAsync(s => s.Zone == zone && s.Date == date);

        if (existing != null)
        {
            existing.TodayPricesJson = today.ToJsonString();
            existing.TomorrowPricesJson = tomorrow.ToJsonString();
            existing.SavedAtUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            _db.PriceSnapshots.Add(new PriceSnapshot
            {
                Zone = zone,
                Date = date,
                SavedAtUtc = DateTimeOffset.UtcNow,
                TodayPricesJson = today.ToJsonString(),
                TomorrowPricesJson = tomorrow.ToJsonString()
            });
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Get latest snapshot for a zone (most recent by date).
    /// </summary>
    public async Task<PriceSnapshot?> GetLatestAsync(string zone)
    {
        zone = zone.Trim().ToUpperInvariant();
        return await _db.PriceSnapshots
            .Where(s => s.Zone == zone)
            .OrderByDescending(s => s.Date)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Get snapshot for specific zone+date.
    /// </summary>
    public async Task<PriceSnapshot?> GetByDateAsync(string zone, DateOnly date)
    {
        zone = zone.Trim().ToUpperInvariant();
        return await _db.PriceSnapshots
            .FirstOrDefaultAsync(s => s.Zone == zone && s.Date == date);
    }

    /// <summary>
    /// Get all snapshots for a zone within a date range (inclusive).
    /// </summary>
    public async Task<List<PriceSnapshot>> GetByDateRangeAsync(string zone, DateOnly from, DateOnly to)
    {
        zone = zone.Trim().ToUpperInvariant();
        return await _db.PriceSnapshots
            .Where(s => s.Zone == zone && s.Date >= from && s.Date <= to)
            .OrderBy(s => s.Date)
            .ToListAsync();
    }

    /// <summary>
    /// Delete old snapshots (retention). Returns count of deleted records.
    /// </summary>
    public async Task<int> DeleteOlderThanAsync(DateOnly cutoff)
    {
        var old = await _db.PriceSnapshots
            .Where(s => s.Date < cutoff)
            .ToListAsync();

        if (old.Count == 0) return 0;

        _db.PriceSnapshots.RemoveRange(old);
        await _db.SaveChangesAsync();
        return old.Count;
    }
}
