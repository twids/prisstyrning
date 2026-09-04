using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Prisstyrning.Thermal.Optimization;

public sealed class EmhassOptions
{
    public const string SectionName = "Emhass";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://emhass:5000";
    public int SolverTimeoutSeconds { get; set; } = 45;
    public int OptimizationTimeStepMinutes { get; set; } = 15;
    public int HorizonHours { get; set; } = 48;
    public string ResultPath { get; set; } = "/emhass-data/opt_res_latest.csv";
}

public sealed record EmhassThermalConfig(
    double HeatingRateCPerHour,
    double CoolingConstantPerHourPerC,
    double ThermalInertiaHours,
    double StartTemperatureC,
    IReadOnlyList<double> MinimumTemperaturesC,
    IReadOnlyList<double> MaximumTemperaturesC);

public sealed record EmhassOptimizationRequest(
    IReadOnlyList<decimal> LoadCostForecast,
    IReadOnlyList<double> OutsideTemperatureForecastC,
    IReadOnlyList<double> BaseLoadForecastW,
    EmhassThermalConfig Thermal,
    int? DhwStartStep,
    int DhwDurationSteps,
    double HeatPumpElectricPowerW,
    double DhwElectricPowerW,
    bool TariffEnabled = false,
    double CapacityCostPerKw = 0)
{
    public ThermalPlanningModelEvidence? ModelEvidence { get; init; }
    public ThermalPlanningInputEvidence? InputEvidence { get; init; }
    public DateTimeOffset? HorizonStartUtc { get; init; }
}

public sealed record EmhassOptimizationStep(
    int Index,
    double SpaceHeatingPowerW,
    double? PredictedTemperatureC,
    double UnitCost);

public sealed record EmhassOptimizationResult(
    IReadOnlyList<EmhassOptimizationStep> Steps,
    int SolverDurationMs,
    decimal ObjectiveCost);

public interface IEmhassClient
{
    Task<EmhassOptimizationResult> OptimizeAsync(EmhassOptimizationRequest request, CancellationToken cancellationToken = default);
}

public sealed class EmhassHealthState
{
    private readonly object _gate = new();
    public bool Available { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public int? LastDurationMs { get; private set; }
    public string? LastError { get; private set; }

    public void Success(int durationMs)
    {
        lock (_gate)
        {
            Available = true;
            LastSuccessUtc = DateTimeOffset.UtcNow;
            LastDurationMs = durationMs;
            LastError = null;
        }
    }

    public void Failure(string error)
    {
        lock (_gate)
        {
            Available = false;
            LastError = error;
        }
    }
}

public sealed class EmhassClient : IEmhassClient
{
    private static readonly SemaphoreSlim ResultFileGate = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EmhassOptions _options;
    private readonly EmhassHealthState _health;

    public EmhassClient(IHttpClientFactory httpClientFactory, IOptions<EmhassOptions> options, EmhassHealthState health)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _health = health;
    }

