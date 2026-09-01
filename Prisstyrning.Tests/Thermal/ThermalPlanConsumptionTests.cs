using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalPlanConsumptionTests
{
    [Theory]
    [InlineData("rejected")]
    [InlineData("shadow")]
    [InlineData("missing-evidence")]
    [InlineData("missing-input-evidence")]
    [InlineData("revoked-model")]
    [InlineData("changed-settings")]
    [InlineData("rollback")]
    [InlineData("missing-step")]
    [InlineData("duplicate-step")]
    [InlineData("step-gap")]
    [InlineData("excess-deviation")]
    [InlineData("nan-heat")]
    [InlineData("dhw-deviation")]
    [InlineData("bad-room-json")]
    [InlineData("bad-reason-json")]
    [InlineData("bad-confidence")]
    [InlineData("changed-telemetry")]
    [InlineData("changed-price")]
    [InlineData("changed-zone")]
    public async Task ActiveConsumer_RejectsPlanThatIsNotCompleteCurrentAndProven(string fault)
    {
        await using var fixture = await ActiveFixtureAsync();
        await fixture.ChangeAsync(async db =>
        {
            var plan = await db.ThermalPlans.Include(x => x.Steps).SingleAsync();
            if (fault == "rejected") plan.Status = "Rejected";
            if (fault == "shadow") plan.IsShadow = true;
            if (fault == "missing-evidence") plan.InputSnapshotJson = "{}";
            if (fault == "missing-input-evidence")
            {
                var input = JsonNode.Parse(plan.InputSnapshotJson)!.AsObject();
                input.Remove("inputEvidence");
                plan.InputSnapshotJson = input.ToJsonString();
            }
            if (fault == "revoked-model") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "COP")).IsActive = false;
            if (fault == "changed-settings") (await db.ThermalSiteConfigs.SingleAsync()).UpperComfortBandC += .1;
            if (fault == "rollback") (await db.ThermalSiteConfigs.SingleAsync()).ControlMode = "Legacy";
            if (fault == "missing-step") db.ThermalPlanSteps.Remove(plan.Steps[0]);
            if (fault == "duplicate-step") plan.Steps.Add(Copy(plan.Steps[0]));
            if (fault == "step-gap") plan.Steps[0].StartUtc = plan.Steps[0].StartUtc.AddMinutes(1);
            if (fault == "excess-deviation") plan.Steps[0].DesiredLwtDeviationC = 1.5;
            if (fault == "nan-heat") plan.Steps[0].DesiredHeatOutputKw = double.NaN;
            if (fault == "dhw-deviation") { plan.Steps[0].DhwReserved = true; plan.Steps[0].DhwMode = "Eco"; plan.Steps[0].DesiredLwtDeviationC = .5; }
            if (fault == "bad-room-json") plan.Steps[0].ExpectedRoomsJson = "{}";
            if (fault == "bad-reason-json") plan.Steps[0].DecisionReasonJson = "{}";
            if (fault == "bad-confidence") plan.Steps[0].Confidence = 2;
            if (fault == "changed-telemetry") (await db.ThermalTelemetrySamples.SingleAsync()).HeatPumpPowerKw += .1;
            if (fault == "changed-price") (await db.PriceSnapshots.SingleAsync()).SavedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            if (fault == "changed-zone") (await db.UserSettings.SingleAsync()).Zone = "SE2";
        });

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() => ReadAsync(fixture, DateTimeOffset.UtcNow));

        await fixture.ChangeAsync(async db =>
        {
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
        });
    }

    [Fact]
    public async Task ActiveConsumer_AcceptsOneCurrentCompleteStepAndDoesNotAgeOriginalTelemetryAsLiveData()
    {
        await using var fixture = await ActiveFixtureAsync();

        var validated = await ReadAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(20));

        Assert.NotNull(validated);
        Assert.False(validated!.Plan.IsShadow);
        Assert.Equal("Valid", validated.Plan.Status);
        Assert.True(validated.CurrentStep.StartUtc <= DateTimeOffset.UtcNow.AddMinutes(20));
        Assert.True(validated.CurrentStep.EndUtc > DateTimeOffset.UtcNow.AddMinutes(20));
        Assert.Equal("LwtActive", validated.ModelEvidence.ControlMode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("step")]
    [InlineData("settings")]
    [InlineData("telemetry")]
    [InlineData("price")]
    public async Task WriteBoundary_RejectsPlanOrConfigurationChangedAfterInitialRead(string change)
    {
        await using var fixture = await ActiveFixtureAsync();
        var validated = await ReadAsync(fixture, DateTimeOffset.UtcNow);
        await fixture.ChangeAsync(async db =>
        {
            if (change == "status") (await db.ThermalPlans.SingleAsync()).Status = "Rejected";
            if (change == "step") (await db.ThermalPlanSteps.OrderBy(x => x.StartUtc).FirstAsync()).DesiredLwtDeviationC = .5;
            if (change == "settings") (await db.ThermalSiteConfigs.SingleAsync()).LowerComfortBandC += .1;
            if (change == "telemetry") (await db.ThermalTelemetrySamples.SingleAsync()).PropertyPowerKw += .1;
            if (change == "price") (await db.PriceSnapshots.SingleAsync()).TomorrowPricesJson = "[]";
        });

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() => EnsureStillCurrentAsync(fixture, validated!));
    }

    [Fact]
    public async Task WriteBoundary_RejectsWhenTheInitiallyReadStepHasEnded()
    {
        await using var fixture = await ActiveFixtureAsync();
        var validated = await ReadAsync(fixture, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() =>
            EnsureStillCurrentAsync(fixture, validated!, validated!.CurrentStep.EndUtc));
    }

    [Fact]
    public async Task ActiveConsumer_IgnoresForeignPlansAndReturnsNullWithoutAnOwnCurrentPlan()
    {
        await using var fixture = await ActiveFixtureAsync();
        await fixture.ChangeAsync(async db =>
        {
            var own = await db.ThermalPlans.Include(x => x.Steps).SingleAsync();
            own.ValidUntilUtc = own.ValidFromUtc;
            db.ThermalPlans.Add(new ThermalPlan
            {
                UserId = "account-b", CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ValidUntilUtc = DateTimeOffset.UtcNow.AddHours(1), Status = "Valid", IsShadow = false
            });
        });

        Assert.Null(await ReadAsync(fixture, DateTimeOffset.UtcNow));
    }

    private static async Task<JointPlanModelConsumptionTests.Fixture> ActiveFixtureAsync()
    {
        var fixture = await JointPlanModelConsumptionTests.Fixture.CreateAsync();
        await fixture.ChangeAsync(async db =>
        {
            var site = await db.ThermalSiteConfigs.SingleAsync();
            site.ControlMode = "LwtActive";
            site.UpdatedAtUtc = (await db.ThermalTelemetrySamples.SingleAsync()).TimestampUtc.AddSeconds(-1);
        });
        await fixture.ReplanAsync();
        return fixture;
    }

    private static async Task<ValidatedThermalPlan?> ReadAsync(
        JointPlanModelConsumptionTests.Fixture fixture, DateTimeOffset now)
    {
        ValidatedThermalPlan? result = null;
        await fixture.ChangeAsync(async db => result = await ThermalPlanConsumption.ReadCurrentAsync(db, "account-a", now, CancellationToken.None));
        return result;
    }

    private static async Task EnsureStillCurrentAsync(
        JointPlanModelConsumptionTests.Fixture fixture, ValidatedThermalPlan validated, DateTimeOffset? now = null)
    {
        await fixture.ChangeAsync(db => ThermalPlanConsumption.EnsureStillCurrentAsync(
            db, "account-a", validated, now ?? DateTimeOffset.UtcNow, CancellationToken.None));
    }

    private static ThermalPlanStep Copy(ThermalPlanStep value) => new()
    {
        StartUtc = value.StartUtc, EndUtc = value.EndUtc, DesiredHeatOutputKw = value.DesiredHeatOutputKw,
        DesiredLwtDeviationC = value.DesiredLwtDeviationC, DhwReserved = value.DhwReserved, DhwMode = value.DhwMode,
        IncrementalCost = value.IncrementalCost, Confidence = value.Confidence,
        ExpectedRoomsJson = value.ExpectedRoomsJson, DecisionReasonJson = value.DecisionReasonJson
    };
}
