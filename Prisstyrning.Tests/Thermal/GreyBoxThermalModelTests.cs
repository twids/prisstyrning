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