    public async Task<EmhassOptimizationResult> OptimizeAsync(
        EmhassOptimizationRequest request,
        CancellationToken cancellationToken = default)
    {
        EmhassOptimizationValidation.ValidateRequest(request);
        if (!_options.Enabled) throw new InvalidOperationException("EMHASS is disabled.");
        var timeoutSeconds = Math.Clamp(_options.SolverTimeoutSeconds, 1, 45);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var payload = BuildRuntimePayload(request);
        var stopwatch = Stopwatch.StartNew();
        await ResultFileGate.WaitAsync(timeout.Token);
        try
        {
            var previousResultWriteUtc = File.Exists(_options.ResultPath)
                ? File.GetLastWriteTimeUtc(_options.ResultPath)
                : (DateTime?)null;
            var solveStartedUtc = DateTimeOffset.UtcNow;
            var client = _httpClientFactory.CreateClient("Emhass");
            using var response = await client.PostAsJsonAsync("action/naive-mpc-optim", payload, timeout.Token);
            response.EnsureSuccessStatusCode();
            var steps = await ReadSavedPlanAsync(
                _options.ResultPath,
                request.LoadCostForecast,
                solveStartedUtc,
                previousResultWriteUtc,
                request.HorizonStartUtc,
                _options.OptimizationTimeStepMinutes,
                timeout.Token);
            stopwatch.Stop();
            var objective = steps.Sum(step =>
                (decimal)(step.SpaceHeatingPowerW / 1000d * _options.OptimizationTimeStepMinutes / 60d) *
                request.LoadCostForecast[step.Index]);
            var result = new EmhassOptimizationResult(steps, (int)stopwatch.ElapsedMilliseconds, decimal.Round(objective, 4));
            EmhassOptimizationValidation.ValidateResult(request, result, _options.OptimizationTimeStepMinutes);
            _health.Success((int)stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or FormatException or TaskCanceledException or InvalidOperationException)
        {
            stopwatch.Stop();
            _health.Failure(exception is TaskCanceledException ? $"Solvern överskred {timeoutSeconds} sekunder." : exception.Message);
            throw;
        }
        finally
        {
            ResultFileGate.Release();
        }
    }

    internal object BuildRuntimePayload(EmhassOptimizationRequest request)
    {
        var numberOfLoads = request.DhwStartStep is null ? 1 : 2;
        var nominalPowers = request.DhwStartStep is null
            ? new[] { request.HeatPumpElectricPowerW }
            : new[] { request.HeatPumpElectricPowerW, request.DhwElectricPowerW };
        var operatingSteps = request.DhwStartStep is null
            ? new[] { 0 }
            : new[] { 0, request.DhwDurationSteps };
        var starts = request.DhwStartStep is null
            ? new[] { 0 }
            : new[] { 0, request.DhwStartStep.Value };
        var ends = request.DhwStartStep is null
            ? new[] { request.LoadCostForecast.Count - 1 }
            : new[] { request.LoadCostForecast.Count - 1, request.DhwStartStep!.Value + request.DhwDurationSteps };
        var loadConfigs = request.DhwStartStep is null
            ? new object[] { new { thermal_config = ThermalPayload(request.Thermal) } }
            : new object[] { new { thermal_config = ThermalPayload(request.Thermal) }, new { } };
        var loadGroups = request.DhwStartStep is null
            ? Array.Empty<object>()
            : new object[] { new { names = new[] { "deferrable0", "deferrable1" }, mutual_exclusion = true } };

        return new
        {
            prediction_horizon = request.LoadCostForecast.Count,
            optimization_time_step = _options.OptimizationTimeStepMinutes,
            continual_publish = false,
            entity_save = false,
            load_cost_forecast = request.LoadCostForecast,
            load_power_forecast = request.BaseLoadForecastW,
            pv_power_forecast = Enumerable.Repeat(0, request.LoadCostForecast.Count).ToArray(),
            prod_price_forecast = Enumerable.Repeat(0, request.LoadCostForecast.Count).ToArray(),
            outdoor_temperature_forecast = request.OutsideTemperatureForecastC,
            number_of_deferrable_loads = numberOfLoads,
            nominal_power_of_deferrable_loads = nominalPowers,
            operating_timesteps_of_each_deferrable_load = operatingSteps,
            start_timesteps_of_each_deferrable_load = starts,
            end_timesteps_of_each_deferrable_load = ends,
            treat_deferrable_load_as_semi_cont = Enumerable.Repeat(true, numberOfLoads).ToArray(),
            set_deferrable_load_single_constant = numberOfLoads == 1 ? new[] { false } : new[] { false, true },
            def_load_config = loadConfigs,
            deferrable_load_groups = loadGroups,
            capacity_cost_per_kw = request.TariffEnabled ? request.CapacityCostPerKw : 0
        };
    }

    private static async Task<IReadOnlyList<EmhassOptimizationStep>> ReadSavedPlanAsync(
        string resultPath,
        IReadOnlyList<decimal> prices,
        DateTimeOffset solveStartedUtc,
        DateTime? previousResultWriteUtc,
        DateTimeOffset? expectedStartUtc,
        int optimizationTimeStepMinutes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resultPath)) throw new InvalidOperationException("EMHASS result path is not configured.");
        var file = new FileInfo(resultPath);
        file.Refresh();
        if (!file.Exists) throw new InvalidOperationException($"EMHASS did not create '{resultPath}'.");
        if (file.LastWriteTimeUtc < solveStartedUtc.UtcDateTime.AddSeconds(-1) ||
            previousResultWriteUtc is { } previous && file.LastWriteTimeUtc <= previous)
            throw new InvalidOperationException("EMHASS result file is stale and belongs to an earlier solve.");

