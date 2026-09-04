using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalModelProvenanceTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddMinutes(15);

    [Fact]
    public void Create_IsDeterministicAndBindsAccountSamplesAndConfiguration()
    {
        var baseline = Create("account-a", Samples("account-a"), Rooms("account-a"), Entities("account-a"));
        var repeated = Create("account-a", Samples("account-a"), Rooms("account-a"), Entities("account-a"));
        var changedSamples = Samples("account-a");
        changedSamples[1].HeatOutputKw = 4.2;
        var changedRooms = Rooms("account-a");
        changedRooms[0].Weight = 2;
        var sampleChange = Create("account-a", changedSamples, Rooms("account-a"), Entities("account-a"));
        var configChange = Create("account-a", Samples("account-a"), changedRooms, Entities("account-a"));
        var accountChange = Create("account-b", Samples("account-b"), Rooms("account-b"), Entities("account-b"));

        Assert.Equal(baseline.SampleFingerprint, repeated.SampleFingerprint);
        Assert.Equal(baseline.ConfigurationFingerprint, repeated.ConfigurationFingerprint);
        Assert.NotEqual(baseline.SampleFingerprint, sampleChange.SampleFingerprint);
        Assert.Equal(baseline.ConfigurationFingerprint, sampleChange.ConfigurationFingerprint);
        Assert.Equal(baseline.SampleFingerprint, configChange.SampleFingerprint);
        Assert.NotEqual(baseline.ConfigurationFingerprint, configChange.ConfigurationFingerprint);
        Assert.NotEqual(baseline.SampleFingerprint, accountChange.SampleFingerprint);
        Assert.NotEqual(baseline.ConfigurationFingerprint, accountChange.ConfigurationFingerprint);
        Assert.Matches("^[0-9A-F]{64}$", baseline.SampleFingerprint);
        Assert.Matches("^[0-9A-F]{64}$", baseline.ConfigurationFingerprint);
        Assert.Equal(ThermalCurrentModelTestData.BuildRevision, baseline.BuildRevision);
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-time")]
    [InlineData("wrong-account")]
    [InlineData("outside-window")]
    [InlineData("incomplete-split")]
    [InlineData("wrong-config-account")]
    [InlineData("duplicate-config")]
    public void Create_RejectsAnAmbiguousOrInconsistentSelection(string fault)
    {
        var samples = Samples("account-a");
        var entities = Entities("account-a");
        if (fault == "duplicate-id") samples[1].Id = samples[0].Id;
        if (fault == "duplicate-time") samples[1].TimestampUtc = samples[0].TimestampUtc;
        if (fault == "wrong-account") samples[1].UserId = "account-b";
        if (fault == "outside-window") samples[1].TimestampUtc = To.AddMinutes(1);
        if (fault == "wrong-config-account") entities[0].UserId = "account-b";
        if (fault == "duplicate-config") entities = [entities[0], new() { Id = 22, UserId = "account-a", Role = entities[0].Role, EntityId = "sensor.other" }];

        Assert.Throws<ArgumentException>(() => ThermalModelProvenance.Create(
            "account-a", "2R2C", From, To, samples, Rooms("account-a"), entities,
            fault == "incomplete-split" ? 3 : 2, 1, heatPumpPowerSignVerified: false,
            ThermalCurrentModelTestData.BuildRevision));
    }

    [Fact]
    public void Summary_ReturnsOnlySafeMetadataAndRejectsUnknownEvidenceVersions()
    {
        var model = ThermalModelEvidenceTests.ValidModel("COP", To.AddDays(31));
        var valid = ThermalModelProvenance.Summary(model);
        var source = ThermalModelProvenance.Read(model)!;

        Assert.True(valid.Verifiable);
        Assert.Equal(ThermalModelProvenance.CopAlgorithmVersion, valid.AlgorithmVersion);
        Assert.Equal(ThermalCurrentModelTestData.BuildRevision, valid.BuildRevision);
        Assert.Equal(600, valid.ObservationCount);

        model.SourceEvidenceJson = ThermalModelProvenance.Serialize(source with { SchemaVersion = 3 });
        var unknown = ThermalModelProvenance.Summary(model);

        Assert.False(unknown.Verifiable);
        Assert.Null(unknown.AlgorithmVersion);
        Assert.Null(unknown.BuildRevision);
        Assert.Null(unknown.ObservationCount);
    }

    private static ThermalModelSourceEvidence Create(
        string userId,
        ThermalTelemetrySample[] samples,
        ThermalRoomConfig[] rooms,
        ThermalEntityConfig[] entities) =>
        ThermalModelProvenance.Create(userId, "2R2C", From, To, samples, rooms, entities, 2, 1,
            heatPumpPowerSignVerified: false, ThermalCurrentModelTestData.BuildRevision);

    private static ThermalTelemetrySample[] Samples(string userId) =>
    [
        Sample(userId, 101, From.AddMinutes(5), 4.0),
        Sample(userId, 102, From.AddMinutes(10), 4.1),
        Sample(userId, 103, From.AddMinutes(15), 4.2)
    ];

    private static ThermalTelemetrySample Sample(string userId, long id, DateTimeOffset timestamp, double heatOutput) => new()
    {
        Id = id,
        UserId = userId,
        TimestampUtc = timestamp,
        OutsideTemperatureC = 5,
        LeavingWaterTemperatureC = 35,
        ReturnWaterTemperatureC = 30,
        FlowLitresPerMinute = 12,
        HeatOutputKw = heatOutput,
        DhwActive = false,
        DefrostActive = false,
        BackupHeaterActive = false,
        RoomTemperaturesJson = "{\"sensor.room\":21.5}",
        QualityJson = "{\"source\":\"Live\"}"
    };

    private static ThermalRoomConfig[] Rooms(string userId) =>
    [
        new() { Id = 11, UserId = userId, EntityId = "sensor.room", Name = "Vardagsrum", IsCritical = true }
    ];

    private static ThermalEntityConfig[] Entities(string userId) =>
    [
        new() { Id = 21, UserId = userId, Role = "outside_temperature", EntityId = "sensor.outside", ExpectedUnit = "°C" }
    ];
}
