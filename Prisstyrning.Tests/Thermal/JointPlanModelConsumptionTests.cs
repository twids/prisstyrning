using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class JointPlanModelConsumptionTests
{
    [Theory]
    [InlineData("missing-thermal")]
    [InlineData("missing-cop")]
    [InlineData("foreign-models")]
    [InlineData("inactive-thermal")]
    [InlineData("old-thermal-metrics")]
    [InlineData("bad-thermal-json")]
    [InlineData("failed-cop-validation")]
    [InlineData("future-model")]
    [InlineData("unverified-meter")]
    [InlineData("future-telemetry")]
    [InlineData("missing-brine")]
    [InlineData("invalid-lwt")]
    [InlineData("missing-heat")]
    public async Task Replan_RejectsUnprovenInputsBeforeDispatchOrPlanPersistence(string fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeAsync(async db =>
        {
            var models = await db.ThermalModelVersions.ToListAsync();
            var thermal = models.Single(x => x.ModelType == "2R2C");
            var cop = models.Single(x => x.ModelType == "COP");
            if (fault == "missing-thermal") db.Remove(thermal);
            if (fault == "missing-cop") db.Remove(cop);
            if (fault == "foreign-models") models.ForEach(x => x.UserId = "account-b");
            if (fault == "inactive-thermal") thermal.IsActive = false;
            if (fault == "old-thermal-metrics") thermal.MetricsJson = "{\"twoHourMaeC\":0.1,\"dayMaeC\":0.2}";
            if (fault == "bad-thermal-json") thermal.ParametersJson = "[]";
            if (fault == "failed-cop-validation") cop.MetricsJson = "{\"mae\":0.9,\"trainingSamples\":480,\"validationSamples\":120,\"validationVersion\":1}";
            if (fault == "future-model") thermal.CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(1);
            if (fault == "unverified-meter") (await db.ThermalSiteConfigs.SingleAsync()).HeatPumpPowerSignVerified = false;
            if (fault == "future-telemetry") (await db.ThermalTelemetrySamples.SingleAsync()).TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            if (fault == "missing-brine") (await db.ThermalTelemetrySamples.SingleAsync()).BrineInC = null;
            if (fault == "invalid-lwt") (await db.ThermalTelemetrySamples.SingleAsync()).LeavingWaterTemperatureC = double.NaN;
            if (fault == "missing-heat") (await db.ThermalTelemetrySamples.SingleAsync()).HeatOutputKw = null;
        });

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.ReplanAsync());

        Assert.Equal(0, fixture.Dispatcher.Calls);
        await fixture.AssertNoPlansOrCommandsAsync();
    }

    [Fact]
    public async Task Replan_ReadsActualCamelCaseTrainingParametersAndPreservesLegacyWriter()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.ReplanAsync();

        var request = Assert.IsType<EmhassOptimizationRequest>(fixture.Dispatcher.Request);
        Assert.Equal(4.75, request.Thermal.HeatingRateCPerHour, 6);
        Assert.Equal(.175, request.Thermal.CoolingConstantPerHourPerC, 6);
        Assert.Equal(1, fixture.Dispatcher.Calls);
        await fixture.ChangeAsync(async db =>
        {
            var plan = await db.ThermalPlans.SingleAsync();
            Assert.Equal("Valid", plan.Status);
            Assert.True(plan.IsShadow);
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        });
    }

    [Theory]
    [InlineData("partial-horizon")]
    [InlineData("wrong-index")]
    [InlineData("duplicate-index")]
    [InlineData("negative-power")]
    [InlineData("excess-power")]
    [InlineData("nan-power")]
    [InlineData("missing-temperature")]
    [InlineData("nan-temperature")]
    [InlineData("below-comfort")]
    [InlineData("above-comfort")]
    [InlineData("nan-unit-cost")]
    [InlineData("slow-solver")]
    [InlineData("wrong-objective")]
    public async Task Replan_RejectsInvalidSolverResultBeforePlanOrDhwPersistence(string fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Dispatcher.ResultFactory = request =>
        {
            var steps = request.LoadCostForecast.Select((price, index) =>
                new EmhassOptimizationStep(index, 1200, 21, (double)price)).ToList();
            if (fault == "partial-horizon") steps.RemoveAt(steps.Count - 1);
            if (fault == "wrong-index") steps[0] = steps[0] with { Index = 1 };
            if (fault == "duplicate-index") steps[1] = steps[1] with { Index = 0 };
            if (fault == "negative-power") steps[0] = steps[0] with { SpaceHeatingPowerW = -1 };
            if (fault == "excess-power") steps[0] = steps[0] with { SpaceHeatingPowerW = 2600 };
            if (fault == "nan-power") steps[0] = steps[0] with { SpaceHeatingPowerW = double.NaN };
            if (fault == "missing-temperature") steps[0] = steps[0] with { PredictedTemperatureC = null };
            if (fault == "nan-temperature") steps[0] = steps[0] with { PredictedTemperatureC = double.NaN };
            if (fault == "below-comfort") steps[0] = steps[0] with { PredictedTemperatureC = 20.98 };
            if (fault == "above-comfort") steps[0] = steps[0] with { PredictedTemperatureC = 22.22 };
            if (fault == "nan-unit-cost") steps[0] = steps[0] with { UnitCost = double.NaN };
            var objective = ExpectedObjective(request, steps);
            return new(steps, fault == "slow-solver" ? 45_001 : 100, fault == "wrong-objective" ? objective + 1 : objective);
        };

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() => fixture.ReplanAsync());

        Assert.Equal(1, fixture.Dispatcher.Calls);
        await fixture.AssertNoPlansOrCommandsAsync();
    }

    [Theory]
    [InlineData("deactivated-model")]
    [InlineData("changed-metrics")]
    [InlineData("changed-settings")]
    [InlineData("rollback")]
    [InlineData("room-change")]
    [InlineData("ha-revision")]
    [InlineData("same-revision-setting-change")]
    public async Task Replan_DiscardsResultWhenInputsAreRevokedWhileSolverRuns(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Dispatcher.BeforeReturn = () => fixture.ChangeAsync(async db =>
        {
            if (change == "deactivated-model") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C")).IsActive = false;
            if (change == "changed-metrics") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "COP")).MetricsJson = "{}";
            if (change == "changed-settings") (await db.ThermalSiteConfigs.SingleAsync()).UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (change == "rollback") (await db.ThermalSiteConfigs.SingleAsync()).ControlMode = "Legacy";
            if (change == "room-change") db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "account-a", EntityId = "sensor.new_room" });
            if (change == "ha-revision") db.HomeAssistantConnections.Add(new HomeAssistantConnection { UserId = "account-a", UpdatedAtUtc = DateTimeOffset.UtcNow });
            if (change == "same-revision-setting-change") (await db.ThermalSiteConfigs.SingleAsync()).BaseRoomTargetC += 1;
        });

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.ReplanAsync());

        Assert.Equal(1, fixture.Dispatcher.Calls);
        await fixture.AssertNoPlansOrCommandsAsync();
    }

    [Fact]
    public async Task Replan_NewValidVersionRecoversWithoutChangingWriterOrActivatingControl()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeAsync(async db => (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C")).IsActive = false);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.ReplanAsync());
        await fixture.ChangeAsync(db => { db.ThermalModelVersions.Add(ThermalModelEvidenceTests.ValidModel("2R2C", DateTimeOffset.UtcNow)); return Task.CompletedTask; });

        await fixture.ReplanAsync();

        Assert.Equal(1, fixture.Dispatcher.Calls);
        Assert.NotNull(fixture.Dispatcher.Request?.ModelEvidence);
        await fixture.ChangeAsync(async db =>
        {
            Assert.Single(await db.ThermalPlans.ToListAsync());
            Assert.Equal("Shadow", (await db.ThermalSiteConfigs.SingleAsync()).ControlMode);
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        });
    }

    [Fact]
    public async Task EvidenceFailure_PersistsUnderstandableDeduplicatedReasonWithoutWritingControl()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var attempt = 0; attempt < 2; attempt++)
            await fixture.Coordinator.RecordPlanningFailureAsync("account-a", "COP-modellen behöver tränas om.", CancellationToken.None, evidenceFailure: true);

        await fixture.ChangeAsync(async db =>
        {
            var entry = await db.ThermalEvents.SingleAsync();
            Assert.Equal("Warning", entry.Severity);
            Assert.Contains("COP-modellen behöver tränas om", entry.Message);
            Assert.Contains("60 minuter", entry.Message);
        });
        await fixture.AssertNoPlansOrCommandsAsync();
    }

    [Fact]
    public async Task ComfortBreach_IsRecordedAsAnActionRequiredShadowBlocker()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Coordinator.RecordPlanningFailureAsync(
            "account-a", EmhassOptimizationValidation.ComfortBreachReason, CancellationToken.None, evidenceFailure: true);

        await fixture.ChangeAsync(async db =>
        {
            var entry = await db.ThermalEvents.SingleAsync();
            Assert.Equal("ActionRequired", entry.Severity);
            Assert.Equal("SimulatedComfortBreach", entry.Category);
            Assert.Contains("ingen ny plan", entry.Message);
        });
        await fixture.AssertNoPlansOrCommandsAsync();
    }

    [Fact]
    public async Task Replan_LegacyNeverReadsModelsOrDispatches()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeAsync(async db =>
        {
            (await db.ThermalSiteConfigs.SingleAsync()).ControlMode = "Legacy";
            db.ThermalModelVersions.RemoveRange(await db.ThermalModelVersions.ToListAsync());
        });

        await fixture.ReplanAsync();

        Assert.Equal(0, fixture.Dispatcher.Calls);
        await fixture.AssertNoPlansOrCommandsAsync();
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal ServiceProvider Services { get; }
        internal RecordingDispatcher Dispatcher { get; } = new();
        internal RecordingSolver Solver { get; } = new();
        internal JointPlanCoordinator Coordinator { get; }

        private Fixture()
        {
            var database = $"model-consumption-{Guid.NewGuid():N}";
            Services = new ServiceCollection().AddDbContext<PrisstyrningDbContext>(options => options.UseInMemoryDatabase(database))
                .AddSingleton<CopModel>().AddSingleton<IEmhassOptimizationDispatcher>(Dispatcher)
                .AddSingleton<IEmhassClient>(Solver).BuildServiceProvider();
            Coordinator = new(Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new EmhassOptions { Enabled = true }),
                new WriterLeaseIdentity(), NullLogger<JointPlanCoordinator>.Instance);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.ChangeAsync(db =>
            {
                var now = DateTimeOffset.UtcNow;
                db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", ControlMode = "Shadow", HeatPumpPowerSignVerified = true, UpdatedAtUtc = now.AddHours(-1) });
                db.ThermalModelVersions.AddRange(ThermalModelEvidenceTests.ValidModel("2R2C", now), ThermalModelEvidenceTests.ValidModel("COP", now));
                db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample { UserId = "account-a", TimestampUtc = now.AddMinutes(-1),
                    OutsideTemperatureC = 2, RoomTemperaturesJson = "{\"sensor.room\":21}", BrineInC = 0,
                    LeavingWaterTemperatureC = 35, HeatOutputKw = 4, HeatPumpPowerKw = 1, PropertyPowerKw = 2 });
                db.PriceSnapshots.Add(new PriceSnapshot { Zone = "SE3", SavedAtUtc = now, Date = DateOnly.FromDateTime(now.UtcDateTime),
                    TodayPricesJson = JsonSerializer.Serialize(new[] { new { start = now.AddHours(-1), value = .5m } }) });
                return Task.CompletedTask;
            });
            return fixture;
        }

        internal Task ReplanAsync() => Coordinator.ReplanAsync("account-a", CancellationToken.None);
        internal async Task ChangeAsync(Func<PrisstyrningDbContext, Task> action)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            await action(db);
            await db.SaveChangesAsync();
        }

        internal Task AssertNoPlansOrCommandsAsync() => ChangeAsync(async db =>
        {
            Assert.Empty(await db.ThermalPlans.ToListAsync());
            Assert.Empty(await db.DhwCycles.ToListAsync());
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
            Assert.Null(Services.GetService<IHomeAssistantControlClient>());
        });

        public async ValueTask DisposeAsync() { Coordinator.Dispose(); await Services.DisposeAsync(); }
    }

    internal sealed class RecordingDispatcher : IEmhassOptimizationDispatcher
    {
        internal int Calls { get; private set; }
        internal EmhassOptimizationRequest? Request { get; private set; }
        internal Func<Task>? BeforeReturn { get; set; }
        internal Func<EmhassOptimizationRequest, EmhassOptimizationResult>? ResultFactory { get; set; }

        public async Task<EmhassOptimizationResult> EnqueueAndWaitAsync(string userId, string reason, EmhassOptimizationRequest request,
            int priority = 0, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            if (BeforeReturn is not null) await BeforeReturn();
            if (ResultFactory is not null) return ResultFactory(request);
            var steps = request.LoadCostForecast.Select((price, index) => new EmhassOptimizationStep(index, 1200, 21, (double)price)).ToArray();
            return new(steps, 100, ExpectedObjective(request, steps));
        }
    }

    internal sealed class RecordingSolver : IEmhassClient
    {
        internal int Calls { get; private set; }
        internal Func<Task>? BeforeReturn { get; set; }
        internal Func<EmhassOptimizationRequest, EmhassOptimizationResult>? ResultFactory { get; set; }
        public async Task<EmhassOptimizationResult> OptimizeAsync(EmhassOptimizationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (BeforeReturn is not null) await BeforeReturn();
            if (ResultFactory is not null) return ResultFactory(request);
            var steps = request.LoadCostForecast.Select((price, index) => new EmhassOptimizationStep(index, 1200, 21, (double)price)).ToArray();
            return new(steps, 100, ExpectedObjective(request, steps));
        }
    }

    internal static decimal ExpectedObjective(EmhassOptimizationRequest request, IEnumerable<EmhassOptimizationStep> steps) =>
        decimal.Round(steps.Where(x => x.Index >= 0 && x.Index < request.LoadCostForecast.Count && double.IsFinite(x.SpaceHeatingPowerW))
            .Sum(step => (decimal)(step.SpaceHeatingPowerW / 1000d * 15d / 60d) * request.LoadCostForecast[step.Index]), 4);
}
