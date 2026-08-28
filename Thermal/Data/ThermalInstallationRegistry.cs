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

        var siteUsers = await _db.ThermalSiteConfigs.AsNoTracking()
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (siteUsers.Count == 1) return siteUsers[0];
        if (siteUsers.Count > 1)
        {
            if (siteUsers.Contains(requestedUserId, StringComparer.Ordinal)) return requestedUserId;
            throw new InvalidOperationException("Flera termiska installationer finns; välj den befintliga ägaren innan konfigurationen ändras.");
        }

        // On first use, bind the single thermal installation to the existing
        // legacy DHW owner whenever that owner can be determined unambiguously.
        var automaticLegacyUsers = await _db.UserSettings.AsNoTracking()
            .Where(x => x.AutoApplySchedule)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (automaticLegacyUsers.Count == 1) return automaticLegacyUsers[0];

        var tokenUsers = await _db.DaikinTokens.AsNoTracking()
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return tokenUsers.Count == 1 ? tokenUsers[0] : requestedUserId;
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
