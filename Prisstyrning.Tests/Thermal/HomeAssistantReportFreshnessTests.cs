using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantReportFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static HomeAssistantState State(DateTimeOffset? report) => new(
        "sensor.room", "21", new JsonObject { ["unit_of_measurement"] = "°C" },
        Now.AddHours(-4), Now.AddHours(-4), Now) { LastReportedUtc = report };

    [Fact]
    public void UnchangedValue_RecentReportIsValidInCollectorAndCatalog()
    {
        var state = State(Now.AddMinutes(-1));
        var result = SensorTimestampValidator.Assess(state, Now, MaxAge);
        Assert.Equal(DataQuality.Valid, result.Quality);
        Assert.Contains("Oförändrat", result.Reason);
        var catalog = HomeAssistantEntityCatalog.Project(state, Now, 10);
        Assert.Equal(DataQuality.Valid, catalog.Quality);
        Assert.Equal(Now.AddMinutes(9), catalog.ValidUntilUtc);
        Assert.Equal(state.LastReportedUtc, catalog.LastReportedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-11)]
    public void SuccessfulRestRead_DoesNotMakeFrozenOrUnreportedValueFresh(int? reportMinutes)
    {
        var state = State(reportMinutes is { } minutes ? Now.AddMinutes(minutes) : null);
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(state, Now, MaxAge).Quality);
        Assert.Equal(DataQuality.Stale, HomeAssistantEntityCatalog.Project(state, Now, 10).Quality);
    }

    [Fact]
    public void FreshReport_DoesNotHideStaleTransport()
    {
        var state = State(Now.AddMinutes(-11)) with { ReceivedAtUtc = Now.AddMinutes(-11) };
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(state, Now, MaxAge).Quality);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-241)]
    public void ContradictoryReport_IsInvalid(int reportMinutes) =>
        Assert.Equal(DataQuality.Invalid, SensorTimestampValidator.Assess(State(Now.AddMinutes(reportMinutes)), Now, MaxAge).Quality);

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"2026-09-05T10:00:00\"")]
    [InlineData("\"nonsense\"")]
    public void MalformedReport_IsNotSilentlyReplacedByUpdateTime(string reportJson)
    {
        using var json = JsonDocument.Parse("""
            {"entity_id":"sensor.room","state":"21","attributes":{"unit_of_measurement":"°C"},
             "last_updated":"2026-09-05T10:00:00Z","last_reported":REPORT}
            """.Replace("REPORT", reportJson));
        var state = HomeAssistantTelemetryClient.ParseState(json.RootElement, Now)!;
        Assert.True(state.ReportTimestampMalformed);
        Assert.Equal(DataQuality.Invalid, SensorTimestampValidator.Assess(state, Now, MaxAge).Quality);
    }

    [Fact]
    public void History_DoesNotBorrowFreshnessFromLaterLiveReportOrImportReceipt()
    {
        var state = State(Now);
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(state, Now.AddHours(-1), MaxAge, Now).Quality);
    }

    [Fact]
    public void ParseState_PreservesExplicitReportOffsetAndUnchangedUpdateTime()
    {
        using var json = JsonDocument.Parse("""
            {"entity_id":"sensor.room","state":"21","attributes":{"unit_of_measurement":"°C"},
             "last_updated":"2026-09-05T06:00:00Z","last_reported":"2026-09-05T11:59:00+02:00"}
            """);
        var state = HomeAssistantTelemetryClient.ParseState(json.RootElement, Now)!;
        Assert.Equal(Now.AddMinutes(-1), state.LastReportedUtc);
        Assert.Equal(Now.AddHours(-4), state.LastUpdatedUtc);
        Assert.False(state.ReportTimestampMalformed);
        Assert.Equal(DataQuality.Valid, SensorTimestampValidator.Assess(state, Now, MaxAge).Quality);
    }

    [Fact]
    public void Recovery_RequiresThreeDistinctSourceReports_NotThreeHttpReads()
    {
        var tracker = new SensorQualityTracker();
        var rules = new SensorValidationRules(5, 35, 10, MaxAge);
        var normalized = new NormalizedSensorValue(21, null, "°C", DataQuality.Valid, null);
        for (var i = 0; i < 3; i++) tracker.Assess("room", State(null), normalized, rules, Now.AddMinutes(i * 5));
        var report = Now.AddMinutes(15);
        for (var i = 0; i < 3; i++)
        {
            var now = report.AddMinutes(i);
            var result = tracker.Assess("room", State(report) with { ReceivedAtUtc = now }, normalized, rules, now);
            Assert.True(result.Excluded);
        }
        var next = report.AddMinutes(5);
        Assert.True(tracker.Assess("room", State(next) with { ReceivedAtUtc = next }, normalized, rules, next).Excluded);
        next = report.AddMinutes(10);
        var recovered = tracker.Assess("room", State(next) with { ReceivedAtUtc = next }, normalized, rules, next);
        Assert.True(recovered.BecameRecovered);
        Assert.Equal(next, recovered.LastValidUtc);
    }

    [Fact]
    public void RateCheck_UsesLastConfirmedReport_NotAgeOfUnchangedValue()
    {
        var tracker = new SensorQualityTracker();
        var rules = new SensorValidationRules(5, 35, 10, MaxAge);
        tracker.Assess("room", State(Now), new(21, null, "°C", DataQuality.Valid, null), rules, Now);
        var later = Now.AddMinutes(1);
        var changed = State(later) with { State = "22", LastUpdatedUtc = later, ReceivedAtUtc = later };
        Assert.Equal(DataQuality.Invalid, tracker.Assess("room", changed,
            new(22, null, "°C", DataQuality.Valid, null), rules, later).Quality);
    }
}
