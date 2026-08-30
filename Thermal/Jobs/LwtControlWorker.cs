using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

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

        var lease = scope.ServiceProvider.GetRequiredService<WriterLeaseService>();
        var leaseHeld = await lease.TryAcquireOrRenewAsync(userId, _leaseIdentity.Owner, TimeSpan.FromMinutes(15), cancellationToken);
        db.ChangeTracker.Clear();
        var state = await db.ThermalControlStates.SingleAsync(x => x.UserId == userId, cancellationToken);
        var telemetry = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId).OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(cancellationToken);
        var plan = await db.ThermalPlans.AsNoTracking()
            .Where(x => x.UserId == userId && x.ValidFromUtc <= DateTimeOffset.UtcNow && x.ValidUntilUtc > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var planStep = plan is null ? null : await db.ThermalPlanSteps.AsNoTracking()
            .Where(x => x.ThermalPlanId == plan.Id && x.StartUtc <= DateTimeOffset.UtcNow && x.EndUtc > DateTimeOffset.UtcNow)
            .OrderByDescending(x => x.StartUtc).FirstOrDefaultAsync(cancellationToken);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var haConnection = await db.HomeAssistantConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var heatingDeviationEntityId = haConnection?.HeatingDeviationEntityId ?? string.Empty;
        var (representativeError, criticalBelow) = RoomComfort(telemetry?.RoomTemperaturesJson, rooms, site);
        var writeFailureLatched = state.FallbackReason.StartsWith("P1P2-skrivningen", StringComparison.Ordinal);
        var p1p2Healthy = !writeFailureLatched &&
                          haConnection?.ControlEnabled == true &&
                          _cache.TryGet(userId, heatingDeviationEntityId, out var p1p2State) &&
                          p1p2State?.LastUpdatedUtc is { } p1p2Updated &&
                          DateTimeOffset.UtcNow - p1p2Updated <= TimeSpan.FromMinutes(10) &&
                          !p1p2State.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase);

        var decision = _regulator.Evaluate(new LwtRegulatorInput(
            mode,
            DateTimeOffset.UtcNow,
            telemetry?.TimestampUtc,
            plan?.CreatedAtUtc,
            planStep?.DesiredLwtDeviationC ?? 0,
            representativeError,
            criticalBelow,
            telemetry?.DhwActive == true,
            telemetry?.DefrostActive == true,
            telemetry?.FlowLitresPerMinute,
            leaseHeld,
            p1p2Healthy,
            state.ManualOverrideUntilUtc > DateTimeOffset.UtcNow,
            state.CurrentDeviationC,
            state.LastDeviationWriteUtc,
            state.PiIntegral,
            site?.ActiveDeviationLimitC ?? 1));
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

    private static (double Error, bool CriticalBelow) RoomComfort(
        string? roomsJson,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        ThermalSiteConfig? site)
    {
        if (string.IsNullOrWhiteSpace(roomsJson) || rooms.Count == 0) return (0, false);
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, double>>(roomsJson) ?? [];
            var included = rooms.Where(x => x.Weight > 0 && values.ContainsKey(x.EntityId)).ToArray();
            if (included.Length == 0) return (0, false);
            var baseTarget = site?.BaseRoomTargetC ?? 21.5;
            var error = included.Sum(x => (values[x.EntityId] - (baseTarget + x.TargetOffsetC)) * x.Weight) / included.Sum(x => x.Weight);
            var lowerBand = site?.LowerComfortBandC ?? 0.5;
            var critical = included.Any(x => x.IsCritical && values[x.EntityId] < baseTarget + x.TargetOffsetC - lowerBand);
            return (error, critical);
        }
        catch (JsonException)
        {
            return (0, false);
        }
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
