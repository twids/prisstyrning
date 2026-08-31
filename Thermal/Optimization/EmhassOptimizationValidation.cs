namespace Prisstyrning.Thermal.Optimization;

internal static class EmhassOptimizationValidation
{
    private const int MaximumHorizonSteps = 384;
    internal const string ComfortBreachReason = "Solverresultatet bryter mot det tillåtna komfortbandet.";

    internal static void ValidateRequest(EmhassOptimizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = request.LoadCostForecast.Count;
        if (count is <= 0 or > MaximumHorizonSteps ||
            request.OutsideTemperatureForecastC.Count != count ||
            request.BaseLoadForecastW.Count != count ||
            request.Thermal.MinimumTemperaturesC.Count != count ||
            request.Thermal.MaximumTemperaturesC.Count != count)
            throw new ThermalPlanningEvidenceException("Planeringsprognoserna har olika eller ogiltig längd.");
        if (request.LoadCostForecast.Any(x => x is < -1_000 or > 1_000) ||
            request.OutsideTemperatureForecastC.Any(x => !FiniteBetween(x, -100, 80)) ||
            request.BaseLoadForecastW.Any(x => !FiniteBetween(x, 0, 1_000_000)))
            throw new ThermalPlanningEvidenceException("Planeringsprognosen innehåller ogiltiga pris-, väder- eller lastvärden.");
        if (!FiniteBetween(request.Thermal.HeatingRateCPerHour, 0.001, 20) ||
            !FiniteBetween(request.Thermal.CoolingConstantPerHourPerC, 0.00001, 5) ||
            !FiniteBetween(request.Thermal.ThermalInertiaHours, 0.001, 240) ||
            !FiniteBetween(request.Thermal.StartTemperatureC, -20, 50) ||
            !FiniteBetween(request.HeatPumpElectricPowerW, 1, 100_000) ||
            !FiniteBetween(request.DhwElectricPowerW, 1, 100_000))
            throw new ThermalPlanningEvidenceException("Planeringens termiska parametrar eller nominella effekter är ogiltiga.");
        for (var index = 0; index < count; index++)
        {
            var minimum = request.Thermal.MinimumTemperaturesC[index];
            var maximum = request.Thermal.MaximumTemperaturesC[index];
            if (!FiniteBetween(minimum, -20, 50) || !FiniteBetween(maximum, -20, 50) || minimum > maximum)
                throw new ThermalPlanningEvidenceException("Planeringens komfortgränser är ogiltiga.");
        }
        if (request.DhwStartStep is { } start)
        {
            if (start < 0 || request.DhwDurationSteps <= 0 || start + request.DhwDurationSteps > count)
                throw new ThermalPlanningEvidenceException("DHW-reservationen ligger utanför planeringshorisonten.");
        }
        else if (request.DhwDurationSteps != 0)
        {
            throw new ThermalPlanningEvidenceException("DHW-reservationen saknar en entydig start.");
        }
        if (request.TariffEnabled && !FiniteBetween(request.CapacityCostPerKw, 0, 1_000_000))
            throw new ThermalPlanningEvidenceException("Effekttariffens kostnad är ogiltig.");
    }

    internal static void ValidateResult(
        EmhassOptimizationRequest request,
        EmhassOptimizationResult result,
        int optimizationTimeStepMinutes)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(result);
        if (optimizationTimeStepMinutes is <= 0 or > 60)
            throw new ThermalPlanningEvidenceException("Optimeringens tidssteg är ogiltigt.");
        if (result.SolverDurationMs is < 0 or > 45_000)
            throw new ThermalPlanningEvidenceException("Solverresultatet överskred den tillåtna körtiden.");
        if (result.Steps.Count != request.LoadCostForecast.Count)
            throw new ThermalPlanningEvidenceException("Solverresultatet täcker inte hela planeringshorisonten.");

        var objective = 0m;
        for (var index = 0; index < result.Steps.Count; index++)
        {
            var step = result.Steps[index];
            if (step.Index != index)
                throw new ThermalPlanningEvidenceException("Solverresultatets steg är dubbla, saknas eller ligger i fel ordning.");
            if (!FiniteBetween(step.SpaceHeatingPowerW, 0, request.HeatPumpElectricPowerW * 1.001))
                throw new ThermalPlanningEvidenceException("Solverresultatet innehåller en ogiltig värmeeffekt.");
            if (step.PredictedTemperatureC is not { } predicted || !double.IsFinite(predicted))
                throw new ThermalPlanningEvidenceException("Solverresultatet saknar en giltig temperaturprognos.");
            if (predicted < request.Thermal.MinimumTemperaturesC[index] - 0.01 ||
                predicted > request.Thermal.MaximumTemperaturesC[index] + 0.01)
                throw new ThermalPlanningEvidenceException(ComfortBreachReason);
            if (!double.IsFinite(step.UnitCost))
                throw new ThermalPlanningEvidenceException("Solverresultatet innehåller en ogiltig kostnad.");
            if (request.DhwStartStep is { } dhwStart && index >= dhwStart && index < dhwStart + request.DhwDurationSteps &&
                step.SpaceHeatingPowerW > Math.Max(5, request.HeatPumpElectricPowerW * 0.001))
                throw new ThermalPlanningEvidenceException("Solverresultatet använder husvärme under reserverad DHW-kapacitet.");
            objective += (decimal)(step.SpaceHeatingPowerW / 1000d * optimizationTimeStepMinutes / 60d) *
                         request.LoadCostForecast[index];
        }

        objective = decimal.Round(objective, 4);
        var tolerance = Math.Max(0.01m, Math.Abs(objective) * 0.001m);
        if (Math.Abs(result.ObjectiveCost - objective) > tolerance)
            throw new ThermalPlanningEvidenceException("Solverresultatets kostnad stämmer inte med planstegen.");
    }

    private static bool FiniteBetween(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
