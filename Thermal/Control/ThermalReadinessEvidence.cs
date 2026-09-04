using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.Control;

internal sealed record HeatingDayEvidence(int HeatingDays, int ZeroDeviationDays, int ComfortDays);
internal sealed record ModePeriodEvidence(DateTimeOffset? ShadowSinceUtc, DateTimeOffset? ActiveSinceUtc);

internal static class ThermalReadinessEvidence
{
    internal static ModePeriodEvidence ModePeriods(IEnumerable<ThermalEvent> events, string? currentMode, DateTimeOffset now)
    {
        ControlMode? mode = ControlMode.Legacy;
        DateTimeOffset? shadow = null;
        DateTimeOffset? active = null;
        foreach (var entry in events.Where(x => x.Category == "ControlMode" && x.TimestampUtc <= now).OrderBy(x => x.TimestampUtc))
        {
            var transition = (from before in Enum.GetValues<ControlMode>()
                              from after in Enum.GetValues<ControlMode>()
                              where entry.Message == $"Driftläget ändrades från {before} till {after}."
                              select (Before: before, After: after)).ToArray();
            if (transition.Length != 1 || transition[0].Before != mode)
            {
                mode = null;
                shadow = active = null;
                // An explicit new Legacy -> Shadow is a fresh evidence period.
                if (transition.Length != 1 || transition[0] != (ControlMode.Legacy, ControlMode.Shadow)) continue;
            }
            mode = transition[0].After;
            if (mode == ControlMode.Legacy) shadow = active = null;
            if (mode == ControlMode.Shadow) { shadow = entry.TimestampUtc; active = null; }
            if (mode == ControlMode.LwtActive && transition[0].Before == ControlMode.Shadow) active = entry.TimestampUtc;
        }
        return mode?.ToString() == currentMode && mode is ControlMode.Shadow or ControlMode.LwtActive or ControlMode.FullActive
            ? new(shadow, active) : new(null, null);
    }

