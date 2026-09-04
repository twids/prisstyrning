using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

internal static class ThermalCurrentModelTestData
{
    internal const string BuildRevision = "0123456789abcdef0123456789abcdef01234567";
    internal static RuntimeBuildProvenance Build { get; } = RuntimeBuildProvenance.FromRevision(BuildRevision);
    private const int ThermalValidationSamples = 289;
    private const int CopValidationSamples = 20;

    internal static Task<ThermalTelemetrySample> LatestTelemetryAsync(
        PrisstyrningDbContext db,
        string userId = "account-a") => db.ThermalTelemetrySamples
        .Where(x => x.UserId == userId)
        .OrderByDescending(x => x.TimestampUtc)
        .ThenByDescending(x => x.Id)
        .FirstAsync();

    internal static async Task<IReadOnlyList<ThermalModelVersion>> SeedAsync(
        PrisstyrningDbContext db,
        string userId,
        DateTimeOffset now,
        params string[] modelTypes)
    {
        await db.SaveChangesAsync();
        var last = now.AddDays(-1);
        var samples = Enumerable.Range(0, 489).Select(index =>
        {
            var sample = ThermalModelTrainingDataTests.ValidSample(last.AddMinutes((index - 488) * 5));
            sample.UserId = userId;
            return sample;
        }).ToArray();
        db.ThermalTelemetrySamples.AddRange(samples);
        await db.SaveChangesAsync();

        var versions = new List<ThermalModelVersion>();
        foreach (var modelType in modelTypes)
            versions.Add(await AddVersionAsync(db, userId, modelType, now));
        return versions;
    }

    internal static async Task<ThermalModelVersion> AddVersionAsync(
        PrisstyrningDbContext db,
        string userId,
        string modelType,
        DateTimeOffset now)
    {
        await db.SaveChangesAsync();
        var selectionFromUtc = now.AddDays(-31);
        var selectionToUtc = now.AddMinutes(-2);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled).OrderBy(x => x.Id).ToListAsync();
        var entities = await db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled).OrderBy(x => x.Id).ToListAsync();
        var candidates = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= selectionFromUtc && x.TimestampUtc <= selectionToUtc)
            .OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).ToListAsync();
        var selected = modelType switch
        {
            "2R2C" => ThermalModelTrainingData.SelectThermal(
                candidates, userId, selectionFromUtc, selectionToUtc, rooms, entities).Select(x => x.Sample).ToArray(),
            "COP" => ThermalModelTrainingData.SelectCop(
                candidates, userId, selectionFromUtc, selectionToUtc, entities).Select(x => x.Sample).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(modelType))
        };
        var validationSamples = modelType == "2R2C" ? ThermalValidationSamples : CopValidationSamples;
        var minimumTraining = modelType == "2R2C" ? 200 : 80;
        if (selected.Length < minimumTraining + validationSamples)
            throw new InvalidOperationException("The test fixture does not contain enough exact model source rows.");
        var trainingSamples = selected.Length - validationSamples;
        var source = ThermalModelProvenance.Create(
            userId,
            modelType,
            selectionFromUtc,
            selectionToUtc,
            selected,
            rooms,
            entities,
            trainingSamples,
            validationSamples,
            heatPumpPowerSignVerified: true,
            BuildRevision);
        var version = new ThermalModelVersion
        {
            UserId = userId,
            ModelType = modelType,
            IsActive = true,
            TrainingFromUtc = selected[0].TimestampUtc,
            TrainingToUtc = selected[^1].TimestampUtc,
            CreatedAtUtc = now.AddMinutes(-1),
            ParametersJson = modelType == "COP"
                ? JsonSerializer.Serialize(CopModel.ConservativeDefault, JsonSerializerOptions.Web)
                : JsonSerializer.Serialize(new GreyBoxParameters(2, 35, .35, .8, .95, 35, -.45), JsonSerializerOptions.Web),
            MetricsJson = modelType == "COP"
                ? JsonSerializer.Serialize(new CopModelMetrics(.1, trainingSamples, validationSamples, 1), JsonSerializerOptions.Web)
                : JsonSerializer.Serialize(new ThermalModelMetrics(
                    .1, .2, trainingSamples, validationSamples,
                    validationSamples - 24, validationSamples - 288, 1), JsonSerializerOptions.Web),
            SourceEvidenceJson = ThermalModelProvenance.Serialize(source)
        };
        db.ThermalModelVersions.Add(version);
        await db.SaveChangesAsync();
        return version;
    }
}
