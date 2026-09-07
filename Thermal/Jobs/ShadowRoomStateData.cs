using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.HomeAssistant;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.Jobs;

internal sealed record ShadowRoomState(double Value, bool Assumed, DateTimeOffset? SourceTimestampUtc);

// Deliberately separate from ThermalModelTrainingData.ReadRooms: the latter is
// also used for heat-response training and must never admit assumed room states.
internal static class ShadowRoomStateData
{
    internal static Dictionary<string, ShadowRoomState> Read(ThermalTelemetrySample sample)
    {
        using var quality = Object(sample.QualityJson);
        if (quality is null) return [];
        var root = quality.RootElement;
        var assessments = Property(root, "rooms");
        if (assessments.ValueKind != JsonValueKind.Object) return [];
        var result = ThermalModelTrainingData.ReadRooms(sample).ToDictionary(x => x.Key,
            x => new ShadowRoomState(x.Value, false, SourceTime(Property(assessments, x.Key), sample.TimestampUtc)),
            StringComparer.OrdinalIgnoreCase);

        // Do not reinterpret historical HeldRoom/unmarked stale/fallback values.
        // Only the live collector can issue the new explicit usage marker, after
        // validating units, ranges, rate, communication and exclusion state.
        if (Property(root, "source").ValueKind != JsonValueKind.Undefined ||
            Timestamp(root, "collectedAtUtc") is not { } collected || collected < sample.TimestampUtc ||
            collected - sample.TimestampUtc >= TimeSpan.FromMinutes(5)) return result;
        foreach (var entry in assessments.EnumerateObject())
        {
            var assessment = Property(assessments, entry.Name);
            var status = Property(assessment, "quality");
            var stale = status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 1 ||
                status.ValueKind == JsonValueKind.String && status.GetString() == "Stale";
            if (!stale || Property(assessment, "excluded").ValueKind != JsonValueKind.False ||
                Property(assessment, "usage").ToString() != "AssumedUnchanged" ||
                Number(assessment, "value") is not { } value ||
                Timestamp(assessment, "receivedAtUtc") is not { } received ||
                received > collected + SensorTimestampValidator.ClockTolerance ||
                collected - received > SensorFreshnessPolicy.CommunicationTimeout ||
                Timestamp(assessment, "valueUpdatedUtc") is not { } updated ||
                SourceTime(assessment, collected) is not { } reported ||
                updated > reported + SensorTimestampValidator.ClockTolerance ||
                reported > received + SensorTimestampValidator.ClockTolerance) continue;
            result[entry.Name] = new(value, true, reported);
        }
        return result;
    }

    private static DateTimeOffset? SourceTime(JsonElement assessment, DateTimeOffset at)
    {
        var timestamp = Timestamp(assessment, "sourceTimestampUtc");
        // Missing provenance may still supply a valid initial baseline, but is
        // never replaced by the collection time to manufacture new observations.
        return timestamp <= at + TimeSpan.FromMinutes(5) ? timestamp : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement parent, string name) =>
        Property(parent, name) is var value && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var timestamp) && timestamp != default ? timestamp : null;
}
