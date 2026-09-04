using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.Optimization;

public sealed record ThermalModelSourceEvidence(
    int SchemaVersion,
    string AlgorithmVersion,
    string SelectionVersion,
    string BuildRevision,
    DateTimeOffset SelectionFromUtc,
    DateTimeOffset SelectionToUtc,
    int ObservationCount,
    int TrainingSamples,
    int ValidationSamples,
    long FirstSampleId,
    long LastSampleId,
    string SampleFingerprint,
    string ConfigurationFingerprint);

public sealed record ThermalModelProvenanceSummary(
    bool Verifiable,
    string? AlgorithmVersion,
    string? SelectionVersion,
    string? BuildRevision,
    DateTimeOffset? SelectionFromUtc,
    DateTimeOffset? SelectionToUtc,
    int? ObservationCount,
    int? TrainingSamples,
    int? ValidationSamples);

public sealed record ThermalModelSourceValidation(
    bool Passed,
    string Status,
    string Reason,
    DateTimeOffset CheckedAtUtc);

internal static class ThermalModelProvenance
{
    internal const int SchemaVersion = 2;
    internal const string ThermalAlgorithmVersion = "grey-box-2r2c-v1";
    internal const string CopAlgorithmVersion = "ridge-cop-v1";
    internal const string ThermalSelectionVersion = "thermal-validated-history-v1";
    internal const string CopSelectionVersion = "cop-validated-history-v1";

    internal static ThermalModelSourceEvidence Create(
        string userId,
        string modelType,
        DateTimeOffset selectionFromUtc,
        DateTimeOffset selectionToUtc,
        IReadOnlyCollection<ThermalTelemetrySample> samples,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities,
        int trainingSamples,
        int validationSamples,
        bool heatPumpPowerSignVerified,
        string buildRevision)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("An account is required.", nameof(userId));
        var normalizedBuildRevision = RuntimeBuildProvenance.Normalize(buildRevision);
        if (normalizedBuildRevision is null)
            throw new ArgumentException("A valid 40- or 64-character build revision is required.", nameof(buildRevision));
        var (algorithmVersion, selectionVersion) = Versions(modelType);
        if (selectionFromUtc == default || selectionToUtc <= selectionFromUtc)
            throw new ArgumentException("The source selection window is invalid.", nameof(selectionFromUtc));
        if (rooms.Any(x => x.Id <= 0 || x.UserId != userId || !x.Enabled) ||
            rooms.Select(x => x.Id).Distinct().Count() != rooms.Count ||
            rooms.Select(x => x.EntityId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rooms.Count ||
            entities.Count == 0 || entities.Any(x => x.Id <= 0 || x.UserId != userId || !x.Enabled) ||
            entities.Select(x => x.Id).Distinct().Count() != entities.Count ||
            entities.Select(x => x.Role).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entities.Count)
            throw new ArgumentException("The selected model configuration is incomplete or belongs to another account.");
        var ordered = samples.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).ToArray();
        if (ordered.Length == 0 || ordered.Any(x => x.Id <= 0 || x.UserId != userId || x.TimestampUtc == default) ||
            ordered.Select(x => x.Id).Distinct().Count() != ordered.Length ||
            ordered.Select(x => x.TimestampUtc).Distinct().Count() != ordered.Length ||
            ordered[0].TimestampUtc < selectionFromUtc || ordered[^1].TimestampUtc > selectionToUtc ||
            trainingSamples <= 0 || validationSamples <= 0 || trainingSamples + validationSamples > ordered.Length)
            throw new ArgumentException("The selected model source is incomplete or inconsistent.", nameof(samples));

