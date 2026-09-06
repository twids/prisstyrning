using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class SensorLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);
    private static HomeAssistantState Room() => new("sensor.room", "21", new() { ["unit_of_measurement"] = "°C" }, Now.AddHours(-3), Now.AddHours(-3), Now);
    private static HomeAssistantState Heartbeat(string value) => new("sensor.seen", value, new(), Now, Now, Now);

    [Theory]
    [InlineData("2026-09-06T12:00:00Z")]
    [InlineData("2026-09-06T14:00:00+02:00")]
    public void TimestampEntity_AcceptsUnchangedValueWithoutChangingMeasurementTime(string timestamp)
    {
        var raw = Room();
        var evidence = SensorLiveness.Resolve(raw, "sensor.seen", null, _ => Heartbeat(timestamp), Now);
        var result = new SensorQualityTracker().Assess("room", raw, SensorValueNormalizer.Normalize(raw, "°C"), new(5, 35, 3, Limit), Now, liveness: evidence);
        Assert.Equal(DataQuality.Valid, result.Quality);
        Assert.Equal(raw.LastUpdatedUtc, result.LastValidUtc);
        Assert.Contains("inte en ny", result.Reason);
    }

    [Fact]
    public void TimestampAttributeAndBothUnixFormats_AreIntegrationIndependent()
    {
        foreach (var node in new JsonNode[] { JsonValue.Create(Now.ToUnixTimeSeconds())!, JsonValue.Create(Now.ToUnixTimeMilliseconds())!, JsonValue.Create(Now.ToUnixTimeSeconds().ToString())! })
        {
            var raw = Room() with { LastUpdatedUtc = Now };
            raw.Attributes["last_seen"] = node;
            var evidence = SensorLiveness.Resolve(raw, null, "last_seen", _ => throw new InvalidOperationException(), Now);
            Assert.Equal(Now, evidence!.TimestampUtc);
            Assert.Null(evidence.Warning);
        }
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    [InlineData("available")]
    [InlineData("21")]
    [InlineData("2026-09-06T12:00:00")]
    [InlineData("2026-09-06T12:01:00Z")]
    [InlineData("2026-09-06T11:49:00Z")]
    public void MissingMalformedOrOldLiveness_IsWarningAndCannotExcludeTemperature(string text)
    {
        var tracker = new SensorQualityTracker();
        for (var i = 0; i < 4; i++)
        {
            var now = Now.AddMinutes(5 * i);
            var evidence = SensorLiveness.Resolve(Room(), "sensor.seen", null, _ => Heartbeat(text), Now);
            var result = tracker.Assess("room", Room(), SensorValueNormalizer.Normalize(Room(), "°C"), new(5, 35, 3, Limit), now, liveness: evidence);
            Assert.Equal(DataQuality.Stale, result.Quality);
            Assert.False(result.Excluded);
        }
    }

    [Fact]
    public void LiveLiveness_CannotHideBadValuesMissingEntitiesOrLostCommunication()
    {
        var evidence = new SensorLivenessEvidence(Now, null);
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(Room() with { ReceivedAtUtc = Now.AddMinutes(-11) }, Now, Limit, liveness: evidence).Quality);
        Assert.Equal(DataQuality.Unavailable, SensorTimestampValidator.Assess(null, Now, Limit, liveness: evidence).Quality);
        var bad = Room() with { State = "90" };
        Assert.Equal(DataQuality.Invalid, new SensorQualityTracker().Assess("room", bad, SensorValueNormalizer.Normalize(bad, "°C"), new(5, 35, 3, Limit), Now, liveness: evidence).Quality);
        Assert.NotNull(SensorLiveness.Resolve(Room(), "sensor.seen", null, _ => null, Now)!.Warning);
        Assert.NotNull(SensorLiveness.Resolve(Room(), "sensor.seen", null, _ => Heartbeat(Now.ToString("O")) with { EntityId = "sensor.other" }, Now)!.Warning);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("dhw_active")]
    [InlineData("tank_temperature")]
    [InlineData("defrost_active")]
    [InlineData("leaving_water_temperature")]
    [InlineData("heat_pump_power")]
    public void AmbientLiveness_CannotRelaxPumpOrHygieneEvidence(string role) =>
        Assert.Throws<ArgumentException>(() => SensorLiveness.Validate(role, "sensor.seen", null));

    [Fact]
    public void Recovery_RequiresThreeMeasurementReports_NotThreeHeartbeats()
    {
        var tracker = new SensorQualityTracker();
        for (var i = 0; i < 3; i++)
            tracker.Assess("room", Room() with { State = "90" }, new(90, null, "°C", DataQuality.Valid, null), new(5, 35, 3, Limit), Now.AddMinutes(i * 5));
        for (var i = 0; i < 3; i++)
        {
            var now = Now.AddMinutes(15 + 5 * i);
            var raw = Room() with { ReceivedAtUtc = now, LastUpdatedUtc = now, LastReportedUtc = now };
            var result = tracker.Assess("room", raw, SensorValueNormalizer.Normalize(raw, "°C"), new(5, 35, 3, Limit), now, liveness: new(now, null));
            Assert.True(result.Excluded);
            Assert.False(result.BecameRecovered);
        }
    }

    [Fact]
    public void History_NeverUsesLiveLivenessFromTheFuture()
    {
        var bucket = Now.AddDays(-1);
        var raw = Room() with { LastChangedUtc = bucket.AddHours(-3), LastUpdatedUtc = bucket.AddHours(-3) };
        var evidence = SensorLiveness.Resolve(raw, "sensor.seen", null, _ => Heartbeat(Now.ToString("O")), bucket, Now);
        Assert.NotNull(evidence!.Warning);
        Assert.Equal(DataQuality.Stale, SensorTimestampValidator.Assess(raw, bucket, Limit, Now, evidence).Quality);
    }
}
