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

        var text = state.State.Trim();
        if (text.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("unavailable", StringComparison.OrdinalIgnoreCase) || text.Length == 0)
        {
            return new(null, null, expectedUnit, DataQuality.Unavailable, "Home Assistant saknar ett tillgängligt värde.");
        }

        if (state.AttributesMalformed || state.Attributes["unit_of_measurement"] is not null && state.Unit is null)
            return new(null, null, expectedUnit, DataQuality.Invalid, "Givarens attribut eller enhet har ett felaktigt format.");

        if (IsBooleanUnit(expectedUnit))
        {
            if (!string.IsNullOrWhiteSpace(state.Unit) && !IsBooleanUnit(state.Unit.Trim()))
                return new(null, null, "bool", DataQuality.Invalid, "En givare med fysisk enhet kan inte användas som av/på-signal.");
            if (TrueValues.Contains(state.State)) return new(null, true, "bool", DataQuality.Valid, null);
            if (FalseValues.Contains(state.State)) return new(null, false, "bool", DataQuality.Valid, null);
            return new(null, null, "bool", DataQuality.Invalid, "Värdet kan inte tolkas som av/på.");
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawValue))
        {
            return new(null, null, expectedUnit, DataQuality.Invalid, "Värdet är inte numeriskt.");
        }
        if (!double.IsFinite(rawValue))
            return new(null, null, expectedUnit, DataQuality.Invalid, "Värdet är inte ett ändligt tal.");

        var sourceUnit = NormalizeUnit(state.Unit);
        var targetUnit = NormalizeUnit(expectedUnit);
        var converted = Convert(rawValue, sourceUnit, targetUnit);
        if (converted is null)
            return new(null, null, expectedUnit, DataQuality.Invalid, "Givarens enhet saknas eller kan inte konverteras till den konfigurerade enheten.");
        if (!double.IsFinite(converted.Value))
            return new(null, null, expectedUnit, DataQuality.Invalid, "Enhetskonverteringen ger ett för stort värde.");
        if (targetUnit == "sek/kwh" && ToDecimal(converted) is null)
            return new(null, null, expectedUnit, DataQuality.Invalid, "Priset är för stort för att lagras säkert.");
        return new(converted, null, CanonicalUnit(targetUnit), DataQuality.Valid, null);
    }

    internal static decimal? ToDecimal(double? value)
    {
        if (value is null || !double.IsFinite(value.Value)) return null;
        try { return (decimal)value.Value; }
        catch (OverflowException) { return null; }
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
