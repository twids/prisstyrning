using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Optimization;

internal sealed record ValidatedThermalPlan(
    ThermalPlan Plan,
    ThermalPlanStep CurrentStep,
    ThermalPlanningModelEvidence ModelEvidence,
    ThermalPlanningInputEvidence InputEvidence,
    string Fingerprint);

internal static class ThermalPlanConsumption
{
    private static readonly TimeSpan StepDuration = TimeSpan.FromMinutes(15);

    internal static async Task<ValidatedThermalPlan?> ReadCurrentAsync(
        PrisstyrningDbContext db,
        string userId,
        DateTimeOffset now,
        RuntimeBuildProvenance build,
        CancellationToken cancellationToken)
    {
        var plan = await db.ThermalPlans.AsNoTracking().Include(x => x.Steps)
            .Where(x => x.UserId == userId && x.ValidFromUtc <= now && x.ValidUntilUtc > now)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (plan is null) return null;
        if (!string.Equals(plan.Status, "Valid", StringComparison.Ordinal) || plan.IsShadow)
            throw new ThermalPlanningEvidenceException("Den aktuella planen är inte godkänd för aktiv LWT-styrning.");
        if (plan.CreatedAtUtc > now || plan.ValidUntilUtc <= plan.ValidFromUtc ||
            plan.SolverDurationMs is < 0 or > 45_000 || plan.ObjectiveCost is null ||
            !double.IsFinite(plan.Confidence) || plan.Confidence is < 0 or > 1 || string.IsNullOrWhiteSpace(plan.Summary))
            throw new ThermalPlanningEvidenceException("Den aktuella planen har ogiltig status eller metadata.");

        var evidence = ReadEvidence(plan.InputSnapshotJson);
        var inputEvidence = ReadInputEvidence(plan.InputSnapshotJson);
        var models = await ThermalPlanningModels.EnsureStoredPlanCurrentAsync(db, userId, evidence, now, build, cancellationToken);
        await ThermalPlanningInputs.EnsureCurrentAsync(
            db, userId, inputEvidence, now, cancellationToken, requireFreshTelemetry: false);
        var duration = plan.ValidUntilUtc - plan.ValidFromUtc;
        if (duration.Ticks % StepDuration.Ticks != 0)
            throw new ThermalPlanningEvidenceException("Planens tidsaxel följer inte 15-minutersgränser.");
        var expectedSteps = (int)(duration.Ticks / StepDuration.Ticks);
        if (expectedSteps is <= 0 or > 384 || plan.Steps.Count != expectedSteps)
            throw new ThermalPlanningEvidenceException("Planen täcker inte hela sin giltighetsperiod.");

        var ordered = plan.Steps.OrderBy(x => x.StartUtc).ThenBy(x => x.Id).ToArray();
        for (var index = 0; index < ordered.Length; index++)
            ValidateStep(ordered[index], plan, models.Site, index);
        var current = ordered.Where(x => x.StartUtc <= now && x.EndUtc > now).ToArray();
        if (current.Length != 1)
            throw new ThermalPlanningEvidenceException("Planen saknar ett entydigt aktuellt steg.");
        return new(plan, current[0], evidence, inputEvidence, Fingerprint(userId, plan, ordered));
    }

