using System.Linq.Expressions;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Optimization;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.Jobs;

internal static class ThermalModelTrainingData
{
    internal static IQueryable<ThermalTelemetrySample> ThermalCandidates(
        IQueryable<ThermalTelemetrySample> samples,
        string userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc) => samples.Where(ThermalCandidate(userId, fromUtc, toUtc));

    internal static IQueryable<ThermalTelemetrySample> CopCandidates(
        IQueryable<ThermalTelemetrySample> samples,
        string userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc) => samples.Where(CopCandidate(userId, fromUtc, toUtc));

    internal static (ThermalTelemetrySample Sample, ThermalObservation Observation)[] SelectThermal(
        IEnumerable<ThermalTelemetrySample> samples,
        string userId,
        DateTimeOffset selectionFromUtc,
        DateTimeOffset selectionToUtc,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities) => samples
        .Where(ThermalCandidate(userId, selectionFromUtc, selectionToUtc).Compile())
        .GroupBy(x => x.TimestampUtc)
        .Where(x => x.Count() == 1)
        .Select(group =>
        {
            var sample = group.Single();
            return (Sample: sample, Observation: Thermal(sample, rooms, entities, selectionToUtc));
        })
        .Where(x => x.Observation is not null)
        .Select(x => (x.Sample, x.Observation!))
        .OrderBy(x => x.Sample.TimestampUtc)
        .ThenBy(x => x.Sample.Id)
        .ToArray();

    internal static (ThermalTelemetrySample Sample, CopObservation Observation)[] SelectCop(
        IEnumerable<ThermalTelemetrySample> samples,
        string userId,
        DateTimeOffset selectionFromUtc,
        DateTimeOffset selectionToUtc,
        IReadOnlyCollection<ThermalEntityConfig> entities) => samples
        .Where(CopCandidate(userId, selectionFromUtc, selectionToUtc).Compile())
        .GroupBy(x => x.TimestampUtc)
        .Where(x => x.Count() == 1)
        .Select(group =>
        {
            var sample = group.Single();
            return (Sample: sample, Observation: Cop(sample, entities, selectionToUtc));
        })
        .Where(x => x.Observation is not null)
        .Select(x => (x.Sample, x.Observation!))
        .OrderBy(x => x.Sample.TimestampUtc)
        .ThenBy(x => x.Sample.Id)
        .ToArray();

    private static Expression<Func<ThermalTelemetrySample, bool>> ThermalCandidate(
        string userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc) => x =>
        x.UserId == userId && x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc &&
        x.DhwActive != null && x.DefrostActive == false &&
        x.OutsideTemperatureC != null && x.HeatOutputKw != null;

    private static Expression<Func<ThermalTelemetrySample, bool>> CopCandidate(
        string userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc) => x =>
        x.UserId == userId && x.TimestampUtc >= fromUtc && x.TimestampUtc <= toUtc &&
        x.BackupHeaterActive == false && x.DefrostActive == false &&
        x.BrineInC != null && x.LeavingWaterTemperatureC != null &&
        x.HeatOutputKw != null && x.Cop != null && x.Cop >= 1.2 && x.Cop <= 8 &&
        x.HeatOutputKw > 0.5;

