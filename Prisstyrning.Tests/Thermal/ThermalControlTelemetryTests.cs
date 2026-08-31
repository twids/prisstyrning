using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalControlTelemetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidLiveSignalsAndRoomsProvideControlInputs()
    {
        var sample = Sample();
        sample.DhwActive = true;
        sample.RoomTemperaturesJson = JsonSerializer.Serialize(new Dictionary<string, double>
        {
            ["sensor.critical"] = 20.5,
            ["sensor.other"] = 22
        });

        var result = ThermalControlTelemetry.Assess(sample, Rooms(), Entities(), Site(), Now);

        Assert.True(result.SafeToControl);
        Assert.True(result.DhwActive);
        Assert.False(result.DefrostActive);
        Assert.True(result.CriticalRoomBelowMinimum);
        Assert.Equal(-0.25, result.RepresentativeTemperatureErrorC, 6);
        Assert.Equal(12, result.FlowLitresPerMinute);
    }

    [Fact]
    public void ExcludedCriticalFallbackCannotPretendToBeAColdLiveRoom()
    {
        var sample = Sample();
        sample.RoomTemperaturesJson = "{\"sensor.critical\":5,\"sensor.other\":21.5}";
        sample.QualityJson = QualityJson(criticalQuality: DataQuality.Invalid, criticalExcluded: true);

        var result = ThermalControlTelemetry.Assess(sample, Rooms(), Entities(), Site(), Now);

        Assert.True(result.SafeToControl);
        Assert.False(result.CriticalRoomBelowMinimum);
        Assert.Equal(0, result.RepresentativeTemperatureErrorC, 6);
    }

    [Theory]
    [InlineData("flow-quality")]
    [InlineData("dhw-missing")]
    [InlineData("defrost-unmapped")]
    [InlineData("all-rooms-invalid")]
    [InlineData("stale")]
    [InlineData("changed-config")]
    [InlineData("history-import")]
    public void UnverifiedSafetyInputFailsClosed(string fault)
    {
        var sample = Sample();
        var rooms = Rooms();
        var entities = Entities().ToList();
        var site = Site();
        if (fault == "flow-quality") sample.QualityJson = QualityJson(flowQuality: DataQuality.Stale);
        if (fault == "dhw-missing") sample.DhwActive = null;
        if (fault == "defrost-unmapped") entities.RemoveAll(x => x.Role == ThermalEntityRoles.DefrostActive);
        if (fault == "all-rooms-invalid") sample.QualityJson = QualityJson(DataQuality.Invalid, true, DataQuality.Unavailable, false);
        if (fault == "stale") sample.TimestampUtc = Now.AddMinutes(-11);
        if (fault == "changed-config") site.UpdatedAtUtc = Now;
        if (fault == "history-import") sample.QualityJson = "{\"source\":\"HomeAssistantHistoryImport\"," + sample.QualityJson[1..];

        var result = ThermalControlTelemetry.Assess(sample, rooms, entities, site, Now);

        Assert.False(result.SafeToControl);
        Assert.Equal(0, result.RepresentativeTemperatureErrorC);
        Assert.False(result.CriticalRoomBelowMinimum);
        Assert.Null(result.FlowLitresPerMinute);
        Assert.Contains("LWT återgår", result.InvalidReason);
    }

    private static ThermalTelemetrySample Sample() => new()
    {
        UserId = "account-a",
        TimestampUtc = Now.AddMinutes(-1),
        FlowLitresPerMinute = 12,
        DhwActive = false,
        DefrostActive = false,
        RoomTemperaturesJson = "{\"sensor.critical\":21.5,\"sensor.other\":21.5}",
        QualityJson = QualityJson()
    };

    private static ThermalSiteConfig Site() => new()
    {
        UserId = "account-a",
        BaseRoomTargetC = 21.5,
        LowerComfortBandC = 0.5,
        UpdatedAtUtc = Now.AddMinutes(-2)
    };

    private static ThermalRoomConfig[] Rooms() =>
    [
        new() { UserId = "account-a", EntityId = "sensor.critical", IsCritical = true, Weight = 1 },
        new() { UserId = "account-a", EntityId = "sensor.other", Weight = 1 }
    ];

    private static ThermalEntityConfig[] Entities() =>
    [
        Entity(ThermalEntityRoles.Flow),
        Entity(ThermalEntityRoles.DhwActive),
        Entity(ThermalEntityRoles.DefrostActive)
    ];

    private static ThermalEntityConfig Entity(string role) => new()
    {
        UserId = "account-a",
        Role = role,
        EntityId = $"sensor.{role}"
    };

    private static string QualityJson(
        DataQuality criticalQuality = DataQuality.Valid,
        bool criticalExcluded = false,
        DataQuality otherQuality = DataQuality.Valid,
        bool otherExcluded = false,
        DataQuality flowQuality = DataQuality.Valid) => JsonSerializer.Serialize(new
    {
        rooms = new Dictionary<string, object>
        {
            ["sensor.critical"] = new { quality = criticalQuality, excluded = criticalExcluded },
            ["sensor.other"] = new { quality = otherQuality, excluded = otherExcluded }
        },
        entities = new Dictionary<string, object>
        {
            [ThermalEntityRoles.Flow] = new { quality = flowQuality, excluded = false },
            [ThermalEntityRoles.DhwActive] = new { quality = DataQuality.Valid, excluded = false },
            [ThermalEntityRoles.DefrostActive] = new { quality = DataQuality.Valid, excluded = false }
        }
    });
}
