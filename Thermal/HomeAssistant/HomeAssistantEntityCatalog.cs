using System.Globalization;
using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

/// <summary>
/// Read-only, preliminary checks for the settings picker. Never calls the stateful
/// SensorQualityTracker: browsing settings must not exclude or recover a sensor.
/// Compatibility is not a range, trend, comfort, or readiness assessment.
/// </summary>
internal static class HomeAssistantEntityCatalog
{
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(30);
    private static readonly string[] NumericUnits = ["°C", "kW", "l/min", "kWh", "SEK/kWh", "m/s", "W/m²"];

    public static ThermalEntityStateDto Project(
        HomeAssistantState state,
        DateTimeOffset nowUtc,
        int staleAfterMinutes,
        string? connectionIssue = null)
    {
        var name = StringAttribute(state.Attributes, "friendly_name");
        var unit = StringAttribute(state.Attributes, "unit_of_measurement");
        var staleAfter = TimeSpan.FromMinutes(Math.Clamp(staleAfterMinutes, 1, 60));
        var updated = state.LastUpdatedUtc;
        var received = state.ReceivedAtUtc;
        var value = state.State.Trim();
        DateTimeOffset? validUntil = null;

        ThermalEntityStateDto Result(DataQuality quality, string? reason, IReadOnlyList<string>? units = null) => new(
            state.EntityId, string.IsNullOrWhiteSpace(name) ? state.EntityId : name,
            state.State, unit, updated, received, quality, reason,
            units ?? [], nowUtc, validUntil);

        if (connectionIssue is not null) return Result(DataQuality.Unavailable, connectionIssue);
        if (value.Length == 0 || value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
            return Result(DataQuality.Unavailable, "Home Assistant saknar ett tillgängligt värde för denna entity.");
        if (state.AttributesMalformed || state.Attributes["unit_of_measurement"] is not null && unit is null)
            return Result(DataQuality.Invalid, "Enheten har ett felaktigt format i Home Assistant.");
        if (updated is null || received == default)
            return Result(DataQuality.Unavailable, "Uppdaterings- eller mottagningstid saknas; värdets ålder kan inte verifieras.");
        if (updated - nowUtc > ClockTolerance || received - nowUtc > ClockTolerance || updated - received > ClockTolerance)
            return Result(DataQuality.Invalid, "Tidsstämplarna är motsägelsefulla eller ligger i framtiden. Kontrollera klockorna.");
        validUntil = (updated.Value < received ? updated.Value : received).Add(staleAfter);
        if (nowUtc - updated > staleAfter || nowUtc - received > staleAfter)
            return Result(DataQuality.Stale, $"Värdet är äldre än kontots gräns på {Math.Clamp(staleAfterMinutes, 1, 60)} minuter.");
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsFinite(number))
            return Result(DataQuality.Invalid, "Värdet är inte ett ändligt tal.");

        // Reuse exactly the collector's unit conversions, but not its health counters.
        var compatibleUnits = new List<string>();
        foreach (var expected in NumericUnits)
        {
            var normalized = SensorValueNormalizer.Normalize(state, expected);
            if (normalized.Quality == DataQuality.Valid && normalized.Value is { } numeric && double.IsFinite(numeric))
                compatibleUnits.Add(expected);
        }
        // A temperature of 0/1 must not masquerade as a boolean operating signal.
        if ((string.IsNullOrWhiteSpace(unit) || unit.Trim().Equals("bool", StringComparison.OrdinalIgnoreCase) ||
             unit.Trim().Equals("boolean", StringComparison.OrdinalIgnoreCase)) &&
            SensorValueNormalizer.Normalize(state, "bool") is { Quality: DataQuality.Valid, BooleanValue: not null })
            compatibleUnits.Add("bool");

        if (HasSupportedForecastUnits(state.Attributes) &&
            state.Attributes["forecast"] is JsonArray &&
            HomeAssistantWeatherForecastParser.Parse(state, nowUtc) is { Quality: DataQuality.Valid } forecast &&
            forecast.Points.Count(point => point.TimestampUtc >= nowUtc) >= 2)
            compatibleUnits.Add("forecast");

        return Result(DataQuality.Valid, null, compatibleUnits);
    }

    private static string? StringAttribute(JsonObject attributes, string key) =>
        attributes[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool HasSupportedForecastUnits(JsonObject attributes)
    {
        // The existing parser supplies defaults for absent units,
        // but unknown or malformed units must not receive a positive picker result.
        var temperature = StringAttribute(attributes, "temperature_unit")?.Trim().ToLowerInvariant();
        var wind = StringAttribute(attributes, "wind_speed_unit")?.Trim().ToLowerInvariant();
        return (attributes["temperature_unit"] is null || temperature is "°c" or "c" or "celsius" or "°f" or "f" or "fahrenheit" or "k" or "kelvin") &&
               (attributes["wind_speed_unit"] is null || wind is "m/s" or "mps" or "km/h" or "kmh" or "mph");
    }
}
