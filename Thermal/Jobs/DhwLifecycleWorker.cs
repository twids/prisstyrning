using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Data;

namespace Prisstyrning.Thermal.Jobs;

public sealed class DhwLifecycleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DhwLifecycleWorker> _logger;

    public DhwLifecycleWorker(IServiceScopeFactory scopeFactory, ILogger<DhwLifecycleWorker> logger)
    {
        _scopeFactory = scopeFactory;
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
                var userIds = await installations.GetUsersAsync(includeLegacy: true, activeLwtOnly: false, cancellationToken: stoppingToken);
                foreach (var userId in userIds)
                {
                    try { await ObserveAsync(userId, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { _logger.LogError(exception, "DHW lifecycle observation failed for user {UserId}.", userId); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogError(exception, "Could not enumerate thermal installations for DHW observation."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ObserveAsync(string userId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var sample = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (sample is null || DateTimeOffset.UtcNow - sample.TimestampUtc > TimeSpan.FromMinutes(10)) return;

        var site = await db.ThermalSiteConfigs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken);
        var writer = ThermalEnumParser.DhwWriterOrLegacy(site?.DhwWriter);
        var cycle = writer == DhwWriter.Legacy
            ? await db.DhwCycles
                .Where(x => x.UserId == userId && x.Source == "LegacyObserved" &&
                            x.ActualStartUtc != null && x.ActualEndUtc == null)
                .OrderByDescending(x => x.ActualStartUtc)
                .FirstOrDefaultAsync(cancellationToken)
            : await db.DhwCycles
                .Where(x => x.UserId == userId && (x.Source == "Joint" || x.Source == "JointObserved") && x.ActualEndUtc == null &&
                            x.PlannedStartUtc <= DateTimeOffset.UtcNow.AddMinutes(10))
                .OrderByDescending(x => x.PlannedStartUtc)
                .FirstOrDefaultAsync(cancellationToken);

        if (cycle is null && sample.DhwActive == true)
        {
            cycle = new DhwCycle
            {
                UserId = userId,
                Kind = "Eco",
                Source = writer == DhwWriter.Legacy ? "LegacyObserved" : "JointObserved",
                Status = "Running",
                PlannedStartUtc = sample.TimestampUtc,
                ActualStartUtc = sample.TimestampUtc,
                StartTemperatureC = sample.TankTemperatureC,
                TargetTemperatureC = 45,
                PredictedDurationMinutes = 45,
                ReservedDurationMinutes = 60,
                EstimatedCompletionUtc = sample.TimestampUtc.AddMinutes(60)
            };
            db.DhwCycles.Add(cycle);
            db.ThermalEvents.Add(Event(
                userId,
                writer == DhwWriter.Legacy ? "Information" : "ActionRequired",
                "DhwCycle",
                writer == DhwWriter.Legacy
                    ? "En faktisk legacy-DHW-körning observerades och följs separat från shadowplanen."
                    : "DHW startade utan en matchande accepterad joint-cykel."));
        }

        if (cycle is null) return;

        if (sample.DhwActive == true && cycle.ActualStartUtc is null)
        {
            cycle.ActualStartUtc = sample.TimestampUtc;
            cycle.StartTemperatureC = sample.TankTemperatureC;
            cycle.Status = "Running";
            db.ThermalEvents.Add(Event(userId, "Information", "DhwCycle", "Första verifierade DHW-drift har registrerats."));
        }

        if (cycle.ActualStartUtc is { } actualStart)
        {
            cycle.BackupHeaterUsed |= sample.BackupHeaterActive == true;
            if (sample.TankTemperatureC is { } temperature && cycle.LastVerificationSampleUtc != sample.TimestampUtc)
            {
                var previousVerificationUtc = cycle.LastVerificationSampleUtc;
                cycle.LastVerificationSampleUtc = sample.TimestampUtc;

                if (cycle.Source == "LegacyObserved" && temperature >= 60 &&
                    !cycle.Kind.Equals("Comfort", StringComparison.OrdinalIgnoreCase))
                {
                    cycle.Kind = "Comfort";
                    cycle.TargetTemperatureC = 60;
                    cycle.TargetReachedUtc = null;
                    cycle.TargetVerificationCount = 0;
                }

                var followsPrevious = previousVerificationUtc is { } previous &&
                                      sample.TimestampUtc > previous &&
                                      sample.TimestampUtc - previous <= TimeSpan.FromMinutes(7.5);
                cycle.TargetVerificationCount = temperature >= cycle.TargetTemperatureC
                    ? followsPrevious ? cycle.TargetVerificationCount + 1 : 1
                    : 0;
                if (cycle.TargetVerificationCount >= 2)
                {
                    cycle.TargetReachedUtc ??= sample.TimestampUtc;
                    cycle.Status = "TargetReached";
                }
                else
                {
                    cycle.EstimatedCompletionUtc = await EstimateCompletionAsync(
                        db,
                        userId,
                        actualStart,
                        sample,
                        cycle.TargetTemperatureC,
                        cycle.ReservedDurationMinutes,
                        cancellationToken);
                }
            }

            if (sample.DhwActive == false && sample.TimestampUtc - actualStart > TimeSpan.FromMinutes(10))
            {
                cycle.ActualEndUtc = sample.TimestampUtc;
                cycle.ActualCost = site?.HeatPumpPowerSignVerified == true
                    ? await CalculateActualCostAsync(db, userId, actualStart, sample.TimestampUtc, site.VariableCostComponentsJson, cancellationToken)
                    : null;
                if (cycle.TargetReachedUtc is not null)
                {
                    cycle.Status = "Completed";
                    db.ThermalEvents.Add(Event(
                        userId,
                        "Information",
                        "DhwVerification",
                        cycle.TargetTemperatureC >= 60
                            ? "60 °C har verifierats i två efterföljande giltiga femminutersmätningar vid tankgivaren."
                            : "DHW-målet har verifierats i två efterföljande giltiga femminutersmätningar."));
                }
                else
                {
                    cycle.Status = "TargetMissed";
                    db.ThermalEvents.Add(Event(userId, "ActionRequired", "DhwVerification", "DHW-driften avslutades innan temperaturmålet verifierades; cykeln omplaneras."));
                }
            }
        }
        else if (DateTimeOffset.UtcNow - cycle.PlannedStartUtc > TimeSpan.FromMinutes(30) && cycle.Status is "Accepted" or "Planned")
        {
            cycle.Status = "StartDelayed";
            db.ThermalEvents.Add(Event(userId, "Warning", "DhwCycle", "Planerad DHW-start har inte verifierats inom 30 minuter."));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<decimal?> CalculateActualCostAsync(
        PrisstyrningDbContext db,
        string userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string variableCostsJson,
        CancellationToken cancellationToken)
    {
        var powers = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc &&
                        x.HeatPumpPowerKw != null && x.HeatPumpPowerKw >= 0)
            .OrderBy(x => x.TimestampUtc)
            .Select(x => new { x.TimestampUtc, PowerKw = x.HeatPumpPowerKw!.Value })
            .ToListAsync(cancellationToken);
        if (powers.Count == 0) return null;

        var zone = await db.UserSettings.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.Zone)
            .SingleOrDefaultAsync(cancellationToken) ?? "SE3";
        var snapshots = await db.PriceSnapshots.AsNoTracking()
            .Where(x => x.Zone == zone && x.Date >= DateOnly.FromDateTime(fromUtc.UtcDateTime.AddDays(-1)) &&
                        x.Date <= DateOnly.FromDateTime(toUtc.UtcDateTime.AddDays(1)))
            .ToListAsync(cancellationToken);
        var prices = snapshots
            .SelectMany(x => ParsePrices(x.TodayPricesJson).Concat(ParsePrices(x.TomorrowPricesJson)))
            .GroupBy(x => x.StartUtc)
            .Select(x => x.Last())
            .OrderBy(x => x.StartUtc)
            .ToArray();
        if (prices.Length == 0) return null;

        decimal variableCosts;
        try { variableCosts = JsonSerializer.Deserialize<Dictionary<string, decimal>>(variableCostsJson)?.Values.Sum() ?? 0; }
        catch (JsonException) { variableCosts = 0; }
        decimal cost = 0;
        foreach (var sample in powers)
        {
            var point = prices.LastOrDefault(x => x.StartUtc <= sample.TimestampUtc);
            if (point is null) return null;
            var next = prices.FirstOrDefault(x => x.StartUtc > point.StartUtc);
            if (next is null && sample.TimestampUtc - point.StartUtc >= TimeSpan.FromHours(1) ||
                next is not null && sample.TimestampUtc >= next.StartUtc)
                return null;
            cost += (decimal)(sample.PowerKw * 5d / 60d) * (point.Price + variableCosts);
        }
        return decimal.Round(cost, 4);
    }

    private static IEnumerable<PricePoint> ParsePrices(string json)
    {
        JsonArray? array;
        try { array = JsonNode.Parse(json) as JsonArray; }
        catch (JsonException) { array = null; }
        if (array is null) yield break;
        foreach (var node in array.OfType<JsonObject>())
        {
            if (!DateTimeOffset.TryParse(node["start"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) ||
                !decimal.TryParse(node["value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            yield return new PricePoint(start.ToUniversalTime(), value);
        }
    }

    private static async Task<DateTimeOffset> EstimateCompletionAsync(
        PrisstyrningDbContext db,
        string userId,
        DateTimeOffset actualStart,
        ThermalTelemetrySample latest,
        double targetC,
        int reservedMinutes,
        CancellationToken cancellationToken)
    {
        var safeReservedMinutes = Math.Max(5, reservedMinutes);
        var first = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= actualStart && x.TankTemperatureC != null)
            .OrderBy(x => x.TimestampUtc).FirstOrDefaultAsync(cancellationToken);
        if (first?.TankTemperatureC is null || latest.TankTemperatureC is null || latest.TimestampUtc <= first.TimestampUtc)
            return actualStart.AddMinutes(safeReservedMinutes);
        var risePerMinute = (latest.TankTemperatureC.Value - first.TankTemperatureC.Value) /
                            (latest.TimestampUtc - first.TimestampUtc).TotalMinutes;
        if (risePerMinute <= 0.01) return actualStart.AddMinutes(safeReservedMinutes);
        var remaining = Math.Clamp((targetC - latest.TankTemperatureC.Value) / risePerMinute, 0, safeReservedMinutes);
        return latest.TimestampUtc.AddMinutes(remaining);
    }

    private static ThermalEvent Event(string userId, string severity, string category, string message) => new()
    {
        UserId = userId,
        TimestampUtc = DateTimeOffset.UtcNow,
        Severity = severity,
        Category = category,
        Message = message
    };

    private sealed record PricePoint(DateTimeOffset StartUtc, decimal Price);
}
