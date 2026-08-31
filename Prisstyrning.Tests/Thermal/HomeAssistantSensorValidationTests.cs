using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantSensorValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SensorValidationRules Rules = new(5, 35, 10, TimeSpan.FromMinutes(10));

    [Theory]
    [InlineData("NaN", "°C", "°C")]
    [InlineData("Infinity", "kW", "kW")]
    [InlineData("-Infinity", "l/min", "l/min")]
    [InlineData("1e999", "°C", "°C")]
    [InlineData("1e307", "MW", "kW")]
    [InlineData("1e30", "SEK/kWh", "SEK/kWh")]
    [InlineData("1", "°C", "bool")]
    public void Normalize_RejectsNonFiniteOverflowAndUnitBearingBoolean(string value, string source, string target)
    {
        var result = SensorValueNormalizer.Normalize(State(value, source), target);

        Assert.Equal(DataQuality.Invalid, result.Quality);
        Assert.Null(result.Value);
        Assert.Null(result.BooleanValue);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("true")]
    public void Normalize_MalformedUnitIsIsolatedInsteadOfThrowing(string unitJson)
    {
        var state = State("21", "°C");
        state.Attributes["unit_of_measurement"] = JsonNode.Parse(unitJson);

        Assert.Null(Record.Exception(() => SensorValueNormalizer.Normalize(state, "°C")));
        Assert.Equal(DataQuality.Invalid, SensorValueNormalizer.Normalize(state, "°C").Quality);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"entity_id\":42}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"state\":42,\"last_updated\":false}")]
    public void ParseState_MalformedIndividualRecordDoesNotAbortOtherEntities(string json)
    {
        using var document = JsonDocument.Parse(json);

        HomeAssistantState? parsed = null;
        Assert.Null(Record.Exception(() => parsed = HomeAssistantTelemetryClient.ParseState(document.RootElement, Now)));
        if (parsed is not null)
            Assert.NotEqual(DataQuality.Valid, SensorValueNormalizer.Normalize(parsed, "°C").Quality);
    }

    [Fact]
    public void State_MalformedFriendlyNameFallsBackToEntityId()
    {
        var state = State("21", "°C");
        state.Attributes["friendly_name"] = new JsonArray(42);
        Assert.Equal("sensor.room", state.FriendlyName);
    }

    [Theory]
    [InlineData("2026-08-01")]
    [InlineData("2026-08-01T12:00:00")]
    [InlineData("invalid")]
    public void ParseState_TimestampWithoutExplicitZoneIsUnavailable(string timestamp)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            entity_id = "sensor.room", state = "21", attributes = new { unit_of_measurement = "°C" }, last_updated = timestamp
        }));
        var state = HomeAssistantTelemetryClient.ParseState(document.RootElement, Now)!;
        Assert.Null(state.LastUpdatedUtc);
        Assert.Equal(DataQuality.Unavailable, new SensorQualityTracker().Assess("room", state,
            SensorValueNormalizer.Normalize(state, "°C"), Rules, Now).Quality);
    }

    [Fact]
    public void ParseState_PreservesExplicitOffsetAcrossDstAmbiguity()
    {
        using var document = JsonDocument.Parse("""
            {"entity_id":"sensor.room","state":"21","attributes":{"unit_of_measurement":"°C"},"last_updated":"2026-10-25T02:30:00+02:00"}
            """);
        var state = HomeAssistantTelemetryClient.ParseState(document.RootElement, Now)!;
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), state.LastUpdatedUtc);
    }

    [Theory]
    [InlineData("missing-updated", DataQuality.Unavailable)]
    [InlineData("missing-received", DataQuality.Unavailable)]
    [InlineData("future-updated", DataQuality.Invalid)]
    [InlineData("future-received", DataQuality.Invalid)]
    [InlineData("updated-after-received", DataQuality.Invalid)]
    [InlineData("old-received", DataQuality.Stale)]
    public void Tracker_RequiresConsistentSourceAndReceiptTimes(string fault, DataQuality expected)
    {
        var state = fault switch
        {
            "missing-updated" => State("21", "°C") with { LastUpdatedUtc = null },
            "missing-received" => State("21", "°C") with { ReceivedAtUtc = default },
            "future-updated" => State("21", "°C") with { LastUpdatedUtc = Now.AddMinutes(1) },
            "future-received" => State("21", "°C") with { ReceivedAtUtc = Now.AddMinutes(1) },
            "updated-after-received" => State("21", "°C") with { LastUpdatedUtc = Now.AddMinutes(-1), ReceivedAtUtc = Now.AddMinutes(-2) },
            _ => State("21", "°C") with { LastChangedUtc = Now.AddMinutes(-11), LastUpdatedUtc = Now.AddMinutes(-11), ReceivedAtUtc = Now.AddMinutes(-11) }
        };
        var result = new SensorQualityTracker().Assess("sensor.room", state, new(21, null, "°C", DataQuality.Valid, null), Rules, Now);

        Assert.Equal(expected, result.Quality);
        Assert.Null(result.LastValidValue);
        Assert.Null(result.LastValidUtc);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Tracker_RejectsNonFiniteEvenWhenCallerClaimsValid(double value)
    {
        var assessment = new SensorQualityTracker().Assess("sensor.room", State("21", "°C"),
            new(value, null, "°C", DataQuality.Valid, null), new(null, null, null, TimeSpan.FromMinutes(10)), Now);

        Assert.Equal(DataQuality.Invalid, assessment.Quality);
        Assert.Null(assessment.Value);
        Assert.Null(assessment.LastValidValue);
    }

    [Fact]
    public void Tracker_LastValidTimeAndRateUseMeasurementTimeNotPollingTime()
    {
        var tracker = new SensorQualityTracker();
        var first = tracker.Assess("sensor.room", State("21", "°C"), new(21, null, "°C", DataQuality.Valid, null), Rules, Now.AddMinutes(4));
        var next = State("21.5", "°C") with { LastUpdatedUtc = Now.AddMinutes(1), ReceivedAtUtc = Now.AddMinutes(5) };
        var second = tracker.Assess("sensor.room", next, new(21.5, null, "°C", DataQuality.Valid, null), Rules, Now.AddMinutes(5));

        Assert.Equal(Now, first.LastValidUtc);
        Assert.Equal(DataQuality.Invalid, second.Quality);
        Assert.Equal(Now, second.LastValidUtc);
    }

    [Fact]
    public void Tracker_RepeatedCachedMeasurementDoesNotRecoverExcludedSensor()
    {
        var tracker = new SensorQualityTracker();
        for (var index = 0; index < 3; index++)
            tracker.Assess("sensor.room", State("99", "°C"), new(99, null, "°C", DataQuality.Valid, null), Rules, Now.AddMinutes(index * 5));
        var recoveredAt = Now.AddMinutes(15);
        var valid = State("21", "°C") with { LastUpdatedUtc = recoveredAt, ReceivedAtUtc = recoveredAt };
        SensorAssessment result = null!;
        for (var index = 0; index < 3; index++)
            result = tracker.Assess("sensor.room", valid, new(21, null, "°C", DataQuality.Valid, null), Rules, recoveredAt.AddMinutes(index));

        Assert.True(result.Excluded);
        Assert.False(result.BecameRecovered);
    }

    [Theory]
    [InlineData(double.NaN, 35, 30)]
    [InlineData(double.PositiveInfinity, 35, 30)]
    [InlineData(12, double.PositiveInfinity, 30)]
    [InlineData(12, 35, double.NaN)]
    [InlineData(1e308, 1e308, 0)]
    public void HeatOutput_RejectsNonFiniteInputsAndOverflow(double flow, double lwt, double rwt) =>
        Assert.Null(HomeAssistantTelemetryCollector.CalculateHeatOutput(flow, lwt, rwt));

    [Theory]
    [InlineData("nonfinite")]
    [InlineData("imported")]
    [InlineData("excluded-entity")]
    [InlineData("missing-entity-quality")]
    [InlineData("missing-room-exclusion")]
    [InlineData("nonfinite-room")]
    public void Readiness_DoesNotCountInvalidOrImportedTelemetry(string fault)
    {
        var sample = CompleteSample();
        var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
        switch (fault)
        {
            case "nonfinite": sample.OutsideTemperatureC = double.NaN; break;
            case "imported": quality["source"] = "HomeAssistantHistoryImport"; break;
            case "excluded-entity": quality["entities"]!["flow"]!["excluded"] = true; break;
            case "missing-entity-quality": quality.Remove("entities"); break;
            case "missing-room-exclusion": quality["rooms"]!["sensor.room"]!.AsObject().Remove("excluded"); break;
            case "nonfinite-room": sample.RoomTemperaturesJson = "{\"sensor.room\":1e999}"; break;
        }
        sample.QualityJson = quality.ToJsonString();

        Assert.False(ThermalReadinessService.HasRequiredTelemetry(sample, [new ThermalRoomConfig { EntityId = "sensor.room", IsCritical = true }]));
    }

    [Fact]
    public void Readiness_CountsCompleteFiniteLiveTelemetry() =>
        Assert.True(ThermalReadinessService.HasRequiredTelemetry(CompleteSample(), [new ThermalRoomConfig { EntityId = "sensor.room", IsCritical = true }]));

    internal static ThermalTelemetrySample CompleteSample() => new()
    {
        OutsideTemperatureC = 0, LeavingWaterTemperatureC = 35, ReturnWaterTemperatureC = 30, FlowLitresPerMinute = 12,
        RoomTemperaturesJson = "{\"sensor.room\":21}",
        QualityJson = """
            {"entities":{"outside_temperature":{"quality":0,"excluded":false},"leaving_water_temperature":{"quality":0,"excluded":false},"return_water_temperature":{"quality":0,"excluded":false},"flow":{"quality":0,"excluded":false}},"rooms":{"sensor.room":{"quality":0,"excluded":false}}}
            """
    };

    private static HomeAssistantState State(string value, string unit) => new(
        "sensor.room", value, new JsonObject { ["unit_of_measurement"] = unit }, Now, Now, Now);
}
