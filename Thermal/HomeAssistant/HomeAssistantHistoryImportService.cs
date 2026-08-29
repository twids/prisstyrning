using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed record HomeAssistantHistoryImportResult(
    int ImportedSamples,
    int ExistingSamplesPreserved,
    int RequestedEntities,
    IReadOnlyList<string> EntitiesWithoutHistory);

/// <summary>
/// Imports change-based HA history and resamples it to the same five-minute
/// grid as live telemetry. Existing snapshots are never overwritten.
/// </summary>
public sealed class HomeAssistantHistoryImportService
{
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(5);
    private readonly PrisstyrningDbContext _db;
    private readonly IHomeAssistantTelemetryClient _client;

    public HomeAssistantHistoryImportService(PrisstyrningDbContext db, IHomeAssistantTelemetryClient client)
    {
        _db = db;
        _client = client;
    }

    public async Task<HomeAssistantHistoryImportResult> ImportAsync(
        string userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (toUtc <= fromUtc || toUtc - fromUtc < Step || toUtc - fromUtc > TimeSpan.FromDays(90))
            throw new ArgumentException("Historikimporten måste omfatta 5 minuter–90 dagar.");
        if (toUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("Historikimport kan inte göras från framtiden.");

        var site = await _db.ThermalSiteConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? new ThermalSiteConfig { UserId = userId };
        var entityConfigs = await _db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled && x.Role != ThermalEntityRoles.WeatherForecast)
            .ToListAsync(cancellationToken);
        var rooms = await _db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled)
            .ToListAsync(cancellationToken);
        var entityIds = entityConfigs.Select(x => x.EntityId).Concat(rooms.Select(x => x.EntityId))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (entityIds.Length == 0) throw new ArgumentException("Det finns inga aktiverade HA-entities att importera.");

