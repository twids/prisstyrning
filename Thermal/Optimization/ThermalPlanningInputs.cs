using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Optimization;

// Local orchestration metadata. It is persisted with the plan and queued
// request, but BuildRuntimePayload never sends it to EMHASS.
public sealed record ThermalPlanningInputEvidence(
    long TelemetrySampleId,
    DateTimeOffset TelemetryTimestampUtc,
    string TelemetryFingerprint,
    int PriceSnapshotId,
    string PriceZone,
    DateTimeOffset PriceSavedAtUtc,
    string PriceFingerprint,
    ThermalPlanningDhwEvidence? DhwEvidence = null);

public sealed record ThermalPlanningDhwEvidence(
    long? ReservedCycleId,
    int OpenCycleCount,
    string OpenCycleFingerprint);

internal sealed record ThermalPlanningTelemetry(
    long SampleId,
    DateTimeOffset TimestampUtc,
    double RepresentativeRoomTemperatureC,
    double OutsideTemperatureC,
    double LeavingWaterTemperatureC,
    double ReturnWaterTemperatureC,
    double FlowLitresPerMinute,
    double BrineInC,
    double TankTemperatureC,
    double HeatOutputKw,
    double HeatPumpPowerKw,
    double PropertyPowerKw,
    bool DhwActive,
    bool DefrostActive,
    bool BackupHeaterActive,
    string Fingerprint);

internal static class ThermalPlanningInputs
{
    private static readonly string[] RequiredSignalRoles =
    [
        ThermalEntityRoles.OutsideTemperature,
        ThermalEntityRoles.LeavingWaterTemperature,
        ThermalEntityRoles.ReturnWaterTemperature,
        ThermalEntityRoles.Flow,
        ThermalEntityRoles.BrineIn,
        ThermalEntityRoles.TankTemperature,
        ThermalEntityRoles.HeatPumpPower,
        ThermalEntityRoles.PropertyPower,
        ThermalEntityRoles.DhwActive,
        ThermalEntityRoles.DefrostActive,
        ThermalEntityRoles.BackupHeaterActive,
        ThermalEntityRoles.WeatherForecast
    ];

