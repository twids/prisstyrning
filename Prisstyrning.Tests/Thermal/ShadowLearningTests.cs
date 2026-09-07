using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public class ShadowLearningTests
{
    [Fact]
    public async Task StartsWithOneValidRoomObservation_WithoutAnyHeatModelOrWrites()
    {
        await using var db = Database();
        var now = DateTimeOffset.UtcNow;
        Configure(db);
        db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(now.AddMinutes(-5)));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var model = await db.ThermalModelVersions.SingleAsync();
        Assert.Equal("ShadowTrend", model.ModelType);
        Assert.False(model.IsActive);
        Assert.False(ThermalModelEvidence.Assess(model, now).Passed);
        var result = Assert.Single(await job.GetAsync("account-a", now, CancellationToken.None));
        Assert.Equal("Persistence", result.Learning.Stage);
        Assert.Null(result.TwoHourErrorC);
        Assert.All(result.Learning.Forecast, x => Assert.Null(x.ActualC));
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
    }

    [Fact]
    public async Task ScoresOnlyLaterObservedOutcome_AndNeverRewritesIssuedForecast()
    {
        await using var db = Database();
        var now = DateTimeOffset.UtcNow.AddHours(-3);
        Configure(db);
        db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(now.AddMinutes(-5)));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var before = (await db.ThermalModelVersions.SingleAsync()).MetricsJson;
        var outcome = ThermalModelTrainingDataTests.ValidSample(now.AddHours(2));
        db.ThermalTelemetrySamples.Add(outcome);
        await db.SaveChangesAsync();
        var result = Assert.Single(await job.GetAsync("account-a", now.AddHours(3), CancellationToken.None));
        Assert.Equal(0, result.TwoHourErrorC);
        Assert.Null(result.DayErrorC);
        Assert.Equal(before, (await db.ThermalModelVersions.SingleAsync()).MetricsJson);
        Assert.Empty(await job.GetAsync("another-user", now.AddHours(3), CancellationToken.None));
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("LwtActive")]
    [InlineData("FullActive")]
    public async Task DoesNotTrainOutsideExplicitShadow(string mode)
    {
        await using var db = Database();
        Configure(db, mode);
        db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(DateTimeOffset.UtcNow.AddMinutes(-5)));
        await db.SaveChangesAsync();
        await new ShadowLearningJob(db).ExecuteAsync();
        Assert.Empty(await db.ThermalModelVersions.ToListAsync());
    }

    [Fact]
    public async Task ChangedRoomConfigurationHidesIncomparableScores()
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(now.AddMinutes(-5)));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        (await db.ThermalRoomConfigs.FirstAsync()).TargetOffsetC = 1;
        await db.SaveChangesAsync();
        Assert.Empty(await job.GetAsync("account-a", now, CancellationToken.None));
        Assert.Single(await db.ThermalModelVersions.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    public async Task MissingRoomOnlyBlocksIfCriticalOrWeighted(bool critical, double weight, bool expectedModel)
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig
        {
            UserId = "account-a", EntityId = "sensor.observation_only", Enabled = true,
            IsCritical = critical, Weight = weight
        });
        db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(now.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var versions = await job.GetAsync("account-a", now, CancellationToken.None);
        Assert.Equal(expectedModel ? 1 : 0, versions.Count);
        if (expectedModel)
        {
            // Later scoring must use the same relevant rooms as fitting.
            db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(now.AddHours(2)));
            await db.SaveChangesAsync();
            var scored = Assert.Single(await job.GetAsync("account-a", now.AddHours(2), CancellationToken.None));
            Assert.Equal(0, scored.TwoHourErrorC);
        }
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Shadow", (await db.ThermalSiteConfigs.SingleAsync()).ControlMode);
        Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
    }

    private static PrisstyrningDbContext Database() => new(new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static void Configure(PrisstyrningDbContext db, string mode = "Shadow")
    {
        db.ThermalSiteConfigs.Add(new() { UserId = "account-a", ControlMode = mode });
        db.ThermalRoomConfigs.AddRange(ThermalModelTrainingDataTests.Rooms.Select(x => new ThermalRoomConfig
        { UserId = "account-a", EntityId = x.EntityId, IsCritical = x.IsCritical }));
    }
}
