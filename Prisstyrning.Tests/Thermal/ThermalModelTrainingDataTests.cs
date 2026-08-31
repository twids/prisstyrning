using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalModelTrainingDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
    internal static readonly ThermalRoomConfig[] Rooms = [new() { UserId = "account-a", EntityId = "sensor.room", IsCritical = true }];
    internal static readonly ThermalEntityConfig[] Entities = ThermalEntityRoles.Known.Select(role => new ThermalEntityConfig
    { UserId = "account-a", Role = role, EntityId = "sensor." + role }).ToArray();

    [Fact]
    public void Data_ValidImportedMeasurementsCanTrainButNeverCertifyLiveReadiness()
    {
        var sample = ValidSample(Now.AddDays(-1));
        var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
        quality["source"] = "HomeAssistantHistoryImport";
        sample.QualityJson = quality.ToJsonString();

        Assert.NotNull(ThermalModelTrainingData.Thermal(sample, Rooms, Entities, Now));
        Assert.NotNull(ThermalModelTrainingData.Cop(sample, Entities, Now));
        Assert.False(ThermalReadinessService.HasRequiredTelemetry(sample, Rooms, Entities));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("nonfinite-room")]
    [InlineData("nonfinite-outside")]
    [InlineData("wrong-room-type")]
    [InlineData("missing-room-exclusion")]
    [InlineData("invalid-flow-quality")]
    [InlineData("unknown-dhw")]
    [InlineData("unknown-defrost")]
    [InlineData("inconsistent-heat")]
    [InlineData("unknown-source")]
    public void Data_InvalidSavedThermalRowsAreExcludedBeforeFitting(string fault)
    {
        var sample = ValidSample(Now.AddMinutes(-5));
        if (fault == "future") sample.TimestampUtc = Now.AddMinutes(5);
        if (fault == "nonfinite-room") sample.RoomTemperaturesJson = "{\"sensor.room\":1e999}";
        if (fault == "nonfinite-outside") sample.OutsideTemperatureC = double.NaN;
        if (fault == "wrong-room-type") sample.RoomTemperaturesJson = "{\"sensor.room\":\"21.5\"}";
        if (fault == "unknown-dhw") sample.DhwActive = null;
        if (fault == "unknown-defrost") sample.DefrostActive = null;
        if (fault == "inconsistent-heat") sample.HeatOutputKw = 200;
        var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
        if (fault == "missing-room-exclusion") quality["rooms"]!["sensor.room"]!.AsObject().Remove("excluded");
        if (fault == "invalid-flow-quality") quality["entities"]!["flow"]!["quality"] = 2;
        if (fault == "unknown-source") quality["source"] = "Unknown";
        sample.QualityJson = quality.ToJsonString();

        Assert.Null(ThermalModelTrainingData.Thermal(sample, Rooms, Entities, Now));
    }

    [Fact]
    public void Data_DhwKeepsObservedRoomEvolutionButDoesNotTeachTankHeatAsSpaceHeat()
    {
        var sample = ValidSample(Now.AddMinutes(-5));
        sample.DhwActive = true;
        var observation = ThermalModelTrainingData.Thermal(sample, Rooms, Entities, Now);

        Assert.NotNull(observation);
        Assert.Equal(0, observation.HeatOutputKw);
        Assert.Equal(21.5, observation.AirTemperatureC);
        Assert.Null(observation.LeavingWaterTemperatureC);
    }

    [Theory]
    [InlineData("unknown-backup")]
    [InlineData("backup-active")]
    [InlineData("defrost")]
    [InlineData("inconsistent-cop")]
    [InlineData("bad-power")]
    [InlineData("bad-brine")]
    [InlineData("missing-quality")]
    public void Data_InvalidCopPhasesAndInputsCannotReachTraining(string fault)
    {
        var sample = ValidSample(Now.AddMinutes(-5));
        if (fault == "unknown-backup") sample.BackupHeaterActive = null;
        if (fault == "backup-active") sample.BackupHeaterActive = true;
        if (fault == "defrost") sample.DefrostActive = true;
        if (fault == "inconsistent-cop") sample.Cop = 6;
        if (fault == "bad-power") sample.HeatPumpPowerKw = double.NaN;
        if (fault == "bad-brine") sample.BrineInC = double.PositiveInfinity;
        if (fault == "missing-quality") sample.QualityJson = "{}";

        Assert.Null(ThermalModelTrainingData.Cop(sample, Entities, Now));
    }

    [Fact]
    public void Data_MalformedRoomProfilesCannotCrashOrLeakNonfiniteValues()
    {
        var sample = ValidSample(Now.AddMinutes(-5));
        sample.RoomTemperaturesJson = "{\"sensor.room\":1e999}";
        Assert.Empty(ThermalModelTrainingData.ReadRooms(sample));
        sample.RoomTemperaturesJson = "null";
        Assert.Empty(ThermalModelTrainingData.ReadRooms(sample));
    }

    internal static ThermalTelemetrySample ValidSample(DateTimeOffset timestamp)
    {
        var sample = ThermalReadinessEvidenceTests.Sample(timestamp);
        sample.BrineInC = 5;
        sample.HeatPumpPowerKw = 2;
        sample.Cop = 2.093;
        var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
        foreach (var entity in Entities)
            quality["entities"]![entity.Role] = JsonNode.Parse("{\"quality\":0,\"excluded\":false}");
        sample.QualityJson = quality.ToJsonString();
        return sample;
    }
}
