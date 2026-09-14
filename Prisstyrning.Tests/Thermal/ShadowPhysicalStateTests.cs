using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class ShadowPhysicalStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 17, 0, 0, TimeSpan.Zero);
    private static HomeAssistantState Raw => new("sensor.test", "20",
        new JsonObject { ["unit_of_measurement"] = "°C" }, Now.AddHours(-4), Now.AddHours(-4), Now);
    private static SensorAssessment Assessment => new(DataQuality.Stale, 20, null,
        "Ingen ny rapport", false, false, false, 20, Now.AddHours(-4))
        { ReportAgeOnly = true, SourceTimestampUtc = Now.AddHours(-4) };

    [Theory]
    [InlineData(ThermalEntityRoles.OutsideTemperature)]
    [InlineData(ThermalEntityRoles.LeavingWaterTemperature)]
    [InlineData(ThermalEntityRoles.ReturnWaterTemperature)]
    [InlineData(ThermalEntityRoles.Flow)]
    [InlineData(ThermalEntityRoles.BrineIn)]
    [InlineData(ThermalEntityRoles.TankTemperature)]
    [InlineData(ThermalEntityRoles.HeatPumpPower)]
    public void UnchangedPhysicalValue_IsMarkedForShadowWithoutUpgradingQuality(string role)
    {
        var result = ThermalSensorUsagePolicy.Describe(role, Assessment, Raw, Now, false);
        Assert.Equal("AssumedUnchanged", result.Usage);
        Assert.Equal(DataQuality.Stale, result.Quality);
        Assert.Equal(20, result.Value);
        Assert.Equal(Now.AddHours(-4), result.SourceTimestampUtc);
        Assert.Equal(Now.AddHours(-4), result.ValueChangedUtc);
        Assert.Equal(Now, result.ReceivedAtUtc);
        Assert.Contains("inte en ny mätning", result.Reason);
    }

    [Theory]
    [InlineData("communication")]
    [InlineData("excluded")]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("liveness")]
    [InlineData("nan")]
    [InlineData("history")]
    public void ShadowAssumption_DoesNotExcuseOtherEvidenceFailures(string fault)
    {
        var raw = fault == "communication" ? Raw with { ReceivedAtUtc = Now.AddMinutes(-11) } : Raw;
        var assessment = fault switch
        {
            "excluded" => Assessment with { Excluded = true },
            "invalid" => Assessment with { Quality = DataQuality.Invalid },
            "missing" => Assessment with { Quality = DataQuality.Unavailable },
            "liveness" => Assessment with { ReportAgeOnly = false },
            "nan" => Assessment with { Value = double.NaN },
            _ => Assessment
        };
        var result = ThermalSensorUsagePolicy.Describe(ThermalEntityRoles.ReturnWaterTemperature,
            assessment, raw, Now, false, fault == "history" ? Now : null);
        Assert.Equal("Unusable", result.Usage);
    }

    [Theory]
    [InlineData(ThermalEntityRoles.SpotPrice)]
    [InlineData(ThermalEntityRoles.CopRealtime)]
    [InlineData(ThermalEntityRoles.DhwActive)]
    [InlineData(ThermalEntityRoles.BackupHeaterActive)]
    [InlineData(ThermalEntityRoles.WeatherForecast)]
    public void PhaseSensitiveAndForecastRoles_AreNotAssumed(string role)
    {
        Assert.Equal("Unusable", ThermalSensorUsagePolicy.Describe(role, Assessment, Raw, Now, false).Usage);
    }
}
