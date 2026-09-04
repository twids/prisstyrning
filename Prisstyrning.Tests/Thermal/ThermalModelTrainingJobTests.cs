using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalModelTrainingJobTests
{
    [Fact]
    public async Task ThermalTraining_ValidDailyDhwDataProducesAuditableModelWithoutChangingLegacy()
    {
        await using var db = Database();
        Configure(db);
        var end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300 * 300).AddMinutes(-5);
        var samples = Enumerable.Range(0, 6500).Select(index =>
        {
            var sample = ThermalModelTrainingDataTests.ValidSample(end.AddMinutes((index - 6499) * 5));
            sample.OutsideTemperatureC = 21.5;
            sample.DhwActive = index % 288 < 6;
            if (sample.DhwActive == false) { sample.LeavingWaterTemperatureC = 30; sample.HeatOutputKw = 0; }
            return sample;
        });
        db.ThermalTelemetrySamples.AddRange(samples);
        await db.SaveChangesAsync();

        await new ThermalModelTrainingJob(db, new GreyBoxThermalModel(), ThermalCurrentModelTestData.Build)
            .TrainUserAsync("account-a", CancellationToken.None);

        var version = await db.ThermalModelVersions.SingleAsync();
        Assert.True(version.IsActive);
        Assert.True(ThermalModelEvidence.Assess(version, DateTimeOffset.UtcNow).Passed);
        Assert.Contains("\"dayValidationWindows\":", version.MetricsJson);
        var provenance = ThermalModelProvenance.Read(version);
        Assert.NotNull(provenance);
        Assert.Equal(ThermalModelProvenance.ThermalAlgorithmVersion, provenance.AlgorithmVersion);
        Assert.Equal(6500, provenance.ObservationCount);
        Assert.DoesNotContain("account-a", version.SourceEvidenceJson, StringComparison.OrdinalIgnoreCase);
        await AssertLegacyAsync(db);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopTraining_RequiresVerifiedMeterAndKeepsStoredVersionOnMissingEvidence(bool verified)
    {
        await using var db = Database();
        Configure(db, verified);
        var end = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.ThermalTelemetrySamples.AddRange(Enumerable.Range(0, 600).Select(index =>
            ThermalModelTrainingDataTests.ValidSample(end.AddMinutes((index - 599) * 5))));
        var old = ThermalModelEvidenceTests.ValidModel("COP", end);
        db.ThermalModelVersions.Add(old);
        await db.SaveChangesAsync();

        await new CopModelTrainingJob(db, new CopModel(), ThermalCurrentModelTestData.Build)
            .TrainUserAsync("account-a", CancellationToken.None);

        var versions = await db.ThermalModelVersions.ToListAsync();
        Assert.Equal(verified ? 2 : 1, versions.Count);
        Assert.Single(versions.Where(x => x.IsActive));
        Assert.Equal(!verified, old.IsActive);
        if (verified)
        {
            var provenance = ThermalModelProvenance.Read(versions.Single(x => x != old));
            Assert.NotNull(provenance);
            Assert.Equal(ThermalModelProvenance.CopAlgorithmVersion, provenance.AlgorithmVersion);
            Assert.Equal(600, provenance.ObservationCount);
        }
        await AssertLegacyAsync(db);
    }

    [Fact]
    public async Task CopTraining_UnknownHeaterRowsNeverReplacePreviousModel()
    {
        await using var db = Database();
        Configure(db, true);
        var end = DateTimeOffset.UtcNow.AddMinutes(-5);
        db.ThermalTelemetrySamples.AddRange(Enumerable.Range(0, 600).Select(index =>
        {
            var sample = ThermalModelTrainingDataTests.ValidSample(end.AddMinutes((index - 599) * 5));
            sample.BackupHeaterActive = null;
            return sample;
        }));
        db.ThermalModelVersions.Add(ThermalModelEvidenceTests.ValidModel("COP", end));
        await db.SaveChangesAsync();

        await new CopModelTrainingJob(db, new CopModel(), ThermalCurrentModelTestData.Build)
            .TrainUserAsync("account-a", CancellationToken.None);

        Assert.True((await db.ThermalModelVersions.SingleAsync()).IsActive);
        await AssertLegacyAsync(db);
    }

    private static PrisstyrningDbContext Database() => new(new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static void Configure(PrisstyrningDbContext db, bool meterVerified = false)
    {
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", HeatPumpPowerSignVerified = meterVerified });
        db.ThermalRoomConfigs.AddRange(ThermalModelTrainingDataTests.Rooms.Select(x => new ThermalRoomConfig { UserId = x.UserId, EntityId = x.EntityId, IsCritical = x.IsCritical }));
        db.ThermalEntityConfigs.AddRange(ThermalModelTrainingDataTests.Entities.Select(x => new ThermalEntityConfig { UserId = x.UserId, EntityId = x.EntityId, Role = x.Role }));
    }
    private static async Task AssertLegacyAsync(PrisstyrningDbContext db)
    {
        var site = await db.ThermalSiteConfigs.SingleAsync();
        Assert.Equal("Legacy", site.ControlMode);
        Assert.Equal("Legacy", site.DhwWriter);
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
    }
}
