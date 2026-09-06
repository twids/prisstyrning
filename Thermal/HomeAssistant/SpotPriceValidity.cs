using System.Globalization;
using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.HomeAssistant;

internal static class SpotPriceValidity
{
    // Corroborate the value against a real UTC quarter, not its last change.
    // Never replace an HA price that might include different taxes/fees silently.
    internal static SensorAssessment Assess(SensorAssessment assessment, HomeAssistantState? raw,
        PriceSnapshot? snapshot, DateTimeOffset now, DateTimeOffset? importedAtUtc = null)
    {
        if (assessment.Excluded || assessment.Quality is not (DataQuality.Valid or DataQuality.Stale) ||
            assessment.Value is not { } price || snapshot is null || snapshot.SavedAtUtc == default ||
            snapshot.SavedAtUtc > now.AddMinutes(2) || now - snapshot.SavedAtUtc > TimeSpan.FromHours(36) ||
            SensorTimestampValidator.Assess(raw, now, TimeSpan.MaxValue, importedAtUtc).Quality != DataQuality.Valid)
            return assessment;
        var normalized = SensorValueNormalizer.Normalize(raw, "SEK/kWh");
        if (normalized.Quality != DataQuality.Valid || normalized.Value != assessment.Value) return assessment;
        try
        {
            var points = new Dictionary<DateTimeOffset, double>();
            foreach (var json in new[] { snapshot.TodayPricesJson, snapshot.TomorrowPricesJson })
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return assessment;
                foreach (var point in document.RootElement.EnumerateArray())
                {
                    var text = Property(point, "start");
                    if (text.ValueKind != JsonValueKind.String || Number(point, "value") is not { } value || value is < -1000 or > 1000)
                        return assessment;
                    var timestamp = text.GetString()!;
                    if (!(timestamp.EndsWith('Z') || timestamp.Length >= 6 && timestamp[^6] is '+' or '-' && timestamp[^3] == ':') ||
                        !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                        start == default || start.UtcTicks % TimeSpan.FromMinutes(15).Ticks != 0 || !points.TryAdd(start, value))
                        return assessment;
                }
            }
            var utc = now.ToUniversalTime();
            var quarter = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, TimeSpan.Zero);
            if (!points.TryGetValue(quarter, out var confirmed)) return assessment;
            return Math.Abs(confirmed - price) <= .0005
                ? assessment with { Quality = DataQuality.Valid, Reason = $"Priset är verifierat mot prisunderlag #{snapshot.Id} för aktuell kvart {quarter:HH:mm}–{quarter.AddMinutes(15):HH:mm} UTC. Oförändrat pris är normalt." }
                : assessment with { Quality = DataQuality.Stale, Reason = "HA-priset skiljer sig från aktuell kvart i kontots prisunderlag. Kontrollera prisentity, elområde och eventuella påslag." };
        }
        catch (JsonException) { return assessment; }
    }
}
