using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Domain;
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
    [InlineData("missing-entity")]
    [InlineData("invalid-room-quality")]
    [InlineData("history-import")]
    [InlineData("missing-return")]
    [InlineData("inconsistent-heat")]
    [InlineData("dhw-active")]
    [InlineData("defrost-active")]
    [InlineData("unknown-backup")]
    [InlineData("invalid-forecast")]
    [InlineData("short-forecast")]
    [InlineData("stale-price")]
    [InlineData("duplicate-price")]
    [InlineData("changed-zone")]
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
            if (fault == "future-telemetry") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            if (fault == "missing-brine") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).BrineInC = null;
            if (fault == "invalid-lwt") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).LeavingWaterTemperatureC = double.NaN;
            if (fault == "missing-heat") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).HeatOutputKw = null;
            if (fault == "missing-entity") db.Remove(await db.ThermalEntityConfigs.SingleAsync(x => x.Role == ThermalEntityRoles.OutsideTemperature));
            if (fault is "invalid-room-quality" or "history-import" or "invalid-forecast")
            {
                var sample = await ThermalCurrentModelTestData.LatestTelemetryAsync(db);
                var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
                if (fault == "invalid-room-quality") quality["rooms"]!["sensor.room"]!["quality"] = 2;
                if (fault == "history-import") quality["source"] = "HomeAssistantHistoryImport";
                if (fault == "invalid-forecast") quality["forecast"]!["quality"] = 2;
                sample.QualityJson = quality.ToJsonString();
            }
            if (fault == "missing-return") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).ReturnWaterTemperatureC = null;
            if (fault == "inconsistent-heat") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).HeatOutputKw = 40;
            if (fault == "dhw-active") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).DhwActive = true;
            if (fault == "defrost-active") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).DefrostActive = true;
            if (fault == "unknown-backup") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).BackupHeaterActive = null;
            if (fault == "short-forecast")
            {
                var sample = await ThermalCurrentModelTestData.LatestTelemetryAsync(db);
                sample.OutsideTemperatureForecastJson = JsonSerializer.Serialize(new[]
                {
                    new WeatherForecastPoint(sample.TimestampUtc.AddHours(-1), 2, null, null),
                    new WeatherForecastPoint(sample.TimestampUtc, 2, null, null)
                });
            }
            if (fault == "stale-price") (await db.PriceSnapshots.SingleAsync()).SavedAtUtc = DateTimeOffset.UtcNow.AddHours(-37);
            if (fault == "duplicate-price")
            {
                var snapshot = await db.PriceSnapshots.SingleAsync();
                var point = new { start = FloorToQuarter(DateTimeOffset.UtcNow), value = .5m };
                snapshot.TodayPricesJson = JsonSerializer.Serialize(new[] { point, point });
                snapshot.TomorrowPricesJson = "[]";
            }
            if (fault == "changed-zone") (await db.UserSettings.SingleAsync()).Zone = "SE2";
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
        Assert.NotNull(request.InputEvidence);
        Assert.Null(request.InputEvidence.DhwEvidence?.ReservedCycleId);
        Assert.Equal(0, request.InputEvidence.DhwEvidence?.OpenCycleCount);
        Assert.Equal("Estimated", request.InputEvidence.DhwProfileEvidence?.Source);
        Assert.Null(request.InputEvidence.DhwProfileEvidence?.StoredCycleId);
        Assert.False(string.IsNullOrWhiteSpace(
            request.InputEvidence.DhwProfileEvidence?.EstimatedEvidence?.SourceFingerprint));
        Assert.Equal(1, fixture.Dispatcher.Calls);
        await fixture.ChangeAsync(async db =>
        {
            var plan = await db.ThermalPlans.SingleAsync();
            var cycle = await db.DhwCycles.SingleAsync();
            using var input = JsonDocument.Parse(plan.InputSnapshotJson);
            var evidence = input.RootElement.GetProperty("inputEvidence")
                .Deserialize<ThermalPlanningInputEvidence>(JsonSerializerOptions.Web)!;
            Assert.Equal("Valid", plan.Status);
            Assert.True(plan.IsShadow);
            Assert.InRange(plan.Confidence, 0.4, 0.85);
            Assert.Contains("inputEvidence", plan.InputSnapshotJson);
            Assert.Contains("estimatedSteps", plan.InputSnapshotJson);
            Assert.Equal(cycle.Id, evidence.DhwEvidence?.ReservedCycleId);
            Assert.Equal(1, evidence.DhwEvidence?.OpenCycleCount);
            Assert.False(string.IsNullOrWhiteSpace(evidence.DhwEvidence?.OpenCycleFingerprint));
            Assert.Equal("Estimated", evidence.DhwProfileEvidence?.Source);
            Assert.Equal(
                request.InputEvidence.DhwProfileEvidence?.EstimatedEvidence,
                evidence.DhwProfileEvidence?.EstimatedEvidence);
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        });
    }

    [Fact]
    public async Task Replan_ExcludedCriticalFallbackCannotBiasPlanAndUsesAnotherVerifiedRoom()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeAsync(async db =>
        {
            db.ThermalRoomConfigs.Add(new ThermalRoomConfig
            {
                UserId = "account-a",
                Name = "Annat rum",
                EntityId = "sensor.other",
                Weight = 1
            });
            var sample = await ThermalCurrentModelTestData.LatestTelemetryAsync(db);
            sample.RoomTemperaturesJson = "{\"sensor.room\":5,\"sensor.other\":21.8}";
            var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
            quality["rooms"]!["sensor.room"] = JsonNode.Parse("{\"quality\":2,\"excluded\":true}");
            quality["rooms"]!["sensor.other"] = JsonNode.Parse("{\"quality\":0,\"excluded\":false}");
            sample.QualityJson = quality.ToJsonString();
            (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C")).IsActive = false;
            await ThermalCurrentModelTestData.AddVersionAsync(
                db, "account-a", "2R2C", DateTimeOffset.UtcNow);
        });

        await fixture.ReplanAsync();

        Assert.Equal(21.8, fixture.Dispatcher.Request!.Thermal.StartTemperatureC, 6);
        await fixture.ChangeAsync(async db =>
        {
            Assert.True((await db.ThermalPlans.SingleAsync()).IsShadow);
            Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        });
    }

    [Fact]
    public async Task Replan_TrackedRunningDhwUsesWeatherCurveCopInputAndExtendsReservation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeAsync(async db =>
        {
            var sample = await ThermalCurrentModelTestData.LatestTelemetryAsync(db);
            sample.DhwActive = true;
            db.DhwCycles.Add(new DhwCycle
            {
                UserId = "account-a",
                Kind = "Eco",
                Source = "Shadow",
                Status = "Running",
                PlannedStartUtc = sample.TimestampUtc.AddMinutes(-15),
                ActualStartUtc = sample.TimestampUtc.AddMinutes(-10),
                TargetTemperatureC = 45,
                PredictedDurationMinutes = 45,
                ReservedDurationMinutes = 60,
                EstimatedCompletionUtc = sample.TimestampUtc.AddMinutes(50),
                PowerProfileJson = JsonSerializer.Serialize(new[]
                {
                    new DhwPowerStep(0, 2.1, 3.1, false),
                    new DhwPowerStep(40, 2.3, 2.3, false)
                })
            });
        });

        await fixture.ReplanAsync();

        var request = fixture.Dispatcher.Request!;
        Assert.Equal(0, request.DhwStartStep);
        Assert.True(request.DhwDurationSteps >= 3);
        await fixture.ChangeAsync(async db =>
        {
            var cycle = await db.DhwCycles.SingleAsync();
            var plan = await db.ThermalPlans.Include(x => x.Steps).SingleAsync();
            using var input = JsonDocument.Parse(plan.InputSnapshotJson);
            var evidence = input.RootElement.GetProperty("inputEvidence")
                .Deserialize<ThermalPlanningInputEvidence>(JsonSerializerOptions.Web)!;
            Assert.Equal(cycle.Id, request.InputEvidence?.DhwEvidence?.ReservedCycleId);
            Assert.Equal(cycle.Id, evidence.DhwEvidence?.ReservedCycleId);
            Assert.Contains("weatherCurveEstimateDuringDhw", plan.InputSnapshotJson);
            Assert.Contains(plan.Steps, x => x.DhwReserved);
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
                new EmhassOptimizationStep(index, IsDhwReserved(request, index) ? 0 : 1200, 21, (double)price)).ToList();
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
    [InlineData("changed-build")]
    [InlineData("changed-settings")]
    [InlineData("rollback")]
    [InlineData("room-change")]
    [InlineData("ha-revision")]
    [InlineData("same-revision-setting-change")]
    [InlineData("telemetry-change")]
    [InlineData("historical-source-change")]
    [InlineData("price-change")]
    [InlineData("zone-change")]
    [InlineData("dhw-cycle-created")]
    [InlineData("dhw-profile-source-created")]
    public async Task Replan_DiscardsResultWhenInputsAreRevokedWhileSolverRuns(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Dispatcher.BeforeReturn = () => fixture.ChangeAsync(async db =>
        {
            if (change == "deactivated-model") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C")).IsActive = false;
            if (change == "changed-metrics") (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "COP")).MetricsJson = "{}";
            if (change == "changed-build")
            {
                var version = await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C");
                var source = ThermalModelProvenance.Read(version)!;
                version.SourceEvidenceJson = ThermalModelProvenance.Serialize(source with
                { BuildRevision = "fedcba9876543210fedcba9876543210fedcba98" });
            }
            if (change == "changed-settings") (await db.ThermalSiteConfigs.SingleAsync()).UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (change == "rollback") (await db.ThermalSiteConfigs.SingleAsync()).ControlMode = "Legacy";
            if (change == "room-change") db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "account-a", EntityId = "sensor.new_room" });
            if (change == "ha-revision") db.HomeAssistantConnections.Add(new HomeAssistantConnection { UserId = "account-a", UpdatedAtUtc = DateTimeOffset.UtcNow });
            if (change == "same-revision-setting-change") (await db.ThermalSiteConfigs.SingleAsync()).BaseRoomTargetC += 1;
            if (change == "telemetry-change") (await ThermalCurrentModelTestData.LatestTelemetryAsync(db)).PropertyPowerKw += .1;
            if (change == "historical-source-change")
                (await db.ThermalTelemetrySamples.OrderBy(x => x.TimestampUtc).FirstAsync()).RoomTemperaturesJson = "{\"sensor.room\":21.6}";
            if (change == "price-change") (await db.PriceSnapshots.SingleAsync()).SavedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1);
            if (change == "zone-change") (await db.UserSettings.SingleAsync()).Zone = "SE2";
            if (change == "dhw-cycle-created") db.DhwCycles.Add(new DhwCycle
            {
                UserId = "account-a",
                Kind = "Eco",
                Source = "LegacyObserved",
                Status = "Running",
                PlannedStartUtc = DateTimeOffset.UtcNow,
                ActualStartUtc = DateTimeOffset.UtcNow,
                TargetTemperatureC = 45,
                PredictedDurationMinutes = 45,
                ReservedDurationMinutes = 60
            });
            if (change == "dhw-profile-source-created")
            {
                var start = DateTimeOffset.UtcNow.AddDays(-1);
                db.DhwCycles.Add(new DhwCycle
                {
                    UserId = "account-a",
                    Kind = "Comfort",
                    Source = "LegacyObserved",
                    Status = "Completed",
                    PlannedStartUtc = start,
                    ActualStartUtc = start,
                    TargetReachedUtc = start.AddMinutes(60),
                    ActualEndUtc = start.AddMinutes(65),
                    StartTemperatureC = 40,
                    TargetTemperatureC = 60,
                    TargetVerificationCount = 2
                });
            }
        });

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.ReplanAsync());

        Assert.Equal(1, fixture.Dispatcher.Calls);
        if (change is "dhw-cycle-created" or "dhw-profile-source-created")
        {
            await fixture.ChangeAsync(async db =>
            {
                Assert.Empty(await db.ThermalPlans.ToListAsync());
                Assert.Single(await db.DhwCycles.ToListAsync());
                Assert.Empty(await db.ThermalControlCommands.ToListAsync());
                Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).DhwWriter);
            });
        }
        else
        {
            await fixture.AssertNoPlansOrCommandsAsync();
        }
    }

    [Fact]
    public async Task Replan_NewValidVersionRecoversWithoutChangingWriterOrActivatingControl()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeAsync(async db => (await db.ThermalModelVersions.FirstAsync(x => x.ModelType == "2R2C")).IsActive = false);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.ReplanAsync());
        await fixture.ChangeAsync(async db =>
            await ThermalCurrentModelTestData.AddVersionAsync(db, "account-a", "2R2C", DateTimeOffset.UtcNow));

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
                .AddSingleton<CopModel>().AddScoped<DhwProfileEstimator>().AddSingleton<DhwCyclePlanner>()
                .AddSingleton(ThermalCurrentModelTestData.Build)
                .AddSingleton<IEmhassOptimizationDispatcher>(Dispatcher)
                .AddSingleton<IEmhassClient>(Solver).BuildServiceProvider();
            Coordinator = new(Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new EmhassOptions { Enabled = true }),
                new WriterLeaseIdentity(), NullLogger<JointPlanCoordinator>.Instance);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.ChangeAsync(async db =>
            {
                var now = DateTimeOffset.UtcNow;
                var forecast = Enumerable.Range(0, 51).Select(hour =>
                    new WeatherForecastPoint(now.AddHours(hour - 1), 2 + hour * .02, 3, 0)).ToArray();
                var quality = JsonSerializer.Serialize(new
                {
                    rooms = new Dictionary<string, object>
                    {
                        ["sensor.room"] = new { quality = 0, excluded = false }
                    },
                    entities = PlanningRoles.ToDictionary(
                        role => role,
                        role => (object)new { quality = 0, excluded = false }),
                    forecast = new { quality = 0 }
                });
                var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                var pricePoints = Enumerable.Range(0, 192).Select(index => new
                {
                    start = dayStart.AddMinutes(index * 15),
                    value = .5m + index % 16 * .01m
                }).ToArray();

                db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", ControlMode = "Shadow", HeatPumpPowerSignVerified = true, UpdatedAtUtc = now.AddMinutes(-5) });
                db.UserSettings.Add(new UserSettings { UserId = "account-a", Zone = "SE3" });
                db.ThermalRoomConfigs.Add(new ThermalRoomConfig
                {
                    UserId = "account-a",
                    Name = "Rum",
                    EntityId = "sensor.room",
                    IsCritical = true,
                    Weight = 1
                });
                db.ThermalEntityConfigs.AddRange(PlanningRoles.Select(role => new ThermalEntityConfig
                {
                    UserId = "account-a",
                    Role = role,
                    EntityId = role == ThermalEntityRoles.WeatherForecast
                        ? "weather.home" : $"sensor.{role}"
                }));
                db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
                {
                    UserId = "account-a",
                    TimestampUtc = now.AddMinutes(-1),
                    OutsideTemperatureC = 2,
                    OutsideTemperatureForecastJson = JsonSerializer.Serialize(forecast),
                    RoomTemperaturesJson = "{\"sensor.room\":21}",
                    QualityJson = quality,
                    BrineInC = 0,
                    LeavingWaterTemperatureC = 35,
                    ReturnWaterTemperatureC = 30,
                    FlowLitresPerMinute = 240d / 20.93d,
                    HeatOutputKw = 4,
                    TankTemperatureC = 40,
                    HeatPumpPowerKw = 1,
                    PropertyPowerKw = 2,
                    DhwActive = false,
                    DefrostActive = false,
                    BackupHeaterActive = false
                });
                db.PriceSnapshots.Add(new PriceSnapshot
                {
                    Zone = "SE3",
                    SavedAtUtc = now,
                    Date = DateOnly.FromDateTime(now.UtcDateTime),
                    TodayPricesJson = JsonSerializer.Serialize(pricePoints[..96]),
                    TomorrowPricesJson = JsonSerializer.Serialize(pricePoints[96..])
                });
                await ThermalCurrentModelTestData.SeedAsync(db, "account-a", now, "2R2C", "COP");
            });
            return fixture;
        }

        private static readonly string[] PlanningRoles =
        [
            ThermalEntityRoles.OutsideTemperature,
            ThermalEntityRoles.LeavingWaterTemperature,
            ThermalEntityRoles.ReturnWaterTemperature,
            ThermalEntityRoles.Flow,
            ThermalEntityRoles.BrineIn,
            ThermalEntityRoles.TankTemperature,
            ThermalEntityRoles.HeatPumpPower,
            ThermalEntityRoles.PropertyPower,
            ThermalEntityRoles.DhwActive,
            ThermalEntityRoles.DefrostActive,
            ThermalEntityRoles.BackupHeaterActive,
            ThermalEntityRoles.WeatherForecast
        ];

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
            var steps = request.LoadCostForecast.Select((price, index) => new EmhassOptimizationStep(
                index, IsDhwReserved(request, index) ? 0 : 1200, 21, (double)price)).ToArray();
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
            var steps = request.LoadCostForecast.Select((price, index) => new EmhassOptimizationStep(
                index, IsDhwReserved(request, index) ? 0 : 1200, 21, (double)price)).ToArray();
            return new(steps, 100, ExpectedObjective(request, steps));
        }
    }

    internal static decimal ExpectedObjective(EmhassOptimizationRequest request, IEnumerable<EmhassOptimizationStep> steps) =>
        decimal.Round(steps.Where(x => x.Index >= 0 && x.Index < request.LoadCostForecast.Count && double.IsFinite(x.SpaceHeatingPowerW))
            .Sum(step => (decimal)(step.SpaceHeatingPowerW / 1000d * 15d / 60d) * request.LoadCostForecast[step.Index]), 4);

    internal static bool IsDhwReserved(EmhassOptimizationRequest request, int index) =>
        request.DhwStartStep is { } start && index >= start && index < start + request.DhwDurationSteps;

    private static DateTimeOffset FloorToQuarter(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute / 15 * 15, 0, value.Offset);
}
