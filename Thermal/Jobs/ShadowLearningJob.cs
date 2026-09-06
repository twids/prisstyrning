using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Jobs;

public sealed record ShadowForecastPoint(DateTimeOffset TimestampUtc, double PredictedC, double? ActualC = null);
public sealed record ShadowLearningState(string Stage, string Configuration, int Samples, int HeatingSamples,
    double? MinimumOutsideC, double? MaximumOutsideC, double TrendCPerHour,
    double? HeldOutMaeC, double? PersistenceMaeC, IReadOnlyList<ShadowForecastPoint> Forecast);
public sealed record ShadowLearningVersion(long Id, DateTimeOffset IssuedAtUtc, ShadowLearningState Learning,
    double? TwoHourErrorC, double? DayErrorC);

/// <summary>
/// A deliberately simple, write-free cold-start baseline. Not a heat-response
/// model, not consumed by EMHASS/readiness/LWT, and never marked IsActive.
/// Issued forecasts are immutable; later measurements score those predictions.
/// </summary>
public sealed class ShadowLearningJob(PrisstyrningDbContext db)
{
    internal const string ModelType = "ShadowTrend";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    [DisableConcurrentExecution(1800)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var users = await db.ThermalSiteConfigs.AsNoTracking().Where(x => x.ControlMode == "Shadow")
            .Select(x => x.UserId).ToArrayAsync(cancellationToken);
        foreach (var user in users) await TrainAsync(user, DateTimeOffset.UtcNow, cancellationToken);
    }

    internal async Task TrainAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        // Recheck mode for each account. This job never changes mode/writer/commands.
        if (!await db.ThermalSiteConfigs.AnyAsync(x => x.UserId == userId && x.ControlMode == "Shadow", ct)) return;
        var rooms = await Rooms(userId, ct);
        var signature = Signature(rooms);
        var samples = await Samples(userId, now, ct);
        var rows = Observations(samples, rooms);
        if (rows.Length == 0 || now - rows[^1].Time > TimeSpan.FromMinutes(10)) return;
        var previous = await db.ThermalModelVersions.AsNoTracking().Where(x => x.UserId == userId && x.ModelType == ModelType)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (previous is not null && now - previous.CreatedAtUtc < TimeSpan.FromMinutes(55) && Read(previous)?.Configuration == signature) return;

