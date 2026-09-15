namespace Prisstyrning.Thermal.Control;

/// <summary>
/// Model-independent control kernel. It proposes commands; it never owns a lease,
/// performs commissioning, or writes to HA. The caller must supply verified safety
/// evidence and persist evaluation state separately from accepted write state.
/// TelemetryUtc is the time of the validated live snapshot, not the sensor's
/// last_changed timestamp: an unchanged temperature is not inherently stale.
/// </summary>
public sealed record ConservativeLwtInput(
    DateTimeOffset NowUtc,
    DateTimeOffset? TelemetryUtc,
    DateTimeOffset? PreviousEvaluationUtc,
    DateTimeOffset? LastAcceptedWriteUtc,
    double ObservedDeviationC,
    double RepresentativeTemperatureErrorC,
    bool CriticalRoomBelowMinimum,
    bool DhwActive,
    bool DefrostActive,
    double? FlowLitresPerMinute,
    double Integral,
    bool CommissioningVerified,
    bool WriterLeaseHeld,
    bool CommunicationHealthy,
    bool ManualOverride,
    string? SafetyInvalidReason = null,
    double DeviationLimitC = 1,
    double DeviationStepC = 0.5);

public sealed class ConservativeLwtRegulator
{
    private const double Kp = 0.8;
    private const double KiPerHour = 0.08;
    private const double DeadbandC = 0.1;
    private const double IntegralLimit = 10;

    public LwtRegulatorDecision Evaluate(ConservativeLwtInput input)
        => EvaluateCore(input, 0);

    // Only the plan-consumption boundary may supply this already validated bias.
    // Statistical failure must remove the bias, not disable basic room control.
    internal LwtRegulatorDecision EvaluateWithValidatedBias(ConservativeLwtInput input, double bias) =>
        EvaluateCore(input, double.IsFinite(bias) ? Math.Clamp(bias, -.5, .5) : 0);

    private static LwtRegulatorDecision EvaluateCore(ConservativeLwtInput input, double bias)
    {
        var invalid = InvalidReason(input);
        if (invalid is not null) return Zero(input, invalid);

        // Safety fallback has priority; ordinary regulation is frozen during DHW.
        if (input.DhwActive || input.DefrostActive)
            return new(false, input.ObservedDeviationC, input.Integral, false,
                input.DhwActive ? "Försiktig reglering är fryst under varmvattenkörning." : "Försiktig reglering är fryst under avfrostning.");
        if (input.FlowLitresPerMinute is not > 1)
            return Zero(input, "Flödet räcker inte för reglering; återgå till grundkurvan utan att bygga upp PI-korrigering.");
        if (input.CriticalRoomBelowMinimum && input.ObservedDeviationC < 0)
            return Zero(input, "Ett kritiskt rum är för kallt; ta bort den negativa avvikelsen och återgå till grundkurvan.");

        // On startup use P only. Never integrate over a gap or charge the same
        // interval repeatedly when a write was not needed or was rate limited.
        var elapsed = input.PreviousEvaluationUtc is { } previous ? input.NowUtc - previous : TimeSpan.Zero;
        var discontinuity = input.PreviousEvaluationUtc is null || elapsed > TimeSpan.FromMinutes(10);
        var integral = discontinuity ? 0 : input.Integral;
        var elapsedHours = discontinuity ? 0 : elapsed.TotalHours;
        var error = Math.Abs(input.RepresentativeTemperatureErrorC) <= DeadbandC ? 0 : input.RepresentativeTemperatureErrorC;
        var limit = Math.Floor(input.DeviationLimitC / input.DeviationStepC) * input.DeviationStepC;
        var proposedIntegral = Math.Clamp(integral - error * elapsedHours, -IntegralLimit, IntegralLimit);
        var proposedOutput = bias - Kp * error + KiPerHour * proposedIntegral;
        // Conditional integration: stop accumulating further into saturation,
        // but allow the integrator to unwind when the room error reverses.
        if (!(proposedOutput > limit && proposedIntegral > integral ||
              proposedOutput < -limit && proposedIntegral < integral))
            integral = proposedIntegral;

        var requested = Math.Clamp(bias - Kp * error + KiPerHour * integral, -limit, limit);
        if (input.CriticalRoomBelowMinimum) requested = Math.Max(requested, input.DeviationStepC);
        requested = Math.Clamp(Math.Round(requested / input.DeviationStepC, MidpointRounding.AwayFromZero) * input.DeviationStepC, -limit, limit);

        // One hardware step per accepted write, including changes in direction.
        // Safety zeroing above is deliberately not subject to this restriction.
        requested = Math.Clamp(requested, input.ObservedDeviationC - input.DeviationStepC, input.ObservedDeviationC + input.DeviationStepC);
        var material = Math.Abs(requested - input.ObservedDeviationC) >= input.DeviationStepC - 1e-6;
        var limited = input.LastAcceptedWriteUtc is { } written && input.NowUtc - written < TimeSpan.FromMinutes(30);
        return new(!limited && material, requested, integral, false,
            limited ? "Försiktig reglering väntar på 30-minutersgränsen." : material
                ? bias == 0 ? "Grundkurva med långsam rumskorrigering; inget pris- eller modellbidrag."
                    : "Långsam rumskorrigering med begränsat bidrag från validerad prisplan."
                : "Försiktig reglering: ingen ändring av avvikelsen behövs.");
    }

