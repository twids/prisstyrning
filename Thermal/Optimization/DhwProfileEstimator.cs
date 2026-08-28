using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Optimization;

public sealed class DhwProfileEstimator
{
    private readonly PrisstyrningDbContext _db;

    public DhwProfileEstimator(PrisstyrningDbContext db) => _db = db;

    public async Task<DhwCycleProfile> EstimateAsync(
        string userId,
        string kind,
        double startTemperatureC,
        double targetTemperatureC,
        double? brineTemperatureC,
        CancellationToken cancellationToken = default)
    {
        var cycles = await _db.DhwCycles.AsNoTracking()
            .Where(x => x.UserId == userId && x.Kind == kind && x.Status == "Completed" &&
                        x.ActualStartUtc != null && x.TargetReachedUtc != null &&
                        x.StartTemperatureC != null && Math.Abs(x.StartTemperatureC.Value - startTemperatureC) <= 5)
            .OrderByDescending(x => x.ActualStartUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        var observed = cycles
            .Select(x => (int)Math.Ceiling((x.TargetReachedUtc!.Value - x.ActualStartUtc!.Value).TotalMinutes / 5) * 5)
            .Where(x => x is >= 15 and <= 180)
            .OrderBy(x => x)
            .ToArray();

        var temperatureLift = Math.Max(1, targetTemperatureC - startTemperatureC);
        var fallback = kind.Equals("Comfort", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp((int)Math.Ceiling((35 + temperatureLift * 1.5) / 5) * 5, 45, 90)
            : Math.Clamp((int)Math.Ceiling((20 + temperatureLift * 1.3) / 5) * 5, 30, 75);
        var expected = observed.Length >= 3 ? (int)Math.Round(observed.Average() / 5) * 5 : fallback;
        var reserved = observed.Length >= 3 ? Percentile(observed, 0.9) : Math.Min(180, expected + 15);
        var basePower = brineTemperatureC is < 0 ? 2.4 : 2.1;
        var phaseObservations = await LoadPhaseObservationsAsync(userId, cycles, cancellationToken);
        var early = phaseObservations.Where(x => x.Fraction < 0.65).ToArray();
        var middle = phaseObservations.Where(x => x.Fraction is >= 0.65 and < 0.85).ToArray();
        var late = phaseObservations.Where(x => x.Fraction >= 0.85).ToArray();
        var hasEmpiricalPower = phaseObservations.Count >= 12;
        var middleMinute = AlignToFive(reserved * 0.65, reserved);
        var lateMinute = AlignToFive(reserved * 0.85, reserved);
        var steps = new List<DhwPowerStep>
        {
            new(0, MeanPower(early, basePower), MeanCop(early, 3.1), false),
            new(middleMinute, MeanPower(middle, basePower * 1.05), MeanCop(middle, 2.5), false)
        };
        if (hasEmpiricalPower || kind.Equals("Comfort", StringComparison.OrdinalIgnoreCase))
        {
            var backup = hasEmpiricalPower
                ? late.Count(x => x.BackupHeater) >= Math.Max(1, (int)Math.Ceiling(late.Length * 0.2))
                : kind.Equals("Comfort", StringComparison.OrdinalIgnoreCase);
            steps.Add(new(
                lateMinute,
                MeanPower(late, kind.Equals("Comfort", StringComparison.OrdinalIgnoreCase) ? 3.0 : basePower * 1.1),
                backup ? 1.0 : MeanCop(late, 2.2),
                backup));
        }
        return new DhwCycleProfile(
            kind,
            expected,
            reserved,
            steps.GroupBy(x => x.Minute).Select(x => x.Last()).OrderBy(x => x.Minute).ToArray());
    }

    private async Task<IReadOnlyList<PhaseObservation>> LoadPhaseObservationsAsync(
        string userId,
        IReadOnlyCollection<DhwCycle> cycles,
        CancellationToken cancellationToken)
    {
        if (cycles.Count < 3) return [];
        var signVerified = await _db.ThermalSiteConfigs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.HeatPumpPowerSignVerified)
            .SingleOrDefaultAsync(cancellationToken);
        if (!signVerified) return [];

        var from = cycles.Min(x => x.ActualStartUtc!.Value);
        var to = cycles.Max(x => x.TargetReachedUtc!.Value);
        var samples = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= from && x.TimestampUtc <= to &&
                        x.DhwActive == true && x.HeatPumpPowerKw > 0.1 && x.HeatPumpPowerKw < 20)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
        var result = new List<PhaseObservation>();
        foreach (var cycle in cycles)
        {
            var start = cycle.ActualStartUtc!.Value;
            var end = cycle.TargetReachedUtc!.Value;
            var duration = (end - start).TotalMinutes;
            if (duration <= 0) continue;
            foreach (var sample in samples.Where(x => x.TimestampUtc >= start && x.TimestampUtc <= end))
            {
                result.Add(new PhaseObservation(
                    Math.Clamp((sample.TimestampUtc - start).TotalMinutes / duration, 0, 1),
                    sample.HeatPumpPowerKw!.Value,
                    sample.Cop is >= 1.2 and <= 8 ? sample.Cop : null,
                    sample.BackupHeaterActive == true));
            }
        }
        return result;
    }

    private static int AlignToFive(double minute, int duration) =>
        Math.Clamp((int)Math.Round(minute / 5, MidpointRounding.AwayFromZero) * 5, 0, Math.Max(0, duration - 5));

    private static double MeanPower(IReadOnlyCollection<PhaseObservation> values, double fallback) =>
        values.Count == 0 ? fallback : Math.Clamp(values.Average(x => x.PowerKw), 0.1, 20);

    private static double MeanCop(IReadOnlyCollection<PhaseObservation> values, double fallback)
    {
        var valid = values.Where(x => x.Cop is not null).Select(x => x.Cop!.Value).ToArray();
        return valid.Length == 0 ? fallback : Math.Clamp(valid.Average(), 1.2, 8);
    }

    internal static int Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) throw new ArgumentException("At least one duration is required.");
        var index = Math.Clamp((int)Math.Ceiling(percentile * sortedValues.Count) - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    private sealed record PhaseObservation(double Fraction, double PowerKw, double? Cop, bool BackupHeater);
}