        return new(
            SchemaVersion,
            algorithmVersion,
            selectionVersion,
            normalizedBuildRevision,
            selectionFromUtc,
            selectionToUtc,
            ordered.Length,
            trainingSamples,
            validationSamples,
            ordered[0].Id,
            ordered[^1].Id,
            SampleFingerprint(userId, modelType, ordered),
            ConfigurationFingerprint(userId, modelType, rooms, entities, heatPumpPowerSignVerified));
    }

    internal static string Serialize(ThermalModelSourceEvidence evidence) =>
        JsonSerializer.Serialize(evidence, JsonSerializerOptions.Web);

    internal static ThermalModelSourceEvidence? Read(ThermalModelVersion model)
    {
        using var document = Object(model.SourceEvidenceJson);
        if (document is null) return null;
        ThermalModelSourceEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<ThermalModelSourceEvidence>(model.SourceEvidenceJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
        if (evidence is null) return null;
        string algorithm;
        string selection;
        try
        {
            (algorithm, selection) = Versions(model.ModelType);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (evidence.SchemaVersion != SchemaVersion || evidence.AlgorithmVersion != algorithm ||
            evidence.SelectionVersion != selection ||
            RuntimeBuildProvenance.Normalize(evidence.BuildRevision) is null ||
            RuntimeBuildProvenance.Normalize(evidence.BuildRevision) != evidence.BuildRevision ||
            evidence.SelectionFromUtc == default ||
            evidence.SelectionToUtc <= evidence.SelectionFromUtc || model.TrainingFromUtc < evidence.SelectionFromUtc ||
            model.TrainingToUtc > evidence.SelectionToUtc || evidence.SelectionToUtc > model.CreatedAtUtc ||
            evidence.ObservationCount <= 0 || evidence.TrainingSamples <= 0 || evidence.ValidationSamples <= 0 ||
            evidence.TrainingSamples + evidence.ValidationSamples > evidence.ObservationCount ||
            evidence.FirstSampleId <= 0 || evidence.LastSampleId <= 0 || !IsSha256(evidence.SampleFingerprint) ||
            !IsSha256(evidence.ConfigurationFingerprint))
            return null;
        return evidence;
    }

    internal static ThermalModelProvenanceSummary Summary(ThermalModelVersion model)
    {
        var evidence = Read(model);
        return evidence is null
            ? new(false, null, null, null, null, null, null, null, null)
            : new(
                true,
                evidence.AlgorithmVersion,
                evidence.SelectionVersion,
                evidence.BuildRevision,
                evidence.SelectionFromUtc,
                evidence.SelectionToUtc,
                evidence.ObservationCount,
                evidence.TrainingSamples,
                evidence.ValidationSamples);
    }

    internal static async Task<IReadOnlyDictionary<long, ThermalModelSourceValidation>> VerifyCurrentAsync(
        PrisstyrningDbContext db,
        string userId,
        IReadOnlyCollection<ThermalModelVersion> models,
        IReadOnlyCollection<ThermalRoomConfig> enabledRooms,
        IReadOnlyCollection<ThermalEntityConfig> enabledEntities,
        bool heatPumpPowerSignVerified,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken,
        RuntimeBuildProvenance runningBuild)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("An account is required.", nameof(userId));
        if (models.Select(x => x.Id).Any(x => x <= 0) || models.Select(x => x.Id).Distinct().Count() != models.Count)
            throw new ArgumentException("Persisted model versions must have unique positive identifiers.", nameof(models));
        ArgumentNullException.ThrowIfNull(runningBuild);
        var currentBuildRevision = runningBuild.Revision;

        var result = new Dictionary<long, ThermalModelSourceValidation>();
        var candidates = new List<(ThermalModelVersion Model, ThermalModelSourceEvidence Source)>();
        foreach (var model in models)
        {
            if (model.UserId != userId)
            {
                result[model.Id] = Block("Invalid", "Modellversionen tillhör inte det verifierade kontot.", checkedAtUtc);
                continue;
            }
            var source = Read(model);
            if (source is null)
            {
                result[model.Id] = Block("Unproven", "Modellen saknar ett läsbart källbevis med exakt träningsurval och byggrevision. Träna om modellen.", checkedAtUtc);
                continue;
            }
            if (currentBuildRevision is null)
            {
                result[model.Id] = Block("Unproven", "Den körande programversionen saknar en inbakad källkodsrevision. Använd en revisionsmärkt build; imagesignatur kontrolleras separat före driftsättning.", checkedAtUtc);
                continue;
            }
            if (!string.Equals(source.BuildRevision, currentBuildRevision, StringComparison.Ordinal))
            {
                result[model.Id] = Block("BuildChanged", "Modellen tränades med en annan kodrevision än den som körs nu. Träna en ny modellversion med den aktuella programversionen.", checkedAtUtc);
                continue;
            }
            candidates.Add((model, source));
        }

        if (candidates.Count == 0) return result;
        var selectionFromUtc = candidates.Min(x => x.Source.SelectionFromUtc);
        var selectionToUtc = candidates.Max(x => x.Source.SelectionToUtc);
        var samples = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= selectionFromUtc && x.TimestampUtc <= selectionToUtc)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var (model, source) in candidates)
        {
            try
            {
                var selectedSamples = model.ModelType switch
                {
                    "2R2C" => ThermalModelTrainingData.SelectThermal(
                        samples, userId, source.SelectionFromUtc, source.SelectionToUtc, enabledRooms, enabledEntities)
                        .Select(x => x.Sample).ToArray(),
                    "COP" => ThermalModelTrainingData.SelectCop(
                        samples, userId, source.SelectionFromUtc, source.SelectionToUtc, enabledEntities)
                        .Select(x => x.Sample).ToArray(),
                    _ => []
                };
                var current = Create(
                    userId,
                    model.ModelType,
                    source.SelectionFromUtc,
                    source.SelectionToUtc,
                    selectedSamples,
                    enabledRooms,
                    enabledEntities,
                    source.TrainingSamples,
                    source.ValidationSamples,
                    heatPumpPowerSignVerified,
                    currentBuildRevision!);
                result[model.Id] = current == source
                    ? new(true, "Current", "Exakt historiskt urval, konfiguration och körande kodrevision matchar modellens sparade källbevis.", checkedAtUtc)
                    : Block("Changed", "Historiska mätningar eller konfiguration har ändrats sedan modellen tränades. Träna en ny version.", checkedAtUtc);
            }
            catch (ArgumentException)
            {
                result[model.Id] = Block("Changed", "Modellens historiska urval kan inte längre återskapas säkert. Kontrollera konfigurationen och träna om modellen.", checkedAtUtc);
            }
        }
        return result;
    }

    private static string SampleFingerprint(
        string userId,
        string modelType,
        IReadOnlyCollection<ThermalTelemetrySample> samples)
    {
        object selected = modelType switch
        {
            "2R2C" => samples.Select(x => new
            {
                x.Id,
                x.TimestampUtc,
                x.OutsideTemperatureC,
                x.WindSpeedMps,
                x.SolarIrradianceWm2,
                x.LeavingWaterTemperatureC,
                x.ReturnWaterTemperatureC,
                x.FlowLitresPerMinute,
                x.HeatOutputKw,
                x.DhwActive,
                x.DefrostActive,
                x.BackupHeaterActive,
                x.RoomTemperaturesJson,
                x.QualityJson
            }),
            "COP" => samples.Select(x => new
            {
                x.Id,
                x.TimestampUtc,
                x.LeavingWaterTemperatureC,
                x.ReturnWaterTemperatureC,
                x.FlowLitresPerMinute,
                x.BrineInC,
                x.HeatPumpPowerKw,
                x.HeatOutputKw,
                x.Cop,
                x.DefrostActive,
                x.BackupHeaterActive,
                x.QualityJson
            }),
            _ => throw new ArgumentException("Unsupported thermal model type.", nameof(modelType))
        };
        return Hash(JsonSerializer.Serialize(new { userId, modelType, samples = selected }, JsonSerializerOptions.Web));
    }

    private static string ConfigurationFingerprint(
        string userId,
        string modelType,
        IEnumerable<ThermalRoomConfig> rooms,
        IEnumerable<ThermalEntityConfig> entities,
        bool heatPumpPowerSignVerified)
    {
        var selectedRooms = modelType == "2R2C"
            ? rooms.OrderBy(x => x.Id).Select(x => new
            {
                x.Id,
                x.EntityId,
                x.TargetOffsetC,
                x.Weight,
                x.IsCritical,
                x.Enabled,
                x.MinimumValidC,
                x.MaximumValidC,
                x.MaximumRateCPerHour
            }).ToArray()
            : [];
        var selectedEntities = entities.OrderBy(x => x.Id).Select(x => new
        {
            x.Id,
            x.Role,
            x.EntityId,
            x.ExpectedUnit,
            x.Enabled,
            x.MinimumValid,
            x.MaximumValid,
            x.MaximumRatePerHour
        });
        return Hash(JsonSerializer.Serialize(new
        {
            userId,
            modelType,
            heatPumpPowerSignVerified = modelType == "COP" && heatPumpPowerSignVerified,
            rooms = selectedRooms,
            entities = selectedEntities
        }, JsonSerializerOptions.Web));
    }

    private static (string Algorithm, string Selection) Versions(string modelType) => modelType switch
    {
        "2R2C" => (ThermalAlgorithmVersion, ThermalSelectionVersion),
        "COP" => (CopAlgorithmVersion, CopSelectionVersion),
        _ => throw new ArgumentException("Unsupported thermal model type.", nameof(modelType))
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static ThermalModelSourceValidation Block(string status, string reason, DateTimeOffset checkedAtUtc) =>
        new(false, status, reason, checkedAtUtc);
}
