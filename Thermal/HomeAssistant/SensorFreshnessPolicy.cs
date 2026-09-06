using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public static class SensorFreshnessPolicy
{
    public static readonly TimeSpan CommunicationTimeout = TimeSpan.FromMinutes(10);

    public static int MaximumForRole(string role) => role is
        ThermalEntityRoles.OutsideTemperature or ThermalEntityRoles.WindSpeed or
        ThermalEntityRoles.SolarIrradiance or ThermalEntityRoles.SpotPrice or ThermalEntityRoles.CopAverage ? 1440 : 10;

    public static TimeSpan ReportAge(string role, int? configuredMinutes, TimeSpan fallback) =>
        ReportAge(configuredMinutes, role == ThermalEntityRoles.SpotPrice
            ? TimeSpan.FromMinutes(15) : fallback);

    public static TimeSpan ReportAge(int? configuredMinutes, TimeSpan fallback) =>
        configuredMinutes is { } minutes ? TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440)) : fallback;
}
