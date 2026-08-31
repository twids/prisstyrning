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
    int ValidationSamples,
    int TwoHourValidationWindows = 0,
    int DayValidationWindows = 0,
    int ValidationVersion = 0);

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
        if (observations.Any(x => !IsUsableObservation(x)))
            throw new ArgumentException("Thermal observations must have finite values and valid timestamps.", nameof(observations));
        var ordered = observations.OrderBy(x => x.TimestampUtc).ToArray();
        if (ordered.Select(x => x.TimestampUtc).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Duplicate thermal observation timestamps are not valid training data.", nameof(observations));
        var split = Math.Clamp((int)(ordered.Length * 0.8), 200, ordered.Length - 50);
        var training = ordered[..split];
        // The last 20% is held out from fitting AND weather-feature selection.
        var validation = ordered[split..];
        var weatherCoverage = training.Count(x => x.WindSpeedMps is not null && x.SolarIrradianceWm2 is not null) /
                              (double)training.Length;
        var tuningSplit = Math.Max(200, (int)(training.Length * .8));
        var useTuning = weatherCoverage >= .8 && training.Length - tuningSplit >= 25;
        var fitting = useTuning ? training[..tuningSplit] : training;
        var baseline = Optimize(new GreyBoxParameters(2, 35, 0.35, 0.8, 0.95, 35, -0.45), fitting, includeWeather: false);
        var parameters = baseline;
        if (useTuning)
        {
            var tuning = training[tuningSplit..];
            var weatherCandidate = Optimize(baseline, fitting, includeWeather: true);
            var candidateScore = HorizonMae(tuning, weatherCandidate, 24);
            var baselineScore = HorizonMae(tuning, baseline, 24);
            if (candidateScore.Windows > 0 && baselineScore.Windows > 0 && candidateScore.Mae + .01 < baselineScore.Mae)
                parameters = weatherCandidate;
        }

        parameters = parameters with
        {
            BaseCurveInterceptC = FitBaseCurve(fitting).Intercept,
            BaseCurveSlope = FitBaseCurve(fitting).Slope
        };
        var twoHour = HorizonMae(validation, parameters, 24);
        var day = HorizonMae(validation, parameters, 288);
        var metrics = new ThermalModelMetrics(
            twoHour.Mae, day.Mae, fitting.Length, validation.Length,
            twoHour.Windows, day.Windows, ValidationVersion: 1);
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
        return count == 0 || !double.IsFinite(total) ? double.MaxValue : total / count;
    }

    private (double Mae, int Windows) HorizonMae(IReadOnlyList<ThermalObservation> observations, GreyBoxParameters parameters, int horizonSteps)
    {
        var total = 0d;
        var count = 0;
        // Never shorten a named horizon or integrate across unobserved gaps.
        var stride = Math.Max(1, horizonSteps / 8);
        for (var start = 0; start + horizonSteps < observations.Count; start += stride)
        {
            if (Enumerable.Range(start, horizonSteps).Any(index =>
                    observations[index + 1].TimestampUtc - observations[index].TimestampUtc != TimeSpan.FromMinutes(5)))
                continue;
            var state = new ThermalState(observations[start].AirTemperatureC, observations[start].AirTemperatureC);
            for (var offset = 0; offset < horizonSteps; offset++)
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
            if (!double.IsFinite(state.AirTemperatureC) || !double.IsFinite(state.MassTemperatureC))
                return (double.MaxValue, 0);
            total += Math.Abs(state.AirTemperatureC - observations[start + horizonSteps].AirTemperatureC);
            count++;
        }
        return count == 0 || !double.IsFinite(total) ? (double.MaxValue, 0) : (total / count, count);
    }

    internal static bool IsUsableObservation(ThermalObservation value) =>
        value.TimestampUtc != default && double.IsFinite(value.AirTemperatureC) &&
        double.IsFinite(value.OutsideTemperatureC) && double.IsFinite(value.HeatOutputKw) && value.HeatOutputKw >= 0 &&
        FiniteOptional(value.LeavingWaterTemperatureC) && FiniteOptional(value.BrineTemperatureC) &&
        FiniteOptional(value.Cop) && FiniteOptional(value.WindSpeedMps) && FiniteOptional(value.SolarIrradianceWm2);

    private static bool FiniteOptional(double? value) => value is null || double.IsFinite(value.Value);

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
