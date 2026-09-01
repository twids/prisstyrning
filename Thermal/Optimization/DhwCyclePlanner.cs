using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Optimization;

public sealed record DhwPricePeriod(DateTimeOffset StartUtc, DateTimeOffset EndUtc, decimal SekPerKwh);

public sealed record DhwPowerStep(int Minute, double ElectricPowerKw, double ExpectedCop, bool BackupHeater);

public sealed record DhwCycleProfile(
    string Kind,
    int ExpectedDurationMinutes,
    int ReservedDurationMinutes,
    IReadOnlyList<DhwPowerStep> PowerSteps,
    DhwProfileSourceEvidence? SourceEvidence = null);

public sealed record DhwProfileSourceEvidence(
    string Kind,
    double StartTemperatureC,
    double TargetTemperatureC,
    double? BrineTemperatureC,
    int CompletedCycleCount,
    int PhaseSampleCount,
    bool HeatPumpPowerSignVerified,
    bool UsedEmpiricalDuration,
    bool UsedEmpiricalPower,
    string SourceFingerprint);

public sealed record DhwPlanningInput(
    DateTimeOffset NowUtc,
    DateTimeOffset EarliestStartUtc,
    DateTimeOffset HardDeadlineUtc,
    string Kind,
    double StartTemperatureC,
    double TargetTemperatureC,
    double? BrineTemperatureC,
    IReadOnlyList<DhwPricePeriod> Prices,
    DhwCycleProfile Profile,
    DateTimeOffset? LockedStartUtc = null,
    Func<DateTimeOffset, decimal>? SpaceHeatingPenalty = null);

public sealed record DhwCandidate(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    decimal EnergyCostSek,
    decimal SpaceHeatingPenaltySek,
    decimal TotalCostSek,
    bool FitsDeadline);

public sealed record DhwPlanResult(
    bool Success,
    DhwCandidate? Selected,
    IReadOnlyList<DhwCandidate> Alternatives,
    DecisionReason Reason);

public sealed class DhwCyclePlanner
{
    public DhwPlanResult Plan(DhwPlanningInput input)
    {
        Validate(input);
        if (input.LockedStartUtc is { } locked && locked - input.NowUtc <= TimeSpan.FromMinutes(20))
        {
            var lockedCandidate = CostCandidate(input, NormalizeTenMinuteStart(locked), allowMissingPrices: true);
            return new DhwPlanResult(
                true,
                lockedCandidate,
                [lockedCandidate],
                new DecisionReason("Starttiden ligger inom låsfönstret och flyttas därför inte.", null, null, 1, null));
        }

        var earliest = input.EarliestStartUtc > input.NowUtc ? input.EarliestStartUtc : input.NowUtc;
        var firstStart = CeilingToTenMinutes(earliest);
        var lastStart = input.HardDeadlineUtc.AddMinutes(-input.Profile.ReservedDurationMinutes);
        var candidates = new List<DhwCandidate>();
        for (var start = firstStart; start <= lastStart; start = start.AddMinutes(10))
        {
            if (HasCompletePriceCoverage(input.Prices, start, input.Profile.ReservedDurationMinutes))
                candidates.Add(CostCandidate(input, start));
        }

        if (candidates.Count == 0)
        {
            var emergencyStart = firstStart;
            var emergency = CostCandidate(input, emergencyStart, allowMissingPrices: true);
            return new DhwPlanResult(
                true,
                emergency,
                [emergency],
                new DecisionReason(
                    "Prisprognosen täcker inte hela cykeln; tidigaste säkra start väljs eftersom DHW-fristen går före pris.",
                    PriceAt(input.Prices, emergencyStart, allowMissingPrice: true),
                    null,
                    0.35,
                    "Saknade femminutersperioder kostnadsberäknas med högsta kända pris."));
        }

        var feasible = candidates.Where(x => x.FitsDeadline).OrderBy(x => x.TotalCostSek).ThenBy(x => x.StartUtc).ToArray();
        var selected = feasible.FirstOrDefault() ?? candidates.OrderBy(x => x.StartUtc).First();
        var reason = feasible.Length > 0
            ? $"Start {selected.StartUtc:HH:mm} eftersom hela cykeln beräknas kosta {selected.TotalCostSek:0.00} kr."
            : $"Start {selected.StartUtc:HH:mm} är den tidigaste säkra starten före hård deadline.";
        return new DhwPlanResult(
            true,
            selected,
            candidates.OrderBy(x => x.StartUtc).ToArray(),
            new DecisionReason(reason, PriceAt(input.Prices, selected.StartUtc), null, 0.8, feasible.Skip(1).FirstOrDefault() is { } alternative ? $"Nästa alternativ: {alternative.StartUtc:HH:mm}, {alternative.TotalCostSek:0.00} kr." : null));
    }

