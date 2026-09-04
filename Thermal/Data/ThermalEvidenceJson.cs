using System.Text.Json;

namespace Prisstyrning.Thermal.Data;

internal static class ThermalEvidenceJson
{
    internal static JsonDocument? Object(string? json)
    {
        try
        {
            var document = JsonDocument.Parse(json ?? "null");
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == document.RootElement.EnumerateObject().Count())
                return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    internal static JsonElement Property(JsonElement parent, string name)
    {
        var result = default(JsonElement);
        if (parent.ValueKind == JsonValueKind.Object)
            foreach (var property in parent.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (result.ValueKind != JsonValueKind.Undefined) return default;
                    result = property.Value;
                }
        return result;
    }

    internal static double? Number(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    }

    internal static int? Count(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : null;
    }

    internal static bool ValidAssessment(JsonElement parent)
    {
        var quality = Property(parent, "quality");
        return Property(parent, "excluded").ValueKind == JsonValueKind.False &&
               (quality.ValueKind == JsonValueKind.Number && quality.TryGetInt32(out var number) && number == 0 ||
                quality.ValueKind == JsonValueKind.String && string.Equals(quality.GetString(), "Valid", StringComparison.OrdinalIgnoreCase));
    }
}