    internal static async Task EnsureStillCurrentAsync(
        PrisstyrningDbContext db,
        string userId,
        ValidatedThermalPlan validated,
        DateTimeOffset now,
        RuntimeBuildProvenance build,
        CancellationToken cancellationToken)
    {
        await ThermalPlanningModels.EnsureStoredPlanCurrentAsync(db, userId, validated.ModelEvidence, now, build, cancellationToken);
        await ThermalPlanningInputs.EnsureCurrentAsync(
            db, userId, validated.InputEvidence, now, cancellationToken, requireFreshTelemetry: false);
        var current = await db.ThermalPlans.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == validated.Plan.Id && x.UserId == userId, cancellationToken);
        var currentSteps = current?.Steps.Where(x => x.StartUtc <= now && x.EndUtc > now).ToArray() ?? [];
        if (current is null || current.ValidFromUtc > now || current.ValidUntilUtc <= now ||
            currentSteps.Length != 1 || currentSteps[0].Id != validated.CurrentStep.Id ||
            Fingerprint(userId, current, current.Steps.OrderBy(x => x.StartUtc).ThenBy(x => x.Id)) != validated.Fingerprint)
            throw new ThermalPlanningEvidenceException("Planen ändrades eller återkallades före LWT-skrivningen.");
    }

    private static ThermalPlanningModelEvidence ReadEvidence(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            var matches = document.RootElement.EnumerateObject()
                .Where(x => x.Name.Equals("modelEvidence", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Value.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            return matches[0].Value.Deserialize<ThermalPlanningModelEvidence>(JsonSerializerOptions.Web)
                   ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ThermalPlanningEvidenceException("Planens modellunderlag kan inte läsas säkert.");
        }
    }

    private static ThermalPlanningInputEvidence ReadInputEvidence(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            var matches = document.RootElement.EnumerateObject()
                .Where(x => x.Name.Equals("inputEvidence", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Value.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            return matches[0].Value.Deserialize<ThermalPlanningInputEvidence>(JsonSerializerOptions.Web)
                   ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ThermalPlanningEvidenceException("Planens telemetri- och prisunderlag kan inte läsas säkert.");
        }
    }

    private static void ValidateStep(ThermalPlanStep step, ThermalPlan plan, ThermalSiteConfig site, int index)
    {
        var expectedStart = plan.ValidFromUtc.AddTicks(StepDuration.Ticks * index);
        if (step.StartUtc != expectedStart || step.EndUtc != expectedStart.Add(StepDuration))
            throw new ThermalPlanningEvidenceException("Planstegen är dubbla, saknas eller ligger i fel ordning.");
        var deviationLimit = Math.Min(3, Math.Max(0, site.ActiveDeviationLimitC));
        if (!double.IsFinite(step.DesiredHeatOutputKw) || step.DesiredHeatOutputKw is < 0 or > 100 ||
            !double.IsFinite(step.DesiredLwtDeviationC) || Math.Abs(step.DesiredLwtDeviationC) > deviationLimit + 0.001 ||
            !double.IsFinite(step.Confidence) || step.Confidence is < 0 or > 1 || Math.Abs(step.Confidence - plan.Confidence) > 0.001 ||
            step.DhwReserved && Math.Abs(step.DesiredLwtDeviationC) > 0.001 ||
            step.DhwReserved == string.IsNullOrWhiteSpace(step.DhwMode))
            throw new ThermalPlanningEvidenceException("Planen innehåller ett ogiltigt värme-, LWT- eller DHW-steg.");
        if (!HasFiniteRepresentative(step.ExpectedRoomsJson) || !HasDecisionReason(step.DecisionReasonJson))
            throw new ThermalPlanningEvidenceException("Planstegets temperaturprognos eller beslutsförklaring är ogiltig.");
    }

    private static bool HasFiniteRepresentative(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("representative", out var value) &&
                   value.TryGetDouble(out var temperature) && double.IsFinite(temperature) && temperature is >= -20 and <= 50;
        }
        catch (JsonException) { return false; }
    }

    private static bool HasDecisionReason(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var reasons = document.RootElement.EnumerateObject()
                .Where(x => x.Name.Equals("mainReason", StringComparison.OrdinalIgnoreCase)).ToArray();
            return reasons.Length == 1 && reasons[0].Value.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(reasons[0].Value.GetString());
        }
        catch (JsonException) { return false; }
    }

    private static string Fingerprint(string userId, ThermalPlan plan, IEnumerable<ThermalPlanStep> steps)
    {
        var value = JsonSerializer.Serialize(new
        {
            userId,
            plan.Id,
            plan.CreatedAtUtc,
            plan.ValidFromUtc,
            plan.ValidUntilUtc,
            plan.Status,
            plan.IsShadow,
            plan.SolverDurationMs,
            plan.ObjectiveCost,
            plan.Confidence,
            plan.Summary,
            plan.InputSnapshotJson,
            steps = steps.Select(x => new
            {
                x.Id,
                x.StartUtc,
                x.EndUtc,
                x.DesiredHeatOutputKw,
                x.DesiredLwtDeviationC,
                x.DhwReserved,
                x.DhwMode,
                x.IncrementalCost,
                x.Confidence,
                x.ExpectedRoomsJson,
                x.DecisionReasonJson
            })
        }, JsonSerializerOptions.Web);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
