using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Data;

public sealed class ThermalDataService
{
    private readonly PrisstyrningDbContext _db;
    private readonly ThermalInstallationRegistry _installations;

    public ThermalDataService(PrisstyrningDbContext db, ThermalInstallationRegistry installations)
    {
        _db = db;
        _installations = installations;
    }

    public async Task<ThermalConfigDto> GetConfigAsync(string userId, CancellationToken cancellationToken = default)
    {
        var site = await EnsureSiteAsync(userId, cancellationToken);
        userId = site.UserId;
        var rooms = await _db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var entities = await _db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Role)
            .ToListAsync(cancellationToken);
        return new ThermalConfigDto(site, rooms, entities);
    }

    public async Task<ThermalConfigDto> UpdateConfigAsync(
        string userId,
        ThermalConfigDto requested,
        CancellationToken cancellationToken = default)
    {
        Validate(requested);
        userId = await _installations.ResolveUserAsync(userId, cancellationToken);
        var site = await EnsureSiteAsync(userId, cancellationToken, tracked: true);
        if (requested.Site.ActiveDeviationLimitC > 1 && site.ActiveDeviationLimitC <= 1)
            await EnsureExtendedDeviationIsReadyAsync(userId, cancellationToken);
        CopySiteSettings(requested.Site, site);
        site.UserId = userId;
        site.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var existingRooms = await _db.ThermalRoomConfigs.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        _db.ThermalRoomConfigs.RemoveRange(existingRooms);
        foreach (var room in requested.Rooms)
        {
            _db.ThermalRoomConfigs.Add(new ThermalRoomConfig
            {
                UserId = userId,
                Name = room.Name.Trim(),
                EntityId = room.EntityId.Trim(),
                TargetOffsetC = room.TargetOffsetC,
                Weight = room.Weight,
                IsCritical = room.IsCritical,
                Enabled = room.Enabled,
                MinimumValidC = room.MinimumValidC,
                MaximumValidC = room.MaximumValidC,
                MaximumRateCPerHour = room.MaximumRateCPerHour
            });
        }

        var existingEntities = await _db.ThermalEntityConfigs.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        _db.ThermalEntityConfigs.RemoveRange(existingEntities);
        foreach (var entity in requested.Entities)
        {
            _db.ThermalEntityConfigs.Add(new ThermalEntityConfig
            {
                UserId = userId,
                Role = entity.Role.Trim().ToLowerInvariant(),
                EntityId = entity.EntityId.Trim(),
                ExpectedUnit = entity.ExpectedUnit.Trim(),
                Enabled = entity.Enabled,
                MinimumValid = entity.MinimumValid,
                MaximumValid = entity.MaximumValid,
                MaximumRatePerHour = entity.MaximumRatePerHour
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return await GetConfigAsync(userId, cancellationToken);
    }

    public async Task<ThermalSiteConfig> EnsureSiteAsync(
        string userId,
        CancellationToken cancellationToken = default,
        bool tracked = false)
    {
        userId = await _installations.ResolveUserAsync(userId, cancellationToken);
        var query = tracked ? _db.ThermalSiteConfigs : _db.ThermalSiteConfigs.AsNoTracking();
        var site = await query.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (site is not null) return site;

        site = new ThermalSiteConfig { UserId = userId, ControlMode = "Legacy", DhwWriter = "Legacy" };
        _db.ThermalSiteConfigs.Add(site);
        _db.ThermalControlStates.Add(new ThermalControlState { UserId = userId });
        await _db.SaveChangesAsync(cancellationToken);
        if (!tracked) _db.Entry(site).State = EntityState.Detached;
        return site;
    }

    private static void Validate(ThermalConfigDto config)
    {
        if (config.Site.BaseRoomTargetC is < 10 or > 30) throw new ArgumentException("Rumsmålet måste vara 10–30 °C.");
        if (config.Site.LowerComfortBandC is < 0 or > 5 || config.Site.UpperComfortBandC is < 0 or > 5)
            throw new ArgumentException("Komfortbandet måste vara 0–5 °C.");
        if (config.Site.ActiveDeviationLimitC is < 0 or > 3) throw new ArgumentException("LWT-avvikelsen måste vara 0–3 °C.");
        if (config.Site.ComfortSetpointC is < 60 or > 65) throw new ArgumentException("Hygienmålet får inte vara lägre än 60 °C.");
        if (config.Site.ComfortIntervalDays is < 1 or > 60 || config.Site.ComfortFlexibilityDays is < 0 or > 14)
            throw new ArgumentException("Ogiltigt hygienintervall.");
        if (string.IsNullOrWhiteSpace(config.Site.TimeZone)) throw new ArgumentException("Tidszon måste anges.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(config.Site.TimeZone); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("Tidszonen finns inte på servern."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("Tidszonen är ogiltig på servern."); }
        if (config.Rooms.GroupBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new ArgumentException("Samma rumsgivare kan inte användas av flera rum.");
        if (config.Entities.GroupBy(x => x.Role, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new ArgumentException("Varje entity-roll får bara konfigureras en gång.");
        if (config.Entities.Any(x => !ThermalEntityRoles.Known.Contains(x.Role)))
            throw new ArgumentException("Konfigurationen innehåller en okänd entity-roll.");
        if (config.Rooms.Any(x => string.IsNullOrWhiteSpace(x.Name) || !IsEntityId(x.EntityId) ||
                                  x.Weight is < 0 or > 100 || x.TargetOffsetC is < -5 or > 5 ||
                                  x.MinimumValidC >= x.MaximumValidC || x.MaximumRateCPerHour <= 0))
            throw new ArgumentException("Rumskonfigurationen är ogiltig.");
        if (config.Entities.Any(x => !IsEntityId(x.EntityId) || string.IsNullOrWhiteSpace(x.ExpectedUnit) ||
                                     x.MinimumValid is { } minimum && x.MaximumValid is { } maximum && minimum >= maximum ||
                                     x.MaximumRatePerHour is <= 0))
            throw new ArgumentException("Entity-konfigurationen är ogiltig.");
        ValidateCostJson(config.Site.VariableCostComponentsJson, config.Site.TariffDefinitionJson);
    }

    private static bool IsEntityId(string? entityId) =>
        !string.IsNullOrWhiteSpace(entityId) && entityId.Length <= 255 && entityId.Contains('.') &&
        entityId.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');

    private static void ValidateCostJson(string variableCostsJson, string tariffJson)
    {
        try
        {
            var costs = JsonSerializer.Deserialize<Dictionary<string, decimal>>(variableCostsJson)
                        ?? throw new ArgumentException("Rörliga kostnader måste vara ett JSON-objekt.");
            if (costs.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is < -10 or > 100))
                throw new ArgumentException("Rörliga kostnader måste ha namn och ligga mellan −10 och 100 SEK/kWh.");

            var tariff = JsonNode.Parse(tariffJson) as JsonObject
                          ?? throw new ArgumentException("Tariffdefinitionen måste vara ett JSON-objekt.");
            if (tariff["capacityCostPerKw"] is { } capacityNode &&
                (capacityNode is not JsonValue capacityValue ||
                 !capacityValue.TryGetValue<double>(out var capacity) ||
                 capacity is < 0 or > 10000))
                throw new ArgumentException("Tariffens kapacitetskostnad måste vara 0–10 000 SEK/kW.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Kostnads- och tariffdefinitionerna måste vara giltig JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException("Kostnads- och tariffdefinitionerna har fel datatyp.", exception);
        }
    }

    private static void CopySiteSettings(ThermalSiteConfig from, ThermalSiteConfig to)
    {
        // Mode and writer are deliberately not editable through the configuration endpoint.
        to.BaseRoomTargetC = from.BaseRoomTargetC;
        to.LowerComfortBandC = from.LowerComfortBandC;
        to.UpperComfortBandC = from.UpperComfortBandC;
        to.ActiveDeviationLimitC = from.ActiveDeviationLimitC;
        to.TariffEnabled = from.TariffEnabled;
        to.HeatPumpPowerSignVerified = from.HeatPumpPowerSignVerified;
        to.WeatherCurveVerified = from.WeatherCurveVerified;
        to.ComfortSetpointConfirmed = from.ComfortSetpointConfirmed;
        to.ComfortSetpointC = from.ComfortSetpointC;
        to.ComfortIntervalDays = from.ComfortIntervalDays;
        to.ComfortFlexibilityDays = from.ComfortFlexibilityDays;
        to.TimeZone = from.TimeZone;
        to.VariableCostComponentsJson = from.VariableCostComponentsJson;
        to.TariffDefinitionJson = from.TariffDefinitionJson;
    }

    private async Task EnsureExtendedDeviationIsReadyAsync(string userId, CancellationToken cancellationToken)
    {
        var activatedUtc = await _db.ThermalEvents.AsNoTracking()
            .Where(x => x.UserId == userId && x.Category == "ControlMode" && x.Message.Contains("till LwtActive"))
            .OrderByDescending(x => x.TimestampUtc)
            .Select(x => (DateTimeOffset?)x.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (activatedUtc is null)
            throw new ArgumentException("LWT måste först aktiveras och verifieras inom ±1 °C.");

        var heatingDays = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= activatedUtc && x.HeatOutputKw > 0.5)
            .Select(x => x.TimestampUtc.Date)
            .Distinct()
            .CountAsync(cancellationToken);
        var criticalEvent = await _db.ThermalEvents.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.TimestampUtc >= activatedUtc && x.Severity == "ActionRequired", cancellationToken);
        if (heatingDays < 7 || criticalEvent)
            throw new ArgumentException($"±3 °C låses upp först efter sju problemfria uppvärmningsdygn i ±1 °C; {heatingDays}/7 är registrerade.");
    }
}
