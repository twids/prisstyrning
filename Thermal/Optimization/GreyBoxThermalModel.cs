namespace Prisstyrning.Thermal.Optimization;

public sealed record RoomThermalAdjustment(
    double OffsetC,
    double InertiaHours,
    double DisturbanceStdDevC,
    int Samples);

public sealed record GreyBoxParameters(
    double AirCapacityKwhPerC,
    double MassCapacityKwhPerC,
    double EnvelopeConductanceKwPerC,
    double MassCouplingKwPerC,
    double HeatingGain,
    double BaseCurveInterceptC,
    double BaseCurveSlope,
    double WindLossCoefficientKwPerCPerMps = 0,
    double SolarGainKwPerWm2 = 0,
    IReadOnlyDictionary<string, RoomThermalAdjustment>? RoomAdjustments = null);

public sealed record ThermalState(double AirTemperatureC, double MassTemperatureC);

public sealed record ThermalObservation(
    DateTimeOffset TimestampUtc,
    double AirTemperatureC,
    double OutsideTemperatureC,
    double HeatOutputKw,
    double? LeavingWaterTemperatureC,
    double? BrineTemperatureC,
    double? Cop,
    bool BackupHeaterActive,
    double? WindSpeedMps = null,
    double? SolarIrradianceWm2 = null);

public sealed record ThermalModelMetrics(
    double TwoHourMaeC,
    double DayMaeC,
    int TrainingSamples,
    int ValidationSamples);

public sealed record ThermalTrainingResult(GreyBoxParameters Parameters, ThermalModelMetrics Metrics);

public sealed class GreyBoxThermalModel
{
    public ThermalState Step(
        ThermalState state,
        double outsideTemperatureC,
        double heatOutputKw,
        GreyBoxParameters parameters,
        TimeSpan step,
        double windSpeedMps = 0,
        double solarIrradianceWm2 = 0)
    {
        var hours = step.TotalHours;
        var effectiveConductance = parameters.EnvelopeConductanceKwPerC +
                                   parameters.WindLossCoefficientKwPerCPerMps * Math.Max(0, windSpeedMps);
        var envelopeFlow = effectiveConductance * (outsideTemperatureC - state.AirTemperatureC);
        var massFlow = parameters.MassCouplingKwPerC * (state.MassTemperatureC - state.AirTemperatureC);
        var solarFlow = parameters.SolarGainKwPerWm2 * Math.Max(0, solarIrradianceWm2);
        var airDelta = (envelopeFlow + massFlow + parameters.HeatingGain * heatOutputKw + solarFlow) /
                       parameters.AirCapacityKwhPerC * hours;
        var massDelta = -massFlow / parameters.MassCapacityKwhPerC * hours;
        return new ThermalState(state.AirTemperatureC + airDelta, state.MassTemperatureC + massDelta);
    }

    public ThermalTrainingResult Train(IReadOnlyList<ThermalObservation> observations)
    {
        if (observations.Count < 288) throw new ArgumentException("At least one day of five-minute observations is required.");
        var ordered = observations.OrderBy(x => x.TimestampUtc).ToArray();
        var split = Math.Clamp((int)(ordered.Length * 0.8), 200, ordered.Length - 50);
        var training = ordered[..split];
        var validation = ordered[(split - 1)..];
        var baseline = Optimize(new GreyBoxParameters(2, 35, 0.35, 0.8, 0.95, 35, -0.45), training, includeWeather: false);
        var parameters = baseline;
        var weatherCoverage = training.Count(x => x.WindSpeedMps is not null && x.SolarIrradianceWm2 is not null) /
                              (double)training.Length;
        if (weatherCoverage >= 0.8)
        {
            var weatherCandidate = Optimize(baseline, training, includeWeather: true);
            if (HorizonMae(validation, weatherCandidate, 24) + 0.01 < HorizonMae(validation, baseline, 24))
                parameters = weatherCandidate;
        }

        parameters = parameters with
        {
            BaseCurveInterceptC = FitBaseCurve(training).Intercept,
            BaseCurveSlope = FitBaseCurve(training).Slope
        };
        var metrics = new ThermalModelMetrics(
            HorizonMae(validation, parameters, 24),
            HorizonMae(validation, parameters, 288),
            training.Length,
            validation.Length);
        return new ThermalTrainingResult(parameters, metrics);
    }

    private GreyBoxParameters Optimize(
        GreyBoxParameters initial,
        IReadOnlyList<ThermalObservation> training,
        bool includeWeather)
    {
        var parameters = initial;
        var deltas = includeWeather
            ? new[] { 0.5, 10d, 0.15, 0.3, 0.1, 0.02, 0.001 }
            : new[] { 0.5, 10d, 0.15, 0.3, 0.1 };
        for (var pass = 0; pass < 5; pass++)
        {
            for (var dimension = 0; dimension < deltas.Length; dimension++)
            {
                var candidates = new[] { -1d, 0d, 1d }
                    .Select(direction => Adjust(parameters, dimension, direction * deltas[dimension]))
                    .Where(IsPhysical)
                    .Select(candidate => (Candidate: candidate, Loss: OneStepMae(training, candidate)))
                    .OrderBy(x => x.Loss)
                    .ToArray();
                parameters = candidates[0].Candidate;
            }
            for (var index = 0; index < deltas.Length; index++) deltas[index] *= 0.55;
        }
        return parameters;
    }

