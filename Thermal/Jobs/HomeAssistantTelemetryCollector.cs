using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Thermal.Jobs;

public sealed class HomeAssistantTelemetryCollector : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHomeAssistantStateCache _cache;
    private readonly SensorQualityTracker _qualityTracker;
    private readonly ILogger<HomeAssistantTelemetryCollector> _logger;

    public HomeAssistantTelemetryCollector(
        IServiceScopeFactory scopeFactory,
        IHomeAssistantStateCache cache,
        SensorQualityTracker qualityTracker,
        ILogger<HomeAssistantTelemetryCollector> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _qualityTracker = qualityTracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
                var userIds = await db.HomeAssistantConnections.AsNoTracking()
                    .Where(x => x.TelemetryEnabled)
                    .Select(x => x.UserId)
                    .ToListAsync(stoppingToken);
                foreach (var userId in userIds)
                {
                    try { await CollectAsync(userId, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception) { _logger.LogError("Could not create an account Home Assistant five-minute telemetry snapshot."); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) { _logger.LogError("Could not enumerate thermal installations for telemetry collection."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CollectAsync(string userId, CancellationToken cancellationToken, DateTimeOffset? collectedAtUtc = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var connection = await db.HomeAssistantConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId && x.TelemetryEnabled, cancellationToken);
        if (connection is null) return;
        var staleAfter = TimeSpan.FromMinutes(Math.Clamp(connection.StaleAfterMinutes, 1, 60));
        var entities = await db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled)
            .ToListAsync(cancellationToken);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled)
            .ToListAsync(cancellationToken);
        if (entities.Count == 0 && rooms.Count == 0) return;

        var site = await db.ThermalSiteConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var baseRoomTargetC = site?.BaseRoomTargetC ?? 21.5;

        var now = (collectedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var cache = _cache.ReadAccount(userId);
        var connectionReady = cache.Connected && cache.ConfigurationUpdatedAtUtc == connection.UpdatedAtUtc &&
                              cache.LastSnapshotUtc >= connection.UpdatedAtUtc && cache.LastSnapshotUtc <= now.AddSeconds(30) &&
                              !string.IsNullOrWhiteSpace(connection.TelemetryTokenCiphertext);
        var snapshot = cache.States.ToDictionary(x => x.EntityId, StringComparer.OrdinalIgnoreCase);
        var revision = site?.UpdatedAtUtc > connection.UpdatedAtUtc ? site.UpdatedAtUtc : connection.UpdatedAtUtc;
        var bucket = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, TimeSpan.Zero);
        var sample = await db.ThermalTelemetrySamples
            .SingleOrDefaultAsync(x => x.UserId == userId && x.TimestampUtc == bucket, cancellationToken)
            ?? new ThermalTelemetrySample { UserId = userId, TimestampUtc = bucket };
        if (sample.Id == 0) db.ThermalTelemetrySamples.Add(sample);

        var values = new Dictionary<string, SensorAssessment>(StringComparer.OrdinalIgnoreCase);
        var weatherForecast = new NormalizedWeatherForecast([], DataQuality.Unavailable, "Ingen väderprognos är mappad.");
        foreach (var entity in entities)
        {
            snapshot.TryGetValue(entity.EntityId, out var raw);
            if (entity.Role.Equals(ThermalEntityRoles.WeatherForecast, StringComparison.OrdinalIgnoreCase))
            {
                weatherForecast = !connectionReady ? new([], DataQuality.Unavailable, "En aktuell liveanslutning till Home Assistant saknas.")
                    : entity.EntityId.StartsWith("weather.", StringComparison.Ordinal)
                        ? await scope.ServiceProvider.GetRequiredService<IHomeAssistantTelemetryClient>().GetWeatherForecastAsync(userId, entity.EntityId, cancellationToken)
                        : HomeAssistantWeatherForecastParser.Parse(raw, now);
                continue;
            }
            values[entity.Role] = Assess(
                $"entity|{entity.Role}|{entity.EntityId}",
                raw,
                entity.ExpectedUnit,
                entity.MinimumValid,
                entity.MaximumValid,
                entity.MaximumRatePerHour,
                now,
                SensorFreshnessPolicy.ReportAge(entity.MaximumReportAgeMinutes, staleAfter),
                userId,
                revision,
                connectionReady);
        }

        var roomValues = new Dictionary<string, double>();
        var roomAssessments = new Dictionary<string, SensorAssessment>();
        foreach (var room in rooms)
        {
            snapshot.TryGetValue(room.EntityId, out var raw);
            var assessment = Assess(
                $"room|{room.EntityId}",
                raw,
                "°C",
                room.MinimumValidC,
                room.MaximumValidC,
                room.MaximumRateCPerHour,
                now,
                SensorFreshnessPolicy.ReportAge(room.MaximumReportAgeMinutes, staleAfter),
                userId,
                revision,
                connectionReady);
            roomAssessments[room.EntityId] = assessment;
            if (assessment.Quality == DataQuality.Valid && !assessment.Excluded && assessment.Value is { } valid)
            {
                roomValues[room.EntityId] = valid;
            }
        }

        var representativeError = CalculateRepresentativeError(rooms, roomValues, baseRoomTargetC);
        foreach (var room in rooms.Where(x => x.IsCritical && !roomValues.ContainsKey(x.EntityId)))
        {
            var assessment = roomAssessments[room.EntityId];
            if (assessment.LastValidValue is { } lastValid &&
                assessment.LastValidUtc is { } validAt &&
                now - validAt <= TimeSpan.FromMinutes(30))
            {
                roomValues[room.EntityId] = lastValid;
            }
            else if (representativeError is { } error)
            {
                roomValues[room.EntityId] = baseRoomTargetC + room.TargetOffsetC + error;
            }
        }

        sample.OutsideTemperatureC = Numeric(values, ThermalEntityRoles.OutsideTemperature);
        sample.OutsideTemperatureForecastJson = weatherForecast.Quality == DataQuality.Valid
            ? JsonSerializer.Serialize(weatherForecast.Points, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            : "[]";
        sample.WindSpeedMps = Numeric(values, ThermalEntityRoles.WindSpeed) ?? weatherForecast.Points.FirstOrDefault()?.WindSpeedMps;
        sample.SolarIrradianceWm2 = Numeric(values, ThermalEntityRoles.SolarIrradiance) ?? weatherForecast.Points.FirstOrDefault()?.SolarIrradianceWm2;
        sample.LeavingWaterTemperatureC = Numeric(values, ThermalEntityRoles.LeavingWaterTemperature);
        sample.ReturnWaterTemperatureC = Numeric(values, ThermalEntityRoles.ReturnWaterTemperature);
        sample.FlowLitresPerMinute = Numeric(values, ThermalEntityRoles.Flow);
        sample.BrineInC = Numeric(values, ThermalEntityRoles.BrineIn);
        sample.BrineOutC = Numeric(values, ThermalEntityRoles.BrineOut);
        sample.TankTemperatureC = Numeric(values, ThermalEntityRoles.TankTemperature);
        sample.HeatPumpPowerKw = Numeric(values, ThermalEntityRoles.HeatPumpPower);
        sample.PropertyPowerKw = Numeric(values, ThermalEntityRoles.PropertyPower);
        sample.SpotPriceSekPerKwh = SensorValueNormalizer.ToDecimal(Numeric(values, ThermalEntityRoles.SpotPrice));
        sample.DhwActive = Boolean(values, ThermalEntityRoles.DhwActive);
        sample.DefrostActive = Boolean(values, ThermalEntityRoles.DefrostActive);
        sample.BackupHeaterActive = Boolean(values, ThermalEntityRoles.BackupHeaterActive);
        sample.HeatOutputKw = CalculateHeatOutput(sample.FlowLitresPerMinute, sample.LeavingWaterTemperatureC, sample.ReturnWaterTemperatureC);

        sample.Cop = site?.HeatPumpPowerSignVerified == true && sample.BackupHeaterActive == false &&
                     sample.HeatPumpPowerKw is > 0.1 && sample.HeatOutputKw is { } heatOutput
            ? heatOutput / sample.HeatPumpPowerKw.Value
            : null;
        if (sample.Cop is { } cop && !double.IsFinite(cop)) sample.Cop = null;
        sample.RoomTemperaturesJson = JsonSerializer.Serialize(roomValues);
        sample.QualityJson = JsonSerializer.Serialize(new
        {
            connectionRevisionUtc = connection.UpdatedAtUtc,
            collectedAtUtc = now,
            heatingDeviationC = Numeric(values, ThermalEntityRoles.HeatingDeviation),
            entities = values.ToDictionary(x => x.Key, x => new { x.Value.Quality, x.Value.Reason, x.Value.Excluded }),
            rooms = roomAssessments.ToDictionary(x => x.Key, x => new { x.Value.Quality, x.Value.Reason, x.Value.Excluded }),
            forecast = new { weatherForecast.Quality, weatherForecast.Reason, points = weatherForecast.Points.Count }
        });

        foreach (var transition in values.Select(x => (x.Key, x.Value))
                     .Concat(roomAssessments.Select(x => (x.Key, x.Value))))
        {
            if (!transition.Value.BecameExcluded && !transition.Value.BecameRecovered) continue;
            db.ThermalEvents.Add(new ThermalEvent
            {
                UserId = userId,
                TimestampUtc = now,
                Severity = transition.Value.BecameExcluded ? "ActionRequired" : "Information",
                Category = "DataQuality",
                Message = transition.Value.BecameExcluded
                    ? $"Givaren {transition.Key} har exkluderats efter tre felaktiga mätningar."
                    : $"Givaren {transition.Key} används igen efter tre giltiga mätningar."
            });
        }

        var diagnostics = scope.ServiceProvider.GetRequiredService<ThermalDiagnosticsService>();
        await diagnostics.EvaluateAsync(userId, sample, rooms, site ?? new ThermalSiteConfig { UserId = userId }, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private SensorAssessment Assess(
        string entityId,
        HomeAssistantState? state,
        string expectedUnit,
        double? minimum,
        double? maximum,
        double? maximumRatePerHour,
        DateTimeOffset now,
        TimeSpan staleAfter,
        string userId,
        DateTimeOffset revision,
        bool connectionReady)
    {
        var normalized = SensorValueNormalizer.Normalize(state, expectedUnit);
        if (!connectionReady)
            normalized = normalized with { Quality = DataQuality.Unavailable, Reason = "En aktuell liveanslutning till Home Assistant saknas." };
        return _qualityTracker.Assess(
            $"{userId}|{entityId}",
            state,
            normalized,
            new SensorValidationRules(minimum, maximum, maximumRatePerHour, staleAfter),
            now,
            configurationRevisionUtc: revision);
    }

    private static double? Numeric(IReadOnlyDictionary<string, SensorAssessment> values, string role) =>
        values.TryGetValue(role, out var assessment) && assessment.Quality == DataQuality.Valid && !assessment.Excluded
            ? assessment.Value
            : null;

    private static bool? Boolean(IReadOnlyDictionary<string, SensorAssessment> values, string role) =>
        values.TryGetValue(role, out var assessment) && assessment.Quality == DataQuality.Valid && !assessment.Excluded
            ? assessment.BooleanValue
            : null;

    internal static double? CalculateHeatOutput(double? flowLitresPerMinute, double? lwtC, double? rwtC)
    {
        if (flowLitresPerMinute is not > 0 || lwtC is null || rwtC is null || lwtC <= rwtC ||
            !double.IsFinite(flowLitresPerMinute.Value) || !double.IsFinite(lwtC.Value) || !double.IsFinite(rwtC.Value)) return null;
        var result = flowLitresPerMinute.Value / 60 * 4.186 * (lwtC.Value - rwtC.Value);
        return double.IsFinite(result) ? result : null;
    }

    internal static double? CalculateRepresentativeError(
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyDictionary<string, double> values,
        double baseRoomTargetC)
    {
        var included = rooms.Where(room => values.ContainsKey(room.EntityId) && room.Weight > 0).ToArray();
        if (included.Length == 0) return null;
        var totalWeight = included.Sum(room => room.Weight);
        return included.Sum(room => (values[room.EntityId] - (baseRoomTargetC + room.TargetOffsetC)) * room.Weight) / totalWeight;
    }
}
