using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Thermal.Control;

public sealed class ThermalReadinessService
{
    private static readonly string[] RequiredTelemetryRoles =
        [ThermalEntityRoles.OutsideTemperature, ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature, ThermalEntityRoles.Flow];
    private readonly PrisstyrningDbContext _db;
    private readonly IHomeAssistantStateCache _cache;
    private readonly HomeAssistantConnectionService _connections;

    public ThermalReadinessService(
        PrisstyrningDbContext db,
        IHomeAssistantStateCache cache,
        HomeAssistantConnectionService connections)
    {
        _db = db;
        _cache = cache;
        _connections = connections;
    }

    public async Task<IReadOnlyList<ReadinessCheck>> EvaluateAsync(
        string userId,
        ControlMode targetMode,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = await _connections.GetAsync(userId, cancellationToken);
        var site = await _db.ThermalSiteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var entities = await _db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var rooms = await _db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var latest = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var cache = _cache.ReadAccount(userId);
        var configured = connection is { TelemetryEnabled: true, TelemetryTokenConfigured: true } &&
                         HomeAssistantTelemetryClient.IsSupportedBaseUrl(connection.BaseUrl);
        var currentSnapshot = configured && cache.ConfigurationUpdatedAtUtc == connection!.UpdatedAtUtc &&
                              cache.LastSnapshotUtc >= connection.UpdatedAtUtc && cache.LastSnapshotUtc <= now.AddSeconds(30);
        var configurationUpdated = site?.UpdatedAtUtc > connection?.UpdatedAtUtc ? site?.UpdatedAtUtc : connection?.UpdatedAtUtc;
        var fresh = latest is not null && latest.TimestampUtc <= now && now - latest.TimestampUtc <= TimeSpan.FromMinutes(10) &&
                    !(latest.TimestampUtc < configurationUpdated);

        var checks = new List<ReadinessCheck>
        {
            Check("ha-telemetry-configured", "Home Assistant-telemetri är konfigurerad för kontot", configured, "Konfigurera kontots Home Assistant-adress och separata telemetritoken under Inställningar."),
            Check("ha-snapshot", "En startbild finns från kontots aktuella HA-anslutning", currentSnapshot, "Kontrollera HA-anslutningen och invänta en ny startbild efter sparade ändringar."),
            Check("ha-live", "Home Assistants aktuella WebSocket är ansluten", currentSnapshot && cache.Connected, "Kontrollera nätverk, token och WebSocket-status."),
            Check("telemetry-fresh", "Senaste femminuterstelemetri är aktuell och högst tio minuter gammal", fresh, "Åtgärda gamla eller framtida tidsstämplar och invänta en ny insamling efter ändrade inställningar."),
            Check("telemetry-quality", "Kritiska rum och obligatoriska värmegivare har giltig liveinsamling", fresh && currentSnapshot && cache.Connected && latest is not null && HasRequiredTelemetry(latest, rooms, entities), "Kontrollera givarnas enheter, tidsstämplar och fel i Rum/Inställningar. Historik och ersättningsvärden är inte godkänd liveinsamling."),
            Check("critical-room", "Minst ett kritiskt rum har en aktiverad givare", rooms.Any(x => x.IsCritical), "Markera minst ett komfortkritiskt rum."),
            Check("thermal-inputs", "Utetemperatur, LWT, RWT och flöde är mappade", RequiredRoles(entities, ThermalEntityRoles.OutsideTemperature, ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature, ThermalEntityRoles.Flow), "Mappa obligatoriska P1P2-entities i Inställningar.")
        };

        if (targetMode is ControlMode.LwtActive or ControlMode.FullActive)
        {
            var anotherActiveInstallation = await _db.ThermalSiteConfigs.AsNoTracking()
                .AnyAsync(x => x.UserId != userId &&
                               (x.ControlMode == nameof(ControlMode.LwtActive) || x.ControlMode == nameof(ControlMode.FullActive)),
                    cancellationToken);
            var shadowStartedUtc = await _db.ThermalEvents.AsNoTracking()
                .Where(x => x.UserId == userId && x.Category == "ControlMode" && x.Message.Contains("till Shadow"))
                .OrderByDescending(x => x.TimestampUtc)
                .Select(x => (DateTimeOffset?)x.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken);
            var shadowWindowStart = now.AddDays(-21);
            var shadowSamples = await _db.ThermalTelemetrySamples.AsNoTracking()
                .Where(x => x.UserId == userId && x.TimestampUtc >= shadowWindowStart && x.TimestampUtc <= now)
                .OrderBy(x => x.TimestampUtc)
                .ToListAsync(cancellationToken);
            var expectedSamples = 21 * 24 * 12;
            var validSamples = shadowSamples.Count(x => HasRequiredTelemetry(x, rooms, entities));
            var telemetryCoverage = validSamples / (double)expectedSamples;
            var heatingDays = await _db.ThermalTelemetrySamples.AsNoTracking()
                .Where(x => x.UserId == userId && x.TimestampUtc >= now.AddDays(-60) && x.HeatOutputKw > 0.5)
                .Select(x => x.TimestampUtc.Date)
                .Distinct()
                .CountAsync(cancellationToken);
            var shadowPlans = await _db.ThermalPlans.AsNoTracking()
                .Where(x => x.UserId == userId && x.CreatedAtUtc >= shadowWindowStart && x.Status == "Valid")
                .Select(x => x.SolverDurationMs)
                .ToListAsync(cancellationToken);
            var expectedPlans = 21 * 24 * 4;
            var planCoverage = shadowPlans.Count / (double)expectedPlans;
            var p95SolverMs = Percentile(shadowPlans, 0.95);
            var simulatedComfortBreach = await _db.ThermalEvents.AsNoTracking()
                .AnyAsync(x => x.UserId == userId && x.Category == "SimulatedComfortBreach" && x.TimestampUtc >= shadowWindowStart, cancellationToken);
            var activeModel = await _db.ThermalModelVersions.AsNoTracking()
                .Where(x => x.UserId == userId && x.ModelType == "2R2C" && x.IsActive)
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            checks.Add(Check("shadow-duration", "Shadow har körts i minst 21 dagar", shadowStartedUtc is { } shadowStart && now - shadowStart >= TimeSpan.FromDays(21), "Låt Shadow fortsätta tills 21 hela dygn har samlats."));
            checks.Add(Check("single-active-installation", "Ingen annan installation äger aktiv LWT-styrning", !anotherActiveInstallation, "Återställ den andra installationen till Legacy eller Shadow innan LWT aktiveras."));
            checks.Add(Check("telemetry-coverage", "Minst 98 % av femminuterstelemetrin är komplett under 21 dagar", telemetryCoverage >= 0.98, $"Aktuell komplett täckning är {telemetryCoverage:P1}; åtgärda luckor eller ogiltiga kritiska sensorer."));
            var forecastHours = ForecastHours(latest?.OutsideTemperatureForecastJson, now);
            checks.Add(Check("weather-forecast", "Utetemperaturprognosen täcker minst 24 timmar", forecastHours >= 24, $"Aktuell prognostäckning är {forecastHours:0} timmar; kontrollera weather-entityns forecast-attribut."));
            checks.Add(Check("heating-days", "Minst tio verkliga uppvärmningsdygn finns", heatingDays >= 10, $"Samla {Math.Max(0, 10 - heatingDays)} ytterligare uppvärmningsdygn."));
            checks.Add(Check("shadow-plans", "Shadowplaneringen har minst 98 % täckning och p95 under 15 sekunder", planCoverage >= 0.98 && p95SolverMs is <= 15_000, $"Plantäckning {planCoverage:P1}, p95 {(p95SolverMs is null ? "saknas" : $"{p95SolverMs / 1000d:0.0} s")}."));
            checks.Add(Check("shadow-comfort", "Inga simulerade kritiska komfortbrott finns under shadowperioden", !simulatedComfortBreach, "Granska planerna, komfortbandet och modellen innan aktivering."));
            checks.Add(Check("weather-curve", "Grundkurvan är verifierad med avvikelse noll", site?.WeatherCurveVerified == true && heatingDays >= 7, "Genomför minst sju verkliga uppvärmningsdygn och bekräfta grundkurvan."));
            checks.Add(Check("model", "En validerad 2R2C-modell är aktiv", ModelMeetsThreshold(activeModel?.MetricsJson), "Samla shadowdata och träna modellen tills MAE-kraven är uppfyllda."));
            checks.Add(Check(
                "p1p2-control",
                "P1P2-avvikelsen har en separat styranslutning och exakt tillåten number-entity",
                connection is { ControlEnabled: true, ControlTokenConfigured: true } &&
                HomeAssistantTelemetryClient.IsSupportedBaseUrl(connection.BaseUrl) &&
                HomeAssistantControlClient.IsAllowedNumberEntity(connection.HeatingDeviationEntityId) &&
                entities.Any(x => x.Role == ThermalEntityRoles.HeatingDeviation && x.EntityId == connection.HeatingDeviationEntityId),
                "Aktivera kontots separata HA-styrtoken och mappa exakt Deviation_Heating-entity."));

            if (site?.ActiveDeviationLimitC > 1)
            {
                var lwtActivatedUtc = await _db.ThermalEvents.AsNoTracking()
                    .Where(x => x.UserId == userId && x.Category == "ControlMode" && x.Message.Contains("till LwtActive"))
                    .OrderByDescending(x => x.TimestampUtc)
                    .Select(x => (DateTimeOffset?)x.TimestampUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                var criticalEventAfterActivation = lwtActivatedUtc is { } activeSince && await _db.ThermalEvents.AsNoTracking()
                    .AnyAsync(x => x.UserId == userId && x.TimestampUtc >= activeSince && x.Severity == "ActionRequired", cancellationToken);
                var activeHeatingDays = lwtActivatedUtc is { } heatingSince
                    ? await _db.ThermalTelemetrySamples.AsNoTracking()
                        .Where(x => x.UserId == userId && x.TimestampUtc >= heatingSince && x.HeatOutputKw > 0.5)
                        .Select(x => x.TimestampUtc.Date)
                        .Distinct()
                        .CountAsync(cancellationToken)
                    : 0;
                checks.Add(Check(
                    "extended-lwt-range",
                    "Utökning över ±1 °C har minst sju problemfria aktiva uppvärmningsdygn",
                    activeHeatingDays >= 7 && !criticalEventAfterActivation,
                    $"Behåll ±1 °C tills sju uppvärmningsdygn är klara utan åtgärdskrävande händelser; {activeHeatingDays}/7 är registrerade."));
            }
        }

        if (targetMode == ControlMode.FullActive)
        {
            var activeCopModel = await _db.ThermalModelVersions.AsNoTracking()
                .Where(x => x.UserId == userId && x.ModelType == "COP" && x.IsActive)
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            var normalCycles = await _db.DhwCycles.AsNoTracking()
                .CountAsync(x => x.UserId == userId && x.Kind == "Eco" && x.TargetReachedUtc != null, cancellationToken);
            var hygieneCycle = await _db.DhwCycles.AsNoTracking()
                .AnyAsync(x => x.UserId == userId && x.Kind == "Comfort" && x.TargetReachedUtc != null && x.TargetTemperatureC >= 60, cancellationToken);
            checks.Add(Check("power-sign", "Shelly-tecken, CT-riktning och fasmappning är verifierade", site?.HeatPumpPowerSignVerified == true, "Verifiera mätningen under känd kompressordrift."));
            checks.Add(Check("cop-model", "En separat validerad COP-modell är aktiv", CopModelMeetsThreshold(activeCopModel?.MetricsJson), "Verifiera effektmätningen och samla tillräckligt med kompressordata utan elpatron."));
            checks.Add(Check("dhw-shadow", "Minst tio normala DHW-cykler är verifierade", normalCycles >= 10, $"Verifiera {Math.Max(0, 10 - normalCycles)} ytterligare normala cykler."));
            checks.Add(Check("hygiene-shadow", "En 60-graderscykel har verifierats med två mätningar", hygieneCycle, "Genomför och verifiera en comfort-cykel på 60 °C."));
            checks.Add(Check("comfort-setting", "Daikins comfort-läge är manuellt bekräftat till 60 °C", site?.ComfortSetpointConfirmed == true && site.ComfortSetpointC >= 60, "Kontrollera inställningen i Daikin och bekräfta den här."));
        }

        return checks;
    }

    private static ReadinessCheck Check(string key, string requirement, bool passed, string action) =>
        new(key, requirement, passed, passed ? "Ingen åtgärd krävs." : action, passed ? "Information" : "ActionRequired");

    private static bool RequiredRoles(IEnumerable<Prisstyrning.Data.Entities.ThermalEntityConfig> entities, params string[] roles)
    {
        var configured = entities.Select(x => x.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return roles.All(configured.Contains);
    }

    private static bool ModelMeetsThreshold(string? metricsJson)
    {
        if (string.IsNullOrWhiteSpace(metricsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(metricsJson);
            return document.RootElement.TryGetProperty("twoHourMaeC", out var twoHour) && twoHour.GetDouble() <= 0.3 &&
                   document.RootElement.TryGetProperty("dayMaeC", out var day) && day.GetDouble() <= 0.6;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CopModelMeetsThreshold(string? metricsJson)
    {
        if (string.IsNullOrWhiteSpace(metricsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(metricsJson);
            return document.RootElement.TryGetProperty("mae", out var mae) && mae.GetDouble() <= 0.5;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool HasRequiredTelemetry(
        ThermalTelemetrySample sample,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig>? entities = null)
    {
        var critical = rooms.Where(x => x.Enabled && x.IsCritical).ToArray();
        if (critical.Length == 0) return false;
        var required = RequiredTelemetryRoles.Select(role =>
            entities?.FirstOrDefault(x => x.Enabled && x.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) ??
            new ThermalEntityConfig { Role = role, Enabled = true }).ToArray();
        // Reuse the strict, read-only saved-value assessment for this required
        // subset. It rejects imports, malformed metadata, exclusions and NaN.
        return ThermalStatusQuality.Assess(sample, critical, required, sample.TimestampUtc).Quality == DataQuality.Valid;
    }

    private static int? Percentile(IReadOnlyCollection<int> values, double percentile)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(x => x).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static double ForecastHours(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            var points = JsonSerializer.Deserialize<WeatherForecastPoint[]>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
            var last = points.Where(x => x.TimestampUtc >= now.AddHours(-1)).MaxBy(x => x.TimestampUtc);
            return last is null ? 0 : Math.Max(0, (last.TimestampUtc - now).TotalHours);
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
