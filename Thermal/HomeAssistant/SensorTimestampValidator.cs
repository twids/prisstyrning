using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

internal static class SensorTimestampValidator
{
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);

    // History is assessed at its bucket time, but its HTTP receipt happens at
    // import time. Never invent a measurement time from that later receipt.
    internal static (DataQuality Quality, string? Reason) Assess(
        HomeAssistantState? state,
        DateTimeOffset nowUtc,
        TimeSpan staleAfter,
        DateTimeOffset? historyImportedAtUtc = null)
    {
        if (state?.LastUpdatedUtc is not { } updated || updated == default || state.ReceivedAtUtc == default)
            return (DataQuality.Unavailable, "Uppdaterings- eller mottagningstid saknas; givarens ålder kan inte verifieras.");
        var received = state.ReceivedAtUtc;
        if (state.ReportTimestampMalformed)
            return (DataQuality.Invalid, "Givarens rapporteringstid har ett ogiltigt format.");
        var reported = historyImportedAtUtc is null ? state.LastReportedUtc ?? updated : updated;
        if (updated - nowUtc > ClockTolerance || received - (historyImportedAtUtc ?? nowUtc) > ClockTolerance ||
            updated - received > ClockTolerance || state.LastChangedUtc - updated > ClockTolerance ||
            reported == default || reported - nowUtc > ClockTolerance || reported - received > ClockTolerance ||
            updated - reported > ClockTolerance)
            return (DataQuality.Invalid, "Givarens tidsstämplar är motsägelsefulla eller ligger i framtiden. Kontrollera klockorna.");
        if (nowUtc - reported > staleAfter || historyImportedAtUtc is null && nowUtc - received > staleAfter)
            return (DataQuality.Stale, $"Givarens värde är äldre än gränsen på {staleAfter.TotalMinutes:0} minuter.");
        return (DataQuality.Valid, nowUtc - updated > staleAfter
            ? "Oförändrat värde med aktuell rapportering från HA-integrationen." : null);
    }
}
