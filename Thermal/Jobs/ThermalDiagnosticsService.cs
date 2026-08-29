using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Jobs;

internal sealed record ThermalDiagnosticFinding(
    string Code,
    string Severity,
    string Category,
    string Message,
    string? EntityId = null);

/// <summary>
/// Detects sustained room-balance and hydronic faults. The deliberately long
/// windows avoid turning individual noisy readings into operator alarms.
/// </summary>
internal sealed class ThermalDiagnosticsService
{
    private readonly PrisstyrningDbContext _db;

    public ThermalDiagnosticsService(PrisstyrningDbContext db) => _db = db;

    public async Task EvaluateAsync(
        string userId,
        ThermalTelemetrySample current,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        ThermalSiteConfig site,
        CancellationToken cancellationToken)
    {
        var from = current.TimestampUtc.AddHours(-6);
        var history = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= from && x.TimestampUtc < current.TimestampUtc)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
        history.Add(current);

        var findings = Analyze(history, rooms, site);
        if (findings.Count == 0) return;

        var dedupeFrom = current.TimestampUtc.AddHours(-12);
        var recentDetails = await _db.ThermalEvents.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= dedupeFrom &&
                        (x.Category == "RoomBalance" || x.Category == "Hydraulics"))
            .Select(x => x.DetailsJson)
            .ToListAsync(cancellationToken);

        foreach (var finding in findings.Where(finding =>
                     !recentDetails.Any(details => ContainsCode(details, finding.Code))))
        {
            _db.ThermalEvents.Add(new ThermalEvent
            {
                UserId = userId,
                TimestampUtc = current.TimestampUtc,
                Severity = finding.Severity,
                Category = finding.Category,
                Message = finding.Message,
                DetailsJson = JsonSerializer.Serialize(new { code = finding.Code, entityId = finding.EntityId })
            });
        }
    }

    internal static IReadOnlyList<ThermalDiagnosticFinding> Analyze(
        IReadOnlyCollection<ThermalTelemetrySample> samples,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        ThermalSiteConfig site)
    {
        if (samples.Count == 0) return [];
        var ordered = samples.OrderBy(x => x.TimestampUtc).ToArray();
        var now = ordered[^1].TimestampUtc;
        var findings = new List<ThermalDiagnosticFinding>();
        findings.AddRange(AnalyzeRooms(ordered.Where(x => x.TimestampUtc >= now.AddHours(-6)).ToArray(), rooms, site));
        findings.AddRange(AnalyzeHydraulics(ordered.Where(x => x.TimestampUtc >= now.AddMinutes(-30)).ToArray()));
        return findings;
    }

    private static IEnumerable<ThermalDiagnosticFinding> AnalyzeRooms(
        IReadOnlyList<ThermalTelemetrySample> samples,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        ThermalSiteConfig site)
    {
        var enabled = rooms.Where(x => x.Enabled).ToArray();
        if (enabled.Length < 2 || samples.Count < 70 ||
            samples[^1].TimestampUtc - samples[0].TimestampUtc < TimeSpan.FromHours(5.9) ||
            HasGap(samples, TimeSpan.FromMinutes(10)))
            yield break;

        var parsed = samples.Select(sample => new
        {
            Sample = sample,
            Rooms = ParseRooms(sample.RoomTemperaturesJson)
        }).ToArray();

        foreach (var room in enabled)
        {
            var roomTarget = site.BaseRoomTargetC + room.TargetOffsetC;
            var continuouslyCold = parsed.All(point =>
                IsRoomValid(point.Sample.QualityJson, room.EntityId) &&
                point.Rooms.TryGetValue(room.EntityId, out var value) &&
                value < roomTarget - site.LowerComfortBandC);
            if (!continuouslyCold) continue;

            var otherRoomsWarm = parsed.All(point => enabled
                .Where(other => other.EntityId != room.EntityId)
                .Any(other => IsRoomValid(point.Sample.QualityJson, other.EntityId) &&
                              point.Rooms.TryGetValue(other.EntityId, out var value) &&
                              value >= site.BaseRoomTargetC + other.TargetOffsetC - site.LowerComfortBandC));
            if (!otherRoomsWarm) continue;

            yield return new ThermalDiagnosticFinding(
                $"room-balance:{room.EntityId}",
                "Warning",
                "RoomBalance",
                $"{room.Name} har varit kallt i minst sex timmar medan andra rum hållit komfortnivå. Kontrollera injustering, termostat och flöde.",
                room.EntityId);
        }
    }

    private static IEnumerable<ThermalDiagnosticFinding> AnalyzeHydraulics(IReadOnlyList<ThermalTelemetrySample> samples)
    {
        var active = samples.Where(x => x.DhwActive != true && x.DefrostActive != true &&
                                        x.LeavingWaterTemperatureC is not null &&
                                        x.ReturnWaterTemperatureC is not null &&
                                        x.FlowLitresPerMinute is not null &&
                                        (x.HeatPumpPowerKw > 0.5 || x.LeavingWaterTemperatureC - x.ReturnWaterTemperatureC > 2))
            .ToArray();
        if (active.Length < 6 || active[^1].TimestampUtc - active[0].TimestampUtc < TimeSpan.FromMinutes(25) ||
            HasGap(active, TimeSpan.FromMinutes(10)))
            yield break;

        if (active.All(x => x.FlowLitresPerMinute <= 1))
            yield return new ThermalDiagnosticFinding(
                "hydraulics:low-flow",
                "ActionRequired",
                "Hydraulics",
                "Flödet har varit högst 1 l/min under minst 30 minuters värmedrift. Kontrollera cirkulation, termostater, shunt och givare.");

        if (active.All(x => x.FlowLitresPerMinute > 1 && x.LeavingWaterTemperatureC - x.ReturnWaterTemperatureC >= 15))
            yield return new ThermalDiagnosticFinding(
                "hydraulics:high-delta-t",
                "Warning",
                "Hydraulics",
                "Temperaturskillnaden LWT−RWT har varit minst 15 °C under minst 30 minuter. Det kan tyda på otillräckligt flöde eller stängda termostater.");

        if (active.All(x => x.HeatPumpPowerKw > 0.5 &&
                            Math.Abs(x.LeavingWaterTemperatureC!.Value - x.ReturnWaterTemperatureC!.Value) <= 0.5))
            yield return new ThermalDiagnosticFinding(
                "hydraulics:no-delta-t",
                "Warning",
                "Hydraulics",
                "Värmepumpen har dragit effekt utan mätbar LWT−RWT under minst 30 minuter. Kontrollera temperaturgivare, shunt och cirkulation.");
    }

    private static bool HasGap<T>(IReadOnlyList<T> samples, TimeSpan maximumGap) where T : ThermalTelemetrySample =>
        samples.Zip(samples.Skip(1), (left, right) => right.TimestampUtc - left.TimestampUtc).Any(gap => gap > maximumGap);

    private static Dictionary<string, double> ParseRooms(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static bool IsRoomValid(string qualityJson, string entityId)
    {
        try
        {
            using var document = JsonDocument.Parse(qualityJson);
            if (!TryProperty(document.RootElement, "rooms", out var rooms) ||
                !TryProperty(rooms, entityId, out var room))
                return false;
            var qualityValid = TryProperty(room, "quality", out var quality) &&
                               ((quality.ValueKind == JsonValueKind.Number && quality.TryGetInt32(out var numeric) && numeric == 0) ||
                                (quality.ValueKind == JsonValueKind.String && quality.GetString()?.Equals("Valid", StringComparison.OrdinalIgnoreCase) == true));
            var excluded = TryProperty(room, "excluded", out var excludedValue) && excludedValue.ValueKind == JsonValueKind.True;
            return qualityValid && !excluded;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static bool ContainsCode(string? json, string code)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryProperty(document.RootElement, "code", out var value) && value.GetString() == code;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
