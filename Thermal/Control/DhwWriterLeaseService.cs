using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Control;

/// <summary>
/// Database-backed exclusion for ONECTA schedule writes. Acquisition also
/// verifies the selected writer, so a stale worker cannot write after handoff.
/// </summary>
public sealed class DhwWriterLeaseService
{
    private readonly PrisstyrningDbContext _db;

    public DhwWriterLeaseService(PrisstyrningDbContext db) => _db = db;

    public async Task<bool> TryAcquireAsync(
        string userId,
        DhwWriter writer,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var site = await _db.ThermalSiteConfigs.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
            if (site is null)
            {
                if (writer != DhwWriter.Legacy) return false;
                site = new Prisstyrning.Data.Entities.ThermalSiteConfig { UserId = userId };
                _db.ThermalSiteConfigs.Add(site);
                if (!await _db.ThermalControlStates.AnyAsync(x => x.UserId == userId, cancellationToken))
                    _db.ThermalControlStates.Add(new Prisstyrning.Data.Entities.ThermalControlState { UserId = userId });
            }
            if (ThermalEnumParser.DhwWriterOrLegacy(site.DhwWriter) != writer ||
                (site.DhwLeaseOwner is not null && site.DhwLeaseOwner != owner && site.DhwLeaseExpiresUtc >= now))
                return false;
            site.DhwLeaseOwner = owner;
            site.DhwLeaseExpiresUtc = now.Add(duration);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var updated = await _db.ThermalSiteConfigs
            .Where(x => x.UserId == userId && x.DhwWriter == writer.ToString() &&
                        (x.DhwLeaseOwner == null || x.DhwLeaseOwner == owner ||
                         x.DhwLeaseExpiresUtc == null || x.DhwLeaseExpiresUtc < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DhwLeaseOwner, owner)
                .SetProperty(x => x.DhwLeaseExpiresUtc, now.Add(duration)), cancellationToken);
        if (updated == 1) return true;
        if (writer != DhwWriter.Legacy || await _db.ThermalSiteConfigs.AnyAsync(x => x.UserId == userId, cancellationToken))
            return false;

        _db.ThermalSiteConfigs.Add(new Prisstyrning.Data.Entities.ThermalSiteConfig
        {
            UserId = userId,
            DhwLeaseOwner = owner,
            DhwLeaseExpiresUtc = now.Add(duration)
        });
        _db.ThermalControlStates.Add(new Prisstyrning.Data.Entities.ThermalControlState { UserId = userId });
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

    public async Task ReleaseAsync(string userId, string owner, CancellationToken cancellationToken = default)
    {
        if (!_db.Database.IsRelational())
        {
            var site = await _db.ThermalSiteConfigs.SingleOrDefaultAsync(
                x => x.UserId == userId && x.DhwLeaseOwner == owner,
                cancellationToken);
            if (site is null) return;
            site.DhwLeaseOwner = null;
            site.DhwLeaseExpiresUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }
        _ = await _db.ThermalSiteConfigs
            .Where(x => x.UserId == userId && x.DhwLeaseOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DhwLeaseOwner, (string?)null)
                .SetProperty(x => x.DhwLeaseExpiresUtc, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task<bool> TrySwitchWriterAsync(
        string userId,
        DhwWriter expected,
        DhwWriter target,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_db.Database.IsRelational())
        {
            var site = await _db.ThermalSiteConfigs.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
            if (site is null || ThermalEnumParser.DhwWriterOrLegacy(site.DhwWriter) != expected ||
                (site.DhwLeaseOwner is not null && site.DhwLeaseExpiresUtc >= now))
                return false;
            site.DhwWriter = target.ToString();
            site.DhwLeaseOwner = null;
            site.DhwLeaseExpiresUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        var updated = await _db.ThermalSiteConfigs
            .Where(x => x.UserId == userId && x.DhwWriter == expected.ToString() &&
                        (x.DhwLeaseOwner == null || x.DhwLeaseExpiresUtc == null || x.DhwLeaseExpiresUtc < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DhwWriter, target.ToString())
                .SetProperty(x => x.DhwLeaseOwner, (string?)null)
                .SetProperty(x => x.DhwLeaseExpiresUtc, (DateTimeOffset?)null), cancellationToken);
        return updated == 1;
    }
}
