using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Jobs;

public sealed class JointPlanCoordinator : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmhassOptions _options;
    private readonly WriterLeaseIdentity _leaseIdentity;
    private readonly ILogger<JointPlanCoordinator> _logger;
    private readonly ConcurrentDictionary<string, CoordinatorMemory> _memory = new(StringComparer.Ordinal);

    public JointPlanCoordinator(
        IServiceScopeFactory scopeFactory,
        IOptions<EmhassOptions> options,
        WriterLeaseIdentity leaseIdentity,
        ILogger<JointPlanCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _leaseIdentity = leaseIdentity;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var installations = scope.ServiceProvider.GetRequiredService<ThermalInstallationRegistry>();
                var userIds = await installations.GetUsersAsync(includeLegacy: false, activeLwtOnly: false, cancellationToken: stoppingToken);
                foreach (var userId in userIds)
                {
                    try
                    {
                        if (await ShouldReplanAsync(userId, stoppingToken)) await ReplanAsync(userId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Joint thermal planning failed for user {UserId}.", userId);
                        await RecordPlanningFailureAsync(userId, exception.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not enumerate thermal installations for joint planning.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<bool> ShouldReplanAsync(string userId, CancellationToken cancellationToken)
    {
        var memory = _memory.GetOrAdd(userId, static _ => new CoordinatorMemory());
        if (!_options.Enabled) return false;
        if (memory.LastFailureUtc is { } lastFailure && DateTimeOffset.UtcNow - lastFailure < TimeSpan.FromMinutes(5))
            return false;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var modeText = await db.ThermalSiteConfigs.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.ControlMode).SingleOrDefaultAsync(cancellationToken);
        if (ThermalEnumParser.ControlModeOrLegacy(modeText) == ControlMode.Legacy) return false;
        var latestPlan = await db.ThermalPlans.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).Select(x => (DateTimeOffset?)x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (memory.LastAttemptUtc is { } lastAttempt &&
            DateTimeOffset.UtcNow - lastAttempt < (latestPlan is null ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1)))
            return false;
        var telemetry = await db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(cancellationToken);
        var priceSaved = await db.PriceSnapshots.AsNoTracking().OrderByDescending(x => x.SavedAtUtc)
            .Select(x => (DateTimeOffset?)x.SavedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var materialTankChange = telemetry?.TankTemperatureC is { } tank && memory.LastTankTemperatureC is { } previous && Math.Abs(tank - previous) >= 2;
        var newPrices = priceSaved is { } saved && (memory.LastPriceSavedAtUtc is null || saved > memory.LastPriceSavedAtUtc);
        return latestPlan is null || DateTimeOffset.UtcNow - latestPlan >= TimeSpan.FromMinutes(15) || materialTankChange || newPrices;
    }

    internal async Task ReplanAsync(string userId, CancellationToken cancellationToken)
    {
        var memory = _memory.GetOrAdd(userId, static _ => new CoordinatorMemory());
        memory.LastAttemptUtc = DateTimeOffset.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var site = await db.ThermalSiteConfigs.AsNoTracking().SingleAsync(x => x.UserId == userId, cancellationToken);
        var mode = ThermalEnumParser.ControlModeOrLegacy(site.ControlMode);
        if (mode == ControlMode.Legacy) return;
        var telemetry = await db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No thermal telemetry is available.");
        if (DateTimeOffset.UtcNow - telemetry.TimestampUtc > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Thermal telemetry is stale.");

        var horizonStart = FloorToQuarter(DateTimeOffset.UtcNow);
        var horizonSteps = _options.HorizonHours * 60 / _options.OptimizationTimeStepMinutes;
        var prices = await LoadPriceForecastAsync(db, userId, horizonStart, horizonSteps, site, cancellationToken);
        var modelVersion = await db.ThermalModelVersions.AsNoTracking()
            .Where(x => x.UserId == userId && x.ModelType == "2R2C" && x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var copModelVersion = await db.ThermalModelVersions.AsNoTracking()
            .Where(x => x.UserId == userId && x.ModelType == "COP" && x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var parameters = DeserializeParameters(modelVersion?.ParametersJson);
        var copParameters = DeserializeCopParameters(copModelVersion?.ParametersJson);
        var copModel = scope.ServiceProvider.GetRequiredService<CopModel>();
        var estimatedCop = copParameters is not null
            ? copModel.Predict(
                copParameters,
                telemetry.BrineInC ?? 0,
                telemetry.LeavingWaterTemperatureC ?? parameters.BaseCurveInterceptC,
                telemetry.HeatOutputKw ?? 4)
            : Math.Clamp(telemetry.Cop ?? 3, 1.2, 8);
        var roomTemperature = AverageRoomTemperature(telemetry.RoomTemperaturesJson) ?? site.BaseRoomTargetC;
        var outside = telemetry.OutsideTemperatureC ?? 0;
        var weather = BuildWeatherForecast(
            telemetry.OutsideTemperatureForecastJson,
            horizonStart,
            horizonSteps,
            _options.OptimizationTimeStepMinutes,
            outside,
            telemetry.WindSpeedMps ?? 0);

        var dhw = await PlanDhwAsync(
            scope,
            db,
            userId,
            site,
            telemetry,
            prices.Periods,
            horizonStart,
            weather,
            parameters,
            roomTemperature,
            cancellationToken);
        var horizonEnd = horizonStart.AddHours(_options.HorizonHours);
        var reservationStart = dhw?.Selected is null ? (DateTimeOffset?)null : Max(dhw.Selected.StartUtc, horizonStart);
        var reservationEnd = dhw?.Selected is null ? (DateTimeOffset?)null : Min(dhw.Selected.EndUtc, horizonEnd);
        if (reservationStart is not null && reservationEnd <= reservationStart)
        {
            reservationStart = null;
            reservationEnd = null;
        }
        int? dhwStartStep = reservationStart is null
            ? null
            : Math.Max(0, (int)Math.Floor((reservationStart.Value - horizonStart).TotalMinutes / _options.OptimizationTimeStepMinutes));
        var dhwDurationSteps = reservationStart is null || reservationEnd is null
            ? 0
            : Math.Max(1, (int)Math.Ceiling((reservationEnd.Value - reservationStart.Value).TotalMinutes / _options.OptimizationTimeStepMinutes));
        var minimum = Enumerable.Repeat(site.BaseRoomTargetC - site.LowerComfortBandC, horizonSteps).ToArray();
        var maximum = Enumerable.Repeat(site.BaseRoomTargetC + site.UpperComfortBandC, horizonSteps).ToArray();
        var averageWind = weather.WindSpeedMps.Count == 0 ? 0 : weather.WindSpeedMps.Average();
        var effectiveConductance = parameters.EnvelopeConductanceKwPerC +
                                   parameters.WindLossCoefficientKwPerCPerMps * Math.Max(0, averageWind);
        var outsideForecast = weather.TemperatureC.Select((temperature, index) =>
        {
            var solarGainKw = parameters.SolarGainKwPerWm2 * Math.Max(0, weather.SolarIrradianceWm2[index]);
            return effectiveConductance > 0.001 ? temperature + solarGainKw / effectiveConductance : temperature;
        }).ToArray();
        var baseLoadW = Enumerable.Repeat(Math.Max(0, ((telemetry.PropertyPowerKw ?? 0) - (telemetry.HeatPumpPowerKw ?? 0)) * 1000), horizonSteps).ToArray();
        var thermal = new EmhassThermalConfig(
            Math.Clamp(2.5 * estimatedCop * parameters.HeatingGain / parameters.AirCapacityKwhPerC, 0.1, 10),
            Math.Clamp(effectiveConductance / parameters.AirCapacityKwhPerC, 0.001, 1),
            Math.Clamp(parameters.MassCapacityKwhPerC / parameters.MassCouplingKwPerC, 0, 24),
            roomTemperature,
            minimum,
            maximum);
        var request = new EmhassOptimizationRequest(
            prices.Steps,
            outsideForecast,
            baseLoadW,
            thermal,
            dhwStartStep,
            dhwDurationSteps,
            2500,
            dhw?.Profile.PowerSteps.Max(x => x.ElectricPowerKw) * 1000 ?? 2500,
            site.TariffEnabled,
            CapacityCost(site.TariffDefinitionJson));
        var emhass = scope.ServiceProvider.GetRequiredService<IEmhassClient>();
        var optimized = await emhass.OptimizeAsync(request, cancellationToken);
        var comfortBreach = optimized.Steps.FirstOrDefault(step =>
            step.PredictedTemperatureC is { } predicted && predicted < minimum[step.Index] - 0.01);
        if (comfortBreach is not null)
        {
            var recentBreachExists = await db.ThermalEvents.AsNoTracking().AnyAsync(
                x => x.UserId == userId && x.Category == "SimulatedComfortBreach" &&
                     x.TimestampUtc >= DateTimeOffset.UtcNow.AddHours(-6),
                cancellationToken);
            if (!recentBreachExists)
            {
                db.ThermalEvents.Add(new ThermalEvent
                {
                    UserId = userId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = "ActionRequired",
                    Category = "SimulatedComfortBreach",
                    Message = "Shadowplanen underskrider komfortgränsen och får inte aktiveras.",
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        step = comfortBreach.Index,
                        predictedC = comfortBreach.PredictedTemperatureC,
                        minimumC = minimum[comfortBreach.Index]
                    })
                });
            }
        }

        var plan = new ThermalPlan
        {
            UserId = userId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ValidFromUtc = horizonStart,
            ValidUntilUtc = horizonStart.AddHours(_options.HorizonHours),
            Status = "Valid",
            IsShadow = mode == ControlMode.Shadow,
            SolverDurationMs = optimized.SolverDurationMs,
            ObjectiveCost = optimized.ObjectiveCost + (dhw?.Selected?.TotalCostSek ?? 0),
            Confidence = modelVersion is null ? 0.25 : copModelVersion is null ? 0.6 : 0.85,
            Summary = dhw?.Result.Reason.MainReason ?? "Husvärme optimerad utan DHW-reservation.",
            InputSnapshotJson = JsonSerializer.Serialize(new
            {
                telemetry.TimestampUtc,
                modelVersionId = modelVersion?.Id,
                copModelVersionId = copModelVersion?.Id,
                estimatedCop,
                dhw = dhw?.Selected,
                priceForecast = prices.ActualCoverage,
                weatherForecast = weather.ActualCoverage
            })
        };
        foreach (var step in optimized.Steps)
        {
            var start = horizonStart.AddMinutes(step.Index * _options.OptimizationTimeStepMinutes);
            var end = start.AddMinutes(_options.OptimizationTimeStepMinutes);
            var duty = Math.Clamp(step.SpaceHeatingPowerW / request.HeatPumpElectricPowerW, 0, 1);
            var deviation = Math.Round(((duty - 0.5) * 2 * site.ActiveDeviationLimitC) * 2) / 2;
            var reserved = dhw?.Selected is { } selected && selected.StartUtc < end && selected.EndUtc > start;
            plan.Steps.Add(new ThermalPlanStep
            {
                StartUtc = start,
                EndUtc = end,
                DesiredHeatOutputKw = step.SpaceHeatingPowerW / 1000 * estimatedCop,
                DesiredLwtDeviationC = reserved ? 0 : deviation,
                DhwReserved = reserved,
                DhwMode = reserved ? dhw!.Kind : string.Empty,
                IncrementalCost = (decimal)(step.SpaceHeatingPowerW / 1000 * _options.OptimizationTimeStepMinutes / 60d) * prices.Steps[step.Index],
                Confidence = plan.Confidence,
                ExpectedRoomsJson = JsonSerializer.Serialize(new { representative = step.PredictedTemperatureC }),
                DecisionReasonJson = JsonSerializer.Serialize(new DecisionReason(
                    reserved ? "Kompressorkapaciteten är reserverad för varmvatten." : "EMHASS minimerar kostnaden inom komfortbandet.",
                    prices.Steps[step.Index],
                    step.PredictedTemperatureC - minimum[step.Index],
                    plan.Confidence,
                    null))
            });
        }
        db.ThermalPlans.Add(plan);
        await UpsertDhwCycleAsync(db, userId, mode, dhw, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (mode == ControlMode.FullActive && dhw?.Cycle is { ScheduleAcceptedUtc: null } cycle)
        {
            var lease = scope.ServiceProvider.GetRequiredService<WriterLeaseService>();
            if (await lease.TryAcquireOrRenewAsync(userId, _leaseIdentity.Owner, TimeSpan.FromMinutes(15), cancellationToken))
            {
                var writer = scope.ServiceProvider.GetRequiredService<JointDhwScheduleWriter>();
                await writer.ApplyAsync(cycle.Id, cancellationToken);
            }
        }
        memory.LastTankTemperatureC = telemetry.TankTemperatureC;
        memory.LastPriceSavedAtUtc = prices.SavedAtUtc;
        memory.LastFailureUtc = null;
    }

    private async Task<PlannedDhw?> PlanDhwAsync(
        IServiceScope scope,
        PrisstyrningDbContext db,
        string userId,
        ThermalSiteConfig site,
        ThermalTelemetrySample telemetry,
        IReadOnlyList<DhwPricePeriod> prices,
        DateTimeOffset horizonStart,
        WeatherSeries weather,
        GreyBoxParameters parameters,
        double roomTemperature,
        CancellationToken cancellationToken)
    {
        if (telemetry.TankTemperatureC is null) return null;
        var now = DateTimeOffset.UtcNow;
        var running = await db.DhwCycles
            .Where(x => x.UserId == userId && x.ActualStartUtc != null && x.ActualEndUtc == null)
            .OrderByDescending(x => x.ActualStartUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (running is not null)
            return await ReserveExistingCycleAsync(scope, userId, running, telemetry, horizonStart, now, cancellationToken);

        var existing = await db.DhwCycles
            .Where(x => x.UserId == userId && x.ActualStartUtc == null && x.ActualEndUtc == null &&
                        (x.Status == "Shadow" || x.Status == "Planned" || x.Status == "Accepted") &&
                        x.PlannedStartUtc > now.AddMinutes(-30))
            .OrderBy(x => x.PlannedStartUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is { ScheduleAcceptedUtc: not null } && existing.PlannedStartUtc <= now.AddMinutes(20))
            return await ReserveExistingCycleAsync(scope, userId, existing, telemetry, horizonStart, now, cancellationToken);

        var lastComfort = await db.DhwCycles.AsNoTracking().Where(x => x.UserId == userId && x.Kind == "Comfort" && x.TargetReachedUtc != null)
            .OrderByDescending(x => x.TargetReachedUtc).FirstOrDefaultAsync(cancellationToken);
        var comfortDeadline = lastComfort?.TargetReachedUtc is { } lastVerified
            ? lastVerified.AddDays(site.ComfortIntervalDays + site.ComfortFlexibilityDays)
            : now.AddHours(Math.Min(24, _options.HorizonHours));
        var comfortEarliest = lastComfort is null
            ? now
            : comfortDeadline.AddDays(-site.ComfortFlexibilityDays * 2);
        var kind = comfortDeadline <= horizonStart.AddHours(_options.HorizonHours) ? "Comfort" : "Eco";
        var target = kind == "Comfort" ? site.ComfortSetpointC : 45;
        var earliest = kind == "Comfort" ? Max(now.AddMinutes(20), comfortEarliest) : now.AddMinutes(20);
        var deadline = kind == "Comfort" ? comfortDeadline : now.AddHours(36);
        deadline = Min(deadline, horizonStart.AddHours(_options.HorizonHours));
        var estimator = scope.ServiceProvider.GetRequiredService<DhwProfileEstimator>();
        var profile = await estimator.EstimateAsync(userId, kind, telemetry.TankTemperatureC.Value, target, telemetry.BrineInC, cancellationToken);
        var planner = scope.ServiceProvider.GetRequiredService<DhwCyclePlanner>();
        var comfortPenalty = BuildDhwComfortPenalty(
            horizonStart,
            weather,
            parameters,
            roomTemperature,
            site.BaseRoomTargetC - site.LowerComfortBandC,
            profile.ReservedDurationMinutes);
        var result = planner.Plan(new DhwPlanningInput(
            now,
            earliest,
            deadline,
            kind,
            telemetry.TankTemperatureC.Value,
            target,
            telemetry.BrineInC,
            prices,
            profile,
            existing?.PlannedStartUtc,
            comfortPenalty));
        if (!result.Success || result.Selected is null) return null;

        if (existing is { ScheduleAcceptedUtc: not null } &&
            existing.PlannedStartUtc - now > TimeSpan.FromMinutes(20) &&
            result.Selected.StartUtc != existing.PlannedStartUtc)
        {
            var oldCandidate = result.Alternatives.FirstOrDefault(x => x.StartUtc == existing.PlannedStartUtc);
            if (oldCandidate is not null)
            {
                var requiredSaving = Math.Max(0.25m, Math.Abs(oldCandidate.TotalCostSek) * 0.10m);
                if (oldCandidate.TotalCostSek - result.Selected.TotalCostSek < requiredSaving)
                {
                    result = result with
                    {
                        Selected = oldCandidate,
                        Reason = new DecisionReason(
                            "Den accepterade starten behålls eftersom besparingen av en omskrivning är för liten.",
                            oldCandidate.EnergyCostSek,
                            null,
                            0.9,
                            $"Billigaste alternativet sparade mindre än {requiredSaving:0.00} kr.")
                    };
                }
            }
        }

        return new PlannedDhw(kind, target, profile, result, result.Selected, existing);
    }

    private static async Task<PlannedDhw> ReserveExistingCycleAsync(
        IServiceScope scope,
        string userId,
        DhwCycle cycle,
        ThermalTelemetrySample telemetry,
        DateTimeOffset horizonStart,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profile = DeserializeProfile(cycle.PowerProfileJson, cycle);
        if (profile is null)
        {
            var estimator = scope.ServiceProvider.GetRequiredService<DhwProfileEstimator>();
            profile = await estimator.EstimateAsync(
                userId,
                cycle.Kind,
                cycle.StartTemperatureC ?? telemetry.TankTemperatureC ?? 40,
                cycle.TargetTemperatureC,
                telemetry.BrineInC,
                cancellationToken);
        }

        var start = cycle.ActualStartUtc ?? cycle.PlannedStartUtc;
        var end = cycle.EstimatedCompletionUtc ?? start.AddMinutes(Math.Max(5, cycle.ReservedDurationMinutes));
        if (end <= now && telemetry.DhwActive == true) end = now.AddMinutes(15);
        if (end <= horizonStart) end = horizonStart.AddMinutes(5);
        var cost = cycle.PredictedCost ?? 0;
        var selected = new DhwCandidate(start, end, cost, 0, cost, true);
        var reason = new DecisionReason(
            cycle.ActualStartUtc is null
                ? "ONECTA-starten ligger inom låsfönstret och flyttas inte."
                : "Pågående DHW är ett icke-avbrytbart jobb och reserveras tills beräknad sluttid.",
            null,
            null,
            1,
            null);
        var result = new DhwPlanResult(true, selected, [selected], reason);
        return new PlannedDhw(cycle.Kind, cycle.TargetTemperatureC, profile, result, selected, cycle);
    }

    private static DhwCycleProfile? DeserializeProfile(string json, DhwCycle cycle)
    {
        try
        {
            var steps = JsonSerializer.Deserialize<DhwPowerStep[]>(json);
            return steps is { Length: > 0 }
                ? new DhwCycleProfile(
                    cycle.Kind,
                    Math.Max(5, cycle.PredictedDurationMinutes),
                    Math.Max(5, cycle.ReservedDurationMinutes),
                    steps)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task UpsertDhwCycleAsync(
        PrisstyrningDbContext db,
        string userId,
        ControlMode mode,
        PlannedDhw? planned,
        CancellationToken cancellationToken)
    {
        if (planned is null) return;
        var cycle = planned.Cycle;
        if (cycle is null)
        {
            cycle = new DhwCycle { UserId = userId };
            db.DhwCycles.Add(cycle);
            planned.Cycle = cycle;
        }
        if (cycle.ActualStartUtc is not null) return;

        var scheduleChanged = cycle.Id != 0 &&
                              (cycle.PlannedStartUtc != planned.Selected.StartUtc ||
                               !cycle.Kind.Equals(planned.Kind, StringComparison.OrdinalIgnoreCase));
        if (scheduleChanged && cycle.ScheduleAcceptedUtc is not null)
            cycle.ScheduleAcceptedUtc = null;
        cycle.Kind = planned.Kind;
        cycle.Source = mode == ControlMode.FullActive ? "Joint" : "Shadow";
        cycle.Status = mode == ControlMode.FullActive
            ? cycle.ScheduleAcceptedUtc is null ? "Planned" : "Accepted"
            : "Shadow";
        cycle.PlannedStartUtc = planned.Selected.StartUtc;
        cycle.StartTemperatureC = null;
        cycle.TargetTemperatureC = planned.TargetTemperatureC;
        cycle.PredictedDurationMinutes = planned.Profile.ExpectedDurationMinutes;
        cycle.ReservedDurationMinutes = planned.Profile.ReservedDurationMinutes;
        cycle.PredictedCost = planned.Selected.TotalCostSek;
        cycle.EstimatedCompletionUtc = planned.Selected.EndUtc;
        cycle.PowerProfileJson = JsonSerializer.Serialize(planned.Profile.PowerSteps);
        await Task.CompletedTask;
    }

    private async Task<PriceForecast> LoadPriceForecastAsync(
        PrisstyrningDbContext db,
        string userId,
        DateTimeOffset start,
        int count,
        ThermalSiteConfig site,
        CancellationToken cancellationToken)
    {
        var zone = await db.UserSettings.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.Zone).SingleOrDefaultAsync(cancellationToken) ?? "SE3";
        var snapshot = await db.PriceSnapshots.AsNoTracking().Where(x => x.Zone == zone).OrderByDescending(x => x.SavedAtUtc).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No electricity prices are available.");
        var points = ParsePricePoints(snapshot.TodayPricesJson).Concat(ParsePricePoints(snapshot.TomorrowPricesJson))
            .GroupBy(x => x.StartUtc).Select(x => x.Last()).OrderBy(x => x.StartUtc).ToArray();
        if (points.Length == 0) throw new InvalidOperationException("The latest price snapshot is empty.");
        var variableCosts = VariableCosts(site.VariableCostComponentsJson);
        var average = points.Average(x => x.Price) + variableCosts;
        var stepPrices = new List<decimal>(count);
        var actual = 0;
        for (var index = 0; index < count; index++)
        {
            var timestamp = start.AddMinutes(index * _options.OptimizationTimeStepMinutes);
            var point = points.LastOrDefault(x => x.StartUtc <= timestamp);
            decimal price;
            if (point is not null && NextStart(points, point.StartUtc) is { } next && timestamp < next || point is not null && timestamp - point.StartUtc < TimeSpan.FromHours(1))
            {
                price = point.Price + variableCosts;
                actual++;
            }
            else
            {
                var previousDay = points.LastOrDefault(x => x.StartUtc <= timestamp.AddDays(-1));
                price = previousDay?.Price + variableCosts ?? average;
            }
            stepPrices.Add(price);
        }
        var periods = stepPrices.Select((price, index) => new DhwPricePeriod(
            start.AddMinutes(index * _options.OptimizationTimeStepMinutes),
            start.AddMinutes((index + 1) * _options.OptimizationTimeStepMinutes),
            price)).ToArray();
        return new PriceForecast(stepPrices, periods, actual / (double)count, snapshot.SavedAtUtc);
    }

    private static IEnumerable<PricePoint> ParsePricePoints(string json)
    {
        var array = JsonNode.Parse(json) as JsonArray;
        if (array is null) yield break;
        foreach (var node in array.OfType<JsonObject>())
        {
            if (!DateTimeOffset.TryParse(node["start"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) ||
                !decimal.TryParse(node["value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            yield return new PricePoint(start.ToUniversalTime(), value);
        }
    }

    private static DateTimeOffset? NextStart(IReadOnlyList<PricePoint> points, DateTimeOffset current) =>
        points.FirstOrDefault(x => x.StartUtc > current)?.StartUtc;

    private static GreyBoxParameters DeserializeParameters(string? json)
    {
        try { return JsonSerializer.Deserialize<GreyBoxParameters>(json ?? "") ?? new(2, 35, 0.35, 0.8, 0.95, 35, -0.45); }
        catch (JsonException) { return new(2, 35, 0.35, 0.8, 0.95, 35, -0.45); }
    }

    private static CopParameters? DeserializeCopParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CopParameters>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? AverageRoomTemperature(string json)
    {
        try { var values = JsonSerializer.Deserialize<Dictionary<string, double>>(json); return values?.Count > 0 ? values.Values.Average() : null; }
        catch (JsonException) { return null; }
    }

    private static decimal VariableCosts(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, decimal>>(json)?.Values.Sum() ?? 0; }
        catch (JsonException) { return 0; }
    }

    private static double CapacityCost(string json)
    {
        try { return JsonNode.Parse(json)?["capacityCostPerKw"]?.GetValue<double>() ?? 0; }
        catch (JsonException) { return 0; }
    }

    private static WeatherSeries BuildWeatherForecast(
        string json,
        DateTimeOffset start,
        int count,
        int stepMinutes,
        double fallbackTemperatureC,
        double fallbackWindSpeedMps)
    {
        WeatherForecastPoint[] points;
        try
        {
            points = JsonSerializer.Deserialize<WeatherForecastPoint[]>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch (JsonException)
        {
            points = [];
        }
        points = points.OrderBy(x => x.TimestampUtc).ToArray();
        var temperatures = new double[count];
        var winds = new double[count];
        var solar = new double[count];
        var actual = 0;
        for (var index = 0; index < count; index++)
        {
            var timestamp = start.AddMinutes(index * stepMinutes);
            var point = points.LastOrDefault(x => x.TimestampUtc <= timestamp && timestamp - x.TimestampUtc <= TimeSpan.FromHours(3))
                        ?? points.FirstOrDefault(x => x.TimestampUtc > timestamp && x.TimestampUtc - timestamp <= TimeSpan.FromHours(1));
            if (point is not null) actual++;
            temperatures[index] = point?.TemperatureC ?? fallbackTemperatureC;
            winds[index] = point?.WindSpeedMps ?? fallbackWindSpeedMps;
            solar[index] = point?.SolarIrradianceWm2 ?? 0;
        }
        return new WeatherSeries(temperatures, winds, solar, actual / (double)Math.Max(1, count));
    }

    private Func<DateTimeOffset, decimal> BuildDhwComfortPenalty(
        DateTimeOffset horizonStart,
        WeatherSeries weather,
        GreyBoxParameters parameters,
        double roomTemperatureC,
        double lowerComfortLimitC,
        int reservedDurationMinutes)
    {
        return startUtc =>
        {
            var index = Math.Clamp(
                (int)Math.Floor((startUtc - horizonStart).TotalMinutes / _options.OptimizationTimeStepMinutes),
                0,
                Math.Max(0, weather.TemperatureC.Count - 1));
            var outsideC = weather.TemperatureC[index];
            var windMps = weather.WindSpeedMps[index];
            var solarWm2 = weather.SolarIrradianceWm2[index];
            var conductance = parameters.EnvelopeConductanceKwPerC +
                              parameters.WindLossCoefficientKwPerCPerMps * Math.Max(0, windMps);
            var heatLossKw = Math.Max(0, conductance * (roomTemperatureC - outsideC) -
                                         parameters.SolarGainKwPerWm2 * Math.Max(0, solarWm2));
            var estimatedDropC = heatLossKw / Math.Max(0.1, parameters.AirCapacityKwhPerC) *
                                 reservedDurationMinutes / 60d;
            var marginC = roomTemperatureC - estimatedDropC - lowerComfortLimitC;
            if (marginC >= 0.5) return 0;
            var shortfallC = 0.5 - marginC;
            return decimal.Round((decimal)(10 * shortfallC * shortfallC), 4);
        };
    }

    private async Task RecordPlanningFailureAsync(string userId, string message, CancellationToken cancellationToken)
    {
        var memory = _memory.GetOrAdd(userId, static _ => new CoordinatorMemory());
        var now = DateTimeOffset.UtcNow;
        memory.LastFailureUtc = now;
        if (memory.LastFailureEventUtc is { } previous && now - previous < TimeSpan.FromMinutes(15)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalEvents.Add(new ThermalEvent
            {
                UserId = userId,
                TimestampUtc = now,
                Severity = "Warning",
                Category = "Optimizer",
                Message = "Ingen ny plan skapades; senaste giltiga plan får användas i högst 60 minuter.",
                DetailsJson = JsonSerializer.Serialize(new { error = message })
            });
            await db.SaveChangesAsync(cancellationToken);
            memory.LastFailureEventUtc = now;
        }
        catch { }
    }

    private sealed class CoordinatorMemory
    {
        public double? LastTankTemperatureC { get; set; }
        public DateTimeOffset? LastPriceSavedAtUtc { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public DateTimeOffset? LastFailureUtc { get; set; }
        public DateTimeOffset? LastFailureEventUtc { get; set; }
    }

    private static DateTimeOffset FloorToQuarter(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private sealed record PricePoint(DateTimeOffset StartUtc, decimal Price);
    private sealed record PriceForecast(IReadOnlyList<decimal> Steps, IReadOnlyList<DhwPricePeriod> Periods, double ActualCoverage, DateTimeOffset SavedAtUtc);
    private sealed record WeatherSeries(IReadOnlyList<double> TemperatureC, IReadOnlyList<double> WindSpeedMps, IReadOnlyList<double> SolarIrradianceWm2, double ActualCoverage);
    private sealed record PlannedDhw(string Kind, double TargetTemperatureC, DhwCycleProfile Profile, DhwPlanResult Result, DhwCandidate Selected, DhwCycle? InitialCycle)
    {
        public DhwCycle? Cycle { get; set; } = InitialCycle;
    }
}