    internal static async Task<ThermalPlanningTelemetry> ReadTelemetryAsync(
        PrisstyrningDbContext db,
        string userId,
        ThermalTelemetrySample sample,
        ThermalSiteConfig site,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (sample.Id <= 0 || !string.Equals(sample.UserId, userId, StringComparison.Ordinal) ||
            sample.TimestampUtc == default || sample.TimestampUtc > now || now - sample.TimestampUtc > TimeSpan.FromMinutes(10))
            throw Evidence("Den senaste femminutersinsamlingen kan inte bindas till kontot eller är äldre än tio minuter.");

        var rooms = await db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var entities = await db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var required = new List<ThermalEntityConfig>(RequiredSignalRoles.Length);
        foreach (var role in RequiredSignalRoles)
        {
            var matches = entities.Where(x => x.Role.Equals(role, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw Evidence($"Datakällan {role} måste ha exakt en aktiverad entity innan kostnadsoptimering.");
            required.Add(matches[0]);
        }

        var signalQuality = ThermalStatusQuality.Assess(sample, [], required, now, site.UpdatedAtUtc);
        if (signalQuality.Quality != DataQuality.Valid)
            throw Evidence($"Planeringens sensorer är inte verifierat giltiga. {signalQuality.Reason}");

        var control = ThermalControlTelemetry.Assess(sample, rooms, entities, site, now);
        if (!control.SafeToControl)
            throw Evidence(control.InvalidReason ?? "Rumstemperaturen kan inte verifieras från senaste liveinsamlingen.");
        if (!ThermalReadinessEvidence.HasValidForecastQuality(sample) ||
            ThermalReadinessEvidence.ForecastHours(sample.OutsideTemperatureForecastJson, now) < 24)
            throw Evidence("Väderprognosen måste vara giltig och täcka minst 24 sammanhängande timmar från nu.");

        var outside = Required(sample.OutsideTemperatureC, "Utetemperatur");
        var lwt = Required(sample.LeavingWaterTemperatureC, "Framledningstemperatur");
        var rwt = Required(sample.ReturnWaterTemperatureC, "Returtemperatur");
        var flow = Required(sample.FlowLitresPerMinute, "Flöde");
        var brine = Required(sample.BrineInC, "Köldbärartemperatur");
        var tank = Required(sample.TankTemperatureC, "Tanktemperatur");
        var heatOutput = Required(sample.HeatOutputKw, "Avgiven värmeeffekt");
        var heatPumpPower = Required(sample.HeatPumpPowerKw, "Värmepumpens eleffekt");
        var propertyPower = Required(sample.PropertyPowerKw, "Fastighetens importerade effekt");
        if (flow < 0 || heatOutput < 0 || heatPumpPower < 0)
            throw Evidence("Flöde och värmepumpens uppmätta effekter måste vara icke-negativa.");

        var derivedHeat = Math.Max(0, flow / 60 * 4.186 * (lwt - rwt));
        if (!double.IsFinite(derivedHeat) || Math.Abs(derivedHeat - heatOutput) > Math.Max(.05, derivedHeat * .01))
            throw Evidence("Avgiven värme stämmer inte med flöde och temperaturdifferens. Kontrollera flödes- och temperaturgivarna.");
        if (sample.DefrostActive != false || sample.BackupHeaterActive != false)
            throw Evidence("Ett ostört planeringsögonblick krävs; invänta att avfrostning och elpatron är avstängda.");
        if (sample.DhwActive == true && !await db.DhwCycles.AsNoTracking().AnyAsync(x =>
                x.UserId == userId && x.ActualStartUtc != null && x.ActualEndUtc == null, cancellationToken))
            throw Evidence("DHW är aktiv men någon pågående, kontoägd cykel har ännu inte verifierats. Invänta livscykelregistreringen.");

        var roomTemperature = site.BaseRoomTargetC + control.RepresentativeTemperatureErrorC;
        if (!double.IsFinite(roomTemperature))
            throw Evidence("Husets representativa rumstemperatur kan inte beräknas säkert.");

        return new(
            sample.Id,
            sample.TimestampUtc,
            roomTemperature,
            outside,
            lwt,
            rwt,
            flow,
            brine,
            tank,
            heatOutput,
            heatPumpPower,
            propertyPower,
            sample.DhwActive == true,
            sample.DefrostActive.Value,
            sample.BackupHeaterActive.Value,
            TelemetryFingerprint(userId, sample));
    }

    internal static async Task<ThermalPlanningInputEvidence> EvidenceAsync(
        PrisstyrningDbContext db,
        string userId,
        ThermalPlanningTelemetry telemetry,
        PriceSnapshot snapshot,
        string zone,
        long? reservedDhwCycleId,
        CancellationToken cancellationToken)
    {
        var dhwEvidence = await ReadDhwEvidenceAsync(
            db, userId, reservedDhwCycleId, cancellationToken);
        return new(
            telemetry.SampleId,
            telemetry.TimestampUtc,
            telemetry.Fingerprint,
            snapshot.Id,
            NormalizeZone(zone),
            snapshot.SavedAtUtc,
            PriceFingerprint(snapshot),
            dhwEvidence);
    }

    internal static async Task EnsureCurrentAsync(
        PrisstyrningDbContext db,
        string userId,
        ThermalPlanningInputEvidence? evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool requireFreshTelemetry = true)
    {
        if (evidence is null || evidence.TelemetrySampleId <= 0 || evidence.PriceSnapshotId <= 0 ||
            evidence.TelemetryTimestampUtc == default || evidence.PriceSavedAtUtc == default ||
            string.IsNullOrWhiteSpace(evidence.TelemetryFingerprint) || string.IsNullOrWhiteSpace(evidence.PriceFingerprint) ||
            evidence.DhwEvidence is null || evidence.DhwEvidence.OpenCycleCount < 0 ||
            string.IsNullOrWhiteSpace(evidence.DhwEvidence.OpenCycleFingerprint))
            throw Evidence("Beräkningen saknar verifierbart telemetri-, pris- eller DHW-underlag och behöver skapas om.");

        var sample = await db.ThermalTelemetrySamples.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == evidence.TelemetrySampleId && x.UserId == userId, cancellationToken);
        if (sample is null || sample.TimestampUtc != evidence.TelemetryTimestampUtc ||
            TelemetryFingerprint(userId, sample) != evidence.TelemetryFingerprint ||
            sample.TimestampUtc > now || requireFreshTelemetry && now - sample.TimestampUtc > TimeSpan.FromMinutes(10))
            throw Evidence("Telemetrin ändrades, försvann eller blev för gammal under beräkningen. En ny plan behövs.");

        var zone = await PriceZoneAsync(db, userId, cancellationToken);
        if (!zone.Equals(NormalizeZone(evidence.PriceZone), StringComparison.Ordinal))
            throw Evidence("Kontots elområde ändrades under beräkningen. En ny plan behövs.");
        var snapshot = await db.PriceSnapshots.AsNoTracking().Where(x => x.Zone == zone)
            .OrderByDescending(x => x.SavedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (snapshot is null || snapshot.Id != evidence.PriceSnapshotId || snapshot.SavedAtUtc != evidence.PriceSavedAtUtc ||
            PriceFingerprint(snapshot) != evidence.PriceFingerprint || snapshot.SavedAtUtc > now.AddMinutes(2) ||
            now - snapshot.SavedAtUtc > TimeSpan.FromHours(36))
            throw Evidence("Prisunderlaget ändrades, försvann eller blev för gammalt under beräkningen. En ny plan behövs.");

        var dhwEvidence = await ReadDhwEvidenceAsync(
            db, userId, evidence.DhwEvidence.ReservedCycleId, cancellationToken);
        if (dhwEvidence != evidence.DhwEvidence)
            throw Evidence("DHW-reservationen ändrades, försvann eller ersattes under beräkningen. En ny plan behövs.");
    }

    private static async Task<ThermalPlanningDhwEvidence> ReadDhwEvidenceAsync(
        PrisstyrningDbContext db,
        string userId,
        long? reservedCycleId,
        CancellationToken cancellationToken)
    {
        if (reservedCycleId is <= 0)
            throw Evidence("Den reserverade DHW-cykelns identitet är ogiltig.");

        var cycles = await db.DhwCycles.AsNoTracking()
            .Where(x => x.UserId == userId && x.ActualEndUtc == null)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (reservedCycleId is { } cycleId && cycles.All(x => x.Id != cycleId))
            throw Evidence("Den reserverade DHW-cykeln saknas eller tillhör inte längre det öppna planeringsunderlaget.");

        var serialized = JsonSerializer.Serialize(cycles.Select(x => new
        {
            x.Id,
            x.Kind,
            x.Source,
            x.Status,
            x.PlannedStartUtc,
            x.ScheduleAcceptedUtc,
            x.ActualStartUtc,
            x.TargetReachedUtc,
            x.ActualEndUtc,
            x.StartTemperatureC,
            x.TargetTemperatureC,
            x.PredictedDurationMinutes,
            x.ReservedDurationMinutes,
            x.PredictedCost,
            x.ActualCost,
            x.BackupHeaterUsed,
            x.PowerProfileJson,
            x.TargetVerificationCount,
            x.EstimatedCompletionUtc,
            x.LastVerificationSampleUtc
        }), JsonSerializerOptions.Web);
        return new(reservedCycleId, cycles.Count, Hash(serialized));
    }

    internal static async Task<string> PriceZoneAsync(
        PrisstyrningDbContext db,
        string userId,
        CancellationToken cancellationToken)
    {
        var configured = await db.UserSettings.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.Zone).SingleOrDefaultAsync(cancellationToken);
        return NormalizeZone(configured);
    }

    internal static string PriceFingerprint(PriceSnapshot snapshot)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            snapshot.Id,
            snapshot.Zone,
            snapshot.Date,
            snapshot.SavedAtUtc,
            snapshot.TodayPricesJson,
            snapshot.TomorrowPricesJson
        }, JsonSerializerOptions.Web);
        return Hash(serialized);
    }

    private static string TelemetryFingerprint(string userId, ThermalTelemetrySample sample)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            userId,
            sample.Id,
            sample.TimestampUtc,
            sample.OutsideTemperatureC,
            sample.OutsideTemperatureForecastJson,
            sample.WindSpeedMps,
            sample.SolarIrradianceWm2,
            sample.LeavingWaterTemperatureC,
            sample.ReturnWaterTemperatureC,
            sample.FlowLitresPerMinute,
            sample.BrineInC,
            sample.BrineOutC,
            sample.TankTemperatureC,
            sample.HeatPumpPowerKw,
            sample.PropertyPowerKw,
            sample.SpotPriceSekPerKwh,
            sample.HeatOutputKw,
            sample.Cop,
            sample.DhwActive,
            sample.DefrostActive,
            sample.BackupHeaterActive,
            sample.RoomTemperaturesJson,
            sample.QualityJson
        }, JsonSerializerOptions.Web);
        return Hash(serialized);
    }

    private static double Required(double? value, string label)
    {
        if (value is null || !double.IsFinite(value.Value))
            throw Evidence($"{label} saknas eller är ogiltig. Invänta giltiga mätvärden innan kostnadsoptimering.");
        return value.Value;
    }

    private static string NormalizeZone(string? zone) =>
        string.IsNullOrWhiteSpace(zone) ? "SE3" : zone.Trim().ToUpperInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ThermalPlanningEvidenceException Evidence(string message) => new(message);
}
