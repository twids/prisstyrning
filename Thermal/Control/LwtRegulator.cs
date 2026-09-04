using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Control;

public sealed record LwtRegulatorInput(
    ControlMode Mode,
    DateTimeOffset NowUtc,
    DateTimeOffset? TelemetryUtc,
    DateTimeOffset? PlanCreatedUtc,
    double PlannedDeviationC,
    double RepresentativeTemperatureErrorC,
    bool CriticalRoomBelowMinimum,
    bool DhwActive,
    bool DefrostActive,
    double? FlowLitresPerMinute,
    bool WriterLeaseHeld,
    bool P1P2Healthy,
    bool ManualOverride,
    double CurrentDeviationC,
    DateTimeOffset? LastWriteUtc,
    double Integral,
    double DeviationLimitC,
    string? SafetyInvalidReason = null,
    double Kp = 0.8,
    double KiPerHour = 0.08);

public sealed record LwtRegulatorDecision(
    bool ShouldWrite,
    double RequestedDeviationC,
    double NewIntegral,
    bool IsFallback,
    string Reason);

public sealed class LwtRegulator
{
    public LwtRegulatorDecision Evaluate(LwtRegulatorInput input)
    {
        var fallback = FallbackReason(input);
        if (fallback is not null)
        {
            return new LwtRegulatorDecision(
                !double.IsFinite(input.CurrentDeviationC) || Math.Abs(input.CurrentDeviationC) >= 0.05,
                0,
                0,
                true,
                fallback);
        }

        if (input.DhwActive || input.DefrostActive)
            return new(false, input.CurrentDeviationC, input.Integral, false, input.DhwActive ? "Skrivningar är frysta under DHW." : "Skrivningar är frysta under avfrostning.");

        var elapsedHours = input.LastWriteUtc is { } last
            ? Math.Clamp((input.NowUtc - last).TotalHours, 0, 1)
            : 5d / 60d;
        var integral = Math.Clamp(input.Integral - input.RepresentativeTemperatureErrorC * elapsedHours, -10, 10);
        var correction = -input.Kp * input.RepresentativeTemperatureErrorC + input.KiPerHour * integral;
        if (input.CriticalRoomBelowMinimum) correction = Math.Max(correction, 0.5);
        var limit = Math.Clamp(input.DeviationLimitC, 0, 3);
        var requested = Math.Clamp(input.PlannedDeviationC + correction, -limit, limit);
        requested = Math.Round(requested * 2, MidpointRounding.AwayFromZero) / 2;

        var rateLimited = input.LastWriteUtc is { } written && input.NowUtc - written < TimeSpan.FromMinutes(30);
        var materialChange = Math.Abs(requested - input.CurrentDeviationC) >= 0.5;
        return new LwtRegulatorDecision(
            !rateLimited && materialChange,
            requested,
            integral,
            false,
            rateLimited ? "Nästa skrivning väntar på 30-minutersgränsen." : materialChange ? "Planerad effekt kombinerad med långsam PI-korrigering." : "Förändringen är mindre än 0,5 °C.");
    }

    private static string? FallbackReason(LwtRegulatorInput input)
    {
        if (input.Mode is not (ControlMode.LwtActive or ControlMode.FullActive)) return "LWT-styrning är inte aktiv.";
        if (!double.IsFinite(input.PlannedDeviationC) || !double.IsFinite(input.RepresentativeTemperatureErrorC) ||
            !double.IsFinite(input.CurrentDeviationC) || Math.Abs(input.CurrentDeviationC) > 3.001 ||
            !double.IsFinite(input.Integral) || !double.IsFinite(input.DeviationLimitC) || input.DeviationLimitC is < 0 or > 3 ||
            !double.IsFinite(input.Kp) || input.Kp is < 0 or > 10 ||
            !double.IsFinite(input.KiPerHour) || input.KiPerHour is < 0 or > 10)
            return "Regulatorns säkerhetsunderlag är ogiltigt; LWT återgår till noll.";
        if (!input.WriterLeaseHeld) return "Writer-leasen har förlorats.";
        if (!input.P1P2Healthy) return "P1P2MQTT har rapporterat skriv- eller kommunikationsfel.";
        if (input.ManualOverride) return "Manuell override är aktiv.";
        if (input.TelemetryUtc is null || input.NowUtc - input.TelemetryUtc > TimeSpan.FromMinutes(10)) return "Telemetrin är äldre än tio minuter.";
        if (!string.IsNullOrWhiteSpace(input.SafetyInvalidReason)) return input.SafetyInvalidReason;
        if (input.PlanCreatedUtc is null || input.NowUtc - input.PlanCreatedUtc > TimeSpan.FromMinutes(60)) return "Den senaste giltiga planen är äldre än 60 minuter.";
        if (input.FlowLitresPerMinute is not > 1) return "Flödet är för lågt för säker LWT-styrning.";
        return null;
    }
}
