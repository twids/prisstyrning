using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

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
            var controlTelemetry = ThermalControlTelemetry.Assess(latest, rooms, entities, site, now);
            checks.Add(Check(
                "lwt-safety-inputs",
                "Rumskomfort, flöde, DHW-status och avfrostning är verifierade för säker LWT-styrning",
                currentSnapshot && cache.Connected && controlTelemetry.SafeToControl,
                "Mappa flöde, DHW-status och avfrostning och invänta en giltig liveinsamling. En exkluderad rumsgivare får inte vara enda temperaturunderlag."));
            var anotherActiveInstallation = await _db.ThermalSiteConfigs.AsNoTracking()
                .AnyAsync(x => x.UserId != userId &&
                               (x.ControlMode == nameof(ControlMode.LwtActive) || x.ControlMode == nameof(ControlMode.FullActive)),
                    cancellationToken);
            var modeEvents = await _db.ThermalEvents.AsNoTracking()
                .Where(x => x.UserId == userId && x.Category == "ControlMode").ToListAsync(cancellationToken);
            var periods = ThermalReadinessEvidence.ModePeriods(modeEvents, site?.ControlMode, now);
            var shadowStartedUtc = periods.ShadowSinceUtc;
            var shadowWindowStart = now.AddDays(-21);
            var evidenceStart = shadowStartedUtc is { } started && started > shadowWindowStart ? started : shadowWindowStart;
            var shadowSamples = await _db.ThermalTelemetrySamples.AsNoTracking()
                .Where(x => x.UserId == userId && x.TimestampUtc >= now.AddDays(-60) && x.TimestampUtc <= now)
                .OrderBy(x => x.TimestampUtc)
                .ToListAsync(cancellationToken);
            var expectedSamples = 21 * 24 * 12;
            var validSamples = shadowStartedUtc is null ? 0 : shadowSamples.Where(x => x.TimestampUtc >= evidenceStart &&
                    x.TimestampUtc.UtcTicks % TimeSpan.FromMinutes(5).Ticks == 0)
                .GroupBy(x => x.TimestampUtc).Count(x => x.Count() == 1 && HasRequiredTelemetry(x.Single(), rooms, entities));
            var telemetryCoverage = validSamples / (double)expectedSamples;
            var heating = ThermalReadinessEvidence.HeatingDays(shadowSamples, rooms, entities, site, shadowStartedUtc, now);
            var savedPlans = await _db.ThermalPlans.AsNoTracking()
                .Where(x => x.UserId == userId && x.CreatedAtUtc >= evidenceStart && x.CreatedAtUtc <= now)
                .ToListAsync(cancellationToken);
            var shadowPlans = shadowStartedUtc is null ? [] : savedPlans
                .GroupBy(x => x.CreatedAtUtc.UtcTicks / TimeSpan.FromMinutes(15).Ticks)
                .Select(x => x.MaxBy(plan => plan.CreatedAtUtc)!)
                .Where(x => x.Status == "Valid" && x.SolverDurationMs is >= 0 and <= 45_000 &&
                            x.ValidUntilUtc > x.ValidFromUtc && double.IsFinite(x.Confidence) && x.Confidence is >= 0 and <= 1)
                .Select(x => x.SolverDurationMs).ToArray();
            var expectedPlans = 21 * 24 * 4;
            var planCoverage = shadowPlans.Length / (double)expectedPlans;
            var p95SolverMs = Percentile(shadowPlans, 0.95);
            var simulatedComfortBreach = await _db.ThermalEvents.AsNoTracking()
                .AnyAsync(x => x.UserId == userId && x.Category == "SimulatedComfortBreach" && x.TimestampUtc >= shadowWindowStart, cancellationToken);
            var activeModel = await _db.ThermalModelVersions.AsNoTracking()
                .Where(x => x.UserId == userId && x.ModelType == "2R2C" && x.IsActive)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            var activeCopModel = await _db.ThermalModelVersions.AsNoTracking()
                .Where(x => x.UserId == userId && x.ModelType == "COP" && x.IsActive)
                .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            var activeModels = new[] { activeModel, activeCopModel }.Where(x => x is not null).Select(x => x!).ToArray();
            var modelSources = await ThermalModelProvenance.VerifyCurrentAsync(
                _db,
                userId,
                activeModels,
                rooms,
                entities,
                site?.HeatPumpPowerSignVerified == true,
                now,
                cancellationToken);
            modelSources.TryGetValue(activeModel?.Id ?? 0, out var activeModelSource);
            modelSources.TryGetValue(activeCopModel?.Id ?? 0, out var activeCopModelSource);
            checks.Add(Check("shadow-duration", "Shadow har körts i minst 21 dagar", shadowStartedUtc is { } shadowStart && now - shadowStart >= TimeSpan.FromDays(21), "Låt Shadow fortsätta tills 21 hela dygn har samlats."));
            checks.Add(Check("single-active-installation", "Ingen annan installation äger aktiv LWT-styrning", !anotherActiveInstallation, "Återställ den andra installationen till Legacy eller Shadow innan LWT aktiveras."));
            checks.Add(Check("telemetry-coverage", "Minst 98 % av femminuterstelemetrin är komplett under 21 dagar", telemetryCoverage >= 0.98, $"Aktuell komplett täckning är {telemetryCoverage:P1}; åtgärda luckor eller ogiltiga kritiska sensorer."));
            var forecastHours = ThermalReadinessEvidence.ForecastHours(latest?.OutsideTemperatureForecastJson, now);
            checks.Add(Check("weather-forecast", "Utetemperaturprognosen täcker minst 24 sammanhängande timmar", fresh && forecastHours >= 24 && ThermalReadinessEvidence.HasValidForecastQuality(latest), $"Sammanhängande prognostäckning är {forecastHours:0} timmar. En giltig timprognos måste börja vid nutid och sakna större luckor."));
            checks.Add(Check("heating-days", "Minst tio verkliga uppvärmningsdygn finns", heating.HeatingDays >= 10, $"{heating.HeatingDays}/10 verifierade dygn. Bara avslutade lokala dygn i pågående verifieringsperiod med minst 98 % giltig liveinsamling och sammanhängande husvärme räknas; inte import, varmvatten eller avfrostning."));
            checks.Add(Check("shadow-plans", "Shadowplaneringen har minst 98 % täckning och p95 under 15 sekunder", planCoverage >= 0.98 && p95SolverMs is <= 15_000, $"Plantäckning {planCoverage:P1}, p95 {(p95SolverMs is null ? "saknas" : $"{p95SolverMs / 1000d:0.0} s")}."));
            checks.Add(Check("shadow-comfort", "Inga simulerade kritiska komfortbrott finns under shadowperioden", !simulatedComfortBreach, "Granska planerna, komfortbandet och modellen innan aktivering."));
            checks.Add(Check("weather-curve", "Grundkurvan är verifierad med uppmätt avvikelse noll", site?.WeatherCurveVerified == true && heating.ZeroDeviationDays >= 7, $"{heating.ZeroDeviationDays}/7 verifierade uppvärmningsdygn med giltig nollavvikelse och bibehållen kritisk rumskomfort. Mappa avvikelsens återkoppling, samla data och bekräfta grundkurvan manuellt."));
            var modelEvidence = ThermalModelEvidence.AssessCurrent(activeModel, activeModelSource, now);
            checks.Add(Check("model", "En validerad 2R2C-modell är aktiv", modelEvidence.Passed, modelEvidence.Reason));
            var copEvidence = ThermalModelEvidence.AssessCurrent(activeCopModel, activeCopModelSource, now);
            checks.Add(Check("power-sign", "Shelly-tecken, CT-riktning och fasmappning är verifierade", site?.HeatPumpPowerSignVerified == true, "Verifiera mätningen under känd kompressordrift före kostnadsoptimering."));
            checks.Add(Check("cop-model", "En separat validerad COP-modell är aktiv", copEvidence.Passed, copEvidence.Reason));
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
                var lwtActivatedUtc = periods.ActiveSinceUtc;
                var criticalEventAfterActivation = lwtActivatedUtc is { } activeSince && await _db.ThermalEvents.AsNoTracking()
                    .AnyAsync(x => x.UserId == userId && x.TimestampUtc >= activeSince && x.Severity == "ActionRequired", cancellationToken);
                var activeHeatingDays = ThermalReadinessEvidence.HeatingDays(shadowSamples, rooms, entities, site, lwtActivatedUtc, now).ComfortDays;
                checks.Add(Check(
                    "extended-lwt-range",
                    "Utökning över ±1 °C har minst sju problemfria aktiva uppvärmningsdygn",
                    activeHeatingDays >= 7 && !criticalEventAfterActivation,
                    $"Behåll ±1 °C tills sju uppvärmningsdygn är klara utan åtgärdskrävande händelser; {activeHeatingDays}/7 är registrerade."));
            }
        }

        if (targetMode == ControlMode.FullActive)
        {
            var normalCycles = await _db.DhwCycles.AsNoTracking()
                .CountAsync(x => x.UserId == userId && x.Kind == "Eco" && x.TargetReachedUtc != null, cancellationToken);
            var hygieneCycle = await _db.DhwCycles.AsNoTracking()
                .AnyAsync(x => x.UserId == userId && x.Kind == "Comfort" && x.TargetReachedUtc != null && x.TargetTemperatureC >= 60, cancellationToken);
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

}
