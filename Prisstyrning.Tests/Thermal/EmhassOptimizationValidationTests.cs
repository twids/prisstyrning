using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class EmhassOptimizationValidationTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("too-long")]
    [InlineData("length-mismatch")]
    [InlineData("nan-weather")]
    [InlineData("negative-load")]
    [InlineData("bad-thermal")]
    [InlineData("bad-comfort")]
    [InlineData("duration-without-start")]
    [InlineData("zero-dhw-duration")]
    [InlineData("bad-tariff")]
    [InlineData("bad-power")]
    public void Request_RejectsMalformedOrNonfinitePlanningInput(string fault)
    {
        var request = Request();
        if (fault == "empty") request = request with { LoadCostForecast = [], OutsideTemperatureForecastC = [], BaseLoadForecastW = [], Thermal = request.Thermal with { MinimumTemperaturesC = [], MaximumTemperaturesC = [] } };
        if (fault == "too-long") request = SizedRequest(385);
        if (fault == "length-mismatch") request = request with { OutsideTemperatureForecastC = [2] };
        if (fault == "nan-weather") request = request with { OutsideTemperatureForecastC = [double.NaN, 2] };
        if (fault == "negative-load") request = request with { BaseLoadForecastW = [-1, 500] };
        if (fault == "bad-thermal") request = request with { Thermal = request.Thermal with { HeatingRateCPerHour = 0 } };
        if (fault == "bad-comfort") request = request with { Thermal = request.Thermal with { MinimumTemperaturesC = [23, 20.5], MaximumTemperaturesC = [22, 22] } };
        if (fault == "duration-without-start") request = request with { DhwDurationSteps = 1 };
        if (fault == "zero-dhw-duration") request = request with { DhwStartStep = 0, DhwDurationSteps = 0 };
        if (fault == "bad-tariff") request = request with { TariffEnabled = true, CapacityCostPerKw = double.NaN };
        if (fault == "bad-power") request = request with { HeatPumpElectricPowerW = 0 };

        Assert.Throws<ThermalPlanningEvidenceException>(() => EmhassOptimizationValidation.ValidateRequest(request));
    }

    [Fact]
    public void Result_AllowsNegativeSpotPriceWhenStepsAndObjectiveAreConsistent()
    {
        var request = Request() with { LoadCostForecast = [-1m, .5m] };
        var steps = new[] { new EmhassOptimizationStep(0, 1200, 21, -1), new EmhassOptimizationStep(1, 1200, 21.1, .5) };
        var result = new EmhassOptimizationResult(steps, 100, JointPlanModelConsumptionTests.ExpectedObjective(request, steps));

        EmhassOptimizationValidation.ValidateResult(request, result, 15);
    }

    [Fact]
    public void Result_RejectsSpaceHeatingDuringReservedDhwCapacity()
    {
        var request = Request() with { DhwStartStep = 0, DhwDurationSteps = 1 };
        var steps = new[] { new EmhassOptimizationStep(0, 1200, 21, .5), new EmhassOptimizationStep(1, 1200, 21.1, .8) };
        var result = new EmhassOptimizationResult(steps, 100, JointPlanModelConsumptionTests.ExpectedObjective(request, steps));

        Assert.Throws<ThermalPlanningEvidenceException>(() => EmhassOptimizationValidation.ValidateResult(request, result, 15));
    }

    private static EmhassOptimizationRequest Request() => new(
        [.5m, .8m], [2d, 2d], [500d, 500d], new(2, .1, 1, 21, [20.5d, 20.5d], [22d, 22d]), null, 0, 2500, 2500);

    private static EmhassOptimizationRequest SizedRequest(int count) => new(
        Enumerable.Repeat(.5m, count).ToArray(), Enumerable.Repeat(2d, count).ToArray(), Enumerable.Repeat(500d, count).ToArray(),
        new(2, .1, 1, 21, Enumerable.Repeat(20.5d, count).ToArray(), Enumerable.Repeat(22d, count).ToArray()), null, 0, 2500, 2500);
}
