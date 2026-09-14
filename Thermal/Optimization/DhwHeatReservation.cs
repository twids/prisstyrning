namespace Prisstyrning.Thermal.Optimization;

internal static class DhwHeatReservation
{
    internal static (int Start, int Duration) Rasterize(
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset horizonStart, int stepMinutes, int horizonSteps)
    {
        var first = Math.Clamp((int)Math.Floor((start - horizonStart).TotalMinutes / stepMinutes), 0, horizonSteps);
        var last = Math.Clamp((int)Math.Ceiling((end - horizonStart).TotalMinutes / stepMinutes), 0, horizonSteps);
        return (first, Math.Max(0, last - first));
    }

    // Necessary feasibility check only: even starting at the upper comfort bound,
    // the same no-heating dynamics used by EMHASS must survive every reserved quarter.
    // Passing this check does not replace full-horizon solver/result validation.
    internal static bool CanCoast(
        DhwCandidate candidate, DateTimeOffset horizonStart, int stepMinutes,
        EmhassThermalConfig thermal, IReadOnlyList<double> outside)
    {
        var (first, duration) = Rasterize(candidate.StartUtc, candidate.EndUtc, horizonStart, stepMinutes, outside.Count);
        if (duration == 0) return false;
        var temperature = first == 0 ? thermal.StartTemperatureC : thermal.MaximumTemperaturesC[first];
        var factor = thermal.CoolingConstantPerHourPerC * stepMinutes / 60d;
        for (var index = first; index < first + duration && index + 1 < outside.Count; index++)
        {
            temperature -= factor * (temperature - outside[index]);
            if (!double.IsFinite(temperature) || temperature < thermal.MinimumTemperaturesC[index + 1] - .01)
                return false;
        }
        return true;
    }
}
