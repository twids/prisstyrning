using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

// Display context never upgrades the physical assessment used by control/training.
public sealed record ThermalSensorUsage(DataQuality Quality, string? Reason, bool Excluded,
    double? Value, DateTimeOffset? ValueUpdatedUtc, string Usage);

public static class ThermalSensorUsagePolicy
{
    public static bool ReportedIdle(IReadOnlyDictionary<string, SensorAssessment> values, bool powerVerified,
        Func<string, HomeAssistantState?>? stateForRole = null, DateTimeOffset? now = null, DateTimeOffset? importedAtUtc = null)
    {
        bool NotActive(string role) => values.TryGetValue(role, out var signal) && !signal.Excluded &&
            signal.BooleanValue == false && (signal.Quality == DataQuality.Valid ||
                signal.Quality == DataQuality.Stale && now is { } timestamp &&
                SensorTimestampValidator.Assess(stateForRole?.Invoke(role), timestamp, TimeSpan.MaxValue, importedAtUtc).Quality == DataQuality.Valid);
        return powerVerified && values.TryGetValue(ThermalEntityRoles.HeatPumpPower, out var power) &&
        power.Quality == DataQuality.Valid && !power.Excluded && power.Value is >= 0 and <= .1 &&
        NotActive(ThermalEntityRoles.DhwActive) && NotActive(ThermalEntityRoles.DefrostActive) &&
        NotActive(ThermalEntityRoles.BackupHeaterActive);
    }

    public static ThermalSensorUsage Describe(string role, SensorAssessment assessment, HomeAssistantState? raw,
        DateTimeOffset now, bool reportedIdle, DateTimeOffset? historyImportedAtUtc = null)
    {
        var usage = assessment.Quality == DataQuality.Valid && !assessment.Excluded ? "Current" : "Unusable";
        var reason = assessment.Reason;
        var communicable = raw is not null && !assessment.Excluded &&
            assessment.Quality is DataQuality.Valid or DataQuality.Stale &&
            SensorTimestampValidator.Assess(raw, now, TimeSpan.MaxValue, historyImportedAtUtc).Quality == DataQuality.Valid;
        if (communicable && role == ThermalEntityRoles.CopAverage)
        {
            usage = "HistoricalAverage";
            reason = "Medel-COP visas för uppföljning, aldrig som realtids-COP eller träningspunkt.";
        }
        else if (communicable && reportedIdle && (assessment.Quality == DataQuality.Stale || role == ThermalEntityRoles.CopRealtime) &&
                 (role is ThermalEntityRoles.LeavingWaterTemperature or
                 ThermalEntityRoles.ReturnWaterTemperature or ThermalEntityRoles.BrineIn or ThermalEntityRoles.BrineOut or
                 ThermalEntityRoles.CopRealtime ||
                 role is ThermalEntityRoles.DhwActive or ThermalEntityRoles.DefrostActive or ThermalEntityRoles.BackupHeaterActive && assessment.BooleanValue == false ||
                 role == ThermalEntityRoles.Flow && assessment.Value == 0))
        {
            usage = "HeldWhileIdle";
            reason = "Värmepumpens aktuella eleffekt visar vila. Behållet värde visas, men är inte en ny mätning för styrning eller COP-träning.";
        }
        else if (communicable && role == "room" && assessment.Quality == DataQuality.Stale)
        {
            usage = "HeldRoom";
            reason = "Senast rapporterad rumstemperatur. Osäker ålder är en varning, inte ett konstaterat sensorfel.";
        }
        return new(assessment.Quality, reason, assessment.Excluded,
            communicable ? assessment.Value : null, raw?.LastUpdatedUtc, usage);
    }
}