        // Candidate fits only the first partition. Both candidate and no-change
        // baseline are compared on the same later two-hour windows.
        var split = (int)(rows.Length * .7);
        var fit = rows.Take(split).ToArray();
        var test = rows.Skip(split).ToArray();
        var changes = fit.Zip(fit.Skip(1)).Where(p => p.Second.Time - p.First.Time == TimeSpan.FromMinutes(5))
            .Select(p => (p.Second.Temperature - p.First.Temperature) * 12).Order().ToArray();
        var candidate = changes.Length >= 24 ? Math.Clamp(changes[changes.Length / 2], -.3, .3) : 0;
        var candidateError = Score(test, candidate);
        var baselineError = Score(test, 0);
        var trend = candidateError is { } error && baselineError is { } baseline && error + .01 < baseline ? candidate : 0;
        var last = rows[^1];
        var forecast = Enumerable.Range(1, 96).Select(i => new ShadowForecastPoint(
            now.AddMinutes(i * 15), Predict(last.Temperature, trend, i / 4d))).ToArray();
        var outside = samples.Where(x => x.OutsideTemperatureC is >= -50 and <= 60)
            .Select(x => x.OutsideTemperatureC!.Value).ToArray();
        var learning = new ShadowLearningState(trend == 0 ? "Persistence" : "DampedTrend", signature, rows.Length,
            samples.Count(x => x.DhwActive == false && x.DefrostActive == false && x.HeatOutputKw > .5),
            outside.Length == 0 ? null : outside.Min(), outside.Length == 0 ? null : outside.Max(), trend,
            trend == 0 ? baselineError : candidateError, baselineError, forecast);
        db.ThermalModelVersions.Add(new ThermalModelVersion
        {
            UserId = userId, ModelType = ModelType, CreatedAtUtc = now,
            TrainingFromUtc = rows[0].Time, TrainingToUtc = last.Time, IsActive = false,
            ParametersJson = "{}", MetricsJson = JsonSerializer.Serialize(learning, Json)
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ShadowLearningVersion>> GetAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var rooms = await Rooms(userId, ct);
        var signature = Signature(rooms);
        var actual = Observations(await Samples(userId, now, ct), rooms)
            .GroupBy(x => (x.Time.ToUnixTimeSeconds() + 150) / 300).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var versions = await db.ThermalModelVersions.AsNoTracking()
            .Where(x => x.UserId == userId && x.ModelType == ModelType).OrderByDescending(x => x.CreatedAtUtc)
            .Take(168).ToArrayAsync(ct);
        return versions.Select(version => (Version: version, State: Read(version)))
            .Where(x => x.State?.Configuration == signature).Select(x =>
            {
                var points = x.State!.Forecast.Select(p => p with
                {
                    ActualC = p.TimestampUtc <= now && actual.TryGetValue((p.TimestampUtc.ToUnixTimeSeconds() + 150) / 300, out var value) &&
                        Math.Abs((value.Time - p.TimestampUtc).TotalMinutes) <= 2.5 ? value.Temperature : null
                }).ToArray();
                double? Error(int index) => points.Length > index && points[index].ActualC is { } value
                    ? Math.Abs(value - points[index].PredictedC) : null;
                return new ShadowLearningVersion(x.Version.Id, x.Version.CreatedAtUtc, x.State with { Forecast = points }, Error(7), Error(95));
            }).ToArray();
    }

    internal static double Predict(double initial, double trend, double hours) => initial + trend * 3 * (1 - Math.Exp(-hours / 3));

    private static double? Score((DateTimeOffset Time, double Temperature)[] rows, double trend)
    {
        var errors = new List<double>();
        for (var i = 0; i + 24 < rows.Length; i += 24)
        {
            if (Enumerable.Range(i, 24).Any(j => rows[j + 1].Time - rows[j].Time != TimeSpan.FromMinutes(5))) continue;
            errors.Add(Math.Abs(Predict(rows[i].Temperature, trend, 2) - rows[i + 24].Temperature));
        }
        return errors.Count == 0 ? null : errors.Average();
    }

    private Task<ThermalRoomConfig[]> Rooms(string userId, CancellationToken ct) => db.ThermalRoomConfigs.AsNoTracking()
        .Where(x => x.UserId == userId && x.Enabled).OrderBy(x => x.EntityId).ToArrayAsync(ct);

    private Task<ThermalTelemetrySample[]> Samples(string userId, DateTimeOffset now, CancellationToken ct) =>
        db.ThermalTelemetrySamples.AsNoTracking().Where(x => x.UserId == userId && x.TimestampUtc >= now.AddDays(-30) && x.TimestampUtc <= now)
            .OrderBy(x => x.TimestampUtc).ToArrayAsync(ct);

    private static (DateTimeOffset Time, double Temperature)[] Observations(IEnumerable<ThermalTelemetrySample> samples, ThermalRoomConfig[] rooms)
    {
        if (rooms.Length == 0 || !rooms.Any(x => x.IsCritical)) return [];
        return samples.GroupBy(x => x.TimestampUtc).Where(x => x.Count() == 1).Select(x => x.Single()).Select(sample =>
        {
            var values = ThermalModelTrainingData.ReadRooms(sample);
            var valid = rooms.All(r => values.TryGetValue(r.EntityId, out var value) && value >= r.MinimumValidC && value <= r.MaximumValidC);
            var weight = rooms.Sum(r => r.Weight);
            return (sample.TimestampUtc, Value: valid && weight > 0 ? (double?)rooms.Sum(r => (values[r.EntityId] - r.TargetOffsetC) * r.Weight) / weight : null);
        }).Where(x => x.Value is { } v && double.IsFinite(v)).Select(x => (x.TimestampUtc, x.Value!.Value)).OrderBy(x => x.TimestampUtc).ToArray();
    }

    private static string Signature(ThermalRoomConfig[] rooms) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(rooms.Select(r => new { r.EntityId, r.Weight, r.TargetOffsetC, r.IsCritical, r.MinimumValidC, r.MaximumValidC }), Json))));

    private static ShadowLearningState? Read(ThermalModelVersion version)
    {
        try { return JsonSerializer.Deserialize<ShadowLearningState>(version.MetricsJson, Json); }
        catch (JsonException) { return null; }
    }
}
