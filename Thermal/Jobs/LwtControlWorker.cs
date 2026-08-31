using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Jobs;

public sealed class LwtControlWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHomeAssistantStateCache _cache;
    private readonly LwtRegulator _regulator;
    private readonly WriterLeaseIdentity _leaseIdentity;
    private readonly ILogger<LwtControlWorker> _logger;

    public LwtControlWorker(
        IServiceScopeFactory scopeFactory,
        IHomeAssistantStateCache cache,
        LwtRegulator regulator,
        WriterLeaseIdentity leaseIdentity,
        ILogger<LwtControlWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _regulator = regulator;
        _leaseIdentity = leaseIdentity;
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
                var installations = scope.ServiceProvider.GetRequiredService<ThermalInstallationRegistry>();
                var userIds = await installations.GetUsersAsync(includeLegacy: false, activeLwtOnly: true, cancellationToken: stoppingToken);
                foreach (var userId in userIds)
                {
                    try { await EvaluateAsync(userId, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { _logger.LogError(exception, "LWT regulator evaluation failed for user {UserId}.", userId); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) { _logger.LogError(exception, "Could not enumerate active thermal installations for LWT control."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task EvaluateAsync(string userId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var site = await db.ThermalSiteConfigs.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var mode = ThermalEnumParser.ControlModeOrLegacy(site?.ControlMode);
        if (mode is not (ControlMode.LwtActive or ControlMode.FullActive)) return;

        var now = DateTimeOffset.UtcNow;
        ValidatedThermalPlan? validatedPlan = null;
        string? invalidPlanReason = null;
        try
        {
            validatedPlan = await ThermalPlanConsumption.ReadCurrentAsync(db, userId, now, cancellationToken);
        }
        catch (ThermalPlanningEvidenceException)
        {
            invalidPlanReason = "Planens verifierade underlag gäller inte längre; LWT återgår säkert till noll.";
            _logger.LogWarning("LWT plan evidence is no longer valid for account {UserId}.", userId);
        }
        var lease = scope.ServiceProvider.GetRequiredService<WriterLeaseService>();
        var leaseHeld = await lease.TryAcquireOrRenewAsync(userId, _leaseIdentity.Owner, TimeSpan.FromMinutes(15), cancellationToken);
        db.ChangeTracker.Clear();
        var state = await db.ThermalControlStates.SingleAsync(x => x.UserId == userId, cancellationToken);
        var telemetry = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId).OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(cancellationToken);
        var plan = validatedPlan?.Plan;
        var planStep = validatedPlan?.CurrentStep;
        var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var haConnection = await db.HomeAssistantConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var heatingDeviationEntityId = haConnection?.HeatingDeviationEntityId ?? string.Empty;
        var controlTelemetry = ThermalControlTelemetry.Assess(telemetry, rooms, entities, site, now);
        var writeFailureLatched = state.FallbackReason.StartsWith("P1P2-skrivningen", StringComparison.Ordinal);
        var p1p2Healthy = !writeFailureLatched &&
                          haConnection?.ControlEnabled == true &&
                          _cache.TryGet(userId, heatingDeviationEntityId, out var p1p2State) &&
                          p1p2State?.LastUpdatedUtc is { } p1p2Updated &&
                          now - p1p2Updated <= TimeSpan.FromMinutes(10) &&
                          !p1p2State.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase);

        var input = new LwtRegulatorInput(
            mode,
            now,
            telemetry?.TimestampUtc,
            plan?.CreatedAtUtc,
            planStep?.DesiredLwtDeviationC ?? 0,
            controlTelemetry.RepresentativeTemperatureErrorC,
            controlTelemetry.CriticalRoomBelowMinimum,
            controlTelemetry.DhwActive,
            controlTelemetry.DefrostActive,
            controlTelemetry.FlowLitresPerMinute,
            leaseHeld,
            p1p2Healthy,
            state.ManualOverrideUntilUtc > now,
            state.CurrentDeviationC,
            state.LastDeviationWriteUtc,
            state.PiIntegral,
            site?.ActiveDeviationLimitC ?? 1,
            invalidPlanReason ?? controlTelemetry.InvalidReason);
        var decision = _regulator.Evaluate(input);
        if (decision.ShouldWrite && !decision.IsFallback && validatedPlan is not null)
        {
            try
            {
                await ThermalPlanConsumption.EnsureStillCurrentAsync(
                    db, userId, validatedPlan, DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (ThermalPlanningEvidenceException)
            {
                decision = _regulator.Evaluate(input with
                {
                    PlanCreatedUtc = null,
                    SafetyInvalidReason = "Planens verifierade underlag ändrades före skrivning; LWT återgår säkert till noll."
                });
                _logger.LogWarning("LWT plan evidence changed before writing for account {UserId}.", userId);
            }
        }
        state.PiIntegral = decision.NewIntegral;

        if (decision.ShouldWrite && leaseHeld)
        {
            var previousDeviation = state.CurrentDeviationC;
            try
            {
                var control = scope.ServiceProvider.GetRequiredService<IHomeAssistantControlClient>();
                await control.SetHeatingDeviationAsync(userId, decision.RequestedDeviationC, cancellationToken);
                state.CurrentDeviationC = decision.RequestedDeviationC;
                state.LastDeviationWriteUtc = DateTimeOffset.UtcNow;
                state.FallbackReason = decision.IsFallback ? decision.Reason : string.Empty;
                db.ThermalControlCommands.Add(Command(
                    userId,
                    heatingDeviationEntityId,
                    decision.RequestedDeviationC,
                    previousDeviation,
                    "Accepted",
                    decision.Reason));
                db.ThermalEvents.Add(Event(userId, decision.IsFallback ? "Warning" : "Information", decision.IsFallback ? "Fallback" : "LwtCommand", decision.Reason, decision.RequestedDeviationC));
            }
            catch (Exception exception)
            {
                state.FallbackReason = "P1P2-skrivningen avvisades; manuell kontroll krävs.";
                db.ThermalControlCommands.Add(Command(
                    userId,
                    heatingDeviationEntityId,
                    decision.RequestedDeviationC,
                    previousDeviation,
                    "Rejected",
                    decision.Reason,
                    exception.Message));
                if (Math.Abs(previousDeviation) >= 0.01 && Math.Abs(decision.RequestedDeviationC) >= 0.01)
                {
                    try
                    {
                        var control = scope.ServiceProvider.GetRequiredService<IHomeAssistantControlClient>();
                        await control.SetHeatingDeviationAsync(userId, 0, cancellationToken);
                        state.CurrentDeviationC = 0;
                        state.LastDeviationWriteUtc = DateTimeOffset.UtcNow;
                        db.ThermalControlCommands.Add(Command(
                            userId,
                            heatingDeviationEntityId,
                            0,
                            previousDeviation,
                            "Accepted",
                            "Automatisk nollställning efter ett avvisat LWT-kommando."));
                    }
                    catch (Exception zeroException)
                    {
                        db.ThermalControlCommands.Add(Command(
                            userId,
                            heatingDeviationEntityId,
                            0,
                            previousDeviation,
                            "Rejected",
                            "Automatisk nollställning efter ett avvisat LWT-kommando.",
                            zeroException.Message));
                    }
                }
                db.ThermalEvents.Add(Event(userId, "ActionRequired", "P1P2Write", state.FallbackReason, null, exception.Message));
            }
        }
        else if (decision.IsFallback && state.FallbackReason != decision.Reason)
        {
            state.FallbackReason = decision.Reason;
            db.ThermalEvents.Add(Event(userId, "Warning", "Fallback", decision.Reason, null));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static ThermalEvent Event(string userId, string severity, string category, string message, double? deviation, string? detail = null) => new()
    {
        UserId = userId,
        TimestampUtc = DateTimeOffset.UtcNow,
        Severity = severity,
        Category = category,
        Message = message,
        DetailsJson = JsonSerializer.Serialize(new { deviation, detail })
    };

    private static ThermalControlCommand Command(
        string userId,
        string target,
        double requested,
        double previous,
        string outcome,
        string reason,
        string? error = null) => new()
    {
        UserId = userId,
        TimestampUtc = DateTimeOffset.UtcNow,
        CommandType = "LwtDeviation",
        Target = target,
        RequestedValue = requested,
        PreviousValue = previous,
        Outcome = outcome,
        Reason = reason.Length <= 500 ? reason : reason[..500],
        Error = string.IsNullOrWhiteSpace(error) ? string.Empty : error.Length <= 1000 ? error : error[..1000]
    };
}
