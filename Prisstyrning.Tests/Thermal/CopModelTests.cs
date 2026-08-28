using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class CopModelTests
{
    [Fact]
    public void Train_RecoversPhysicalEffectsAndValidatesOnHeldOutData()
    {
        var observations = Enumerable.Range(0, 600).Select(index =>
        {
            var brine = -4 + index % 13 * 0.75;
            var lwt = 28 + index % 17 * 0.8;
            var load = 1.5 + index % 11 * 0.5;
            var cop = 4.2 + 0.09 * brine - 0.07 * (lwt - 35) - 0.04 * (load - 4) + Math.Sin(index) * 0.03;
            return new CopObservation(DateTimeOffset.UnixEpoch.AddMinutes(index * 5), brine, lwt, load, cop);
        }).ToArray();

        var result = new CopModel().Train(observations);

        Assert.True(result.Parameters.BrineCoefficient > 0);
        Assert.True(result.Parameters.LwtCoefficient < 0);
        Assert.True(result.Metrics.Mae < 0.1);
        Assert.Equal(480, result.Metrics.TrainingSamples);
        Assert.Equal(120, result.Metrics.ValidationSamples);
    }

    [Fact]
    public void Predict_IsBoundedWhenInputsOrParametersAreExtreme()
    {
        var model = new CopModel();
        Assert.Equal(8, model.Predict(new CopParameters(100, 1, 1, 1), 20, 60, 20));
        Assert.Equal(1.2, model.Predict(new CopParameters(-100, -1, -1, -1), -20, 20, 0));
    }
}
