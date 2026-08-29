namespace Prisstyrning.Thermal.Optimization;

public sealed record CopObservation(
    DateTimeOffset TimestampUtc,
    double BrineInC,
    double LeavingWaterTemperatureC,
    double HeatOutputKw,
    double Cop);

public sealed record CopParameters(
    double Intercept,
    double BrineCoefficient,
    double LwtCoefficient,
    double LoadCoefficient);

public sealed record CopModelMetrics(double Mae, int TrainingSamples, int ValidationSamples);

public sealed record CopTrainingResult(CopParameters Parameters, CopModelMetrics Metrics);

/// <summary>
/// Small transparent linear model around the reference point 0 °C brine,
/// 35 °C LWT and 4 kW delivered load. Inputs and output are deliberately
/// bounded so bad historical data cannot create unsafe optimization gains.
/// </summary>
public sealed class CopModel
{
    public static readonly CopParameters ConservativeDefault = new(4.0, 0.08, -0.06, -0.03);

    public double Predict(CopParameters parameters, double brineInC, double lwtC, double heatOutputKw)
    {
        var value = parameters.Intercept +
                    parameters.BrineCoefficient * brineInC +
                    parameters.LwtCoefficient * (lwtC - 35) +
                    parameters.LoadCoefficient * (heatOutputKw - 4);
        return Math.Clamp(value, 1.2, 8.0);
    }

    public CopTrainingResult Train(IReadOnlyList<CopObservation> observations)
    {
        if (observations.Count < 100) throw new ArgumentException("At least 100 valid COP observations are required.");
        var ordered = observations.OrderBy(x => x.TimestampUtc).ToArray();
        var split = Math.Clamp((int)(ordered.Length * 0.8), 80, ordered.Length - 20);
        var training = ordered[..split];
        var validation = ordered[split..];
        var parameters = Bound(SolveRidge(training) ?? ConservativeDefault);
        var mae = validation.Average(x => Math.Abs(Predict(parameters, x.BrineInC, x.LeavingWaterTemperatureC, x.HeatOutputKw) - x.Cop));
        return new CopTrainingResult(parameters, new CopModelMetrics(mae, training.Length, validation.Length));
    }

    private static CopParameters? SolveRidge(IReadOnlyList<CopObservation> observations)
    {
        const int dimensions = 4;
        var matrix = new double[dimensions, dimensions + 1];
        foreach (var observation in observations)
        {
            var features = new[]
            {
                1d,
                observation.BrineInC,
                observation.LeavingWaterTemperatureC - 35,
                observation.HeatOutputKw - 4
            };
            for (var row = 0; row < dimensions; row++)
            {
                for (var column = 0; column < dimensions; column++)
                    matrix[row, column] += features[row] * features[column];
                matrix[row, dimensions] += features[row] * observation.Cop;
            }
        }

        for (var index = 1; index < dimensions; index++) matrix[index, index] += 0.1;
        for (var pivot = 0; pivot < dimensions; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < dimensions; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-9) return null;
            if (best != pivot)
            {
                for (var column = pivot; column <= dimensions; column++)
                    (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
            }

            var divisor = matrix[pivot, pivot];
            for (var column = pivot; column <= dimensions; column++) matrix[pivot, column] /= divisor;
            for (var row = 0; row < dimensions; row++)
            {
                if (row == pivot) continue;
                var factor = matrix[row, pivot];
                for (var column = pivot; column <= dimensions; column++)
                    matrix[row, column] -= factor * matrix[pivot, column];
            }
        }

        return new CopParameters(matrix[0, dimensions], matrix[1, dimensions], matrix[2, dimensions], matrix[3, dimensions]);
    }

    private static CopParameters Bound(CopParameters value) => new(
        Math.Clamp(value.Intercept, 1.2, 8),
        Math.Clamp(value.BrineCoefficient, 0, 0.3),
        Math.Clamp(value.LwtCoefficient, -0.3, 0),
        Math.Clamp(value.LoadCoefficient, -0.2, 0.2));
}
