using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public class DhwCyclePlannerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Plan_UsesOnlyTenMinuteStartsAndCostsTheWholeCycle()
    {
        var planner = new DhwCyclePlanner();
        var profile = new DhwCycleProfile("Eco", 30, 30, [new DhwPowerStep(0, 2, 3, false)]);
        var prices = new[]
        {
            new DhwPricePeriod(Start, Start.AddMinutes(15), 0.1m),
            new DhwPricePeriod(Start.AddMinutes(15), Start.AddHours(2), 2m)
        };

        var result = planner.Plan(new DhwPlanningInput(
            Start, Start, Start.AddHours(1), "Eco", 40, 45, 2, prices, profile));

        Assert.True(result.Success);
        Assert.NotNull(result.Selected);
        Assert.Equal(0, result.Selected!.StartUtc.Minute % 10);
        Assert.Equal(1.05m, result.Selected.EnergyCostSek);
        Assert.All(result.Alternatives, candidate => Assert.Equal(0, candidate.StartUtc.Minute % 10));
        Assert.Contains("hela cykeln", result.Reason.MainReason);
    }

    [Fact]
    public void Plan_KeepsStartInsideTwentyMinuteLockWindow()
    {
        var planner = new DhwCyclePlanner();
        var locked = Start.AddMinutes(20);
        var result = planner.Plan(new DhwPlanningInput(
            Start.AddMinutes(5), Start, Start.AddHours(2), "Comfort", 42, 60, 1,
            [new DhwPricePeriod(Start, Start.AddHours(3), 5m)],
            new DhwCycleProfile("Comfort", 60, 60, [new DhwPowerStep(0, 3, 2, false)]),
            locked));

        Assert.Equal(locked, result.Selected!.StartUtc);
        Assert.Contains("låsfönstret", result.Reason.MainReason);
    }

    [Fact]
    public void Plan_SupportsNegativeQuarterPrices()
    {
        var planner = new DhwCyclePlanner();
        var prices = new[]
        {
            new DhwPricePeriod(Start, Start.AddMinutes(30), 1m),
            new DhwPricePeriod(Start.AddMinutes(30), Start.AddHours(1), -0.5m)
        };
        var result = planner.Plan(new DhwPlanningInput(
            Start, Start, Start.AddHours(1), "Eco", 40, 45, 2, prices,
            new DhwCycleProfile("Eco", 30, 30, [new DhwPowerStep(0, 2, 3, false)])));

        Assert.Equal(Start.AddMinutes(30), result.Selected!.StartUtc);
        Assert.True(result.Selected.TotalCostSek < 0);
    }

    [Fact]
    public void Plan_UsesEarliestSafeStartWhenPriceCoverageIsIncomplete()
    {
        var planner = new DhwCyclePlanner();
        var result = planner.Plan(new DhwPlanningInput(
            Start,
            Start,
            Start.AddMinutes(45),
            "Comfort",
            42,
            60,
            1,
            [new DhwPricePeriod(Start, Start.AddMinutes(10), 2m)],
            new DhwCycleProfile("Comfort", 60, 60, [new DhwPowerStep(0, 3, 2, true)])));

        Assert.True(result.Success);
        Assert.Equal(Start, result.Selected!.StartUtc);
        Assert.False(result.Selected.FitsDeadline);
        Assert.Contains("DHW-fristen går före pris", result.Reason.MainReason);
    }

    [Fact]
    public void Plan_AvoidsCheapStartWhenSpaceHeatingComfortPenaltyIsHigher()
    {
        var planner = new DhwCyclePlanner();
        var prices = new[]
        {
            new DhwPricePeriod(Start, Start.AddMinutes(30), 0.1m),
            new DhwPricePeriod(Start.AddMinutes(30), Start.AddHours(2), 1m)
        };
        var result = planner.Plan(new DhwPlanningInput(
            Start,
            Start,
            Start.AddHours(1),
            "Eco",
            40,
            45,
            2,
            prices,
            new DhwCycleProfile("Eco", 30, 30, [new DhwPowerStep(0, 2, 3, false)]),
            SpaceHeatingPenalty: candidate => candidate < Start.AddMinutes(30) ? 10m : 0m));

        Assert.Equal(Start.AddMinutes(30), result.Selected!.StartUtc);
        Assert.Equal(0, result.Selected.SpaceHeatingPenaltySek);
    }

    [Theory]
    [InlineData(92)]
    [InlineData(96)]
    [InlineData(100)]
    public void Plan_AcceptsDstDaysWithVariableQuarterCounts(int quarterCount)
    {
        var planner = new DhwCyclePlanner();
        var prices = Enumerable.Range(0, quarterCount)
            .Select(index => new DhwPricePeriod(
                Start.AddMinutes(index * 15),
                Start.AddMinutes((index + 1) * 15),
                1m))
            .ToArray();

        var result = planner.Plan(new DhwPlanningInput(
            Start,
            Start,
            Start.AddMinutes(quarterCount * 15),
            "Eco",
            40,
            45,
            2,
            prices,
            new DhwCycleProfile("Eco", 45, 45, [new DhwPowerStep(0, 2, 3, false)])));

        Assert.True(result.Success);
        Assert.All(result.Alternatives, candidate => Assert.Equal(0, candidate.StartUtc.Minute % 10));
    }

    [Fact]
    public void CeilingToTenMinutes_NormalizesMinuteSixtyToNextHour()
    {
        Assert.Equal(Start.AddHours(1), DhwCyclePlanner.CeilingToTenMinutes(Start.AddMinutes(59)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    public void JointPayload_AcceptsEveryOnectaTenMinuteBoundary(int minute)
    {
        var start = Start.AddMinutes(minute);
        var payload = ScheduleAlgorithm.ComposeJointDhwSchedule(start, "Eco", TimeZoneInfo.Utc);
        var day = start.DayOfWeek.ToString().ToLowerInvariant();
        Assert.NotNull(payload["0"]?["actions"]?[day]?[start.ToString("HH:mm:ss")]);
    }

    [Fact]
    public void JointPayload_RejectsNonTenMinuteBoundary()
    {
        Assert.Throws<ArgumentException>(() =>
            ScheduleAlgorithm.ComposeJointDhwSchedule(Start.AddMinutes(5), "Eco", TimeZoneInfo.Utc));
    }
}
