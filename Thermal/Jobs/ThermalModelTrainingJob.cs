using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Jobs;

public sealed class ThermalModelTrainingJob
{
    private readonly PrisstyrningDbContext _db;
    private readonly GreyBoxThermalModel _model;
    private readonly RuntimeBuildProvenance _build;

    public ThermalModelTrainingJob(
        PrisstyrningDbContext db,
        GreyBoxThermalModel model,
        RuntimeBuildProvenance build)
    {
        _db = db;
        _model = model;
        _build = build;
    }

    [DisableConcurrentExecution(1800)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var userIds = await _db.ThermalSiteConfigs.AsNoTracking().Select(x => x.UserId).ToListAsync(cancellationToken);
        foreach (var userId in userIds) await TrainUserAsync(userId, cancellationToken);
    }

    internal async Task TrainUserAsync(string userId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-60);
        var rooms = await _db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var entities = await _db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var samples = await ThermalModelTrainingData.ThermalCandidates(
                _db.ThermalTelemetrySamples.AsNoTracking(), userId, from, now)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);
        var selected = ThermalModelTrainingData.SelectThermal(samples, userId, from, now, rooms, entities);
        var observations = selected.Select(x => x.Observation).ToArray();
        if (observations.Length < 21 * 24 * 12 * 0.98) return;

        var result = _model.Train(observations);
        var fittingSampleIds = selected.Take(result.Metrics.TrainingSamples).Select(x => x.Sample.Id).ToHashSet();
        var parameters = result.Parameters with
        {
            RoomAdjustments = EstimateRoomAdjustments(
                selected.Where(x => fittingSampleIds.Contains(x.Sample.Id)).Select(x => x.Sample).ToArray(), rooms)
        };
        var provenance = ThermalModelProvenance.Create(
            userId,
            "2R2C",
            from,
            now,
            selected.Select(x => x.Sample).ToArray(),
            rooms,
            entities,
            result.Metrics.TrainingSamples,
            result.Metrics.ValidationSamples,
            heatPumpPowerSignVerified: false,
            _build.RequireRevision());
        var previous = await _db.ThermalModelVersions
            .Where(x => x.UserId == userId && x.ModelType == "2R2C" && x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var version = new ThermalModelVersion
        {
            UserId = userId,
            ModelType = "2R2C",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TrainingFromUtc = observations.First().TimestampUtc,
            TrainingToUtc = observations.Last().TimestampUtc,
            ParametersJson = JsonSerializer.Serialize(parameters, CamelCase),
            MetricsJson = JsonSerializer.Serialize(result.Metrics, CamelCase),
            SourceEvidenceJson = ThermalModelProvenance.Serialize(provenance)
        };
        var accepted = ThermalModelEvidence.Assess(version, DateTimeOffset.UtcNow).Passed;
        version.IsActive = accepted;
        if (accepted && previous is not null) previous.IsActive = false;
        _db.ThermalModelVersions.Add(version);
        if (accepted && previous is not null && MateriallyChanged(previous.ParametersJson, parameters))
        {
            _db.ThermalEvents.Add(new ThermalEvent
            {
                UserId = userId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Severity = "Warning",
                Category = "ModelDrift",
                Message = "Husets termiska modell har förändrats tydligt. Kontrollera termostater, injustering eller byggnadsändringar."
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyDictionary<string, RoomThermalAdjustment> EstimateRoomAdjustments(
        IReadOnlyCollection<ThermalTelemetrySample> samples,
        IReadOnlyCollection<ThermalRoomConfig>? configuredRooms = null)
    {
        var rows = new List<(DateTimeOffset TimestampUtc, Dictionary<string, double> Rooms, double Representative)>();
        foreach (var sample in samples.OrderBy(x => x.TimestampUtc))
        {
            try
            {
                var validRooms = ThermalModelTrainingData.ReadRooms(sample);
                if (configuredRooms is not null) validRooms = validRooms.Where(pair => configuredRooms.Any(room => room.Enabled &&
                    room.EntityId.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) && pair.Value >= room.MinimumValidC && pair.Value <= room.MaximumValidC))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                if (validRooms is { Count: > 0 }) rows.Add((sample.TimestampUtc, validRooms, validRooms.Values.Average()));
            }
            catch (JsonException)
            {
                // An invalid historical row is excluded from model fitting.
            }
        }

        var result = new Dictionary<string, RoomThermalAdjustment>(StringComparer.OrdinalIgnoreCase);
        foreach (var entityId in rows.SelectMany(x => x.Rooms.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var series = rows
                .Where(x => x.Rooms.ContainsKey(entityId))
                .Select(x => (x.TimestampUtc, Value: x.Rooms[entityId], Relative: x.Rooms[entityId] - x.Representative))
                .ToArray();
            if (series.Length < 12) continue;

            var offset = series.Average(x => x.Relative);
            var disturbance = Math.Sqrt(series.Average(x => Math.Pow(x.Relative - offset, 2)));
            var mean = series.Average(x => x.Value);
            var pairs = series.Zip(series.Skip(1))
                .Where(pair => pair.Second.TimestampUtc > pair.First.TimestampUtc &&
                               pair.Second.TimestampUtc - pair.First.TimestampUtc <= TimeSpan.FromMinutes(15))
                .ToArray();
            var denominator = pairs.Sum(pair => Math.Pow(pair.First.Value - mean, 2));
            var correlation = denominator <= 1e-9
                ? 0
                : pairs.Sum(pair => (pair.First.Value - mean) * (pair.Second.Value - mean)) / denominator;
            correlation = Math.Clamp(correlation, 0, 0.999);
            var stepHours = pairs.Length == 0 ? 5d / 60d : pairs.Average(pair => (pair.Second.TimestampUtc - pair.First.TimestampUtc).TotalHours);
            var inertiaHours = correlation <= 0.01 ? stepHours : -stepHours / Math.Log(correlation);

            result[entityId] = new RoomThermalAdjustment(
                Math.Round(offset, 4),
                Math.Round(Math.Clamp(inertiaHours, 5d / 60d, 72), 3),
                Math.Round(disturbance, 4),
                series.Length);
        }
        return result;
    }

    private static bool MateriallyChanged(string previousJson, GreyBoxParameters current)
    {
        try
        {
            var previous = JsonSerializer.Deserialize<GreyBoxParameters>(previousJson, CamelCase);
            if (previous is null) return false;
            static bool Changed(double oldValue, double newValue) => Math.Abs(newValue - oldValue) / Math.Max(Math.Abs(oldValue), 0.01) > 0.25;
            return Changed(previous.EnvelopeConductanceKwPerC, current.EnvelopeConductanceKwPerC) ||
                   Changed(previous.MassCapacityKwhPerC, current.MassCapacityKwhPerC) ||
                   Changed(previous.HeatingGain, current.HeatingGain);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