    internal static ThermalObservation? Thermal(
        ThermalTelemetrySample sample, IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities, DateTimeOffset now)
    {
        var critical = rooms.Where(x => x.Enabled && x.IsCritical).ToArray();
        if (critical.Length == 0 || sample.DhwActive is null || sample.DefrostActive != false ||
            !HasQuality(sample, critical, entities, now, ThermalEntityRoles.OutsideTemperature,
                ThermalEntityRoles.LeavingWaterTemperature, ThermalEntityRoles.ReturnWaterTemperature,
                ThermalEntityRoles.Flow, ThermalEntityRoles.DhwActive, ThermalEntityRoles.DefrostActive) ||
            !HasConsistentHeat(sample)) return null;
        var validRooms = ReadRooms(sample).Where(pair => rooms.Any(room => room.Enabled &&
            room.EntityId.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) &&
            pair.Value >= room.MinimumValidC && pair.Value <= room.MaximumValidC)).Select(x => x.Value).ToArray();
        if (validRooms.Length == 0) return null;
        // Optional weather features only receive independently valid saved values.
        var wind = HasQuality(sample, [], entities, now, ThermalEntityRoles.WindSpeed) ? sample.WindSpeedMps : null;
        var solar = HasQuality(sample, [], entities, now, ThermalEntityRoles.SolarIrradiance) ? sample.SolarIrradianceWm2 : null;
        // One compressor/shared hydraulic zone: tank heat is not space heat.
        // Keep observed room/outdoor evolution through DHW so a daily tank run
        // does not remove every otherwise complete 24-hour validation window.
        var spaceHeat = sample.DhwActive == true ? 0 : sample.HeatOutputKw!.Value;
        var observation = new ThermalObservation(sample.TimestampUtc, validRooms.Average(), sample.OutsideTemperatureC!.Value,
            spaceHeat, sample.DhwActive == true ? null : sample.LeavingWaterTemperatureC, null, null, sample.BackupHeaterActive == true, wind, solar);
        return GreyBoxThermalModel.IsUsableObservation(observation) ? observation : null;
    }

    internal static CopObservation? Cop(
        ThermalTelemetrySample sample, IReadOnlyCollection<ThermalEntityConfig> entities, DateTimeOffset now)
    {
        if (sample.BackupHeaterActive != false || sample.DefrostActive != false || !HasConsistentHeat(sample) ||
            sample.HeatPumpPowerKw is not > .1 || sample.Cop is null ||
            !HasQuality(sample, [], entities, now, ThermalEntityRoles.BrineIn, ThermalEntityRoles.LeavingWaterTemperature,
                ThermalEntityRoles.ReturnWaterTemperature, ThermalEntityRoles.Flow, ThermalEntityRoles.HeatPumpPower,
                ThermalEntityRoles.BackupHeaterActive, ThermalEntityRoles.DefrostActive)) return null;
        var actual = sample.HeatOutputKw!.Value / sample.HeatPumpPowerKw.Value;
        if (!double.IsFinite(actual) || Math.Abs(actual - sample.Cop.Value) > .01) return null;
        var observation = new CopObservation(sample.TimestampUtc, sample.BrineInC!.Value,
            sample.LeavingWaterTemperatureC!.Value, sample.HeatOutputKw.Value, sample.Cop.Value);
        return CopModel.IsUsableObservation(observation) ? observation : null;
    }

    internal static Dictionary<string, double> ReadRooms(ThermalTelemetrySample sample)
    {
        using var temperatures = Object(sample.RoomTemperaturesJson);
        using var quality = Object(sample.QualityJson);
        if (temperatures is null || quality is null) return [];
        var assessments = Property(quality.RootElement, "rooms");
        return temperatures.RootElement.EnumerateObject()
            .Where(x => Number(temperatures.RootElement, x.Name) is not null && ValidAssessment(Property(assessments, x.Name)))
            .ToDictionary(x => x.Name, x => x.Value.GetDouble(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasQuality(ThermalTelemetrySample sample, IEnumerable<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities, DateTimeOffset now, params string[] roles)
    {
        if (sample.TimestampUtc == default || sample.TimestampUtc > now) return false;
        var selected = roles.Select(role => entities.FirstOrDefault(x => x.Enabled && x.Role.Equals(role, StringComparison.OrdinalIgnoreCase))).ToArray();
        return selected.All(x => x is not null) && ThermalStatusQuality.Assess(sample, rooms, selected.Select(x => x!),
            sample.TimestampUtc, allowHistoryImport: true).Quality == DataQuality.Valid;
    }

    private static bool HasConsistentHeat(ThermalTelemetrySample sample)
    {
        if (sample.HeatOutputKw is not >= 0 || !double.IsFinite(sample.HeatOutputKw.Value) ||
            sample.FlowLitresPerMinute is not >= 0 || sample.LeavingWaterTemperatureC is null || sample.ReturnWaterTemperatureC is null) return false;
        var derived = Math.Max(0, sample.FlowLitresPerMinute.Value / 60 * 4.186 * (sample.LeavingWaterTemperatureC.Value - sample.ReturnWaterTemperatureC.Value));
        return double.IsFinite(derived) && Math.Abs(derived - sample.HeatOutputKw.Value) <= Math.Max(.05, derived * .01);
    }
}