    private static LwtRegulatorDecision Zero(ConservativeLwtInput input, string reason) =>
        new(!double.IsFinite(input.ObservedDeviationC) || Math.Abs(input.ObservedDeviationC) >= 0.05,
            0, 0, true, reason);

    private static string? InvalidReason(ConservativeLwtInput input)
    {
        if (!input.CommissioningVerified) return "Säker inkoppling och återställning är inte verifierade.";
        if (!input.WriterLeaseHeld) return "Writer-leasen saknas; enbart aktuell leaseägare får utföra nollställningen.";
        if (!input.CommunicationHealthy) return "Kommunikationen är inte verifierad; begär noll och kontrollera återkopplingen.";
        if (input.ManualOverride) return "Manuell override är aktiv; återgå till grundkurvan.";
        if (!string.IsNullOrWhiteSpace(input.SafetyInvalidReason)) return input.SafetyInvalidReason;
        if (input.TelemetryUtc is not { } measured || measured > input.NowUtc || input.NowUtc - measured > TimeSpan.FromMinutes(10))
            return "Ett aktuellt verifierat telemetriunderlag saknas.";
        if (input.PreviousEvaluationUtc > input.NowUtc || input.LastAcceptedWriteUtc > input.NowUtc)
            return "Regulatorns tidsstämplar ligger i framtiden.";
        if (!double.IsFinite(input.ObservedDeviationC) || Math.Abs(input.ObservedDeviationC) > 1 ||
            !double.IsFinite(input.RepresentativeTemperatureErrorC) ||
            !double.IsFinite(input.Integral) || Math.Abs(input.Integral) > IntegralLimit ||
            !double.IsFinite(input.DeviationLimitC) || input.DeviationLimitC is < 0.5 or > 1 ||
            !double.IsFinite(input.DeviationStepC) || input.DeviationStepC is < 0.5 or > 1 ||
            input.DeviationStepC > input.DeviationLimitC ||
            Math.Abs(input.ObservedDeviationC) > input.DeviationLimitC ||
            Math.Abs(input.ObservedDeviationC / input.DeviationStepC - Math.Round(input.ObservedDeviationC / input.DeviationStepC)) > 1e-6 ||
            input.FlowLitresPerMinute is not { } flow || !double.IsFinite(flow) || flow < 0)
            return "Regulatorns säkerhetsunderlag är ogiltigt.";
        return null;
    }
}
