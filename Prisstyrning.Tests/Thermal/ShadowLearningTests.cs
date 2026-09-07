using System.Text.Json.Nodes;
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
        db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5)));
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
        db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5)));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var before = (await db.ThermalModelVersions.SingleAsync()).MetricsJson;
        var outcome = Sample(now.AddHours(2));
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
        db.ThermalTelemetrySamples.Add(Sample(DateTimeOffset.UtcNow.AddMinutes(-5)));
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
        db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5)));
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
        db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var versions = await job.GetAsync("account-a", now, CancellationToken.None);
        Assert.Equal(expectedModel ? 1 : 0, versions.Count);
        if (expectedModel)
        {
            // Later scoring must use the same relevant rooms as fitting.
            db.ThermalTelemetrySamples.Add(Sample(now.AddHours(2)));
            await db.SaveChangesAsync();
            var scored = Assert.Single(await job.GetAsync("account-a", now.AddHours(2), CancellationToken.None));
            Assert.Equal(0, scored.TwoHourErrorC);
        }
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Shadow", (await db.ThermalSiteConfigs.SingleAsync()).ControlMode);
        Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
    }

    [Fact]
    public async Task RepeatedAssumedStatesCanStartBaseline_ButCannotTrainTrendOrScoreTheirOwnForecast()
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 300; i++) db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5 * i), true, now.AddDays(-2)));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var version = Assert.Single(await job.GetAsync("account-a", now, CancellationToken.None));
        Assert.Equal(300, version.Learning.Samples);
        Assert.Equal(300, version.Learning.AssumedSamples);
        Assert.Equal(0, version.Learning.IndependentSamples);
        Assert.True(version.Learning.InitialTemperatureAssumed);
        Assert.Equal("Persistence", version.Learning.Stage);
        Assert.Null(version.Learning.HeldOutMaeC);
        Assert.Null(version.Learning.PersistenceMaeC);
        db.ThermalTelemetrySamples.Add(Sample(now.AddHours(2), true, now.AddDays(-2)));
        await db.SaveChangesAsync();
        version = Assert.Single(await job.GetAsync("account-a", now.AddHours(2), CancellationToken.None));
        Assert.Null(version.TwoHourErrorC);
        Assert.All(version.Learning.Forecast, p => Assert.Null(p.ActualC));
        Assert.False((await db.ThermalModelVersions.SingleAsync()).IsActive);
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
    }

    [Fact]
    public async Task AssumedInitialValueCanBeScoredByLaterRealReports()
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        db.ThermalTelemetrySamples.Add(Sample(now, true));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        db.ThermalTelemetrySamples.Add(Sample(now.AddHours(2)));
        await db.SaveChangesAsync();
        var version = Assert.Single(await job.GetAsync("account-a", now.AddHours(2), CancellationToken.None));
        Assert.Equal(0, version.TwoHourErrorC);
        Assert.NotNull(version.Learning.Forecast[7].ActualC);
    }

    [Fact]
    public async Task RepeatedValidReportIsNotIndependent_AndPreIssueReportCannotScoreForecast()
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++) db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5 * i), source: now.AddMinutes(-10)));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var version = Assert.Single(await job.GetAsync("account-a", now, CancellationToken.None));
        Assert.Equal(3, version.Learning.Samples);
        Assert.Equal(1, version.Learning.IndependentSamples);
        db.ThermalTelemetrySamples.Add(Sample(now.AddHours(2), source: now.AddMinutes(-10)));
        await db.SaveChangesAsync();
        version = Assert.Single(await job.GetAsync("account-a", now.AddHours(2), CancellationToken.None));
        Assert.Null(version.TwoHourErrorC);
    }

    [Fact]
    public async Task OldValidPayloadWithoutSourceTimeMayStartButDoesNotCreateIndependentEvidence()
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        db.ThermalTelemetrySamples.Add(ThermalModelTrainingDataTests.ValidSample(now));
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var version = Assert.Single(await job.GetAsync("account-a", now, CancellationToken.None));
        Assert.Equal(0, version.Learning.IndependentSamples);
        Assert.Null(version.Learning.HeldOutMaeC);
    }

    [Fact]
    public async Task NewReportsEveryFifteenMinutesCanLearnTrendWithoutInventingFiveMinuteMeasurements()
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 120; i++)
        {
            var sample = Sample(now.AddMinutes((i - 119) * 15));
            sample.RoomTemperaturesJson = new JsonObject { ["sensor.room"] = 20 + .025 * i }.ToJsonString();
            db.ThermalTelemetrySamples.Add(sample);
        }
        await db.SaveChangesAsync();
        var job = new ShadowLearningJob(db);
        await job.TrainAsync("account-a", now, CancellationToken.None);
        var version = Assert.Single(await job.GetAsync("account-a", now, CancellationToken.None));
        Assert.Equal(120, version.Learning.IndependentSamples);
        Assert.Equal("DampedTrend", version.Learning.Stage);
        Assert.True(version.Learning.HeldOutMaeC < version.Learning.PersistenceMaeC);
        Assert.False((await db.ThermalModelVersions.SingleAsync()).IsActive);
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("old-held-room")]
    [InlineData("excluded")]
    [InlineData("invalid")]
    [InlineData("unavailable")]
    [InlineData("bad-value")]
    [InlineData("old-receipt")]
    [InlineData("missing-receipt")]
    [InlineData("future-report")]
    [InlineData("history")]
    [InlineData("missing-collected")]
    [InlineData("out-of-range")]
    public async Task InvalidAssumptionMetadataCannotStartShadowOrFallBackToEarlierAcceptedRow(string fault)
    {
        await using var db = Database();
        Configure(db);
        var now = DateTimeOffset.UtcNow;
        db.ThermalTelemetrySamples.Add(Sample(now.AddMinutes(-5), true));
        var sample = Sample(now, true);
        var root = JsonNode.Parse(sample.QualityJson)!;
        var room = root["rooms"]!["sensor.room"]!;
        if (fault == "missing-marker") room.AsObject().Remove("Usage");
        if (fault == "old-held-room") room["Usage"] = "HeldRoom";
        if (fault == "excluded") room["Excluded"] = true;
        if (fault == "invalid") room["Quality"] = 2;
        if (fault == "unavailable") room["Quality"] = 3;
        if (fault == "bad-value") room["Value"] = "21.5";
        if (fault == "out-of-range") room["Value"] = 70;
        if (fault == "old-receipt") room["ReceivedAtUtc"] = now.AddMinutes(-11);
        if (fault == "missing-receipt") room.AsObject().Remove("ReceivedAtUtc");
        if (fault == "future-report") room["SourceTimestampUtc"] = now.AddMinutes(1);
        if (fault == "history") root["source"] = "HomeAssistantHistoryImport";
        if (fault == "missing-collected") root.AsObject().Remove("collectedAtUtc");
        sample.QualityJson = root.ToJsonString();
        db.ThermalTelemetrySamples.Add(sample);
        await db.SaveChangesAsync();
        await new ShadowLearningJob(db).TrainAsync("account-a", now, CancellationToken.None);
        Assert.Empty(await db.ThermalModelVersions.ToListAsync());
    }

    private static ThermalTelemetrySample Sample(DateTimeOffset timestamp, bool assumed = false, DateTimeOffset? source = null)
    {
        var sample = ThermalModelTrainingDataTests.ValidSample(timestamp);
        var root = JsonNode.Parse(sample.QualityJson)!;
        root["collectedAtUtc"] = timestamp;
        root["rooms"]!["sensor.room"] = new JsonObject
        {
            ["Quality"] = assumed ? 1 : 0, ["Excluded"] = false,
            ["Usage"] = assumed ? "AssumedUnchanged" : "Current", ["Value"] = 21.5,
            ["SourceTimestampUtc"] = source ?? (assumed ? timestamp.AddHours(-12) : timestamp),
            ["ValueUpdatedUtc"] = source ?? (assumed ? timestamp.AddHours(-12) : timestamp),
            ["ReceivedAtUtc"] = timestamp
        };
        if (assumed) sample.RoomTemperaturesJson = "{}";
        sample.QualityJson = root.ToJsonString();
        return sample;
    }

    private static PrisstyrningDbContext Database() => new(new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static void Configure(PrisstyrningDbContext db, string mode = "Shadow")
    {
        db.ThermalSiteConfigs.Add(new() { UserId = "account-a", ControlMode = mode });
        db.ThermalRoomConfigs.AddRange(ThermalModelTrainingDataTests.Rooms.Select(x => new ThermalRoomConfig
        { UserId = "account-a", EntityId = x.EntityId, IsCritical = x.IsCritical }));
    }
}