        var lines = await File.ReadAllLinesAsync(resultPath, cancellationToken);
        if (lines.Length < 2) throw new InvalidOperationException("EMHASS result file contains no plan rows.");
        var headers = ParseCsvLine(lines[0]);
        var powerColumn = FindColumn(headers, "P_deferrable0");
        var temperatureColumn = FindColumn(headers, "predicted_temp_heater0");
        var costColumn = FindColumn(headers, "unit_load_cost", required: false);

        var rows = lines.Skip(1).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (rows.Length != prices.Count)
            throw new InvalidOperationException($"EMHASS returned {rows.Length} of {prices.Count} requested steps.");
        var result = new List<EmhassOptimizationStep>();
        DateTimeOffset? previousTimestamp = null;
        foreach (var line in rows)
        {
            var cells = ParseCsvLine(line);
            var timestamp = ResultTimestamp(cells);
            var expected = expectedStartUtc?.ToUniversalTime().AddMinutes(result.Count * optimizationTimeStepMinutes);
            if (expected is not null && timestamp != expected ||
                previousTimestamp is { } previousStepTimestamp && timestamp != previousStepTimestamp.AddMinutes(optimizationTimeStepMinutes))
                throw new InvalidOperationException("EMHASS result has a missing, duplicated or unexpected timestamp.");
            var power = Number(cells, powerColumn, "P_deferrable0");
            var temperature = Number(cells, temperatureColumn, "predicted_temp_heater0");
            var cost = costColumn >= 0 && TryNumber(cells, costColumn, out var parsedCost)
                ? parsedCost
                : (double)prices[result.Count];
            result.Add(new EmhassOptimizationStep(result.Count, power, temperature, cost));
            previousTimestamp = timestamp;
        }
        return result;
    }

    private static object ThermalPayload(EmhassThermalConfig value) => new
    {
        heating_rate = value.HeatingRateCPerHour,
        cooling_constant = value.CoolingConstantPerHourPerC,
        thermal_inertia = value.ThermalInertiaHours,
        start_temperature = value.StartTemperatureC,
        sense = "heat",
        min_temperatures = value.MinimumTemperaturesC,
        max_temperatures = value.MaximumTemperaturesC
    };

    private static int FindColumn(IReadOnlyList<string> headers, string name, bool required = true)
    {
        for (var index = 0; index < headers.Count; index++)
            if (headers[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return index;
        if (required) throw new InvalidOperationException($"EMHASS result is missing required CSV column {name}.");
        return -1;
    }

    private static double Number(IReadOnlyList<string> cells, int index, string name)
    {
        if (TryNumber(cells, index, out var value)) return value;
        throw new InvalidOperationException($"EMHASS result contains an invalid {name} value.");
    }

    private static DateTimeOffset ResultTimestamp(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0 || string.IsNullOrWhiteSpace(cells[0]))
            throw new InvalidOperationException("EMHASS result is missing its timestamp.");
        var value = cells[0].Trim();
        var hasOffset = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                        value.Length >= 6 && value[^3] == ':' && (value[^6] == '+' || value[^6] == '-');
        if (!hasOffset || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            throw new InvalidOperationException("EMHASS result contains an invalid or unzoned timestamp.");
        return timestamp;
    }

    private static bool TryNumber(IReadOnlyList<string> cells, int index, out double value)
    {
        value = 0;
        return index >= 0 && index < cells.Count &&
               double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    internal static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(character);
        }
        if (quoted) throw new FormatException("EMHASS result contains an unterminated quoted CSV field.");
        result.Add(current.ToString().Trim());
        return result;
    }

}
