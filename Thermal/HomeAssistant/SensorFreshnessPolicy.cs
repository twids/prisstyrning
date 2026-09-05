using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public static class SensorFreshnessPolicy
{
    public static readonly TimeSpan CommunicationTimeout = TimeSpan.FromMinutes(10);

    public static int MaximumForRole(string role) => role is
        ThermalEntityRoles.OutsideTemperature or ThermalEntityRoles.WindSpeed or
        ThermalEntityRoles.SolarIrradiance or ThermalEntityRoles.SpotPrice ? 1440 : 10;

    public static TimeSpan ReportAge(int? configuredMinutes, TimeSpan fallback) =>
        configuredMinutes is { } minutes ? TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440)) : fallback;
}
