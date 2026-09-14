using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.HomeAssistant;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.Jobs;

// Reads only the explicit live-collector marker. Never reinterprets historical
// stale rows, generic HTTP success, or values rejected by range/unit checks.
internal static class ShadowPhysicalStateData
{
    internal static double? Read(ThermalTelemetrySample sample, string role)
    {
        using var document = Object(sample.QualityJson);
        if (document is null) return null;
        var root = document.RootElement;
        if (Property(root, "source").ValueKind != JsonValueKind.Undefined ||
            Timestamp(root, "collectedAtUtc") is not { } collected || collected < sample.TimestampUtc ||
            collected - sample.TimestampUtc >= TimeSpan.FromMinutes(5)) return null;
        var item = Property(Property(root, "entities"), role);
        var quality = Property(item, "quality");
        var stale = quality.ValueKind == JsonValueKind.Number && quality.TryGetInt32(out var code) && code == 1 ||
            quality.ValueKind == JsonValueKind.String && quality.GetString() == "Stale";
        if (!stale || Property(item, "excluded").ValueKind != JsonValueKind.False ||
            Property(item, "usage").ToString() != "AssumedUnchanged" ||
            Timestamp(item, "receivedAtUtc") is not { } received ||
            received > collected + SensorTimestampValidator.ClockTolerance ||
            collected - received > SensorFreshnessPolicy.CommunicationTimeout ||
            Timestamp(item, "sourceTimestampUtc") is not { } source ||
            Timestamp(item, "valueUpdatedUtc") is not { } updated ||
            updated > source + SensorTimestampValidator.ClockTolerance ||
            source > received + SensorTimestampValidator.ClockTolerance) return null;
        return Number(item, "value");
    }

    private static DateTimeOffset? Timestamp(JsonElement parent, string name) =>
        Property(parent, name) is var item && item.ValueKind == JsonValueKind.String &&
        item.TryGetDateTimeOffset(out var time) && time != default ? time : null;
}
