using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public static class ThermalCopSource
{
    public static readonly IReadOnlySet<string> AveragingPeriods = new HashSet<string>(StringComparer.Ordinal)
        { "Unknown", "Day", "Week", "Month", "Year", "Lifetime", "SinceReset" };

    public static bool IsExternal(IEnumerable<ThermalEntityConfig> entities) => entities.Any(x =>
        x.Enabled && x.Role.Equals(ThermalEntityRoles.CopRealtime, StringComparison.OrdinalIgnoreCase));

    public static bool HasHydraulicSource(IEnumerable<ThermalEntityConfig> entities) =>
        new[] { ThermalEntityRoles.Flow, ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature }
            .All(role => entities.Any(x => x.Enabled && x.Role.Equals(role, StringComparison.OrdinalIgnoreCase)));

    public static bool HasCostSource(IEnumerable<ThermalEntityConfig> entities, bool powerVerified) =>
        powerVerified || IsExternal(entities) && HasHydraulicSource(entities);

    public static double? Select(ThermalTelemetrySample sample, IEnumerable<ThermalEntityConfig> entities,
        IReadOnlyDictionary<string, SensorAssessment> values, bool powerVerified)
    {
        if (IsExternal(entities))
        {
            // Never replace an unavailable selected entity with our own formula or a mean.
            var hasLoad = powerVerified ? sample.HeatPumpPowerKw is > .1 :
                sample.HeatOutputKw is > .5 && double.IsFinite(sample.HeatOutputKw.Value) &&
                new[] { ThermalEntityRoles.Flow, ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature }
                    .All(role => values.TryGetValue(role, out var sensor) && !sensor.Excluded && sensor.Quality == DataQuality.Valid);
            if (!hasLoad || sample.DhwActive != false ||
                sample.BackupHeaterActive != false || sample.DefrostActive != false ||
                !values.TryGetValue(ThermalEntityRoles.CopRealtime, out var realtime) ||
                realtime.Excluded || realtime.Quality != DataQuality.Valid ||
                realtime.Value is not > 0 || !double.IsFinite(realtime.Value.Value)) return null;
            return realtime.Value;
        }
        var derived = powerVerified && sample.BackupHeaterActive == false &&
            sample.HeatPumpPowerKw is > .1 && sample.HeatOutputKw is { } heat
            ? heat / sample.HeatPumpPowerKw.Value : (double?)null;
        return derived is { } value && double.IsFinite(value) ? value : null;
    }
}
