using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Control;

internal sealed record ThermalControlTelemetryAssessment(
    bool SafeToControl,
    double RepresentativeTemperatureErrorC,
    bool CriticalRoomBelowMinimum,
    bool DhwActive,
    bool DefrostActive,
    double? FlowLitresPerMinute,
    string? InvalidReason);

internal static class ThermalControlTelemetry
{
    private static readonly string[] RequiredSignalRoles =
        [ThermalEntityRoles.Flow, ThermalEntityRoles.DhwActive, ThermalEntityRoles.DefrostActive];

    internal static ThermalControlTelemetryAssessment Assess(
        ThermalTelemetrySample? sample,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities,
        ThermalSiteConfig? site,
        DateTimeOffset now)
    {
        const string invalidReason = "Aktuella rum-, flödes-, DHW- eller avfrostningsdata kan inte verifieras; LWT återgår säkert till noll.";
        if (sample is null || site is null ||
            !double.IsFinite(site.BaseRoomTargetC) || !double.IsFinite(site.LowerComfortBandC) || site.LowerComfortBandC < 0)
            return Invalid(invalidReason);

        var weightedRooms = rooms.Where(x => x.Enabled && x.Weight > 0).ToArray();
        if (weightedRooms.Length == 0 || weightedRooms.Any(x =>
                !double.IsFinite(x.Weight) || !double.IsFinite(x.TargetOffsetC) ||
                !double.IsFinite(x.MinimumValidC) || !double.IsFinite(x.MaximumValidC) ||
                x.MinimumValidC >= x.MaximumValidC))
            return Invalid(invalidReason);

        var requiredSignals = RequiredSignalRoles.Select(role => entities.FirstOrDefault(x =>
            x.Enabled && x.Role.Equals(role, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (requiredSignals.Any(x => x is null) ||
            ThermalStatusQuality.Assess(sample, [], requiredSignals.Select(x => x!), now, site.UpdatedAtUtc).Quality != DataQuality.Valid)
            return Invalid(invalidReason);

        var temperatures = ParseTemperatures(sample.RoomTemperaturesJson);
        if (temperatures is null) return Invalid(invalidReason);

        var validRooms = new List<(ThermalRoomConfig Room, double Temperature)>();
        foreach (var room in weightedRooms)
        {
            var quality = ThermalStatusQuality.Assess(sample, [room], [], now, site.UpdatedAtUtc).Quality;
            if (quality != DataQuality.Valid) continue;
            if (!temperatures.TryGetValue(room.EntityId, out var temperature) || !double.IsFinite(temperature))
                return Invalid(invalidReason);
            validRooms.Add((room, temperature));
        }

        if (validRooms.Count == 0) return Invalid(invalidReason);
        var totalWeight = validRooms.Sum(x => x.Room.Weight);
        if (!double.IsFinite(totalWeight) || totalWeight <= 0) return Invalid(invalidReason);
        var representativeError = validRooms.Sum(x =>
            (x.Temperature - (site.BaseRoomTargetC + x.Room.TargetOffsetC)) * x.Room.Weight) / totalWeight;
        if (!double.IsFinite(representativeError)) return Invalid(invalidReason);
        var criticalBelow = validRooms.Any(x => x.Room.IsCritical &&
            x.Temperature < site.BaseRoomTargetC + x.Room.TargetOffsetC - site.LowerComfortBandC);

        return new(
            true,
            representativeError,
            criticalBelow,
            sample.DhwActive!.Value,
            sample.DefrostActive!.Value,
            sample.FlowLitresPerMinute,
            null);
    }

    private static Dictionary<string, double>? ParseTemperatures(string? json)
    {
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, double>>(json ?? "null");
            return values is null ? null : new Dictionary<string, double>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static ThermalControlTelemetryAssessment Invalid(string reason) =>
        new(false, 0, false, false, false, null, reason);
}
