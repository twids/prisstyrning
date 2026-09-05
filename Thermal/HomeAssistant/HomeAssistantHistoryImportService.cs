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
        var importedAt = DateTimeOffset.UtcNow;
        var staleAfterMinutes = await _db.HomeAssistantConnections.AsNoTracking()
            .Where(x => x.UserId == userId).Select(x => (int?)x.StaleAfterMinutes).SingleOrDefaultAsync(cancellationToken) ?? 10;
        var staleAfter = TimeSpan.FromMinutes(Math.Clamp(staleAfterMinutes, 1, 60));
        // An import never mutates the live collector's exclusion/recovery state.
        var tracker = new SensorQualityTracker();

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
            var history = await _client.GetHistoryAsync(userId, entityId, fromUtc, toUtc, cancellationToken);
            var cursor = new HistoryCursor(history.Where(x => x.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase)), importedAt);
            if (!cursor.HasUsableTimeline) missing.Add(entityId);
            cursors[entityId] = cursor;
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
            var values = new Dictionary<string, SensorAssessment>(StringComparer.OrdinalIgnoreCase);
            var entityQuality = new JsonObject();
            foreach (var config in entityConfigs)
            {
                var raw = cursors[config.EntityId].At(bucket);
                var assessed = tracker.Assess($"entity|{config.Role}|{config.EntityId}", raw, SensorValueNormalizer.Normalize(raw, config.ExpectedUnit),
                    new(config.MinimumValid, config.MaximumValid, config.MaximumRatePerHour, SensorFreshnessPolicy.ReportAge(config.MaximumReportAgeMinutes, staleAfter)), bucket, historyImportedAtUtc: importedAt);
                values[config.Role] = assessed;
                entityQuality[config.Role] = Quality(assessed);
            }

            var roomValues = new Dictionary<string, double>();
            var roomQuality = new JsonObject();
            foreach (var room in rooms)
            {
                var raw = cursors[room.EntityId].At(bucket);
                var assessed = tracker.Assess($"room|{room.EntityId}", raw, SensorValueNormalizer.Normalize(raw, "°C"),
                    new(room.MinimumValidC, room.MaximumValidC, room.MaximumRateCPerHour, SensorFreshnessPolicy.ReportAge(room.MaximumReportAgeMinutes, staleAfter)), bucket, historyImportedAtUtc: importedAt);
                roomQuality[room.EntityId] = Quality(assessed);
                if (assessed.Quality == DataQuality.Valid && !assessed.Excluded && assessed.Value is { } value)
                    roomValues[room.EntityId] = value;
            }

            // Still assess these buckets, so a preserved live row cannot hide an
            // invalid history transition from subsequent imported buckets.
            if (existing.Contains(bucket))
            {
                preserved++;
                continue;
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
                SpotPriceSekPerKwh = SensorValueNormalizer.ToDecimal(Numeric(values, ThermalEntityRoles.SpotPrice)),
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
            sample.Cop = site.HeatPumpPowerSignVerified && sample.BackupHeaterActive == false &&
                         sample.HeatPumpPowerKw is > 0.1 && sample.HeatOutputKw is { } heatOutput
                ? heatOutput / sample.HeatPumpPowerKw.Value
                : null;
            if (sample.Cop is { } cop && !double.IsFinite(cop)) sample.Cop = null;
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

    private static JsonObject Quality(SensorAssessment value) => new()
    {
        ["quality"] = (int)value.Quality,
        ["reason"] = value.Reason,
        ["excluded"] = value.Excluded
    };

    private static double? Numeric(IReadOnlyDictionary<string, SensorAssessment> values, string role) =>
        values.TryGetValue(role, out var value) && value.Quality == DataQuality.Valid && !value.Excluded ? value.Value : null;

    private static bool? Boolean(IReadOnlyDictionary<string, SensorAssessment> values, string role) =>
        values.TryGetValue(role, out var value) && value.Quality == DataQuality.Valid && !value.Excluded ? value.BooleanValue : null;

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

        public bool HasUsableTimeline => _states.Length > 0;

        public HistoryCursor(IEnumerable<HomeAssistantState> states, DateTimeOffset importedAt)
        {
            var materialized = states.ToArray();
            // An undated fault cannot be placed safely between two valid states.
            // Reject that entity's timeline instead of silently bridging the fault.
            _states = materialized.Any(x => x.LastUpdatedUtc is null || x.LastUpdatedUtc == default(DateTimeOffset) ||
                                           x.LastUpdatedUtc > importedAt.AddSeconds(30))
                ? [] : materialized.OrderBy(Timestamp).ToArray();
        }

        public HomeAssistantState? At(DateTimeOffset timestampUtc)
        {
            while (_index + 1 < _states.Length && Timestamp(_states[_index + 1]) <= timestampUtc) _index++;
            return _index >= 0 ? _states[_index] : null;
        }

        private static DateTimeOffset Timestamp(HomeAssistantState state) =>
            state.LastUpdatedUtc!.Value;
    }
}
