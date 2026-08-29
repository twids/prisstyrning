using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Jobs;

public sealed class CopModelTrainingJob
{
    private readonly PrisstyrningDbContext _db;
    private readonly CopModel _model;

    public CopModelTrainingJob(PrisstyrningDbContext db, CopModel model)
    {
        _db = db;
        _model = model;
    }

    [DisableConcurrentExecution(1800)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var sites = await _db.ThermalSiteConfigs.AsNoTracking()
            .Where(x => x.HeatPumpPowerSignVerified)
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        foreach (var userId in sites) await TrainUserAsync(userId, cancellationToken);
    }

    internal async Task TrainUserAsync(string userId, CancellationToken cancellationToken)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-60);
        var observations = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId && x.TimestampUtc >= from && x.BackupHeaterActive != true &&
                        x.BrineInC != null && x.LeavingWaterTemperatureC != null &&
                        x.HeatOutputKw != null && x.Cop != null && x.Cop >= 1.2 && x.Cop <= 8 &&
                        x.HeatOutputKw > 0.5)
            .OrderBy(x => x.TimestampUtc)
            .Select(x => new CopObservation(
                x.TimestampUtc,
                x.BrineInC!.Value,
                x.LeavingWaterTemperatureC!.Value,
                x.HeatOutputKw!.Value,
                x.Cop!.Value))
            .ToListAsync(cancellationToken);
        if (observations.Count < 500) return;

        var result = _model.Train(observations);
        var accepted = result.Metrics.Mae <= 0.5;
        var previous = await _db.ThermalModelVersions
            .Where(x => x.UserId == userId && x.ModelType == "COP" && x.IsActive)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (accepted && previous is not null) previous.IsActive = false;
        _db.ThermalModelVersions.Add(new ThermalModelVersion
        {
            UserId = userId,
            ModelType = "COP",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TrainingFromUtc = observations[0].TimestampUtc,
            TrainingToUtc = observations[^1].TimestampUtc,
            IsActive = accepted,
            ParametersJson = JsonSerializer.Serialize(result.Parameters, CamelCase),
            MetricsJson = JsonSerializer.Serialize(result.Metrics, CamelCase)
        });

        if (accepted && previous is not null && MateriallyChanged(previous.ParametersJson, result.Parameters))
        {
            _db.ThermalEvents.Add(new ThermalEvent
            {
                UserId = userId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Severity = "Warning",
                Category = "ModelDrift",
                Message = "COP-modellen har förändrats tydligt. Kontrollera effektmätning, flöde och hydraulik."
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static bool MateriallyChanged(string json, CopParameters current)
    {
        try
        {
            var previous = JsonSerializer.Deserialize<CopParameters>(json, CamelCase);
            if (previous is null) return false;
            static bool Changed(double before, double after) => Math.Abs(after - before) / Math.Max(Math.Abs(before), 0.02) > 0.25;
            return Changed(previous.Intercept, current.Intercept) ||
                   Changed(previous.BrineCoefficient, current.BrineCoefficient) ||
                   Changed(previous.LwtCoefficient, current.LwtCoefficient) ||
                   Changed(previous.LoadCoefficient, current.LoadCoefficient);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
