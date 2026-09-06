using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public class DefrostPolicyTests
{
    [Fact]
    public void ExplicitDeclarationHasNoMeasurementTimestamp_AndIsUsableForTraining()
    {
        var now = DateTimeOffset.UtcNow;
        var declaration = Declaration();
        var usage = ThermalSensorUsagePolicy.Describe(declaration.Role, DefrostPolicy.Assessment(), null, now, true);
        Assert.Equal("NotApplicable", usage.Usage);
        Assert.Null(usage.ValueUpdatedUtc);
        Assert.Equal(0, usage.Value);
        var sample = ThermalModelTrainingDataTests.ValidSample(now.AddMinutes(-5));
        var quality = JsonNode.Parse(sample.QualityJson)!;
        quality["entities"]![ThermalEntityRoles.DefrostActive] = JsonSerializer.SerializeToNode(usage);
        sample.QualityJson = quality.ToJsonString();
        Assert.Equal(DataQuality.Valid, ThermalStatusQuality.Assess(sample, [], [declaration], sample.TimestampUtc).Quality);
        var entities = ThermalModelTrainingDataTests.Entities.Where(x => x.Role != declaration.Role).Append(declaration).ToArray();
        Assert.NotNull(ThermalModelTrainingData.Thermal(sample, ThermalModelTrainingDataTests.Rooms, entities, now));
        declaration.NotApplicable = false;
        declaration.EntityId = "binary_sensor.defrost";
        Assert.NotEqual(DataQuality.Valid, ThermalStatusQuality.Assess(sample, [], [declaration], sample.TimestampUtc).Quality);
    }

    [Fact]
    public void MissingMeasurementDoesNotBecomeFalse_AndOldRowsAreNotRewritten()
    {
        var now = DateTimeOffset.UtcNow;
        var sample = ThermalModelTrainingDataTests.ValidSample(now);
        sample.DefrostActive = null;
        Assert.NotEqual(DataQuality.Valid, ThermalStatusQuality.Assess(sample, [], [Declaration()], now).Quality);
        Assert.Null(sample.DefrostActive);
    }

    [Theory]
    [InlineData("dhw_active", "", "bool")]
    [InlineData("defrost_active", "sensor.defrost", "bool")]
    [InlineData("defrost_active", "", "kW")]
    public void DeclarationCannotMaskOtherSignals(string role, string entity, string unit)
    {
        Assert.False(DefrostPolicy.IsDeclared(new() { Role = role, EntityId = entity, ExpectedUnit = unit, NotApplicable = true }));
    }

    private static ThermalEntityConfig Declaration() => new() { Role = ThermalEntityRoles.DefrostActive, ExpectedUnit = "bool", NotApplicable = true };
}
