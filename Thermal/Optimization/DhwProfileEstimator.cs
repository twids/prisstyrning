using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Optimization;

public sealed class DhwProfileEstimator
{
    private static readonly string[] PhaseRoles =
    [
        ThermalEntityRoles.DhwActive,
        ThermalEntityRoles.DefrostActive,
        ThermalEntityRoles.BackupHeaterActive,
        ThermalEntityRoles.HeatPumpPower
    ];

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
        var source = await ReadSourceAsync(
            _db, userId, kind, startTemperatureC, targetTemperatureC, brineTemperatureC, cancellationToken);
        var observed = source.Cycles
            .Select(x => (int)Math.Ceiling((x.TargetReachedUtc!.Value - x.ActualStartUtc!.Value).TotalMinutes / 5) * 5)
            .OrderBy(x => x)
            .ToArray();

        var temperatureLift = Math.Max(1, targetTemperatureC - startTemperatureC);
        var fallback = source.Evidence.Kind == "Comfort"
            ? Math.Clamp((int)Math.Ceiling((35 + temperatureLift * 1.5) / 5) * 5, 45, 90)
            : Math.Clamp((int)Math.Ceiling((20 + temperatureLift * 1.3) / 5) * 5, 30, 75);
        var expected = source.Evidence.UsedEmpiricalDuration ? (int)Math.Round(observed.Average() / 5) * 5 : fallback;
        var reserved = source.Evidence.UsedEmpiricalDuration ? Percentile(observed, 0.9) : Math.Min(180, expected + 15);
        var basePower = brineTemperatureC is < 0 ? 2.4 : 2.1;
        var early = source.Observations.Where(x => x.Fraction < 0.65).ToArray();
        var middle = source.Observations.Where(x => x.Fraction is >= 0.65 and < 0.85).ToArray();
        var late = source.Observations.Where(x => x.Fraction >= 0.85).ToArray();
        var middleMinute = AlignToFive(reserved * 0.65, reserved);
        var lateMinute = AlignToFive(reserved * 0.85, reserved);
        var steps = new List<DhwPowerStep>
        {
            new(0, MeanPower(early, basePower), MeanCop(early, 3.1), false),
            new(middleMinute, MeanPower(middle, basePower * 1.05), MeanCop(middle, 2.5), false)
        };
        if (source.Evidence.UsedEmpiricalPower || source.Evidence.Kind == "Comfort")
        {
            var backup = source.Evidence.UsedEmpiricalPower
                ? late.Count(x => x.BackupHeater) >= Math.Max(1, (int)Math.Ceiling(late.Length * 0.2))
                : source.Evidence.Kind == "Comfort";
            steps.Add(new(
                lateMinute,
                MeanPower(late, source.Evidence.Kind == "Comfort" ? 3.0 : basePower * 1.1),
                backup ? 1.0 : MeanCop(late, 2.2),
                backup));
        }
        return new DhwCycleProfile(
            source.Evidence.Kind,
            expected,
            reserved,
            steps.GroupBy(x => x.Minute).Select(x => x.Last()).OrderBy(x => x.Minute).ToArray(),
            source.Evidence);
    }

    internal static async Task EnsureCurrentAsync(
        PrisstyrningDbContext db,
        string userId,
        DhwProfileSourceEvidence evidence,
        CancellationToken cancellationToken)
    {
        ProfileSource current;
        try
        {
            current = await ReadSourceAsync(
                db,
                userId,
                evidence.Kind,
                evidence.StartTemperatureC,
                evidence.TargetTemperatureC,
                evidence.BrineTemperatureC,
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ThermalPlanningEvidenceException("DHW-profilens historiska underlag kan inte verifieras.");
        }
        if (current.Evidence != evidence)
            throw new ThermalPlanningEvidenceException("DHW-profilens historiska cykler eller mätningar ändrades. En ny plan behövs.");
    }

    private static async Task<ProfileSource> ReadSourceAsync(
        PrisstyrningDbContext db,
        string userId,
        string kind,
        double startTemperatureC,
        double targetTemperatureC,
        double? brineTemperatureC,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("An account is required.", nameof(userId));
        var normalizedKind = NormalizeKind(kind);
        ValidateTemperature(startTemperatureC, 0, 80, nameof(startTemperatureC));
        ValidateTemperature(targetTemperatureC, 30, 70, nameof(targetTemperatureC));
        if (brineTemperatureC is { } brine && (!double.IsFinite(brine) || brine is < -50 or > 50))
            throw new ArgumentOutOfRangeException(nameof(brineTemperatureC));

        var candidates = await db.DhwCycles.AsNoTracking()
            .Where(x => x.UserId == userId && x.Kind == normalizedKind && x.Status == "Completed" &&
                        x.ActualStartUtc != null && x.TargetReachedUtc != null && x.ActualEndUtc != null &&
                        x.StartTemperatureC != null)
            .OrderByDescending(x => x.ActualStartUtc)
            .Take(200)
            .ToListAsync(cancellationToken);
        var cycles = candidates.Where(x => IsValidCycle(x, startTemperatureC, targetTemperatureC))
            .Take(100)
            .ToArray();
        var signVerified = await db.ThermalSiteConfigs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.HeatPumpPowerSignVerified)
            .SingleOrDefaultAsync(cancellationToken);
        var (samples, observations) = signVerified && cycles.Length >= 3
            ? await ReadPhaseSamplesAsync(db, userId, cycles, cancellationToken)
            : (Array.Empty<ThermalTelemetrySample>(), Array.Empty<PhaseObservation>());
        var usedEmpiricalDuration = cycles.Length >= 3;
        var usedEmpiricalPower = observations.Length >= 12;
        var fingerprint = Fingerprint(
            userId,
            normalizedKind,
            startTemperatureC,
            targetTemperatureC,
            brineTemperatureC,
            signVerified,
            cycles,
            samples);
        var evidence = new DhwProfileSourceEvidence(
            normalizedKind,
            startTemperatureC,
            targetTemperatureC,
            brineTemperatureC,
            cycles.Length,
            samples.Length,
            signVerified,
            usedEmpiricalDuration,
            usedEmpiricalPower,
            fingerprint);
        return new(cycles, observations, evidence);
    }

    private static async Task<(ThermalTelemetrySample[] Samples, PhaseObservation[] Observations)> ReadPhaseSamplesAsync(
        PrisstyrningDbContext db,
        string userId,
        IReadOnlyCollection<DhwCycle> cycles,
        CancellationToken cancellationToken)
    {
        var entities = await db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled)
            .ToListAsync(cancellationToken);
        var required = new List<ThermalEntityConfig>(PhaseRoles.Length);
        foreach (var role in PhaseRoles)
        {
            var matches = entities.Where(x => x.Role.Equals(role, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return ([], []);
            required.Add(matches[0]);
        }

        var from = cycles.Min(x => x.ActualStartUtc!.Value);
        var to = cycles.Max(x => x.TargetReachedUtc!.Value);
        var candidates = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= from && x.TimestampUtc <= to &&
                        x.DhwActive == true && x.DefrostActive == false && x.BackupHeaterActive != null &&
                        x.HeatPumpPowerKw > 0.1 && x.HeatPumpPowerKw < 20)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
        var valid = candidates.GroupBy(x => x.TimestampUtc)
            .Where(x => x.Count() == 1)
            .Select(x => x.Single())
            .Where(x => double.IsFinite(x.HeatPumpPowerKw!.Value) &&
                        ThermalStatusQuality.Assess(
                            x, [], required, x.TimestampUtc, allowHistoryImport: true).Quality == DataQuality.Valid)
            .ToArray();

        var usedSamples = new Dictionary<long, ThermalTelemetrySample>();
        var observations = new List<PhaseObservation>();
        var coveredCycleCount = 0;
        foreach (var cycle in cycles)
        {
            var start = cycle.ActualStartUtc!.Value;
            var end = cycle.TargetReachedUtc!.Value;
            var duration = (end - start).TotalMinutes;
            var samples = valid.Where(x => x.TimestampUtc >= start && x.TimestampUtc <= end).ToArray();
            var expectedSamples = (int)Math.Floor(duration / 5) + 1;
            if (samples.Length < Math.Ceiling(expectedSamples * .8) || samples.Length == 0 ||
                samples[0].TimestampUtc - start > TimeSpan.FromMinutes(7.5) ||
                end - samples[^1].TimestampUtc > TimeSpan.FromMinutes(7.5) ||
                samples.Zip(samples.Skip(1)).Any(x => x.Second.TimestampUtc - x.First.TimestampUtc > TimeSpan.FromMinutes(7.5)))
                continue;
            coveredCycleCount++;
            foreach (var sample in samples)
            {
                usedSamples[sample.Id] = sample;
                var cop = sample.BackupHeaterActive == false && sample.Cop is >= 1.2 and <= 8 && double.IsFinite(sample.Cop.Value)
                    ? sample.Cop
                    : null;
                observations.Add(new PhaseObservation(
                    Math.Clamp((sample.TimestampUtc - start).TotalMinutes / duration, 0, 1),
                    sample.HeatPumpPowerKw!.Value,
                    cop,
                    sample.BackupHeaterActive == true));
            }
        }
        if (coveredCycleCount < 3) return ([], []);
        return (usedSamples.Values.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).ToArray(), observations.ToArray());
    }

    private static bool IsValidCycle(DhwCycle cycle, double startTemperatureC, double targetTemperatureC)
    {
        if (cycle.ActualStartUtc is not { } start || cycle.TargetReachedUtc is not { } reached ||
            cycle.ActualEndUtc is not { } end || cycle.StartTemperatureC is not { } cycleStart ||
            !double.IsFinite(cycleStart) || !double.IsFinite(cycle.TargetTemperatureC) ||
            cycle.TargetVerificationCount < 2 || reached <= start || end < reached ||
            Math.Abs(cycleStart - startTemperatureC) > 5 || Math.Abs(cycle.TargetTemperatureC - targetTemperatureC) > 1)
            return false;
        var duration = (reached - start).TotalMinutes;
        return duration is >= 15 and <= 180;
    }

    private static string Fingerprint(
        string userId,
        string kind,
        double startTemperatureC,
        double targetTemperatureC,
        double? brineTemperatureC,
        bool signVerified,
        IEnumerable<DhwCycle> cycles,
        IEnumerable<ThermalTelemetrySample> samples)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            userId,
            kind,
            startTemperatureC,
            targetTemperatureC,
            brineTemperatureC,
            signVerified,
            cycles = cycles.Select(x => new
            {
                x.Id,
                x.Kind,
                x.Source,
                x.Status,
                x.PlannedStartUtc,
                x.ScheduleAcceptedUtc,
                x.ActualStartUtc,
                x.TargetReachedUtc,
                x.ActualEndUtc,
                x.StartTemperatureC,
                x.TargetTemperatureC,
                x.PredictedDurationMinutes,
                x.ReservedDurationMinutes,
                x.PredictedCost,
                x.ActualCost,
                x.BackupHeaterUsed,
                x.PowerProfileJson,
                x.TargetVerificationCount,
                x.EstimatedCompletionUtc,
                x.LastVerificationSampleUtc
            }),
            samples = samples.Select(x => new
            {
                x.Id,
                x.TimestampUtc,
                x.HeatPumpPowerKw,
                x.Cop,
                x.DhwActive,
                x.DefrostActive,
                x.BackupHeaterActive,
                x.QualityJson
            })
        }, JsonSerializerOptions.Web);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }

    private static string NormalizeKind(string? kind) => kind?.Trim().ToUpperInvariant() switch
    {
        "ECO" => "Eco",
        "COMFORT" => "Comfort",
        _ => throw new ArgumentException("DHW profile kind must be Eco or Comfort.", nameof(kind))
    };

    private static void ValidateTemperature(double value, double minimum, double maximum, string parameter)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameter);
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

    private sealed record ProfileSource(
        IReadOnlyList<DhwCycle> Cycles,
        IReadOnlyList<PhaseObservation> Observations,
        DhwProfileSourceEvidence Evidence);

    private sealed record PhaseObservation(double Fraction, double PowerKw, double? Cop, bool BackupHeater);
}