    internal static DateTimeOffset CeilingToTenMinutes(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var floor = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 10 * 10, 0, TimeSpan.Zero);
        return floor < utc ? floor.AddMinutes(10) : floor;
    }

    internal static DateTimeOffset NormalizeTenMinuteStart(DateTimeOffset value) => CeilingToTenMinutes(value);

    private static DhwCandidate CostCandidate(DhwPlanningInput input, DateTimeOffset start, bool allowMissingPrices = false)
    {
        decimal energyCost = 0;
        var end = start.AddMinutes(input.Profile.ReservedDurationMinutes);
        for (var minute = 0; minute < input.Profile.ReservedDurationMinutes; minute += 5)
        {
            var timestamp = start.AddMinutes(minute);
            var step = ProfileAt(input.Profile, minute);
            energyCost += (decimal)(step.ElectricPowerKw * 5d / 60d) * PriceAt(input.Prices, timestamp, allowMissingPrices);
        }

        var penalty = input.SpaceHeatingPenalty?.Invoke(start) ?? 0;
        return new DhwCandidate(start, end, decimal.Round(energyCost, 4), penalty, decimal.Round(energyCost + penalty, 4), end <= input.HardDeadlineUtc);
    }

    private static DhwPowerStep ProfileAt(DhwCycleProfile profile, int minute) =>
        profile.PowerSteps.Where(x => x.Minute <= minute).OrderByDescending(x => x.Minute).FirstOrDefault()
        ?? new DhwPowerStep(0, 2, 3, false);

    private static decimal PriceAt(IReadOnlyList<DhwPricePeriod> prices, DateTimeOffset timestamp, bool allowMissingPrice = false)
    {
        var period = prices.FirstOrDefault(x => x.StartUtc <= timestamp && x.EndUtc > timestamp);
        if (period is not null) return period.SekPerKwh;
        if (!allowMissingPrice) throw new InvalidOperationException($"Electricity price is missing for {timestamp:O}.");
        return prices.Count == 0 ? 10m : prices.Max(x => x.SekPerKwh);
    }

    private static bool HasCompletePriceCoverage(IEnumerable<DhwPricePeriod> prices, DateTimeOffset start, int durationMinutes)
    {
        for (var minute = 0; minute < durationMinutes; minute += 5)
        {
            var timestamp = start.AddMinutes(minute);
            if (!prices.Any(x => x.StartUtc <= timestamp && x.EndUtc > timestamp)) return false;
        }
        return true;
    }

    private static void Validate(DhwPlanningInput input)
    {
        if (input.HardDeadlineUtc <= input.EarliestStartUtc) throw new ArgumentException("DHW deadline must be after earliest start.");
        if (input.Profile.ReservedDurationMinutes <= 0 || input.Profile.ReservedDurationMinutes % 5 != 0)
            throw new ArgumentException("Reserved DHW duration must use five-minute steps.");
        if (input.Profile.PowerSteps.Count == 0) throw new ArgumentException("DHW power profile is empty.");
        if (input.Prices.Any(x => x.EndUtc <= x.StartUtc)) throw new ArgumentException("Invalid price period.");
    }
}
