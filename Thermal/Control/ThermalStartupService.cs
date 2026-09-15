using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Control;

public sealed record ThermalStartupStatus(bool ReadyToCommission, string Phase,
    bool ConservativeEnabled, IReadOnlyList<ReadinessCheck> SafetyChecks,
    IReadOnlyList<ReadinessCheck> OptimizationChecks);

public sealed record ConservativePreview(DateTimeOffset CalculatedAtUtc, double? ObservedDeviationC,
    double? SuggestedDeviationC, string Reason, bool SimulationOnly = true);

internal sealed class ThermalStartupService(
    PrisstyrningDbContext db, ThermalReadinessService readiness,
    IHomeAssistantStateCache cache, IConfiguration configuration,
    IHomeAssistantControlClient control, RuntimeBuildProvenance build)
{
    internal async Task<ConservativePreview> PreviewAsync(string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var site = await db.ThermalSiteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var sample = await db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(ct);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
        var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
        var observation = await FeedbackAsync(userId, now, ct);
        var assessment = ThermalControlTelemetry.Assess(sample, rooms, entities, site, now);
        if (!assessment.SafeToControl || sample is null || site is null ||
            !ThermalReadinessService.HasRequiredTelemetry(sample, rooms, entities) || !cache.IsConnected(userId))
            return new(now, observation, null, "Giltiga aktuella givare krävs även för ett försiktigt förslag.");
        var connection = await db.HomeAssistantConnections.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        cache.TryGet(userId, connection?.HeatingDeviationEntityId ?? "", out var actuator);
        var step = LwtControlBinding.StartupStep(connection?.HeatingDeviationEntityId ?? "", actuator, now, Math.Min(1, site.ActiveDeviationLimitC));
        if (observation is null || step is null) return new(now, observation, null, "Avvikelsens reglagesteg och återkoppling saknas.");
        // P-only illustration from current readings. No commissioning, lease,
        // integral history or write authority is fabricated or persisted.
        var decision = new ConservativeLwtRegulator().Evaluate(new(now, sample.TimestampUtc, null, null,
            observation.Value, assessment.RepresentativeTemperatureErrorC, assessment.CriticalRoomBelowMinimum,
            assessment.DhwActive, assessment.DefrostActive, assessment.FlowLitresPerMinute, 0,
            true, true, true, false, DeviationLimitC: Math.Min(1, site.ActiveDeviationLimitC), DeviationStepC: step.Value));
        return new(now, observation, decision.IsFallback ? null : decision.RequestedDeviationC,
            "Skrivfri ögonblicksbild utan integraldel eller prisbidrag. " + decision.Reason);
    }

    // These are equipment/safety prerequisites, not statistical learning gates.
    internal static readonly string[] SafetyKeys = ["ha-telemetry-configured", "ha-snapshot", "ha-live",
        "telemetry-fresh", "telemetry-quality", "critical-room", "thermal-inputs", "build-provenance",
        "lwt-safety-inputs", "single-active-installation", "p1p2-control"];

    public async Task<ThermalStartupStatus> GetAsync(string userId, CancellationToken ct)
    {
        // The frequent startup check reads only current safety inputs, never 60
        // days of history. Detailed learning metrics remain on the Model page.
        var checks = await readiness.EvaluateAsync(userId, ControlMode.LwtActive, ct, safetyOnly: true);
        var safety = SafetyKeys.Select(key => checks.SingleOrDefault(x => x.Key == key) ??
            new ReadinessCheck(key, "Säkerhetskontroll saknas", false, "Start är spärrad tills kontrollen kan verifieras.")).ToList();
        var site = await db.ThermalSiteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var startup = await db.ThermalStartupStates.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var controlState = await db.ThermalControlStates.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        safety.Add(Check("startup-lease", "Ingen tidigare writer håller skrivleasen",
            !(controlState?.LeaseExpiresUtc > DateTimeOffset.UtcNow), "Vänta tills föregående skrivlease har släppts."));
        safety.Add(Check("startup-override", "Ingen manuell override är aktiv",
            !(controlState?.ManualOverrideUntilUtc > DateTimeOffset.UtcNow), "Avsluta override före ett inkopplingstest."));
        safety.Add(Check("startup-deployment", "Driftsättningen tillåter försiktig inkoppling",
            configuration.GetValue("Thermal:AllowLwtActive", false) && configuration.GetValue("Thermal:AllowConservativeStart", false),
            "Aktivera driftsättningens separata tillstånd först efter granskad release."));
        safety.Add(Check("startup-shadow", "Inkopplingstest startar från Shadow med Legacy-varmvatten",
            site?.ControlMode == "Shadow" && site.DhwWriter == "Legacy", "Välj Shadow. Varmvattenstyrningen lämnas i Legacy."));
        safety.Add(Check("startup-range", "Försiktig start är begränsad till högst ±1 °C",
            site?.ActiveDeviationLimitC is >= .5 and <= 1, "Välj en gräns på 0,5–1 °C."));
        var neutral = await FeedbackAsync(userId, DateTimeOffset.UtcNow, ct);
        safety.Add(Check("startup-neutral", "Numerisk återkoppling visar nollavvikelse före testet",
            neutral is { } value && Math.Abs(value) <= .05, "Nollställ avvikelsen manuellt och invänta giltig återkoppling."));
        var telemetry = await db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(ct);
        safety.Add(Check("startup-circulation", "Testet kan ske under cirkulation, utan DHW eller avfrostning",
            telemetry is { FlowLitresPerMinute: > 1, DhwActive: false, DefrostActive: false },
            "Vänta på husvärmedrift med verifierat flöde. Testa inte under en varmvattenkörning."));
        return new(safety.All(x => x.Passed), startup?.Phase ?? "NotCommissioned", startup?.ConservativeEnabled == true,
            safety, checks.Where(x => !SafetyKeys.Contains(x.Key)).ToArray());
    }

    // Called only by the mode service while holding the account operation lock.
    internal async Task<(bool Success, string Message)> StartAsync(string userId, ThermalModeRequest request, CancellationToken ct)
    {
        if (!request.Confirmed || !request.WeatherCurveModeConfirmed || !request.IndependentFallbackConfirmed)
            return (false, "Bekräfta LWT/väderkurveläge, fungerande grundkurva och separat återställning innan skrivtestet godkänns.");
        var assessment = await GetAsync(userId, ct);
        if (!assessment.ReadyToCommission) return (false, "Säkerhetskontrollerna för inkoppling är inte godkända.");
        var site = await db.ThermalSiteConfigs.SingleAsync(x => x.UserId == userId, ct);
        var state = await db.ThermalControlStates.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (state is null) { state = new() { UserId = userId }; db.ThermalControlStates.Add(state); }
        if (state.LeaseExpiresUtc > DateTimeOffset.UtcNow || state.ManualOverrideUntilUtc > DateTimeOffset.UtcNow)
            return (false, "Vänta tills föregående writer-lease eller manuell override har löpt ut.");
        var connection = await db.HomeAssistantConnections.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        cache.TryGet(userId, connection.HeatingDeviationEntityId, out var actuator);
        var step = LwtControlBinding.StartupStep(connection.HeatingDeviationEntityId, actuator, DateTimeOffset.UtcNow, site.ActiveDeviationLimitC);
        if (step is not (>= .5 and <= 1)) return (false, "Reglagets steg kan inte verifieras för försiktig start.");
        var startup = await db.ThermalStartupStates.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (startup is null) { startup = new() { UserId = userId }; db.ThermalStartupStates.Add(startup); }
        startup.Phase = "Commissioning";
        startup.ConservativeEnabled = true;
        startup.CommissionedAtUtc = null;
        startup.LastEvaluationUtc = null;
        startup.ConfigurationFingerprint = string.Empty;
        startup.UpdatedAtUtc = DateTimeOffset.UtcNow;
        // Persist recovery intent BEFORE the first possible command. A restarted
        // worker sees Commissioning and must restore zero, never run regulation.
        site.ControlMode = "LwtActive";
        site.DhwWriter = "Legacy";
        state.PiIntegral = 0;
        state.LeaseOwner = "commission-" + Guid.NewGuid().ToString("N");
        state.LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        state.LastHeartbeatUtc = DateTimeOffset.UtcNow;
        state.FallbackReason = "Inkopplingstest pågår; vanlig reglering är spärrad.";
        db.ThermalEvents.Add(Event(userId, "Information", "Inkopplingstest godkänt: LWT/väderkurva och oberoende återställning manuellt bekräftade."));
        await db.SaveChangesAsync(ct);
        var pulseVerified = false;
        var zeroVerified = false;
        // Request disconnect/cancellation must not cancel the mandatory cleanup.
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await SendAsync(userId, 0, state, "Inkoppling: verifiera grundkurvans nollavvikelse.", testTimeout.Token);
            await SendAsync(userId, step.Value, state, "Inkoppling: ett positivt reglagesteg med verifierad återkoppling.", testTimeout.Token);
            pulseVerified = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            db.ThermalEvents.Add(Event(userId, "ActionRequired", "Inkopplingstestet misslyckades; nollställning försöks separat."));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await SendAsync(userId, 0, state, "Inkoppling: obligatorisk återställning till noll.", cleanup.Token);
                zeroVerified = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                db.ThermalEvents.Add(Event(userId, "ActionRequired", "Nollställningen kunde inte verifieras. Kontrollera pumpen manuellt; återställningsförsök fortsätter."));
            }
        }
        if (pulseVerified && zeroVerified)
        {
            site.WeatherCurveVerified = true;
            site.UpdatedAtUtc = DateTimeOffset.UtcNow;
            startup.Phase = "Verified";
            startup.CommissionedAtUtc = DateTimeOffset.UtcNow;
            startup.ConfigurationFingerprint = await FingerprintAsync(userId, site, CancellationToken.None);
            state.FallbackReason = string.Empty;
            db.ThermalEvents.Add(Event(userId, "Information", "Försiktig temperaturreglering aktiverad efter verifierat skrivtest och nollställning. Legacy behåller DHW."));
            var modeEvent = Event(userId, "Information", "Driftläget ändrades från Shadow till LwtActive.");
            modeEvent.Category = "ControlMode";
            db.ThermalEvents.Add(modeEvent);
        }
        else
        {
            startup.Phase = zeroVerified ? "Failed" : "RecoveryRequired";
            startup.ConservativeEnabled = !zeroVerified;
            if (zeroVerified) site.ControlMode = "Shadow";
            state.FallbackReason = zeroVerified ? "Inkoppling misslyckades; Shadow återställd och nollavvikelse verifierad." : "Inkoppling kräver manuell kontroll; nollavvikelse är inte verifierad.";
        }
        startup.UpdatedAtUtc = DateTimeOffset.UtcNow;
        state.LeaseOwner = null;
        state.LeaseExpiresUtc = null;
        await db.SaveChangesAsync(CancellationToken.None);
        return (pulseVerified && zeroVerified, pulseVerified && zeroVerified
            ? "Försiktig LWT-reglering är aktiverad. Legacy styr fortfarande varmvattnet."
            : state.FallbackReason);
    }

    internal async Task<bool> EvidenceMatchesAsync(string userId, ThermalSiteConfig site, ThermalStartupState startup, CancellationToken ct) =>
        startup is { Phase: "Verified", ConservativeEnabled: true, CommissionedAtUtc: not null } &&
        startup.CommissionedAtUtc <= DateTimeOffset.UtcNow &&
        startup.ConfigurationFingerprint == await FingerprintAsync(userId, site, ct);

    // The worker already holds ThermalAccountOperation for the whole evaluation.
    internal async Task EvaluateAsync(string userId, string leaseOwner, CancellationToken ct)
    {
        if (!await new WriterLeaseService(db).TryAcquireOrRenewAsync(userId, leaseOwner, TimeSpan.FromMinutes(15), ct)) return;
        await EvaluateWithLeaseAsync(userId, leaseOwner, ct);
    }

    internal async Task EvaluateWithLeaseAsync(string userId, string leaseOwner, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var site = await db.ThermalSiteConfigs.SingleAsync(x => x.UserId == userId, ct);
        var startup = await db.ThermalStartupStates.SingleAsync(x => x.UserId == userId, ct);
        var state = await db.ThermalControlStates.SingleAsync(x => x.UserId == userId, ct);
        if (state.LeaseOwner != leaseOwner || state.LeaseExpiresUtc <= DateTimeOffset.UtcNow || state.LeaseExpiresUtc is null) return;
        if (site.ControlMode != "LwtActive" || !startup.ConservativeEnabled) return;

        if (!await EvidenceMatchesAsync(userId, site, startup, ct))
        {
            try
            {
                await SendAsync(userId, 0, state, "Återställning efter avbruten inkoppling eller ändrat säkerhetsunderlag.", ct);
                site.ControlMode = "Shadow";
                state.LeaseOwner = null;
                state.LeaseExpiresUtc = null;
                startup.ConservativeEnabled = false;
                startup.Phase = "Invalidated";
                state.PiIntegral = 0;
                state.FallbackReason = "Nollavvikelse verifierad; ny inkopplingskontroll krävs.";
                db.ThermalEvents.Add(Event(userId, "Warning", state.FallbackReason));
                var modeEvent = Event(userId, "Information", "Driftläget ändrades från LwtActive till Shadow.");
                modeEvent.Category = "ControlMode";
                db.ThermalEvents.Add(modeEvent);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                startup.Phase = "RecoveryRequired";
                state.FallbackReason = "Nollavvikelsen kan inte verifieras. Manuell kontroll krävs.";
                db.ThermalEvents.Add(Event(userId, "ActionRequired", state.FallbackReason));
            }
            startup.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var sample = await db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(ct);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
        var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
        var connection = await db.HomeAssistantConnections.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var account = cache.ReadAccount(userId);
        var telemetry = ThermalControlTelemetry.Assess(sample, rooms, entities, site, now);
        var observed = await FeedbackAsync(userId, now, ct);
        cache.TryGet(userId, connection?.HeatingDeviationEntityId ?? "", out var actuator);
        var step = LwtControlBinding.StartupStep(connection?.HeatingDeviationEntityId ?? "", actuator, now, site.ActiveDeviationLimitC);
        var healthy = connection is { TelemetryEnabled: true, ControlEnabled: true } && account.Connected &&
            account.ConfigurationUpdatedAtUtc == connection.UpdatedAtUtc && account.LastSnapshotUtc >= connection.UpdatedAtUtc &&
            observed is not null && step is not null;
        var sensorReason = telemetry.InvalidReason;
        if (sample is null || !ThermalReadinessService.HasRequiredTelemetry(sample, rooms, entities))
            sensorReason = "Kritiska rum eller värmegivare saknar verifierat underlag.";
        if (observed is { } actual && Math.Abs(actual - state.CurrentDeviationC) > .11)
        {
            // Treat an unexplained actuator change as an intervention, not as a
            // new optimization baseline. Recommission after verified zeroing.
            startup.Phase = "RecoveryRequired";
            sensorReason = "Avvikelsen har ändrats utanför regulatorn; ny inkopplingskontroll krävs.";
        }
        var input = new ConservativeLwtInput(now, sample?.TimestampUtc, startup.LastEvaluationUtc,
            state.LastDeviationWriteUtc, observed ?? double.NaN,
            telemetry.RepresentativeTemperatureErrorC, telemetry.CriticalRoomBelowMinimum,
            telemetry.DhwActive, telemetry.DefrostActive, telemetry.FlowLitresPerMinute, state.PiIntegral,
            build.HasRevision && configuration.GetValue("Thermal:AllowLwtActive", false) && configuration.GetValue("Thermal:AllowConservativeStart", false),
            true, healthy, state.ManualOverrideUntilUtc > now, sensorReason, site.ActiveDeviationLimitC, step ?? .5);
        var regulator = new ConservativeLwtRegulator();
        var decision = regulator.Evaluate(input);
        ValidatedThermalPlan? validated = null;
        if (!decision.IsFallback && !telemetry.DhwActive && !telemetry.DefrostActive)
        {
            try
            {
                validated = await ThermalPlanConsumption.ReadCurrentAsync(db, userId, now, build, ct);
                if (validated is not null && now - validated.Plan.CreatedAtUtc <= TimeSpan.FromMinutes(60))
                {
                    // Confidence changes authority only inside the already approved
                    // ±1 °C envelope. A preliminary/Shadow plan can never get here.
                    var bias = validated.CurrentStep.DesiredLwtDeviationC * Math.Clamp(validated.Plan.Confidence, 0, 1) * .5;
                    decision = regulator.EvaluateWithValidatedBias(input, bias);
                    if (decision.ShouldWrite)
                        await ThermalPlanConsumption.EnsureStillCurrentAsync(db, userId, validated, DateTimeOffset.UtcNow, build, ct);
                }
            }
            catch (ThermalPlanningEvidenceException) { decision = regulator.Evaluate(input); }
        }
        startup.LastEvaluationUtc = now;
        state.PiIntegral = decision.NewIntegral;
        if (decision.ShouldWrite)
        {
            try
            {
                await SendAsync(userId, decision.RequestedDeviationC, state, decision.Reason, ct);
                db.ThermalEvents.Add(Event(userId, decision.IsFallback ? "Warning" : "Information", decision.Reason));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                startup.Phase = "RecoveryRequired";
                state.FallbackReason = "P1P2-skrivningen eller återkopplingen misslyckades; återställning krävs.";
                state.PiIntegral = 0;
                db.ThermalEvents.Add(Event(userId, "ActionRequired", state.FallbackReason));
                // Try zero immediately with its own timeout, also on shutdown.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await SendAsync(userId, 0, state, "Nollställning efter misslyckad försiktig styrning.", cleanup.Token); }
                catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException) { /* Recovery stays pending. */ }
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
        }
        state.FallbackReason = decision.IsFallback ? decision.Reason : string.Empty;
        await db.SaveChangesAsync(ct);
    }

    internal async Task<string> FingerprintAsync(string userId, ThermalSiteConfig site, CancellationToken ct)
    {
        var connection = await db.HomeAssistantConnections.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new { x.BaseUrl, x.HeatingDeviationEntityId, x.UpdatedAtUtc, x.ControlEnabled }).SingleOrDefaultAsync(ct);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.EntityId).ToListAsync(ct);
        var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Role).ToListAsync(ct);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            userId, connection, rooms, entities, site.BaseRoomTargetC, site.LowerComfortBandC,
            site.UpperComfortBandC, site.ActiveDeviationLimitC, site.WeatherCurveVerified
        })));
    }

    internal async Task<double?> FeedbackAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var connection = await db.HomeAssistantConnections.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
        var id = LwtControlBinding.FeedbackEntity(connection?.HeatingDeviationEntityId ?? "", entities);
        return id is not null && cache.TryGet(userId, id, out var feedback) && LwtControlBinding.NumericFeedback(feedback, now) &&
               double.TryParse(feedback!.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : null;
    }

    internal async Task SendAsync(string userId, double value, ThermalControlState state, string reason, CancellationToken ct)
    {
        var lease = await db.ThermalControlStates.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
        if (string.IsNullOrWhiteSpace(state.LeaseOwner) || lease.LeaseOwner != state.LeaseOwner ||
            lease.LeaseExpiresUtc is null || lease.LeaseExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(20))
            throw new InvalidOperationException("Skrivleasen kan inte verifieras före kommandot.");
        var target = await db.HomeAssistantConnections.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.HeatingDeviationEntityId).SingleAsync(ct);
        if (value != 0)
        {
            // Model/source validation can take time. Recheck current live safety
            // immediately before a nonzero command, not only at evaluation start.
            var now = DateTimeOffset.UtcNow;
            var site = await db.ThermalSiteConfigs.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
            var connection = await db.HomeAssistantConnections.AsNoTracking().SingleAsync(x => x.UserId == userId, ct);
            var account = cache.ReadAccount(userId);
            var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
            var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(ct);
            var sample = await db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId)
                .OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(ct);
            var safety = ThermalControlTelemetry.Assess(sample, rooms, entities, site, now);
            cache.TryGet(userId, target, out var actuator);
            var step = LwtControlBinding.StartupStep(target, actuator, now, site.ActiveDeviationLimitC);
            if (!build.HasRevision || !configuration.GetValue("Thermal:AllowLwtActive", false) ||
                !configuration.GetValue("Thermal:AllowConservativeStart", false) ||
                site.ControlMode != "LwtActive" || site.DhwWriter != "Legacy" ||
                !account.Connected || account.ConfigurationUpdatedAtUtc != connection.UpdatedAtUtc ||
                !safety.SafeToControl || safety.DhwActive || safety.DefrostActive || safety.FlowLitresPerMinute is not > 1 ||
                sample is null || !ThermalReadinessService.HasRequiredTelemetry(sample, rooms, entities) ||
                step is null || Math.Abs(value) > Math.Min(1, site.ActiveDeviationLimitC) ||
                Math.Abs(value / step.Value - Math.Round(value / step.Value)) > 1e-6)
                throw new InvalidOperationException("Aktuellt säkerhetsunderlag tillåter inte kommandot.");
        }
        var command = new ThermalControlCommand { UserId = userId, TimestampUtc = DateTimeOffset.UtcNow,
            CommandType = "LwtDeviation", Target = target, RequestedValue = value, PreviousValue = state.CurrentDeviationC,
            Reason = reason, Outcome = "Pending" };
        db.ThermalControlCommands.Add(command);
        await db.SaveChangesAsync(CancellationToken.None);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await control.SetHeatingDeviationAsync(userId, value, timeout.Token);
            state.CurrentDeviationC = value;
            state.LastDeviationWriteUtc = DateTimeOffset.UtcNow;
            command.Outcome = "Accepted";
        }
        catch
        {
            command.Outcome = "Rejected";
            command.Error = "Kommandot eller återkopplingen kunde inte verifieras.";
            throw;
        }
        finally { await db.SaveChangesAsync(CancellationToken.None); }
    }

    internal static ThermalEvent Event(string userId, string severity, string message) => new()
    {
        UserId = userId, TimestampUtc = DateTimeOffset.UtcNow, Category = "ConservativeStartup",
        Severity = severity, Message = message, DetailsJson = "{}"
    };
    private static ReadinessCheck Check(string key, string requirement, bool passed, string action) =>
        new(key, requirement, passed, passed ? "Godkänt." : action, passed ? "Information" : "ActionRequired");
}
