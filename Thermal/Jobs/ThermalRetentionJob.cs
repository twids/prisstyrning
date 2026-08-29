using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Jobs;

public sealed class ThermalRetentionJob
{
    private readonly PrisstyrningDbContext _db;

    public ThermalRetentionJob(PrisstyrningDbContext db) => _db = db;

    [DisableConcurrentExecution(1800)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var currentHour = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, TimeSpan.Zero);
        var from = currentHour.AddDays(-2);
        var samples = await _db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.TimestampUtc >= from && x.TimestampUtc < currentHour)
            .OrderBy(x => x.TimestampUtc).ToListAsync(cancellationToken);
        foreach (var group in samples.GroupBy(x => new { x.UserId, Hour = FloorHour(x.TimestampUtc) }))
        {
            var aggregate = await _db.ThermalHourlyAggregates
                .SingleOrDefaultAsync(x => x.UserId == group.Key.UserId && x.HourUtc == group.Key.Hour, cancellationToken)
                ?? new ThermalHourlyAggregate { UserId = group.Key.UserId, HourUtc = group.Key.Hour };
            if (aggregate.Id == 0) _db.ThermalHourlyAggregates.Add(aggregate);
            aggregate.AverageOutsideTemperatureC = Average(group.Select(x => x.OutsideTemperatureC));
            aggregate.AverageLwtC = Average(group.Select(x => x.LeavingWaterTemperatureC));
            aggregate.AverageCop = Average(group.Select(x => x.Cop));
            aggregate.AverageRoomTemperatureC = Average(group.Select(AverageRooms));
            aggregate.HeatPumpEnergyKwh = group.Sum(x => (x.HeatPumpPowerKw ?? 0) * 5 / 60);
            aggregate.HeatOutputKwh = group.Sum(x => (x.HeatOutputKw ?? 0) * 5 / 60);
            aggregate.RoomsJson = AggregateRooms(group);
        }
        await _db.SaveChangesAsync(cancellationToken);
        await _db.ThermalTelemetrySamples.Where(x => x.TimestampUtc < DateTimeOffset.UtcNow.AddDays(-400))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static DateTimeOffset FloorHour(DateTimeOffset value) =>
        new(value.UtcDateTime.Year, value.UtcDateTime.Month, value.UtcDateTime.Day, value.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);

    private static double? Average(IEnumerable<double?> values)
    {
        var present = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private static double? AverageRooms(ThermalTelemetrySample sample)
    {
        try { var rooms = JsonSerializer.Deserialize<Dictionary<string, double>>(sample.RoomTemperaturesJson); return rooms?.Count > 0 ? rooms.Values.Average() : null; }
        catch (JsonException) { return null; }
    }

    private static string AggregateRooms(IEnumerable<ThermalTelemetrySample> samples)
    {
        var values = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            try
            {
                foreach (var room in JsonSerializer.Deserialize<Dictionary<string, double>>(sample.RoomTemperaturesJson) ?? [])
                {
                    if (!values.TryGetValue(room.Key, out var list)) values[room.Key] = list = [];
                    list.Add(room.Value);
                }
            }
            catch (JsonException) { }
        }
        return JsonSerializer.Serialize(values.ToDictionary(x => x.Key, x => x.Value.Average()));
    }
}
