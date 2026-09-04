using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalStatusQualityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 2, 0, 0, TimeSpan.Zero);
    private static ThermalRoomConfig Room() => new() { EntityId = "sensor.room", Name = "Rum", IsCritical = true };
    private static ThermalTelemetrySample Sample() => new()
    {
        TimestampUtc = Now.AddMinutes(-1), RoomTemperaturesJson = "{\"sensor.room\":21.4}",
        QualityJson = "{\"rooms\":{\"sensor.room\":{\"Quality\":0,\"Excluded\":false}}}",
        OutsideTemperatureC = -4, LeavingWaterTemperatureC = 35, ReturnWaterTemperatureC = 30,
        FlowLitresPerMinute = 12, BrineInC = 3, BrineOutC = 0, TankTemperatureC = 45,
        HeatPumpPowerKw = 1.2, PropertyPowerKw = 0, SpotPriceSekPerKwh = -0.1m,
        WindSpeedMps = 0, SolarIrradianceWm2 = 0,
        DhwActive = false, DefrostActive = false, BackupHeaterActive = false
    };

    [Theory]
    [InlineData("{\"Quality\":0,\"Excluded\":false}", DataQuality.Valid)]
    [InlineData("{\"quality\":\"Valid\",\"excluded\":false}", DataQuality.Valid)]
    [InlineData("{\"QUALITY\":\"valid\",\"EXCLUDED\":false}", DataQuality.Valid)]
    [InlineData("{\"Quality\":1,\"Excluded\":false}", DataQuality.Stale)]
    [InlineData("{\"Quality\":2,\"Excluded\":false}", DataQuality.Invalid)]
    [InlineData("{\"Quality\":3,\"Excluded\":false}", DataQuality.Unavailable)]
    [InlineData("{\"quality\":\"Stale\",\"excluded\":false}", DataQuality.Stale)]
    [InlineData("{\"quality\":\"Invalid\",\"excluded\":false}", DataQuality.Invalid)]
    [InlineData("{\"quality\":\"Unavailable\",\"excluded\":false}", DataQuality.Unavailable)]
    [InlineData("{\"Quality\":0,\"Excluded\":true}", DataQuality.Invalid)]
    [InlineData("{\"Quality\":1,\"Excluded\":true}", DataQuality.Invalid)]
    [InlineData("{\"Quality\":3,\"Excluded\":true}", DataQuality.Invalid)]
    [InlineData("{\"Quality\":0}", DataQuality.Unavailable)]
    [InlineData("{\"Quality\":0,\"Excluded\":\"false\"}", DataQuality.Unavailable)]
    [InlineData("{\"Quality\":99,\"Excluded\":false}", DataQuality.Unavailable)]
    [InlineData("{\"Quality\":0.5,\"Excluded\":false}", DataQuality.Unavailable)]
    [InlineData("{\"Quality\":\"0\",\"Excluded\":false}", DataQuality.Unavailable)]
    [InlineData("{\"Quality\":null,\"Excluded\":false}", DataQuality.Unavailable)]
    [InlineData("[]", DataQuality.Unavailable)]
    [InlineData("null", DataQuality.Unavailable)]
    public void AssessesSavedQualityAndExclusionWithoutTrustingFallbackNumbers(string assessment, DataQuality expected)
    {
        var sample = Sample();
        sample.QualityJson = "{\"ROOMS\":{\"sensor.room\":" + assessment + "}}";
        Assert.Equal(expected, ThermalStatusQuality.Assess(sample, [Room()], [], Now).Quality);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{bad")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"rooms\":[]}")]
    public void MissingOrMalformedMetadataIsNotValid(string? json)
    {
        var sample = Sample();
        sample.QualityJson = json!;
        Assert.Equal(DataQuality.Unavailable, ThermalStatusQuality.Assess(sample, [Room()], [], Now).Quality);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{bad")]
    [InlineData("{\"sensor.room\":null}")]
    [InlineData("{\"sensor.room\":\"21.4\"}")]
    [InlineData("{\"sensor.room\":1e999}")]
    [InlineData("{\"sensor.room\":4.9}")]
    [InlineData("{\"sensor.room\":35.1}")]
    public void ValidFlagStillRequiresAnInRangeActualRoomNumber(string temperatures)
    {
        var sample = Sample();
        sample.RoomTemperaturesJson = temperatures;
        Assert.Equal(DataQuality.Invalid, ThermalStatusQuality.Assess(sample, [Room()], [], Now).Quality);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)] // Below comfort, but not a broken sensor.
    [InlineData(35)]
    public void ValidColdRoomAndInclusiveBoundsRemainValid(double temperature)
    {
        var sample = Sample();
        sample.RoomTemperaturesJson = JsonSerializer.Serialize(new Dictionary<string, double> { ["sensor.room"] = temperature });
        Assert.Equal(DataQuality.Valid, ThermalStatusQuality.Assess(sample, [Room()], [], Now).Quality);
    }

    [Theory]
    [InlineData(0, DataQuality.Valid)]
    [InlineData(600, DataQuality.Valid)]
    [InlineData(601, DataQuality.Stale)]
    [InlineData(-1, DataQuality.Invalid)]
    public void EnforcesTenMinuteAgeAndRejectsFutureSnapshots(int ageSeconds, DataQuality expected)
    {
        var sample = Sample();
        sample.TimestampUtc = Now.AddSeconds(-ageSeconds);
        Assert.Equal(expected, ThermalStatusQuality.Assess(sample, [Room()], [], Now).Quality);
    }

    [Theory]
    [InlineData("\"HomeAssistantHistoryImport\"")]
    [InlineData("\"homeassistanthistoryimport\"")]
    [InlineData("\"unknown-source\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void ImportedOrUnknownSourceCannotConfirmLiveQuality(string source)
    {
        var sample = Sample();
        sample.QualityJson = "{\"Source\":" + source + "," + sample.QualityJson[1..];
        Assert.Equal(DataQuality.Unavailable, ThermalStatusQuality.Assess(sample, [Room()], [], Now).Quality);
    }

    [Fact]
    public void MissingSampleAndNoEnabledInputsAreUnavailable()
    {
        Assert.Equal(DataQuality.Unavailable, ThermalStatusQuality.Assess(null, [Room()], [], Now).Quality);
        var disabled = Room();
        disabled.Enabled = false;
        var result = ThermalStatusQuality.Assess(Sample(), [disabled], [new() { Enabled = false }], Now);
        Assert.Equal(DataQuality.Unavailable, result.Quality);
        Assert.Contains("Inga aktiverade", result.Reason);
    }

    [Fact]
    public void DisabledRoomsUnmappedSensorsAndUnmappedForecastDoNotDegradeQuality()
    {
        var sample = Sample();
        sample.QualityJson = """
            {"rooms":{"sensor.room":{"Quality":0,"Excluded":false},"sensor.disabled":{"Quality":2,"Excluded":true}},
             "entities":{"unused":{"Quality":2,"Excluded":true}},"forecast":{"Quality":3,"points":0}}
            """;
        var result = ThermalStatusQuality.Assess(sample, [Room(), new() { EntityId = "sensor.disabled", Enabled = false }],
            [new() { Role = "unused", Enabled = false }], Now);
        Assert.Equal(DataQuality.Valid, result.Quality);
        Assert.Contains("Alla 1 aktiverade", result.Reason);
    }

    [Fact]
    public void ANewlyEnabledRoomWithoutAssessmentIsUnavailable()
    {
        var result = ThermalStatusQuality.Assess(Sample(), [Room(), new() { EntityId = "sensor.new" }], [], Now);
        Assert.Equal(DataQuality.Unavailable, result.Quality);
        Assert.Contains("1/2", result.Reason);
    }

    [Theory]
    [InlineData(-2, DataQuality.Valid)]
    [InlineData(-1, DataQuality.Valid)]
    [InlineData(0, DataQuality.Unavailable)]
    public void ChangedConfigurationWaitsForNewSnapshot(int configurationAgeMinutes, DataQuality expected)
    {
        Assert.Equal(expected, ThermalStatusQuality.Assess(Sample(), [Room()], [], Now, Now.AddMinutes(configurationAgeMinutes)).Quality);
    }

    [Theory]
    [InlineData(ThermalEntityRoles.OutsideTemperature)]
    [InlineData(ThermalEntityRoles.LeavingWaterTemperature)]
    [InlineData(ThermalEntityRoles.ReturnWaterTemperature)]
    [InlineData(ThermalEntityRoles.Flow)]
    [InlineData(ThermalEntityRoles.BrineIn)]
    [InlineData(ThermalEntityRoles.BrineOut)]
    [InlineData(ThermalEntityRoles.TankTemperature)]
    [InlineData(ThermalEntityRoles.HeatPumpPower)]
    [InlineData(ThermalEntityRoles.PropertyPower)]
    [InlineData(ThermalEntityRoles.SpotPrice)]
    [InlineData(ThermalEntityRoles.WindSpeed)]
    [InlineData(ThermalEntityRoles.SolarIrradiance)]
    [InlineData(ThermalEntityRoles.DhwActive)]
    [InlineData(ThermalEntityRoles.DefrostActive)]
    [InlineData(ThermalEntityRoles.BackupHeaterActive)]
    [InlineData(ThermalEntityRoles.HeatingDeviation)]
    public void RecognizesConfiguredRolesIncludingZeroNegativePriceAndFalseBooleans(string role)
    {
        var sample = Sample();
        sample.QualityJson = EntityQuality(role, DataQuality.Valid);
        Assert.Equal(DataQuality.Valid, ThermalStatusQuality.Assess(sample, [], [Entity(role.ToUpperInvariant())], Now).Quality);
    }

    [Theory]
    [InlineData(DataQuality.Stale)]
    [InlineData(DataQuality.Invalid)]
    [InlineData(DataQuality.Unavailable)]
    public void EntityValueDoesNotOverrideItsSavedQuality(DataQuality quality)
    {
        var sample = Sample();
        sample.QualityJson = EntityQuality(ThermalEntityRoles.HeatPumpPower, quality);
        Assert.Equal(quality, ThermalStatusQuality.Assess(sample, [], [Entity(ThermalEntityRoles.HeatPumpPower)], Now).Quality);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(21d)]
    public void EntityNumberMustBePresentFiniteAndWithinConfiguredBounds(double? power)
    {
        var sample = Sample();
        sample.HeatPumpPowerKw = power;
        sample.QualityJson = EntityQuality(ThermalEntityRoles.HeatPumpPower, DataQuality.Valid);
        var entity = Entity(ThermalEntityRoles.HeatPumpPower);
        entity.MinimumValid = 0;
        entity.MaximumValid = 20;
        Assert.Equal(DataQuality.Invalid, ThermalStatusQuality.Assess(sample, [], [entity], Now).Quality);
    }

    [Fact]
    public void MissingBooleanAndUnknownConfiguredRoleCannotBeValid()
    {
        var sample = Sample();
        sample.DhwActive = null;
        sample.QualityJson = EntityQuality(ThermalEntityRoles.DhwActive, DataQuality.Valid);
        Assert.Equal(DataQuality.Invalid, ThermalStatusQuality.Assess(sample, [], [Entity(ThermalEntityRoles.DhwActive)], Now).Quality);
        sample.QualityJson = EntityQuality("unexpected", DataQuality.Valid);
        Assert.Equal(DataQuality.Invalid, ThermalStatusQuality.Assess(sample, [], [Entity("unexpected")], Now).Quality);
    }

    [Theory]
    [InlineData(0, "[{\"startUtc\":\"2026-08-31T03:00:00Z\",\"temperatureC\":5}]", DataQuality.Valid)]
    [InlineData(0, "[]", DataQuality.Invalid)]
    [InlineData(0, "{}", DataQuality.Invalid)]
    [InlineData(0, "{bad", DataQuality.Invalid)]
    [InlineData(1, "[]", DataQuality.Stale)]
    [InlineData(2, "[]", DataQuality.Invalid)]
    [InlineData(3, "[]", DataQuality.Unavailable)]
    public void MappedForecastUsesItsOwnAssessmentWithoutSensorExclusionFlag(int quality, string forecast, DataQuality expected)
    {
        var sample = Sample();
        sample.QualityJson = $"{{\"forecast\":{{\"Quality\":{quality},\"points\":1}}}}";
        sample.OutsideTemperatureForecastJson = forecast;
        Assert.Equal(expected, ThermalStatusQuality.Assess(sample, [], [Entity(ThermalEntityRoles.WeatherForecast)], Now).Quality);
    }

    [Theory]
    [InlineData(DataQuality.Invalid, DataQuality.Unavailable, DataQuality.Invalid)]
    [InlineData(DataQuality.Stale, DataQuality.Invalid, DataQuality.Invalid)]
    [InlineData(DataQuality.Unavailable, DataQuality.Stale, DataQuality.Unavailable)]
    [InlineData(DataQuality.Valid, DataQuality.Stale, DataQuality.Stale)]
    public void AggregatePriorityIsExplicitNotEnumNumericOrder(DataQuality roomQuality, DataQuality entityQuality, DataQuality expected)
    {
        var sample = Sample();
        sample.QualityJson = JsonSerializer.Serialize(new
        {
            rooms = new Dictionary<string, object> { ["sensor.room"] = new { Quality = roomQuality, Excluded = false } },
            entities = new Dictionary<string, object> { [ThermalEntityRoles.HeatPumpPower] = new { Quality = entityQuality, Excluded = false } }
        });
        Assert.Equal(expected, ThermalStatusQuality.Assess(sample, [Room()], [Entity(ThermalEntityRoles.HeatPumpPower)], Now).Quality);
    }

    [Fact]
    public void ReasonDoesNotEchoRawStateOrStoredReason()
    {
        var sample = Sample();
        sample.QualityJson = "{\"rooms\":{\"sensor.room\":{\"Quality\":2,\"Excluded\":false,\"Reason\":\"sensitive-state\"}}}";
        Assert.DoesNotContain("sensitive-state", ThermalStatusQuality.Assess(sample, [Room()], [], Now).Reason);
    }

    private static ThermalEntityConfig Entity(string role) => new() { Role = role, EntityId = "sensor.entity" };
    private static string EntityQuality(string role, DataQuality quality) => JsonSerializer.Serialize(new
    {
        entities = new Dictionary<string, object> { [role] = new { Quality = quality, Excluded = false } }
    });
}