        var cursors = new Dictionary<string, HistoryCursor>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var entityId in entityIds)
        {
            var history = await _client.GetHistoryAsync(entityId, fromUtc, toUtc, cancellationToken);
            if (history.Count == 0) missing.Add(entityId);
            cursors[entityId] = new HistoryCursor(history);
        }

        var firstBucket = CeilingToStep(fromUtc);
        var lastBucket = FloorToStep(toUtc);
        var existingTimestamps = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= firstBucket && x.TimestampUtc <= lastBucket)
            .Select(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
        var existing = existingTimestamps.ToHashSet();
        var imported = 0;
        var preserved = 0;

        for (var bucket = firstBucket; bucket <= lastBucket; bucket = bucket.Add(Step))
        {
            if (existing.Contains(bucket))
            {
                preserved++;
                continue;
            }

            var values = new Dictionary<string, NormalizedSensorValue>(StringComparer.OrdinalIgnoreCase);
            var entityQuality = new JsonObject();
            foreach (var config in entityConfigs)
            {
                var normalized = Normalize(cursors[config.EntityId].At(bucket), config.ExpectedUnit, config.MinimumValid, config.MaximumValid);
                values[config.Role] = normalized;
                entityQuality[config.Role] = Quality(normalized);
            }

            var roomValues = new Dictionary<string, double>();
            var roomQuality = new JsonObject();
            foreach (var room in rooms)
            {
                var normalized = Normalize(cursors[room.EntityId].At(bucket), "°C", room.MinimumValidC, room.MaximumValidC);
                roomQuality[room.EntityId] = Quality(normalized);
                if (normalized.Quality == DataQuality.Valid && normalized.Value is { } value)
                    roomValues[room.EntityId] = value;
            }

            var sample = new ThermalTelemetrySample
            {
                UserId = userId,
                TimestampUtc = bucket,
                OutsideTemperatureForecastJson = "[]",
                OutsideTemperatureC = Numeric(values, ThermalEntityRoles.OutsideTemperature),
                WindSpeedMps = Numeric(values, ThermalEntityRoles.WindSpeed),
                SolarIrradianceWm2 = Numeric(values, ThermalEntityRoles.SolarIrradiance),
                LeavingWaterTemperatureC = Numeric(values, ThermalEntityRoles.LeavingWaterTemperature),
                ReturnWaterTemperatureC = Numeric(values, ThermalEntityRoles.ReturnWaterTemperature),
                FlowLitresPerMinute = Numeric(values, ThermalEntityRoles.Flow),
                BrineInC = Numeric(values, ThermalEntityRoles.BrineIn),
                BrineOutC = Numeric(values, ThermalEntityRoles.BrineOut),
                TankTemperatureC = Numeric(values, ThermalEntityRoles.TankTemperature),
                HeatPumpPowerKw = Numeric(values, ThermalEntityRoles.HeatPumpPower),
                PropertyPowerKw = Numeric(values, ThermalEntityRoles.PropertyPower),
                SpotPriceSekPerKwh = Numeric(values, ThermalEntityRoles.SpotPrice) is { } spot ? (decimal)spot : null,
                DhwActive = Boolean(values, ThermalEntityRoles.DhwActive),
                DefrostActive = Boolean(values, ThermalEntityRoles.DefrostActive),
                BackupHeaterActive = Boolean(values, ThermalEntityRoles.BackupHeaterActive),
                RoomTemperaturesJson = JsonSerializer.Serialize(roomValues),
                QualityJson = new JsonObject
                {
                    ["source"] = "HomeAssistantHistoryImport",
                    ["entities"] = entityQuality,
                    ["rooms"] = roomQuality
                }.ToJsonString()
            };
            sample.HeatOutputKw = HomeAssistantTelemetryCollector.CalculateHeatOutput(
                sample.FlowLitresPerMinute,
                sample.LeavingWaterTemperatureC,
                sample.ReturnWaterTemperatureC);
            sample.Cop = site.HeatPumpPowerSignVerified && sample.BackupHeaterActive != true &&
                         sample.HeatPumpPowerKw is > 0.1 && sample.HeatOutputKw is { } heatOutput
                ? heatOutput / sample.HeatPumpPowerKw.Value
                : null;
            _db.ThermalTelemetrySamples.Add(sample);
            imported++;

            if (imported % 1000 == 0) await _db.SaveChangesAsync(cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        _db.ThermalEvents.Add(new ThermalEvent
        {
            UserId = userId,
            TimestampUtc = DateTimeOffset.UtcNow,
            Severity = missing.Count == 0 ? "Information" : "Warning",
            Category = "HistoryImport",
            Message = $"HA-historik importerades till {imported} nya femminuterspunkter; {preserved} befintliga punkter bevarades.",
            DetailsJson = JsonSerializer.Serialize(new { fromUtc, toUtc, entityCount = entityIds.Length, missing })
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new HomeAssistantHistoryImportResult(imported, preserved, entityIds.Length, missing);
    }

    private static NormalizedSensorValue Normalize(
        HomeAssistantState? state,
        string expectedUnit,
        double? minimum,
        double? maximum)
    {
        var normalized = SensorValueNormalizer.Normalize(state, expectedUnit);
        if (normalized.Quality != DataQuality.Valid || normalized.Value is not { } value) return normalized;
        if (minimum is { } min && value < min || maximum is { } max && value > max)
            return normalized with { Value = null, Quality = DataQuality.Invalid, Reason = "Värdet ligger utanför konfigurerat intervall." };
        return normalized;
    }

    private static JsonObject Quality(NormalizedSensorValue value) => new()
    {
        ["quality"] = (int)value.Quality,
        ["reason"] = value.Reason,
        ["excluded"] = false
    };

    private static double? Numeric(IReadOnlyDictionary<string, NormalizedSensorValue> values, string role) =>
        values.TryGetValue(role, out var value) && value.Quality == DataQuality.Valid ? value.Value : null;

    private static bool? Boolean(IReadOnlyDictionary<string, NormalizedSensorValue> values, string role) =>
        values.TryGetValue(role, out var value) && value.Quality == DataQuality.Valid ? value.BooleanValue : null;

    private static DateTimeOffset FloorToStep(DateTimeOffset value)
    {
        var utcTicks = value.ToUniversalTime().Ticks;
        return new DateTimeOffset(utcTicks - utcTicks % Step.Ticks, TimeSpan.Zero);
    }

    private static DateTimeOffset CeilingToStep(DateTimeOffset value)
    {
        var floor = FloorToStep(value);
        return floor == value.ToUniversalTime() ? floor : floor.Add(Step);
    }

    private sealed class HistoryCursor
    {
        private readonly HomeAssistantState[] _states;
        private int _index = -1;

        public HistoryCursor(IEnumerable<HomeAssistantState> states) =>
            _states = states.OrderBy(Timestamp).ToArray();

        public HomeAssistantState? At(DateTimeOffset timestampUtc)
        {
            while (_index + 1 < _states.Length && Timestamp(_states[_index + 1]) <= timestampUtc) _index++;
            return _index >= 0 ? _states[_index] : null;
        }

        private static DateTimeOffset Timestamp(HomeAssistantState state) =>
            state.LastUpdatedUtc ?? state.LastChangedUtc ?? state.ReceivedAtUtc;
    }
}
