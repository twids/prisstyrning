using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class AssumedRoomStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly SensorValidationRules Rules = new(5, 35, 3, TimeSpan.FromMinutes(10));
    private static HomeAssistantState Room() => new("sensor.room", "21", new() { ["unit_of_measurement"] = "°C" },
        Now.AddHours(-12), Now.AddHours(-12), Now);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecentHaReadOfOldPlausibleRoom_IsExplicitlyAssumedButRemainsStale(bool hasReportedTime)
    {
        var raw = Room() with { LastReportedUtc = hasReportedTime ? Now.AddHours(-6) : null };
        var assessment = Assess(raw);
        var usage = ThermalSensorUsagePolicy.Describe("room", assessment, raw, Now, false);

        Assert.True(assessment.ReportAgeOnly);
        Assert.Equal(DataQuality.Stale, usage.Quality);
        Assert.Equal("AssumedUnchanged", usage.Usage);
        Assert.Equal(21, usage.Value);
        Assert.Equal(raw.LastReportedUtc ?? raw.LastUpdatedUtc, usage.SourceTimestampUtc);
        Assert.Equal(Now.AddHours(-12), usage.ValueChangedUtc);
        Assert.Equal(Now, usage.ReceivedAtUtc);
        Assert.Null(assessment.LastValidUtc); // No manufactured active-control fallback.
        Assert.False(assessment.Excluded);
        Assert.Equal("Unusable", ThermalSensorUsagePolicy.Describe(ThermalEntityRoles.TankTemperature, assessment, raw, Now, false).Usage);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("stale-transport")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData("wrong-unit")]
    [InlineData("out-of-range")]
    [InlineData("bad-report")]
    [InlineData("future")]
    [InlineData("missing-updated")]
    [InlineData("history")]
    [InlineData("old-liveness")]
    [InlineData("missing-liveness")]
    public void AssumptionCannotHideErrorsOrBorrowLiveFreshnessForHistory(string fault)
    {
        HomeAssistantState? raw = Room();
        raw = fault switch
        {
            "missing" => null,
            "unknown" or "unavailable" => raw with { State = fault },
            "stale-transport" => raw with { ReceivedAtUtc = Now.AddMinutes(-11) },
            "wrong-unit" => raw with { Attributes = new() { ["unit_of_measurement"] = "kWh" } },
            "out-of-range" => raw with { State = "80" },
            "bad-report" => raw with { ReportTimestampMalformed = true },
            "future" => raw with { LastReportedUtc = Now.AddMinutes(1) },
            "missing-updated" => raw with { LastUpdatedUtc = null },
            _ => raw
        };
        var normalized = SensorValueNormalizer.Normalize(raw, "°C");
        if (fault == "disconnected") normalized = normalized with { Quality = DataQuality.Unavailable };
        var liveness = fault switch
        {
            "old-liveness" => new SensorLivenessEvidence(Now.AddHours(-1), null),
            "missing-liveness" => new SensorLivenessEvidence(null, "Saknas"),
            _ => null
        };
        var imported = fault == "history" ? Now : (DateTimeOffset?)null;
        var assessment = new SensorQualityTracker().Assess("room", raw, normalized, Rules, Now, historyImportedAtUtc: imported, liveness: liveness);
        Assert.False(assessment.ReportAgeOnly);
        Assert.NotEqual("AssumedUnchanged", ThermalSensorUsagePolicy.Describe("room", assessment, raw, Now, false, imported).Usage);
    }

    [Fact]
    public void ExcludedSensorCannotRecoverThroughRepeatedHeldValues()
    {
        var tracker = new SensorQualityTracker();
        for (var i = 0; i < 3; i++) Assess(Room() with { State = "90" }, tracker, Now.AddMinutes(i * 5));
        for (var i = 3; i < 12; i++)
        {
            var now = Now.AddMinutes(i * 5);
            var raw = Room() with { ReceivedAtUtc = now };
            var assessment = Assess(raw, tracker, now);
            Assert.True(assessment.Excluded);
            Assert.False(assessment.BecameRecovered);
            Assert.Equal("Unusable", ThermalSensorUsagePolicy.Describe("room", assessment, raw, now, false).Usage);
        }
    }

    [Fact]
    public void InvalidRateCannotBeReclassifiedAsOnlyReportAge()
    {
        var tracker = new SensorQualityTracker();
        var old = Room() with { LastReportedUtc = Now.AddMinutes(-15), ReceivedAtUtc = Now.AddMinutes(-15) };
        Assess(old, tracker, Now.AddMinutes(-15));
        var jumped = old with { State = "30", LastUpdatedUtc = Now.AddMinutes(-14), LastReportedUtc = Now.AddMinutes(-14), ReceivedAtUtc = Now };
        var assessment = Assess(jumped, tracker);
        Assert.Equal(DataQuality.Invalid, assessment.Quality);
        Assert.False(assessment.ReportAgeOnly);
        Assert.Equal("Unusable", ThermalSensorUsagePolicy.Describe("room", assessment, jumped, Now, false).Usage);
    }

    private static SensorAssessment Assess(HomeAssistantState raw, SensorQualityTracker? tracker = null, DateTimeOffset? now = null) =>
        (tracker ?? new()).Assess("room", raw, SensorValueNormalizer.Normalize(raw, "°C"), Rules, now ?? Now);
}
