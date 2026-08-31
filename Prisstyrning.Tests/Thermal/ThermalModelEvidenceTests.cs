using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalModelEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2R2C")]
    [InlineData("COP")]
    public void Assess_CompletePhysicalEvidencePassesWithoutChangingStoredActivation(string type)
    {
        var model = ValidModel(type, Now);
        model.IsActive = false;
        var result = ThermalModelEvidence.Assess(model, Now);

        Assert.True(result.Passed);
        Assert.Equal("Validated", result.Status);
        Assert.False(model.IsActive);
    }

    [Theory]
    [InlineData("twoHourMaeC", "-0.1")]
    [InlineData("twoHourMaeC", "-1e999")]
    [InlineData("dayMaeC", "1e999")]
    [InlineData("dayMaeC", "null")]
    [InlineData("dayMaeC", "\"0.1\"")]
    [InlineData("dayMaeC", "true")]
    [InlineData("trainingSamples", "-1")]
    [InlineData("validationSamples", "999999")]
    [InlineData("twoHourValidationWindows", "0")]
    [InlineData("dayValidationWindows", "0")]
    [InlineData("dayValidationWindows", "9000")]
    [InlineData("validationVersion", "0")]
    public void Assess_InvalidMetricOrWindowEvidenceFailsClosed(string field, string value)
    {
        var model = ValidModel("2R2C", Now);
        model.MetricsJson = Replace(model.MetricsJson, field, value);

        var result = ThermalModelEvidence.Assess(model, Now);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Reason);
    }

    [Theory]
    [InlineData("airCapacityKwhPerC", "0")]
    [InlineData("heatingGain", "1e999")]
    [InlineData("baseCurveInterceptC", "null")]
    [InlineData("baseCurveSlope", "\"-0.5\"")]
    [InlineData("massCouplingKwPerC", "-1")]
    [InlineData("roomAdjustments", "[]")]
    [InlineData("roomAdjustments", "{\"sensor.room\":null}")]
    [InlineData("roomAdjustments", "{\"sensor.room\":{\"offsetC\":0,\"inertiaHours\":-1,\"disturbanceStdDevC\":0.1,\"samples\":50}}")]
    public void Assess_NonphysicalParametersCannotUseOtherwiseGoodMetrics(string field, string value)
    {
        var model = ValidModel("2R2C", Now);
        model.ParametersJson = Replace(model.ParametersJson, field, value);
        Assert.False(ThermalModelEvidence.Assess(model, Now).Passed);
    }

    [Theory]
    [InlineData("missing-start")]
    [InlineData("reversed-period")]
    [InlineData("future-created")]
    [InlineData("training-after-creation")]
    [InlineData("unknown-model")]
    public void Assess_InvalidVersionTimelineOrTypeCannotApprove(string fault)
    {
        var model = ValidModel("2R2C", Now);
        if (fault == "missing-start") model.TrainingFromUtc = default;
        if (fault == "reversed-period") model.TrainingToUtc = model.TrainingFromUtc.AddHours(-1);
        if (fault == "future-created") model.CreatedAtUtc = Now.AddMinutes(1);
        if (fault == "training-after-creation") model.TrainingToUtc = Now;
        if (fault == "unknown-model") model.ModelType = "unknown";
        Assert.False(ThermalModelEvidence.Assess(model, Now).Passed);
    }

    [Fact]
    public void Assess_HighButValidErrorsRemainVisibleAsThresholdExceeded()
    {
        var model = ValidModel("2R2C", Now);
        model.MetricsJson = Replace(model.MetricsJson, "dayMaeC", "0.8");
        var result = ThermalModelEvidence.Assess(model, Now);

        Assert.False(result.Passed);
        Assert.Equal("ThresholdExceeded", result.Status);
        Assert.Equal(.8, result.DayMaeC);
        Assert.Equal(4, result.DayValidationWindows);
    }

    internal static ThermalModelVersion ValidModel(string type, DateTimeOffset now) => new()
    {
        UserId = "account-a", ModelType = type, IsActive = true,
        TrainingFromUtc = now.AddDays(-30), TrainingToUtc = now.AddDays(-1), CreatedAtUtc = now.AddMinutes(-1),
        ParametersJson = type == "COP" ? JsonSerializer.Serialize(CopModel.ConservativeDefault, JsonSerializerOptions.Web)
            : JsonSerializer.Serialize(new GreyBoxParameters(2, 35, .35, .8, .95, 35, -.45), JsonSerializerOptions.Web),
        MetricsJson = type == "COP" ? JsonSerializer.Serialize(new CopModelMetrics(.1, 480, 120, 1), JsonSerializerOptions.Web)
            : JsonSerializer.Serialize(new ThermalModelMetrics(.1, .2, 1600, 400, 126, 4, 1), JsonSerializerOptions.Web)
    };

    private static string Replace(string json, string field, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root[field] = JsonNode.Parse(value);
        return root.ToJsonString();
    }
}
