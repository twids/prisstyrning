using Prisstyrning.Thermal.Optimization;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using System.Text.Json;

namespace Prisstyrning.Tests.Thermal;

public class GreyBoxThermalModelTests
{
    private readonly GreyBoxParameters _parameters = new(2, 35, 0.35, 0.8, 0.95, 35, -0.45);

    [Fact]
    public void Step_LosesHeatWhenColdOutsideAndHeatingIsOff()
    {
        var result = new GreyBoxThermalModel().Step(new ThermalState(21, 21), -5, 0, _parameters, TimeSpan.FromMinutes(15));
        Assert.True(result.AirTemperatureC < 21);
    }

    [Fact]
    public void Step_HeatingRaisesTemperatureComparedWithNoHeating()
    {
        var model = new GreyBoxThermalModel();
        var off = model.Step(new ThermalState(21, 21), -5, 0, _parameters, TimeSpan.FromMinutes(15));
        var on = model.Step(new ThermalState(21, 21), -5, 6, _parameters, TimeSpan.FromMinutes(15));
        Assert.True(on.AirTemperatureC > off.AirTemperatureC);
    }

    [Fact]
    public void Train_ShortHeldOutSeriesCannotClaimDayValidation()
    {
        var result = new GreyBoxThermalModel().Train(Equilibrium(400));

        Assert.True(result.Metrics.DayMaeC > .6);
    }

    [Fact]
    public void Train_GapsCannotBeBridgedToClaimTwoHourOrDayValidation()
    {
        var observations = Equilibrium(2000).Select((point, index) => point with
        {
            TimestampUtc = point.TimestampUtc.AddMinutes(index / 12 * 60)
        }).ToArray();

        var result = new GreyBoxThermalModel().Train(observations);

        Assert.True(result.Metrics.TwoHourMaeC > .3);
        Assert.True(result.Metrics.DayMaeC > .6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Train_NonfiniteHistoricalInputIsRejected(double invalid)
    {
        var observations = Equilibrium(400);
        observations[10] = observations[10] with { OutsideTemperatureC = invalid };

        Assert.Throws<ArgumentException>(() => new GreyBoxThermalModel().Train(observations));
    }

    internal static ThermalObservation[] Equilibrium(int count)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count).Select(index => new ThermalObservation(start.AddMinutes(index * 5), 21, 21, 0, 30, 5, 4, false)).ToArray();
    }

    [Fact]
    public void Train_FullHeldOutHorizonsHaveExplicitCountsAndNoTrainingOverlap()
    {
        var result = new GreyBoxThermalModel().Train(Equilibrium(2000));

        Assert.Equal(1600, result.Metrics.TrainingSamples);
        Assert.Equal(400, result.Metrics.ValidationSamples);
        Assert.Equal(1, result.Metrics.ValidationVersion);
        Assert.True(result.Metrics.TwoHourValidationWindows > 0);
        Assert.True(result.Metrics.DayValidationWindows > 0);
        Assert.Equal(0, result.Metrics.TwoHourMaeC);
        Assert.Equal(0, result.Metrics.DayMaeC);
    }

    [Fact]
    public void Train_FinalValidationCannotInfluenceFittedOrWeatherSelectedParameters()
    {
        var observations = Equilibrium(2000).Select((point, index) => point with { WindSpeedMps = index % 5, SolarIrradianceWm2 = index % 20 * 10 }).ToArray();
        var original = new GreyBoxThermalModel().Train(observations);
        var changed = observations.Select((point, index) => index < 1600 ? point : point with { AirTemperatureC = 25, OutsideTemperatureC = -10 }).ToArray();
        var differentValidation = new GreyBoxThermalModel().Train(changed);

        Assert.Equal(original.Parameters, differentValidation.Parameters);
        Assert.Equal(1280, original.Metrics.TrainingSamples);
        Assert.Equal(400, original.Metrics.ValidationSamples);
        Assert.NotEqual(original.Metrics.TwoHourMaeC, differentValidation.Metrics.TwoHourMaeC);
    }

    [Fact]
    public void Train_DuplicateTimestampsCannotIncreaseEvidence()
    {
        var observations = Equilibrium(400);
        observations[10] = observations[9];
        Assert.Throws<ArgumentException>(() => new GreyBoxThermalModel().Train(observations));
    }

    [Fact]
    public void EstimateRoomAdjustments_LearnsOffsetInertiaAndDisturbancePerRoom()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 48).Select(index =>
        {
            var common = 21 + Math.Sin(index / 8d) * 0.3;
            return new ThermalTelemetrySample
            {
                TimestampUtc = start.AddMinutes(index * 5),
                QualityJson = "{\"rooms\":{\"sensor.room_a\":{\"quality\":0,\"excluded\":false},\"sensor.room_b\":{\"quality\":0,\"excluded\":false}}}",
                RoomTemperaturesJson = JsonSerializer.Serialize(new Dictionary<string, double>
                {
                    ["sensor.room_a"] = common - 0.8,
                    ["sensor.room_b"] = common + 1.2 + Math.Sin(index / 3d) * 0.05
                })
            };
        }).ToArray();

        var adjustments = ThermalModelTrainingJob.EstimateRoomAdjustments(samples);

        Assert.Equal(2, adjustments.Count);
        Assert.InRange(adjustments["sensor.room_a"].OffsetC, -1.1, -0.9);
        Assert.InRange(adjustments["sensor.room_b"].OffsetC, 0.9, 1.1);
        Assert.True(adjustments["sensor.room_a"].InertiaHours > 0);
        Assert.True(adjustments["sensor.room_b"].DisturbanceStdDevC > 0);
        Assert.Equal(48, adjustments["sensor.room_a"].Samples);
    }
}