    private double OneStepMae(IReadOnlyList<ThermalObservation> observations, GreyBoxParameters parameters)
    {
        var total = 0d;
        var count = 0;
        var mass = observations[0].AirTemperatureC;
        for (var index = 0; index < observations.Count - 1; index++)
        {
            var current = observations[index];
            var next = observations[index + 1];
            var elapsed = next.TimestampUtc - current.TimestampUtc;
            if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(15))
            {
                mass = next.AirTemperatureC;
                continue;
            }
            var predicted = Step(
                new ThermalState(current.AirTemperatureC, mass),
                current.OutsideTemperatureC,
                current.HeatOutputKw,
                parameters,
                elapsed,
                current.WindSpeedMps ?? 0,
                current.SolarIrradianceWm2 ?? 0);
            total += Math.Abs(predicted.AirTemperatureC - next.AirTemperatureC);
            count++;
            mass = predicted.MassTemperatureC;
        }
        return count == 0 ? double.MaxValue : total / count;
    }

    private double HorizonMae(IReadOnlyList<ThermalObservation> observations, GreyBoxParameters parameters, int horizonSteps)
    {
        var total = 0d;
        var count = 0;
        var actualHorizon = Math.Min(horizonSteps, observations.Count - 1);
        var stride = Math.Max(1, actualHorizon / 8);
        for (var start = 0; start + actualHorizon < observations.Count; start += stride)
        {
            var state = new ThermalState(observations[start].AirTemperatureC, observations[start].AirTemperatureC);
            for (var offset = 0; offset < actualHorizon; offset++)
            {
                var current = observations[start + offset];
                var next = observations[start + offset + 1];
                state = Step(
                    state,
                    current.OutsideTemperatureC,
                    current.HeatOutputKw,
                    parameters,
                    next.TimestampUtc - current.TimestampUtc,
                    current.WindSpeedMps ?? 0,
                    current.SolarIrradianceWm2 ?? 0);
            }
            total += Math.Abs(state.AirTemperatureC - observations[start + actualHorizon].AirTemperatureC);
            count++;
        }
        return count == 0 ? double.MaxValue : total / count;
    }

    private static (double Intercept, double Slope) FitBaseCurve(IEnumerable<ThermalObservation> observations)
    {
        var points = observations.Where(x => x.HeatOutputKw > 0.5 && x.LeavingWaterTemperatureC is not null)
            .Select(x => (X: x.OutsideTemperatureC, Y: x.LeavingWaterTemperatureC!.Value)).ToArray();
        if (points.Length < 20) return (35, -0.45);
        var meanX = points.Average(x => x.X);
        var meanY = points.Average(x => x.Y);
        var denominator = points.Sum(x => Math.Pow(x.X - meanX, 2));
        if (denominator < 0.001) return (meanY, -0.45);
        var slope = points.Sum(x => (x.X - meanX) * (x.Y - meanY)) / denominator;
        return (meanY - slope * meanX, slope);
    }

    private static GreyBoxParameters Adjust(GreyBoxParameters value, int dimension, double delta) => dimension switch
    {
        0 => value with { AirCapacityKwhPerC = value.AirCapacityKwhPerC + delta },
        1 => value with { MassCapacityKwhPerC = value.MassCapacityKwhPerC + delta },
        2 => value with { EnvelopeConductanceKwPerC = value.EnvelopeConductanceKwPerC + delta },
        3 => value with { MassCouplingKwPerC = value.MassCouplingKwPerC + delta },
        4 => value with { HeatingGain = value.HeatingGain + delta },
        5 => value with { WindLossCoefficientKwPerCPerMps = value.WindLossCoefficientKwPerCPerMps + delta },
        6 => value with { SolarGainKwPerWm2 = value.SolarGainKwPerWm2 + delta },
        _ => value
    };

    private static bool IsPhysical(GreyBoxParameters value) =>
        value.AirCapacityKwhPerC is > 0.2 and < 20 &&
        value.MassCapacityKwhPerC is > 2 and < 500 &&
        value.EnvelopeConductanceKwPerC is > 0.02 and < 5 &&
        value.MassCouplingKwPerC is > 0.02 and < 10 &&
        value.HeatingGain is > 0.2 and <= 1.2 &&
        value.WindLossCoefficientKwPerCPerMps is >= 0 and < 1 &&
        value.SolarGainKwPerWm2 is >= 0 and < 0.1;
}
