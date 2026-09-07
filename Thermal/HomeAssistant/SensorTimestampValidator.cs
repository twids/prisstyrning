using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

internal sealed record SensorTimestampAssessment(DataQuality Quality, string? Reason, bool ReportAgeOnly = false);

internal static class SensorTimestampValidator
{
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);

    // History is assessed at its bucket time, but its HTTP receipt happens at
    // import time. Never invent a measurement time from that later receipt.
    internal static SensorTimestampAssessment Assess(
        HomeAssistantState? state,
        DateTimeOffset nowUtc,
        TimeSpan staleAfter,
        DateTimeOffset? historyImportedAtUtc = null,
        SensorLivenessEvidence? liveness = null)
    {
        if (state?.LastUpdatedUtc is not { } updated || updated == default || state.ReceivedAtUtc == default)
            return new(DataQuality.Unavailable, "Uppdaterings- eller mottagningstid saknas; givarens ålder kan inte verifieras.");
        var received = state.ReceivedAtUtc;
        if (state.ReportTimestampMalformed)
            return new(DataQuality.Invalid, "Givarens rapporteringstid har ett ogiltigt format.");
        var reported = historyImportedAtUtc is null ? state.LastReportedUtc ?? updated : updated;
        if (updated - nowUtc > ClockTolerance || received - (historyImportedAtUtc ?? nowUtc) > ClockTolerance ||
            updated - received > ClockTolerance || state.LastChangedUtc - updated > ClockTolerance ||
            reported == default || reported - nowUtc > ClockTolerance || reported - received > ClockTolerance ||
            updated - reported > ClockTolerance)
            return new(DataQuality.Invalid, "Givarens tidsstämplar är motsägelsefulla eller ligger i framtiden. Kontrollera klockorna.");
        if (historyImportedAtUtc is null && nowUtc - received > SensorFreshnessPolicy.CommunicationTimeout)
            return new(DataQuality.Stale, "Ingen aktuell avläsning från Home Assistant på tio minuter. Kontrollera anslutningen.");
        if (liveness is not null)
        {
            if (liveness.Warning is not null || liveness.TimestampUtc is not { } heartbeat)
                return new(DataQuality.Stale, liveness.Warning ?? "Livstecknets tid saknas.");
            if (heartbeat > nowUtc + ClockTolerance || heartbeat > received + ClockTolerance)
                return new(DataQuality.Stale, "Livstecknets tid kan inte verifieras.");
            if (nowUtc - heartbeat > staleAfter)
                return new(DataQuality.Stale, $"Inget verifierat livstecken inom {staleAfter.TotalMinutes:0} minuter. Värdet kan vara oförändrat; kontrollera givaren.");
            return new(DataQuality.Valid, "Oförändrat värde med aktuellt, uttryckligt valt livstecken. Livstecknet är inte en ny temperaturmätning.");
        }
        if (nowUtc - reported > staleAfter)
            return new(DataQuality.Stale, $"Ingen ny rapport inom {staleAfter.TotalMinutes:0} minuter. Värdet kan vara oförändrat; mätningens aktualitet är osäker. Kontrollera givarens rapportintervall.",
                ReportAgeOnly: historyImportedAtUtc is null);
        return new(DataQuality.Valid, nowUtc - updated > staleAfter
            ? "Oförändrat värde med aktuell rapportering från HA-integrationen." : null);
    }
}