    internal static HeatingDayEvidence HeatingDays(
        IEnumerable<ThermalTelemetrySample> samples, IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities, ThermalSiteConfig? site,
        DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is null || since >= now || site is null) return new(0, 0, 0);
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { return new(0, 0, 0); }
        var critical = rooms.Where(x => x.Enabled && x.IsCritical).ToArray();
        var heatingDays = 0;
        var zeroDays = 0;
        var comfortDays = 0;
        foreach (var day in samples.Where(x => x.TimestampUtc >= since && x.TimestampUtc <= now &&
                         x.TimestampUtc.UtcTicks % TimeSpan.FromMinutes(5).Ticks == 0)
                     .GroupBy(x => TimeZoneInfo.ConvertTime(x.TimestampUtc, zone).Date))
        {
            var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Key, DateTimeKind.Unspecified), zone));
            var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Key.AddDays(1), DateTimeKind.Unspecified), zone));
            // Completed civil days only: 276/288/300 five-minute buckets across DST.
            if (start < since || end > now) continue;
            var expected = (end - start).TotalMinutes / 5;
            var distinct = day.GroupBy(x => x.TimestampUtc).Where(x => x.Count() == 1).Select(x => x.Single()).OrderBy(x => x.TimestampUtc).ToArray();
            var valid = distinct.Where(x => ThermalReadinessService.HasRequiredTelemetry(x, rooms, entities)).ToArray();
            if (valid.Length / expected < .98) continue;
            var heating = valid.Where(IsSpaceHeating).ToArray();
            if (!heating.Zip(heating.Skip(1)).Any(pair => pair.Second.TimestampUtc - pair.First.TimestampUtc == TimeSpan.FromMinutes(5))) continue;
            heatingDays++;
            var comfort = critical.Length > 0 && valid.All(x => ComfortMaintained(x, critical, site));
            if (comfort) comfortDays++;
            if (comfort && valid.All(HasMeasuredZeroDeviation)) zeroDays++;
        }
        return new(heatingDays, zeroDays, comfortDays);
    }

    internal static bool IsSpaceHeating(ThermalTelemetrySample sample)
    {
        if (sample.DhwActive != false || sample.DefrostActive != false || sample.HeatOutputKw is not > .5 ||
            !double.IsFinite(sample.HeatOutputKw.Value) || sample.FlowLitresPerMinute is not > 0 ||
            sample.LeavingWaterTemperatureC is null || sample.ReturnWaterTemperatureC is null) return false;
        using var quality = Object(sample.QualityJson);
        var entities = Property(quality?.RootElement ?? default, "entities");
        if (!ValidAssessment(Property(entities, ThermalEntityRoles.DhwActive)) ||
            !ValidAssessment(Property(entities, ThermalEntityRoles.DefrostActive))) return false;
        var derived = sample.FlowLitresPerMinute.Value / 60 * 4.186 * (sample.LeavingWaterTemperatureC.Value - sample.ReturnWaterTemperatureC.Value);
        return double.IsFinite(derived) && derived > .5 && Math.Abs(derived - sample.HeatOutputKw.Value) <= Math.Max(.05, derived * .01);
    }

    private static bool HasMeasuredZeroDeviation(ThermalTelemetrySample sample)
    {
        using var quality = Object(sample.QualityJson);
        var root = quality?.RootElement ?? default;
        return ValidAssessment(Property(Property(root, "entities"), ThermalEntityRoles.HeatingDeviation)) &&
               Number(root, "heatingDeviationC") is >= -.05 and <= .05;
    }

    private static bool ComfortMaintained(ThermalTelemetrySample sample, IEnumerable<ThermalRoomConfig> rooms, ThermalSiteConfig site)
    {
        using var temperatures = Object(sample.RoomTemperaturesJson);
        return rooms.All(room => Number(temperatures?.RootElement ?? default, room.EntityId) is { } value &&
            double.IsFinite(site.BaseRoomTargetC) && double.IsFinite(room.TargetOffsetC) &&
            double.IsFinite(site.LowerComfortBandC) && site.LowerComfortBandC >= 0 &&
            value >= site.BaseRoomTargetC + room.TargetOffsetC - site.LowerComfortBandC);
    }

    internal static double ForecastHours(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return 0;
            var points = new List<DateTimeOffset>();
            foreach (var point in document.RootElement.EnumerateArray())
            {
                var timestamp = Property(point, "timestampUtc");
                if (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var time) ||
                    time == default || Number(point, "temperatureC") is not (>= -80 and <= 60)) return 0;
                var raw = timestamp.GetString()!;
                if (!raw.EndsWith('Z') && (raw.Length < 6 || raw[^6] is not ('+' or '-') || raw[^3] != ':')) return 0;
                points.Add(time.ToUniversalTime());
            }
            var ordered = points.OrderBy(x => x).ToArray();
            var anchor = Array.FindLastIndex(ordered, x => x <= now);
            if (anchor < 0 || now - ordered[anchor] > TimeSpan.FromHours(1)) return 0;
            var covered = ordered[anchor];
            for (var index = anchor + 1; index < ordered.Length; index++)
            {
                var gap = ordered[index] - covered;
                if (gap <= TimeSpan.Zero || gap > TimeSpan.FromHours(1)) break;
                covered = ordered[index];
            }
            return Math.Max(0, (covered - now).TotalHours);
        }
        catch (JsonException) { return 0; }
    }

    internal static bool HasValidForecastQuality(ThermalTelemetrySample? sample)
    {
        using var quality = Object(sample?.QualityJson);
        var root = quality?.RootElement ?? default;
        var status = Property(Property(root, "forecast"), "quality");
        return Property(root, "source").ValueKind == JsonValueKind.Undefined &&
               (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var number) && number == 0 ||
                status.ValueKind == JsonValueKind.String && string.Equals(status.GetString(), "Valid", StringComparison.OrdinalIgnoreCase));
    }
}
