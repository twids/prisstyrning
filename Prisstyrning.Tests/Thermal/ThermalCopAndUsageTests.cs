using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalCopAndUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 10, 0, TimeSpan.Zero);
    private static HomeAssistantState Raw(string value = "4", string? unit = null, int age = 0) =>
        new("sensor.test", value, new JsonObject { ["unit_of_measurement"] = unit }, Now.AddMinutes(-age), Now.AddMinutes(-age), Now);
    private static SensorAssessment Value(double? value, DataQuality quality = DataQuality.Valid, bool? flag = null) =>
        new(quality, value, flag, null, false, false, false, null, null);
    private static ThermalEntityConfig Mapping(string role, string unit = "COP") =>
        new() { Role = role, EntityId = "sensor." + role, ExpectedUnit = unit };
    private static ThermalTelemetrySample Sample() => new()
    {
        TimestampUtc = Now, HeatPumpPowerKw = 2, HeatOutputKw = 6, DhwActive = false,
        DefrostActive = false, BackupHeaterActive = false, BrineInC = 6, LeavingWaterTemperatureC = 35
    };

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("1")] [InlineData("COP")]
    public void Normalize_CopAcceptsOnlyDimensionlessUnits(string? unit)
    {
        var normalized = SensorValueNormalizer.Normalize(Raw(unit: unit), "COP");
        Assert.Equal(DataQuality.Valid, normalized.Quality);
        Assert.Equal(4, normalized.Value);
        Assert.Equal("COP", normalized.Unit);
    }

    [Theory]
    [InlineData("%", "400")] [InlineData("°C", "4")] [InlineData("kW", "4")]
    [InlineData(null, "-1")] [InlineData(null, "NaN")] [InlineData(null, "Infinity")]
    public void Normalize_CopRejectsIncorrectEntities(string? unit, string value) =>
        Assert.Equal(DataQuality.Invalid, SensorValueNormalizer.Normalize(Raw(value, unit), "COP").Quality);

    [Fact]
    public void ExternalCop_IsNotRecomputedOrReplacedByMean()
    {
        var entities = new[] { Mapping(ThermalEntityRoles.CopRealtime), Mapping(ThermalEntityRoles.CopAverage) };
        var values = new Dictionary<string, SensorAssessment> { [ThermalEntityRoles.CopRealtime] = Value(4.8), [ThermalEntityRoles.CopAverage] = Value(6) };
        Assert.Equal(4.8, ThermalCopSource.Select(Sample(), entities, values, true));
        values[ThermalEntityRoles.CopRealtime] = Value(4.8, DataQuality.Stale);
        Assert.Null(ThermalCopSource.Select(Sample(), entities, values, true));
        values.Remove(ThermalEntityRoles.CopRealtime);
        Assert.Null(ThermalCopSource.Select(Sample(), entities, values, true));
        Assert.Equal(3, ThermalCopSource.Select(Sample(), [Mapping(ThermalEntityRoles.CopAverage)], values, true));
    }

    [Theory]
    [InlineData("idle")] [InlineData("dhw")] [InlineData("defrost")]
    [InlineData("backup")] [InlineData("power-unverified")] [InlineData("excluded")]
    public void ExternalCop_UnsuitableOperatingPhaseIsNotTrainingData(string phase)
    {
        var sample = Sample();
        if (phase == "idle") sample.HeatPumpPowerKw = .05;
        if (phase == "dhw") sample.DhwActive = true;
        if (phase == "defrost") sample.DefrostActive = true;
        if (phase == "backup") sample.BackupHeaterActive = true;
        var value = Value(4) with { Excluded = phase == "excluded" };
        Assert.Null(ThermalCopSource.Select(sample, [Mapping(ThermalEntityRoles.CopRealtime)],
            new Dictionary<string, SensorAssessment> { [ThermalEntityRoles.CopRealtime] = value }, phase != "power-unverified"));
    }

    [Fact]
    public void ExternalCop_TrainingDoesNotRequireRecalculatedFlowCop()
    {
        var entities = new[] { ThermalEntityRoles.CopRealtime, ThermalEntityRoles.BrineIn, ThermalEntityRoles.LeavingWaterTemperature,
            ThermalEntityRoles.HeatPumpPower, ThermalEntityRoles.DhwActive, ThermalEntityRoles.DefrostActive, ThermalEntityRoles.BackupHeaterActive }.Select(role => Mapping(role)).ToArray();
        var sample = Sample(); sample.Cop = 4; sample.HeatOutputKw = null;
        sample.QualityJson = JsonSerializer.Serialize(new { copSource = "HomeAssistantRealtime", copEntityId = "sensor.cop_realtime",
            entities = entities.ToDictionary(x => x.Role, x => new { quality = 0, excluded = false, value = 4 }) });
        var result = ThermalModelTrainingData.Cop(sample, entities, Now, powerVerified: true);
        Assert.NotNull(result);
        sample.QualityJson = sample.QualityJson.Replace("HomeAssistantRealtime", "Derived");
        Assert.Null(ThermalModelTrainingData.Cop(sample, entities, Now, powerVerified: true));
    }

    [Theory]
    [InlineData("valid")] [InlineData("stale")] [InlineData("excluded")] [InlineData("idle")]
    [InlineData("dhw")] [InlineData("backup")] [InlineData("defrost")]
    public void ExternalCop_HydraulicLoadDoesNotRequirePhaseVerification(string fault)
    {
        var sample = Sample(); sample.HeatPumpPowerKw = null;
        if (fault == "idle") sample.HeatOutputKw = 0;
        if (fault == "dhw") sample.DhwActive = true;
        if (fault == "backup") sample.BackupHeaterActive = true;
        if (fault == "defrost") sample.DefrostActive = true;
        var values = new Dictionary<string, SensorAssessment>
        {
            [ThermalEntityRoles.CopRealtime] = Value(4),
            [ThermalEntityRoles.Flow] = Value(12, fault == "stale" ? DataQuality.Stale : DataQuality.Valid) with { Excluded = fault == "excluded" },
            [ThermalEntityRoles.LeavingWaterTemperature] = Value(35),
            [ThermalEntityRoles.ReturnWaterTemperature] = Value(30)
        };
        var cop = ThermalCopSource.Select(sample, [Mapping(ThermalEntityRoles.CopRealtime)], values, false);
        if (fault == "valid") Assert.Equal(4, cop); else Assert.Null(cop);
        Assert.Null(ThermalCopSource.Select(sample, [], values, false));
    }

    [Theory]
    [InlineData("valid")] [InlineData("missing-flow")] [InlineData("stale-flow")]
    [InlineData("inconsistent-heat")] [InlineData("missing-source")]
    public void ExternalCop_TrainingUsesHydraulicHeatNotUnverifiedElectricity(string fault)
    {
        var entities = new[] { ThermalEntityRoles.CopRealtime, ThermalEntityRoles.BrineIn,
            ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature, ThermalEntityRoles.Flow,
            ThermalEntityRoles.DhwActive, ThermalEntityRoles.DefrostActive, ThermalEntityRoles.BackupHeaterActive }
            .Select(role => Mapping(role)).ToArray();
        var sample = Sample(); sample.Cop = 4; sample.HeatPumpPowerKw = -999;
        sample.FlowLitresPerMinute = 12; sample.ReturnWaterTemperatureC = 30; sample.HeatOutputKw = 4.186;
        if (fault == "missing-flow") sample.FlowLitresPerMinute = null;
        if (fault == "inconsistent-heat") sample.HeatOutputKw = 10;
        sample.QualityJson = JsonSerializer.Serialize(new { copSource = fault == "missing-source" ? "Derived" : "HomeAssistantRealtime",
            copEntityId = "sensor.cop_realtime", entities = entities.ToDictionary(x => x.Role,
                x => new { quality = fault == "stale-flow" && x.Role == ThermalEntityRoles.Flow ? 1 : 0, excluded = false, value = 4 }) });
        var result = ThermalModelTrainingData.Cop(sample, entities, Now, powerVerified: false);
        if (fault == "valid")
        {
            Assert.NotNull(result);
            Assert.Equal(4.186, result.HeatOutputKw);
            Assert.Equal(4, result.Cop);
        }
        else Assert.Null(result);
    }

    [Fact]
    public void CostSource_RequiresRealtimeCopAndAllEnabledHydraulicMappings()
    {
        var entities = new[] { ThermalEntityRoles.CopRealtime, ThermalEntityRoles.Flow,
            ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature }.Select(role => Mapping(role)).ToArray();
        Assert.True(ThermalCopSource.HasCostSource(entities, false));
        foreach (var entity in entities)
        {
            entity.Enabled = false;
            Assert.False(ThermalCopSource.HasCostSource(entities, false));
            entity.Enabled = true;
        }
        entities[0].Role = ThermalEntityRoles.CopAverage;
        Assert.False(ThermalCopSource.HasCostSource(entities, false));
        Assert.True(ThermalCopSource.HasCostSource([], true));
    }

    [Fact]
    public void IdleFlowAndLwt_RemainDisplayOnly_NotValidTrainingEvidence()
    {
        var assessment = Value(22, DataQuality.Stale);
        var result = ThermalSensorUsagePolicy.Describe(ThermalEntityRoles.LeavingWaterTemperature, assessment, Raw("22", "°C", 240), Now, true);
        Assert.Equal("HeldWhileIdle", result.Usage);
        Assert.Equal(DataQuality.Stale, result.Quality);
        Assert.Equal(Now.AddHours(-4), result.ValueUpdatedUtc);
        Assert.Equal(22, result.Value);
        Assert.Equal("Unusable", ThermalSensorUsagePolicy.Describe(ThermalEntityRoles.Flow, Value(12, DataQuality.Stale), Raw(), Now, true).Usage);
        var sample = Sample(); sample.LeavingWaterTemperatureC = null;
        sample.QualityJson = JsonSerializer.Serialize(new { entities = new Dictionary<string, object> { [ThermalEntityRoles.LeavingWaterTemperature] = result } });
        Assert.Equal(DataQuality.Stale, ThermalStatusQuality.Assess(sample, [], [Mapping(ThermalEntityRoles.LeavingWaterTemperature)], Now).Quality);
    }

    [Theory]
    [InlineData(DataQuality.Invalid, false)] [InlineData(DataQuality.Unavailable, false)] [InlineData(DataQuality.Stale, true)]
    public void IdleNeverExcusesInvalidExcludedOrMissingSensors(DataQuality quality, bool excluded)
    {
        var result = ThermalSensorUsagePolicy.Describe(ThermalEntityRoles.LeavingWaterTemperature,
            Value(100, quality) with { Excluded = excluded }, Raw(), Now, true);
        Assert.Equal("Unusable", result.Usage);
        Assert.Null(result.Value);
    }

    [Fact]
    public void LostCommunication_CannotBeExcusedByIdle()
    {
        var raw = Raw(age: 120) with { ReceivedAtUtc = Now.AddMinutes(-11) };
        Assert.Equal("Unusable", ThermalSensorUsagePolicy.Describe(ThermalEntityRoles.Flow, Value(0, DataQuality.Stale), raw, Now, true).Usage);
    }

    [Fact]
    public void IdleRequiresIndependentPowerAndNonConflictingOperatingSignals()
    {
        var values = new Dictionary<string, SensorAssessment> { [ThermalEntityRoles.Flow] = Value(0) };
        Assert.False(ThermalSensorUsagePolicy.ReportedIdle(values, true));
        values[ThermalEntityRoles.HeatPumpPower] = Value(.05);
        foreach (var role in new[] { ThermalEntityRoles.DhwActive, ThermalEntityRoles.DefrostActive, ThermalEntityRoles.BackupHeaterActive })
            values[role] = Value(null, flag: false);
        Assert.True(ThermalSensorUsagePolicy.ReportedIdle(values, true));
        Assert.False(ThermalSensorUsagePolicy.ReportedIdle(values, false));
        values[ThermalEntityRoles.DhwActive] = Value(null, flag: true);
        Assert.False(ThermalSensorUsagePolicy.ReportedIdle(values, true));
    }

    [Fact]
    public void SpotPrice_IdenticalOldValueIsValidOnlyInCoveredQuarter()
    {
        var snapshot = new PriceSnapshot { Id = 1, SavedAtUtc = Now.AddHours(-4), TodayPricesJson = """
            [{"start":"2026-09-06T12:00:00Z","value":1.25},{"start":"2026-09-06T12:15:00Z","value":1.25}]
            """ };
        var raw = Raw("1.25", "SEK/kWh", 180);
        var assessment = Value(1.25, DataQuality.Stale);
        Assert.Equal(DataQuality.Valid, SpotPriceValidity.Assess(assessment, raw, snapshot, Now).Quality);
        Assert.Equal(DataQuality.Valid, SpotPriceValidity.Assess(assessment, raw with { ReceivedAtUtc = Now.AddMinutes(10) }, snapshot, Now.AddMinutes(10)).Quality);
        Assert.Equal(DataQuality.Stale, SpotPriceValidity.Assess(assessment, raw with { ReceivedAtUtc = Now.AddMinutes(20) }, snapshot, Now.AddMinutes(20)).Quality);
        Assert.Equal(DataQuality.Stale, SpotPriceValidity.Assess(Value(2), raw with { State = "2" }, snapshot, Now).Quality);
        Assert.Equal(DataQuality.Stale, SpotPriceValidity.Assess(assessment, raw with { ReceivedAtUtc = Now.AddMinutes(-11) }, snapshot, Now).Quality);
    }

    [Theory]
    [InlineData(92, "2026-03-28T23:00:00Z")]
    [InlineData(96, "2026-09-05T22:00:00Z")]
    [InlineData(100, "2026-10-24T22:00:00Z")]
    public void SpotPrice_ValidatesUtcIntervalsAcrossShortNormalAndLongSwedishDays(int quarters, string startText)
    {
        var start = DateTimeOffset.Parse(startText);
        var snapshot = new PriceSnapshot { SavedAtUtc = start.AddHours(-5), TodayPricesJson = JsonSerializer.Serialize(
            Enumerable.Range(0, quarters).Select(i => new { start = start.AddMinutes(i * 15), value = 1.25 })) };
        foreach (var index in Enumerable.Range(0, quarters))
        {
            var now = start.AddMinutes(index * 15 + 14);
            var raw = Raw("1.25", "SEK/kWh") with { LastUpdatedUtc = start, LastChangedUtc = start, ReceivedAtUtc = now };
            Assert.Equal(DataQuality.Valid, SpotPriceValidity.Assess(Value(1.25, DataQuality.Stale), raw, snapshot, now).Quality);
        }
        var end = start.AddMinutes(quarters * 15);
        Assert.Equal(DataQuality.Stale, SpotPriceValidity.Assess(Value(1.25, DataQuality.Stale),
            Raw("1.25", "SEK/kWh") with { LastUpdatedUtc = start, LastChangedUtc = start, ReceivedAtUtc = end }, snapshot, end).Quality);
    }
}
