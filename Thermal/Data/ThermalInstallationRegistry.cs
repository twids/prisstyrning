using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Data;

/// <summary>
/// Resolves the real user identities that own thermal installations. Thermal
/// workers must never fall back to a synthetic "default" identity because the
/// same identity also owns the corresponding ONECTA authorization.
/// </summary>
public sealed class ThermalInstallationRegistry
{
    private readonly PrisstyrningDbContext _db;

    public ThermalInstallationRegistry(PrisstyrningDbContext db) => _db = db;

    public async Task<string> ResolveUserAsync(
        string requestedUserId,
        CancellationToken cancellationToken)
    {
        if (!AdminService.IsValidUserId(requestedUserId))
            throw new ArgumentException("Invalid thermal installation user id.", nameof(requestedUserId));
        // Account isolation is the boundary: never redirect a signed-in account
        // to the sole installation found in the database. Legacy ownership is
        // migrated explicitly by migration code, not inferred on requests.
        await Task.CompletedTask;
        return requestedUserId;
    }

    public async Task<IReadOnlyList<string>> GetUsersAsync(
        bool includeLegacy,
        bool activeLwtOnly,
        CancellationToken cancellationToken)
    {
        var sites = await _db.ThermalSiteConfigs.AsNoTracking()
            .Select(x => new { x.UserId, x.ControlMode })
            .ToListAsync(cancellationToken);
        return sites
            .Where(x =>
            {
                var mode = ThermalEnumParser.ControlModeOrLegacy(x.ControlMode);
                if (activeLwtOnly) return mode is ControlMode.LwtActive or ControlMode.FullActive;
                return includeLegacy || mode != ControlMode.Legacy;
            })
            .Select(x => x.UserId)
            .Where(AdminService.IsValidUserId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
