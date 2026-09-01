using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class EmhassModelConsumptionTests
{
    [Theory]
    [InlineData("missing-evidence")]
    [InlineData("missing-input-evidence")]
    [InlineData("wrong-fingerprint")]
    [InlineData("wrong-telemetry-fingerprint")]
    [InlineData("wrong-price-fingerprint")]
    [InlineData("wrong-account")]
    [InlineData("revoked-model")]
    [InlineData("rollback")]
    [InlineData("stale-telemetry")]
    [InlineData("disabled")]
    public async Task Worker_RejectsUnprovenOrRevokedQueuedRequestBeforeCallingSolver(string fault)
    {
        await using var fixture = await JointPlanModelConsumptionTests.Fixture.CreateAsync();
        await fixture.ReplanAsync();
        var request = fixture.Dispatcher.Request!;
        if (fault == "missing-evidence") request = request with { ModelEvidence = null };
        if (fault == "missing-input-evidence") request = request with { InputEvidence = null };
        if (fault == "wrong-fingerprint") request = request with { ModelEvidence = request.ModelEvidence! with { Fingerprint = "wrong" } };
        if (fault == "wrong-telemetry-fingerprint") request = request with { InputEvidence = request.InputEvidence! with { TelemetryFingerprint = "wrong" } };
        if (fault == "wrong-price-fingerprint") request = request with { InputEvidence = request.InputEvidence! with { PriceFingerprint = "wrong" } };
        if (fault == "stale-telemetry") request = request with { ModelEvidence = request.ModelEvidence! with { TelemetryTimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-11) } };
        await fixture.ChangeAsync(async db =>
        {
            if (fault == "revoked-model") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "COP")).IsActive = false;
            if (fault == "rollback") (await db.ThermalSiteConfigs.SingleAsync()).ControlMode = "Legacy";
        });
        var queue = CreateQueue(fixture);
        var claim = await EnqueueAsync(queue, request, fault == "wrong-account" ? "account-b" : "account-a");
        using var worker = CreateWorker(fixture, queue, enabled: fault != "disabled");

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() => worker.ProcessClaimAsync(claim, CancellationToken.None));

        Assert.Equal(0, fixture.Solver.Calls);
        await AssertNotCompletedAsync(fixture);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("settings")]
    [InlineData("entity")]
    [InlineData("rollback")]
    [InlineData("telemetry")]
    [InlineData("price")]
    [InlineData("zone")]
    public async Task Worker_DiscardsResultWhenEvidenceChangesDuringSolve(string change)
    {
        await using var fixture = await JointPlanModelConsumptionTests.Fixture.CreateAsync();
        await fixture.ReplanAsync();
        var queue = CreateQueue(fixture);
        var claim = await EnqueueAsync(queue, fixture.Dispatcher.Request!);
        using var worker = CreateWorker(fixture, queue);
        fixture.Solver.BeforeReturn = () => fixture.ChangeAsync(async db =>
        {
            if (change == "model") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C")).IsActive = false;
            if (change == "settings") (await db.ThermalSiteConfigs.SingleAsync()).LowerComfortBandC = .2;
            if (change == "entity") db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "account-a", EntityId = "sensor.new_outside", Role = "outside_temperature" });
            if (change == "rollback") (await db.ThermalSiteConfigs.SingleAsync()).ControlMode = "Legacy";
            if (change == "telemetry") (await db.ThermalTelemetrySamples.SingleAsync()).PropertyPowerKw += .1;
            if (change == "price") (await db.PriceSnapshots.SingleAsync()).TomorrowPricesJson = "[]";
            if (change == "zone") (await db.UserSettings.SingleAsync()).Zone = "SE2";
        });

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() => worker.ProcessClaimAsync(claim, CancellationToken.None));

        Assert.Equal(1, fixture.Solver.Calls);
        await AssertNotCompletedAsync(fixture);
    }

    [Fact]
    public async Task Worker_VerifiedRequestSurvivesPersistenceAndIgnoresOtherAccountChanges()
    {
        await using var fixture = await JointPlanModelConsumptionTests.Fixture.CreateAsync();
        await fixture.ReplanAsync();
        var queue = CreateQueue(fixture);
        var request = fixture.Dispatcher.Request!;
        var claim = await EnqueueAsync(queue, request);
        Assert.Equal(request.ModelEvidence, queue.DeserializeRequest(claim).ModelEvidence);
        Assert.Equal(request.InputEvidence, queue.DeserializeRequest(claim).InputEvidence);
        using var worker = CreateWorker(fixture, queue);
        fixture.Solver.BeforeReturn = () => fixture.ChangeAsync(db =>
        {
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-b" });
            db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "account-b", EntityId = "sensor.foreign" });
            db.HomeAssistantConnections.Add(new HomeAssistantConnection { UserId = "account-b" });
            return Task.CompletedTask;
        });

        await worker.ProcessClaimAsync(claim, CancellationToken.None);

        Assert.Equal(1, fixture.Solver.Calls);
        await fixture.ChangeAsync(async db =>
        {
            var job = await db.ThermalOptimizationJobs.SingleAsync();
            Assert.Equal(ThermalOptimizationJobStatuses.Completed, job.Status);
            var result = JsonSerializer.Deserialize<EmhassOptimizationResult>(job.ResultJson!, JsonSerializerOptions.Web)!;
            Assert.Equal(request.LoadCostForecast.Count, result.Steps.Count);
            Assert.Null(job.LeaseOwner);
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync(x => x.UserId == "account-a")).DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        });
    }

    [Fact]
    public async Task Worker_InvalidSolverResultNeverCompletesPersistentJob()
    {
        await using var fixture = await JointPlanModelConsumptionTests.Fixture.CreateAsync();
        await fixture.ReplanAsync();
        var queue = CreateQueue(fixture);
        var request = fixture.Dispatcher.Request!;
        var claim = await EnqueueAsync(queue, request);
        using var worker = CreateWorker(fixture, queue);
        fixture.Solver.ResultFactory = value =>
        {
            var steps = value.LoadCostForecast.Select((price, index) => new EmhassOptimizationStep(index, 1200, 21, (double)price)).SkipLast(1).ToArray();
            return new(steps, 100, JointPlanModelConsumptionTests.ExpectedObjective(value, steps));
        };

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() => worker.ProcessClaimAsync(claim, CancellationToken.None));

        Assert.Equal(1, fixture.Solver.Calls);
        await AssertNotCompletedAsync(fixture);
    }

    private static Task AssertNotCompletedAsync(JointPlanModelConsumptionTests.Fixture fixture) => fixture.ChangeAsync(async db =>
    {
        var job = await db.ThermalOptimizationJobs.SingleAsync();
        // ProcessClaim is tested directly; the hosted loop records the failure separately.
        Assert.Equal(ThermalOptimizationJobStatuses.Running, job.Status);
        Assert.Null(job.ResultJson);
        Assert.Single(await db.ThermalPlans.ToListAsync()); // Only the fixture's initial shadow plan.
        Assert.Single(await db.DhwCycles.ToListAsync()); // Only the fixture's initial shadow DHW proposal.
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
    });

    private static ThermalOptimizationQueue CreateQueue(JointPlanModelConsumptionTests.Fixture fixture) => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ThermalOptimizationQueueOptions()),
        NullLogger<ThermalOptimizationQueue>.Instance);

    private static EmhassOptimizationWorker CreateWorker(JointPlanModelConsumptionTests.Fixture fixture, ThermalOptimizationQueue queue, bool enabled = true) => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(), queue, Options.Create(new EmhassOptions { Enabled = enabled }),
        Options.Create(new ThermalOptimizationQueueOptions()), NullLogger<EmhassOptimizationWorker>.Instance);

    private static async Task<ClaimedThermalOptimizationJob> EnqueueAsync(ThermalOptimizationQueue queue, EmhassOptimizationRequest request, string account = "account-a")
    {
        await queue.EnqueueOrCoalesceAsync(account, "JointPlan", request, 0, CancellationToken.None);
        return (await queue.ClaimNextAsync("test-worker", CancellationToken.None))!;
    }
}
