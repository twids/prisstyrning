using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalDiagnosticsTests
{
    [Fact]
    public void Analyze_FindsSixHourRoomBalanceProblemOnlyForValidSensor()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 73).Select(index => Sample(
            start.AddMinutes(index * 5),
            new Dictionary<string, double> { ["sensor.cold"] = 20, ["sensor.warm"] = 21.5 },
            valid: true)).ToArray();
        var rooms = new[]
        {
            new ThermalRoomConfig { Name = "Sovrum", EntityId = "sensor.cold", Enabled = true },
            new ThermalRoomConfig { Name = "Vardagsrum", EntityId = "sensor.warm", Enabled = true }
        };

        var findings = ThermalDiagnosticsService.Analyze(samples, rooms, new ThermalSiteConfig
        {
            BaseRoomTargetC = 21,
            LowerComfortBandC = 0.5
        });

        Assert.Contains(findings, x => x.Code == "room-balance:sensor.cold" && x.Category == "RoomBalance");

        samples[^1].QualityJson = QualityJson(["sensor.cold", "sensor.warm"], valid: false);
        findings = ThermalDiagnosticsService.Analyze(samples, rooms, new ThermalSiteConfig
        {
            BaseRoomTargetC = 21,
            LowerComfortBandC = 0.5
        });
        Assert.DoesNotContain(findings, x => x.Code == "room-balance:sensor.cold");
    }

    [Fact]
    public void Analyze_FindsSustainedHighDeltaTButIgnoresDhw()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 7).Select(index => new ThermalTelemetrySample
        {
            TimestampUtc = start.AddMinutes(index * 5),
            LeavingWaterTemperatureC = 45,
            ReturnWaterTemperatureC = 28,
            FlowLitresPerMinute = 3,
            HeatPumpPowerKw = 2,
            DhwActive = false
        }).ToArray();

        var findings = ThermalDiagnosticsService.Analyze(samples, [], new ThermalSiteConfig());
        Assert.Contains(findings, x => x.Code == "hydraulics:high-delta-t");

        foreach (var sample in samples) sample.DhwActive = true;
        findings = ThermalDiagnosticsService.Analyze(samples, [], new ThermalSiteConfig());
        Assert.DoesNotContain(findings, x => x.Category == "Hydraulics");
    }

    private static ThermalTelemetrySample Sample(
        DateTimeOffset timestamp,
        Dictionary<string, double> rooms,
        bool valid) => new()
    {
        TimestampUtc = timestamp,
        RoomTemperaturesJson = JsonSerializer.Serialize(rooms),
        QualityJson = QualityJson(rooms.Keys, valid),
        LeavingWaterTemperatureC = 35,
        ReturnWaterTemperatureC = 30,
        FlowLitresPerMinute = 8,
        HeatPumpPowerKw = 1.5
    };

    private static string QualityJson(IEnumerable<string> entityIds, bool valid) => JsonSerializer.Serialize(new
    {
        rooms = entityIds.ToDictionary(
            entityId => entityId,
            _ => new { quality = valid ? 0 : 2, excluded = !valid })
    });
}
