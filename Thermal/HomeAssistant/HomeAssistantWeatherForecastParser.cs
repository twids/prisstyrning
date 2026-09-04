using System.Globalization;
using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed record NormalizedWeatherForecast(
    IReadOnlyList<WeatherForecastPoint> Points,
    DataQuality Quality,
    string? Reason);

public static class HomeAssistantWeatherForecastParser
{
    public static NormalizedWeatherForecast Parse(HomeAssistantState? state, DateTimeOffset nowUtc)
    {
        if (state is null) return new([], DataQuality.Unavailable, "Väderentity saknas.");
        if (state.State.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            state.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
            return new([], DataQuality.Unavailable, $"Väderentity rapporterar {state.State}.");
        var timestamps = SensorTimestampValidator.Assess(state, nowUtc, TimeSpan.FromHours(3));
        if (timestamps.Quality != DataQuality.Valid) return new([], timestamps.Quality, timestamps.Reason);
        if (state.AttributesMalformed ||
            state.Attributes["temperature_unit"] is not null && state.StringAttribute("temperature_unit") is null ||
            state.Attributes["wind_speed_unit"] is not null && state.StringAttribute("wind_speed_unit") is null)
            return new([], DataQuality.Invalid, "Väderprognosens enheter har ett felaktigt format.");
        if (state.Attributes["forecast"] is not JsonArray forecast)
            return new([], DataQuality.Unavailable, "Väderentity saknar attributet forecast; exponera en HA-templateentity med timprognosen.");

        var temperatureUnit = (state.StringAttribute("temperature_unit") ?? "°C").Trim().ToLowerInvariant();
        var windUnit = (state.StringAttribute("wind_speed_unit") ?? "m/s").Trim().ToLowerInvariant();
        if (temperatureUnit is not ("°c" or "c" or "celsius" or "°f" or "f" or "fahrenheit" or "k" or "kelvin") ||
            windUnit is not ("m/s" or "mps" or "km/h" or "kmh" or "mph"))
            return new([], DataQuality.Invalid, "Väderprognosens temperatur- eller vindenhet stöds inte.");
        var points = new List<WeatherForecastPoint>();
        foreach (var node in forecast.OfType<JsonObject>())
        {
            var timestampText = node["datetime"]?.ToString() ?? node["time"]?.ToString() ?? node["start"]?.ToString();
            if (!DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
                !TryDouble(node["temperature"] ?? node["temp"], out var temperature))
                continue;
            var temperatureC = temperatureUnit.Trim().ToLowerInvariant() switch
            {
                "°f" or "f" or "fahrenheit" => (temperature - 32) * 5 / 9,
                "k" or "kelvin" => temperature - 273.15,
                _ => temperature
            };
            double? wind = TryDouble(node["wind_speed"], out var parsedWind)
                ? windUnit.Trim().ToLowerInvariant() switch
                {
                    "km/h" or "kmh" => parsedWind / 3.6,
                    "mph" => parsedWind * 0.44704,
                    _ => parsedWind
                }
                : null;
            double? solar = TryDouble(node["solar_irradiance"] ?? node["solar_radiation"], out var parsedSolar)
                ? parsedSolar
                : null;
            var utc = timestamp.ToUniversalTime();
            if (utc >= nowUtc.AddHours(-3) && utc <= nowUtc.AddDays(7) && temperatureC is > -80 and < 60)
                points.Add(new WeatherForecastPoint(utc, temperatureC, wind, solar));
        }

        var normalized = points
            .GroupBy(x => x.TimestampUtc)
            .Select(x => x.Last())
            .OrderBy(x => x.TimestampUtc)
            .ToArray();
        return normalized.Length >= 2
            ? new(normalized, DataQuality.Valid, null)
            : new([], DataQuality.Invalid, "Väderprognosen innehåller färre än två giltiga tidpunkter.");
    }

    private static bool TryDouble(JsonNode? node, out double value) =>
        double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);
}
