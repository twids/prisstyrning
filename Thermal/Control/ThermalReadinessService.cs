using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Thermal.Control;

public sealed class ThermalReadinessService
{
    private readonly PrisstyrningDbContext _db;
    private readonly IHomeAssistantStateCache _cache;
    private readonly HomeAssistantTelemetryOptions _telemetryOptions;
    private readonly HomeAssistantControlOptions _controlOptions;
    private readonly IHomeAssistantCredentialProvider _credentials;

    public ThermalReadinessService(
        PrisstyrningDbContext db,
        IHomeAssistantStateCache cache,
        IOptions<HomeAssistantTelemetryOptions> telemetryOptions,
        IOptions<HomeAssistantControlOptions> controlOptions,
        IHomeAssistantCredentialProvider credentials)
    {
        _db = db;
        _cache = cache;
        _telemetryOptions = telemetryOptions.Value;
        _controlOptions = controlOptions.Value;
        _credentials = credentials;
    }

    public async Task<IReadOnlyList<ReadinessCheck>> EvaluateAsync(
        string userId,
        ControlMode targetMode,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var site = await _db.ThermalSiteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var entities = await _db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var rooms = await _db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var latest = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var checks = new List<ReadinessCheck>
        {
            Check("ha-telemetry-configured", "Home Assistant-telemetri är separat konfigurerad", _telemetryOptions.Enabled && HomeAssistantTelemetryClient.IsSupportedBaseUrl(_telemetryOptions.BaseUrl) && _credentials.HasTelemetryToken, "Ange en HTTP(S)-adress och telemetritoken i containerns secrets."),
            Check("ha-snapshot", "En startbild finns från Home Assistant", _cache.LastSnapshotUtc is not null, "Kontrollera HA-anslutningen så att en ny startbild kan hämtas."),
            Check("ha-live", "Home Assistants WebSocket är ansluten", _cache.Connected, "Kontrollera nätverk, token och WebSocket-status."),
            Check("telemetry-fresh", "Senaste femminuterstelemetri är högst tio minuter gammal", latest is not null && now - latest.TimestampUtc <= TimeSpan.FromMinutes(10), "Åtgärda saknade eller gamla sensordata."),
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
            var validSamples = shadowSamples.Count(x => HasRequiredTelemetry(x, rooms));
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
                _credentials.HasControlToken &&
                HomeAssistantTelemetryClient.IsSupportedBaseUrl(_controlOptions.BaseUrl) &&
                HomeAssistantControlClient.IsAllowedNumberEntity(_controlOptions.HeatingDeviationEntityId) &&
                entities.Any(x => x.Role == ThermalEntityRoles.HeatingDeviation && x.EntityId == _controlOptions.HeatingDeviationEntityId),
                "Ange HA-adress och separat styrsecret samt mappa exakt Deviation_Heating-entity."));

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
        Prisstyrning.Data.Entities.ThermalTelemetrySample sample,
        IReadOnlyCollection<Prisstyrning.Data.Entities.ThermalRoomConfig> rooms)
    {
        if (sample.OutsideTemperatureC is null || sample.LeavingWaterTemperatureC is null ||
            sample.ReturnWaterTemperatureC is null || sample.FlowLitresPerMinute is null)
            return false;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, double>>(sample.RoomTemperaturesJson) ?? [];
            return rooms.Where(x => x.IsCritical).All(x =>
                values.ContainsKey(x.EntityId) && Prisstyrning.Thermal.Jobs.ThermalDiagnosticsService.IsRoomValid(sample.QualityJson, x.EntityId));
        }
        catch (JsonException)
        {
            return false;
        }
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
