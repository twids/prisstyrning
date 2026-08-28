using System.Globalization;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public static class SensorValueNormalizer
{
    private static readonly HashSet<string> TrueValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "true", "heating", "active", "1"
    };

    private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "false", "idle", "inactive", "0"
    };

    public static NormalizedSensorValue Normalize(HomeAssistantState? state, string expectedUnit)
    {
        if (state is null)
        {
            return new(null, null, expectedUnit, DataQuality.Unavailable, "Entity saknas i Home Assistant.");
        }

        if (state.State.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            state.State.Equals("unavailable", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(state.State))
        {
            return new(null, null, expectedUnit, DataQuality.Unavailable, $"Entity rapporterar {state.State}.");
        }

        if (IsBooleanUnit(expectedUnit))
        {
            if (TrueValues.Contains(state.State)) return new(null, true, "bool", DataQuality.Valid, null);
            if (FalseValues.Contains(state.State)) return new(null, false, "bool", DataQuality.Valid, null);
            return new(null, null, "bool", DataQuality.Invalid, "Värdet kan inte tolkas som av/på.");
        }

        if (!double.TryParse(state.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawValue))
        {
            return new(null, null, expectedUnit, DataQuality.Invalid, "Värdet är inte numeriskt.");
        }

        var sourceUnit = NormalizeUnit(state.Unit);
        var targetUnit = NormalizeUnit(expectedUnit);
        var converted = Convert(rawValue, sourceUnit, targetUnit);
        return converted is null
            ? new(null, null, expectedUnit, DataQuality.Invalid, $"Enheten {state.Unit ?? "(saknas)"} kan inte konverteras till {expectedUnit}.")
            : new(converted, null, CanonicalUnit(targetUnit), DataQuality.Valid, null);
    }

    private static double? Convert(double value, string source, string target)
    {
        if (source == target) return value;
        if (string.IsNullOrEmpty(source)) return null;
        return (source, target) switch
        {
            ("f", "c") => (value - 32) * 5 / 9,
            ("k", "c") => value - 273.15,
            ("w", "kw") => value / 1000,
            ("mw", "kw") => value * 1000,
            ("m3/h", "l/min") => value * 1000 / 60,
            ("l/s", "l/min") => value * 60,
            ("ore/kwh", "sek/kwh") => value / 100,
            ("sek/mwh", "sek/kwh") => value / 1000,
            ("km/h", "m/s") => value / 3.6,
            ("mph", "m/s") => value * 0.44704,
            _ => null
        };
    }

    private static bool IsBooleanUnit(string unit) =>
        unit.Equals("bool", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("boolean", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUnit(string? unit) => unit?.Trim().ToLowerInvariant() switch
    {
        "°c" or "c" or "celsius" => "c",
        "°f" or "f" or "fahrenheit" => "f",
        "kelvin" or "k" => "k",
        "w" => "w",
        "kw" => "kw",
        "mw" => "mw",
        "l/min" or "lpm" => "l/min",
        "l/s" => "l/s",
        "m³/h" or "m3/h" => "m3/h",
        "kwh" => "kwh",
        "öre/kwh" or "ore/kwh" => "ore/kwh",
        "sek/kwh" => "sek/kwh",
        "sek/mwh" => "sek/mwh",
        "m/s" or "mps" => "m/s",
        "km/h" or "kmh" => "km/h",
        "mph" => "mph",
        "w/m²" or "w/m2" => "w/m2",
        "bool" or "boolean" => "bool",
        null => string.Empty,
        var other => other
    };

    private static string CanonicalUnit(string unit) => unit switch
    {
        "c" => "°C",
        "kw" => "kW",
        "l/min" => "l/min",
        "kwh" => "kWh",
        "sek/kwh" => "SEK/kWh",
        "m/s" => "m/s",
        "w/m2" => "W/m²",
        _ => unit
    };
}
