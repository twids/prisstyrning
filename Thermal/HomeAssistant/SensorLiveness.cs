using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed record SensorLivenessEvidence(DateTimeOffset? TimestampUtc, string? Warning);

/// <summary>Explicit, account-scoped liveness for change-only ambient sensors.
/// Never replaces measurement timestamps or invents reports from HTTP receipt.</summary>
public static class SensorLiveness
{
    public static bool Configured(string? entityId, string? attribute) => entityId is not null || attribute is not null;
    public static bool AllowedForRole(string role) => role is "room" or
        ThermalEntityRoles.OutsideTemperature or ThermalEntityRoles.WindSpeed or ThermalEntityRoles.SolarIrradiance;

    public static void Validate(string role, string? entityId, string? attribute)
    {
        if (!Configured(entityId, attribute)) return;
        if (!AllowedForRole(role))
            throw new ArgumentException("Separata livstecken är endast tillåtna för rum och uppmätt väder. Pumpens driftsignaler kräver egna aktuella rapporter.");
        if (entityId is not null && (entityId.Length > 255 || !Regex.IsMatch(entityId, @"^[a-z_]+\.[a-z0-9_]+$")))
            throw new ArgumentException("Välj ett giltigt entity-ID för livstecknet.");
        if (attribute is not null && (attribute.Length > 100 || !Regex.IsMatch(attribute, @"^[a-zA-Z_][a-zA-Z0-9_]*$")))
            throw new ArgumentException("Livstecknets attribut måste vara ett enkelt attributnamn, till exempel last_seen.");
    }

    public static SensorLivenessEvidence? Resolve(
        HomeAssistantState? measurement, string? entityId, string? attribute,
        Func<string, HomeAssistantState?> lookup, DateTimeOffset now, DateTimeOffset? importedAt = null)
    {
        if (!Configured(entityId, attribute)) return null;
        var source = entityId is null ? measurement : lookup(entityId);
        if (source is null || entityId is not null && source.EntityId != entityId)
            return new(null, "Livstecknets källa saknas. Kontrollera den valda givaren och mappningen.");
        var timing = SensorTimestampValidator.Assess(source, now, TimeSpan.MaxValue, importedAt);
        if (timing.Quality != DataQuality.Valid || source.AttributesMalformed ||
            string.IsNullOrWhiteSpace(source.State) || source.State.Trim().ToLowerInvariant() is "unknown" or "unavailable")
            return new(null, "Livstecknets källa eller HA-avläsning kan inte verifieras. Kontrollera anslutning och tidsstämplar.");
        var timestamp = Parse(attribute is null ? JsonValue.Create(source.State) : source.Attributes[attribute]);
        if (timestamp is null)
            return new(null, "Livstecknet måste vara en tidsstämpel med tidszon eller Unix-tid i sekunder/millisekunder. Saknat eller felaktigt livstecken är en varning, inte ett felaktigt temperaturvärde.");
        if (timestamp > now + SensorTimestampValidator.ClockTolerance || timestamp > source.LastUpdatedUtc + SensorTimestampValidator.ClockTolerance ||
            timestamp > source.ReceivedAtUtc + SensorTimestampValidator.ClockTolerance)
            return new(null, "Livstecknet ligger i framtiden eller efter källans uppdatering. Kontrollera klockor och rätt entity.");
        return new(timestamp, null);
    }

    internal static DateTimeOffset? Parse(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        if (text.Length >= 20 && text.Length <= 40 && text[10] == 'T' &&
            (text.EndsWith('Z') || text.Length >= 25 && text[^6] is '+' or '-' && text[^3] == ':') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time.ToUniversalTime();
        // Explicit numeric epochs only. Do not mistake an ordinary sensor value
        // (e.g. 21 degrees) for a timestamp. Both units have disjoint ranges.
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var epoch)) return null;
        if (epoch is >= 946684800 and < 4102444800) return DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000));
        if (epoch is >= 946684800000 and < 4102444800000) return DateTimeOffset.FromUnixTimeMilliseconds((long)epoch);
        return null;
    }
}
