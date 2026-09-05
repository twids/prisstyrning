using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class SensorFreshnessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    private static HomeAssistantState State() => new("sensor.room", "21", new JsonObject { ["unit_of_measurement"] = "°C" }, Now.AddMinutes(-40), Now.AddMinutes(-40), Now);

    [Fact]
    public void LongerReportInterval_DoesNotExtendCommunicationTimeout()
    {
        Assert.Equal(DataQuality.Valid, SensorTimestampValidator.Assess(State(), Now, TimeSpan.FromMinutes(60)).Quality);
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(State(), Now, TimeSpan.FromMinutes(10)).Quality);
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(State() with { ReceivedAtUtc = Now.AddMinutes(-11) }, Now, TimeSpan.FromMinutes(60)).Quality);
    }

    [Fact]
    public void MissingReports_AreWarnings_NotRepeatedInvalidMeasurements()
    {
        var tracker = new SensorQualityTracker();
        for (var i = 0; i < 5; i++)
        {
            var result = tracker.Assess("room", State(), new(21, null, "°C", DataQuality.Valid, null), new(5, 35, 10, TimeSpan.FromMinutes(10)), Now.AddMinutes(i * 5));
            Assert.Equal(DataQuality.Stale, result.Quality);
            Assert.False(result.Excluded);
            Assert.Null(result.LastValidValue);
        }
    }

    [Fact]
    public void ImplausibleOldValue_IsStillInvalid()
    {
        var result = new SensorQualityTracker().Assess("room", State(), new(90, null, "°C", DataQuality.Valid, null), new(5, 35, 10, TimeSpan.FromMinutes(10)), Now);
        Assert.Equal(DataQuality.Invalid, result.Quality);
    }
}
