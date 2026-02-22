using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;
using Microsoft.Extensions.Configuration;

namespace Prisstyrning.Data.Repositories;

public class UserSettingsRepository
{
    private readonly PrisstyrningDbContext _db;
    private readonly IConfiguration _cfg;

    public UserSettingsRepository(PrisstyrningDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public async Task<UserScheduleSettings> LoadScheduleSettingsAsync(string? userId)
    {
        int comfortHours = int.TryParse(_cfg["Schedule:ComfortHours"], out var ch) ? Math.Clamp(ch, 1, 12) : 3;
        double turnOffPercentile = double.TryParse(_cfg["Schedule:TurnOffPercentile"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tp) ? Math.Clamp(tp, 0.5, 0.99) : 0.9;
        int maxComfortGapHours = int.TryParse(_cfg["Schedule:MaxComfortGapHours"], out var mcgh) ? Math.Clamp(mcgh, 1, 72) : 28;

        string schedulingMode = "Classic";
        int ecoIntervalHours = 24;
        int ecoFlexibilityHours = 12;
        int comfortIntervalDays = 21;
        int comfortFlexibilityDays = 7;
        double comfortEarlyPercentile = 0.10;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var entity = await _db.UserSettings.FindAsync(userId);
            if (entity != null)
            {
                comfortHours = Math.Clamp(entity.ComfortHours, 1, 12);
                turnOffPercentile = Math.Clamp(entity.TurnOffPercentile, 0.5, 0.99);
                maxComfortGapHours = Math.Clamp(entity.MaxComfortGapHours, 1, 72);
                schedulingMode = (entity.SchedulingMode == "Classic" || entity.SchedulingMode == "Flexible")
                    ? entity.SchedulingMode : "Classic";
                ecoIntervalHours = Math.Clamp(entity.EcoIntervalHours, 6, 36);
                ecoFlexibilityHours = Math.Clamp(entity.EcoFlexibilityHours, 1, 18);
                comfortIntervalDays = Math.Clamp(entity.ComfortIntervalDays, 7, 90);
                comfortFlexibilityDays = Math.Clamp(entity.ComfortFlexibilityDays, 1, 30);
                comfortEarlyPercentile = Math.Clamp(entity.ComfortEarlyPercentile, 0.01, 0.50);
            }
        }

        return new UserScheduleSettings(comfortHours, turnOffPercentile, maxComfortGapHours,
            schedulingMode, ecoIntervalHours, ecoFlexibilityHours,
            comfortIntervalDays, comfortFlexibilityDays, comfortEarlyPercentile);
    }

    public UserScheduleSettings LoadScheduleSettings(string? userId)
        => LoadScheduleSettingsAsync(userId).GetAwaiter().GetResult();

    public async Task<string> GetUserZoneAsync(string? userId)
    {
        var def = _cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
        if (string.IsNullOrWhiteSpace(userId)) return def;
        var entity = await _db.UserSettings.FindAsync(userId);
        if (entity != null && IsValidZone(entity.Zone)) return entity.Zone;
        return def;
    }

    public string GetUserZone(string? userId)
        => GetUserZoneAsync(userId).GetAwaiter().GetResult();

    public async Task<bool> SetUserZoneAsync(string? userId, string zone)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (!IsValidZone(zone)) return false;
        zone = zone.Trim().ToUpperInvariant();

        var entity = await _db.UserSettings.FindAsync(userId);
        if (entity == null)
        {
            entity = new UserSettings { UserId = userId, Zone = zone };
            _db.UserSettings.Add(entity);
        }
        else
        {
            entity.Zone = zone;
        }
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<UserSettings> GetOrCreateAsync(string userId)
    {
        var entity = await _db.UserSettings.FindAsync(userId);
        if (entity == null)
        {
            entity = new UserSettings { UserId = userId };
        }
        return entity;
    }

    public async Task SaveSettingsAsync(string userId, int comfortHours, double turnOffPercentile, bool autoApplySchedule, int maxComfortGapHours,
        string? schedulingMode = null, int? ecoIntervalHours = null, int? ecoFlexibilityHours = null,
        int? comfortIntervalDays = null, int? comfortFlexibilityDays = null, double? comfortEarlyPercentile = null)
    {
        var entity = await _db.UserSettings.FindAsync(userId);
        if (entity == null)
        {
            entity = new UserSettings { UserId = userId };
            _db.UserSettings.Add(entity);
        }
        entity.ComfortHours = comfortHours;
        entity.TurnOffPercentile = turnOffPercentile;
        entity.AutoApplySchedule = autoApplySchedule;
        entity.MaxComfortGapHours = maxComfortGapHours;
        if (schedulingMode != null) entity.SchedulingMode = schedulingMode;
        if (ecoIntervalHours.HasValue) entity.EcoIntervalHours = ecoIntervalHours.Value;
        if (ecoFlexibilityHours.HasValue) entity.EcoFlexibilityHours = ecoFlexibilityHours.Value;
        if (comfortIntervalDays.HasValue) entity.ComfortIntervalDays = comfortIntervalDays.Value;
        if (comfortFlexibilityDays.HasValue) entity.ComfortFlexibilityDays = comfortFlexibilityDays.Value;
        if (comfortEarlyPercentile.HasValue) entity.ComfortEarlyPercentile = comfortEarlyPercentile.Value;
        await _db.SaveChangesAsync();
    }

    public async Task<List<string>> GetAutoApplyUserIdsAsync()
    {
        return await _db.UserSettings
            .Where(u => u.AutoApplySchedule)
            .Select(u => u.UserId)
            .ToListAsync();
    }

    public async Task<List<string>> GetAllUserZonesAsync()
    {
        return await _db.UserSettings
            .Select(u => u.Zone)
            .Distinct()
            .ToListAsync();
    }

    public static bool IsValidZone(string? z)
    {
        if (string.IsNullOrWhiteSpace(z)) return false;
        z = z.Trim().ToUpperInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(z, "^(SE[1-4]|NO[1-9]|DK[12]|FI|EE|LV|LT)$");
    }

    public async Task DeleteAsync(string userId)
    {
        var entity = await _db.UserSettings.FindAsync(userId);
        if (entity != null)
        {
            _db.UserSettings.Remove(entity);
            await _db.SaveChangesAsync();
        }
    }
}

public sealed record UserScheduleSettings(
    int ComfortHours,
    double TurnOffPercentile,
    int MaxComfortGapHours,
    string SchedulingMode = "Classic",
    int EcoIntervalHours = 24,
    int EcoFlexibilityHours = 12,
    int ComfortIntervalDays = 21,
    int ComfortFlexibilityDays = 7,
    double ComfortEarlyPercentile = 0.10);
