using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Optimization;

// Local orchestration metadata only. BuildRuntimePayload never sends it to EMHASS.
public sealed record ThermalPlanningModelEvidence(
    long ThermalModelVersionId, long CopModelVersionId, string ControlMode,
    DateTimeOffset TelemetryTimestampUtc, string Fingerprint);

internal sealed record ThermalPlanningModels(
    ThermalSiteConfig Site, GreyBoxParameters Thermal, CopParameters Cop, ThermalPlanningModelEvidence Evidence)
{
    internal static async Task<ThermalPlanningModels> ReadAsync(
        PrisstyrningDbContext db, string userId, DateTimeOffset telemetryTimestampUtc,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var site = await db.ThermalSiteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (site is null || ThermalEnumParser.ControlModeOrLegacy(site.ControlMode) == Domain.ControlMode.Legacy)
            throw new ThermalPlanningEvidenceException("Planering är inte tillåten i Legacy eller utan verifierat driftläge.");
        if (telemetryTimestampUtc == default || telemetryTimestampUtc > now || now - telemetryTimestampUtc > TimeSpan.FromMinutes(10))
            throw new ThermalPlanningEvidenceException("Beräkningens telemetri är för gammal eller har ogiltig tid. Invänta ny insamling.");
        if (!site.HeatPumpPowerSignVerified)
            throw new ThermalPlanningEvidenceException("Effektmätningens tecken, CT-riktning och fasmappning måste verifieras före kostnadsoptimering.");
        var active = await db.ThermalModelVersions.AsNoTracking()
            .Where(x => x.UserId == userId && x.IsActive && (x.ModelType == "2R2C" || x.ModelType == "COP"))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).ToListAsync(cancellationToken);
        var thermal = RequireModel(active.FirstOrDefault(x => x.ModelType == "2R2C"), "Husmodellen", now);
        var cop = RequireModel(active.FirstOrDefault(x => x.ModelType == "COP"), "COP-modellen", now);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var entities = await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        // Never serialize credentials, URLs or connection objects into the evidence.
        var connection = await db.HomeAssistantConnections.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new { x.UpdatedAtUtc, x.TelemetryEnabled, x.ControlEnabled }).SingleOrDefaultAsync(cancellationToken);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            userId, site, rooms, entities, connection, thermal, cop
        }, JsonSerializerOptions.Web))));
        var evidence = new ThermalPlanningModelEvidence(thermal.Id, cop.Id, site.ControlMode, telemetryTimestampUtc, fingerprint);
        try
        {
            // Matches training serialization; default case-sensitive reads silently
            // constructed zero-valued model parameters from valid camelCase JSON.
            return new(site, JsonSerializer.Deserialize<GreyBoxParameters>(thermal.ParametersJson, JsonSerializerOptions.Web)!,
                JsonSerializer.Deserialize<CopParameters>(cop.ParametersJson, JsonSerializerOptions.Web)!, evidence);
        }
        catch (JsonException)
        {
            throw new ThermalPlanningEvidenceException("Modellparametrarna kan inte läsas säkert. Träna en ny modellversion.");
        }
    }

    internal static async Task EnsureCurrentAsync(
        PrisstyrningDbContext db, string userId, ThermalPlanningModelEvidence? evidence,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (evidence is null)
            throw new ThermalPlanningEvidenceException("Beräkningen saknar verifierbart modellunderlag och behöver skapas om.");
        var current = await ReadAsync(db, userId, evidence.TelemetryTimestampUtc, now, cancellationToken);
        if (current.Evidence != evidence)
            throw new ThermalPlanningEvidenceException("Modell, driftläge eller inställningar ändrades under beräkningen. Resultatet används inte; en ny plan behövs.");
    }

    private static ThermalModelVersion RequireModel(ThermalModelVersion? model, string label, DateTimeOffset now)
    {
        var assessment = ThermalModelEvidence.Assess(model, now);
        if (!assessment.Passed) throw new ThermalPlanningEvidenceException($"{label}: {assessment.Reason}");
        return model!;
    }
}

internal sealed class ThermalPlanningEvidenceException(string message) : InvalidOperationException(message);
