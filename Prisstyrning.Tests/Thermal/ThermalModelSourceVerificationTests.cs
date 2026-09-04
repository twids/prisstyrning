using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalModelSourceVerificationTests
{
    [Fact]
    public async Task VerifyCurrent_ExactHistoricalRowsAndConfigurationRemainCurrent()
    {
        await using var fixture = await FixtureAsync("2R2C", "COP");

        var result = await VerifyAsync(fixture.Db, fixture.Now, fixture.Models, heatPumpPowerSignVerified: true);

        Assert.All(fixture.Models, model =>
        {
            var validation = result[model.Id];
            Assert.True(validation.Passed);
            Assert.Equal("Current", validation.Status);
            Assert.Equal(fixture.Now, validation.CheckedAtUtc);
        });
    }

    [Theory]
    [InlineData("changed-row", "2R2C")]
    [InlineData("deleted-row", "2R2C")]
    [InlineData("backfilled-row", "2R2C")]
    [InlineData("room-config", "2R2C")]
    [InlineData("entity-config", "COP")]
    [InlineData("power-sign", "COP")]
    public async Task VerifyCurrent_SourceOrTrainingConfigurationChangeFailsClosed(
        string fault,
        string expectedModelType)
    {
        await using var fixture = await FixtureAsync("2R2C", "COP");
        var thermal = fixture.Models.Single(x => x.ModelType == "2R2C");
        var firstSourceId = ThermalModelProvenance.Read(thermal)!.FirstSampleId;
        var sourceSample = await fixture.Db.ThermalTelemetrySamples.SingleAsync(x => x.Id == firstSourceId);
        if (fault == "changed-row") sourceSample.RoomTemperaturesJson = "{\"sensor.room\":21.6}";
        if (fault == "deleted-row") fixture.Db.ThermalTelemetrySamples.Remove(sourceSample);
        if (fault == "backfilled-row")
        {
            var sample = ThermalModelTrainingDataTests.ValidSample(sourceSample.TimestampUtc.AddMinutes(1));
            sample.UserId = "account-a";
            fixture.Db.ThermalTelemetrySamples.Add(sample);
        }
        if (fault == "room-config")
            (await fixture.Db.ThermalRoomConfigs.SingleAsync()).Weight = 2;
        if (fault == "entity-config")
            (await fixture.Db.ThermalEntityConfigs.FirstAsync()).ExpectedUnit = "changed";
        await fixture.Db.SaveChangesAsync();

        var result = await VerifyAsync(
            fixture.Db,
            fixture.Now,
            fixture.Models,
            heatPumpPowerSignVerified: fault != "power-sign");

        var validation = result[fixture.Models.Single(x => x.ModelType == expectedModelType).Id];
        Assert.False(validation.Passed);
        Assert.Equal("Changed", validation.Status);
        Assert.Contains("Träna", validation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyCurrent_OtherAccountsRowsCannotChangeOrAmbiguateSelection()
    {
        await using var fixture = await FixtureAsync("2R2C", "COP");
        var own = await fixture.Db.ThermalTelemetrySamples
            .Where(x => x.UserId == "account-a").OrderBy(x => x.TimestampUtc).FirstAsync();
        var foreign = ThermalModelTrainingDataTests.ValidSample(own.TimestampUtc);
        foreign.UserId = "account-b";
        foreign.RoomTemperaturesJson = "{\"sensor.room\":5}";
        fixture.Db.ThermalTelemetrySamples.Add(foreign);
        await fixture.Db.SaveChangesAsync();

        var result = await VerifyAsync(fixture.Db, fixture.Now, fixture.Models, heatPumpPowerSignVerified: true);

        Assert.All(result.Values, validation =>
        {
            Assert.True(validation.Passed);
            Assert.Equal("Current", validation.Status);
        });
    }

    [Fact]
    public async Task VerifyCurrent_MissingEvidenceIsUnprovenWithoutReadingForeignHistory()
    {
        await using var db = Database();
        var model = ThermalModelEvidenceTests.ValidModel("2R2C", DateTimeOffset.UtcNow);
        model.SourceEvidenceJson = "{}";
        db.ThermalModelVersions.Add(model);
        await db.SaveChangesAsync();
        var checkedAt = DateTimeOffset.UtcNow;

        var result = await ThermalModelProvenance.VerifyCurrentAsync(
            db, "account-a", [model], [], [], false, checkedAt, CancellationToken.None,
            ThermalCurrentModelTestData.Build);

        var validation = result[model.Id];
        Assert.False(validation.Passed);
        Assert.Equal("Unproven", validation.Status);
        Assert.DoesNotContain("hash", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("changed", "BuildChanged")]
    [InlineData("missing", "Unproven")]
    public async Task VerifyCurrent_RunningBuildMustMatchTheTrainingBuild(string fault, string expectedStatus)
    {
        await using var fixture = await FixtureAsync("2R2C");
        var running = fault == "changed"
            ? RuntimeBuildProvenance.FromRevision("fedcba9876543210fedcba9876543210fedcba98")
            : RuntimeBuildProvenance.FromRevision(null);
        var rooms = await fixture.Db.ThermalRoomConfigs.AsNoTracking().Where(x => x.Enabled).ToListAsync();
        var entities = await fixture.Db.ThermalEntityConfigs.AsNoTracking().Where(x => x.Enabled).ToListAsync();

        var result = await ThermalModelProvenance.VerifyCurrentAsync(
            fixture.Db, "account-a", fixture.Models, rooms, entities, true, fixture.Now,
            CancellationToken.None, running);

        var validation = Assert.Single(result.Values);
        Assert.False(validation.Passed);
        Assert.Equal(expectedStatus, validation.Status);
        Assert.Contains("revision", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Fixture> FixtureAsync(params string[] modelTypes)
    {
        var db = Database();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig
        { UserId = "account-a", HeatPumpPowerSignVerified = true });
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig
        { UserId = "account-a", EntityId = "sensor.room", IsCritical = true });
        db.ThermalEntityConfigs.AddRange(ThermalModelTrainingDataTests.Entities.Select(entity => new ThermalEntityConfig
        {
            UserId = "account-a",
            Role = entity.Role,
            EntityId = entity.EntityId
        }));
        var models = await ThermalCurrentModelTestData.SeedAsync(db, "account-a", now, modelTypes);
        return new(db, now, models);
    }

    private static async Task<IReadOnlyDictionary<long, ThermalModelSourceValidation>> VerifyAsync(
        PrisstyrningDbContext db,
        DateTimeOffset now,
        IReadOnlyCollection<ThermalModelVersion> models,
        bool heatPumpPowerSignVerified)
    {
        var rooms = await db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == "account-a" && x.Enabled).ToListAsync();
        var entities = await db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == "account-a" && x.Enabled).ToListAsync();
        return await ThermalModelProvenance.VerifyCurrentAsync(
            db,
            "account-a",
            models,
            rooms,
            entities,
            heatPumpPowerSignVerified,
            now,
            CancellationToken.None,
            ThermalCurrentModelTestData.Build);
    }

    private static PrisstyrningDbContext Database() => new(
        new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fixture(
        PrisstyrningDbContext Db,
        DateTimeOffset Now,
        IReadOnlyList<ThermalModelVersion> Models) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
