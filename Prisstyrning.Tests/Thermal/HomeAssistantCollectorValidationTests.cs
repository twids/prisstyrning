using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantCollectorValidationTests
{
    [Fact]
    public async Task Collect_ValidLiveValuesProduceFiniteHeatAndCopWithoutChangingLegacy()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CollectAsync(0);
        var sample = await fixture.LatestAsync();

        Assert.True(ThermalReadinessService.HasRequiredTelemetry(sample, [new ThermalRoomConfig { EntityId = "sensor.room", IsCritical = true }]));
        Assert.Equal(4.186, sample.HeatOutputKw!.Value, 6);
        Assert.Equal(2.093, sample.Cop!.Value, 6);
        Assert.All(await fixture.ReadinessAsync(), check => Assert.True(check.Passed, check.Key));
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("malformed-unit")]
    [InlineData("missing-time")]
    [InlineData("future-time")]
    public async Task Collect_InvalidCriticalRoomUsesOnlyMarkedFallbackAndKeepsOtherSensors(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(state => state.EntityId != "sensor.room" ? state : fault switch
        {
            "missing-time" => state with { LastUpdatedUtc = null },
            "future-time" => state with { LastUpdatedUtc = state.ReceivedAtUtc.AddMinutes(1) },
            "malformed-unit" => state with { Attributes = new JsonObject { ["unit_of_measurement"] = new JsonObject() } },
            _ => state with { State = fault }
        });
        await fixture.CollectAsync(0);
        var sample = await fixture.LatestAsync();

        Assert.Equal(5, sample.OutsideTemperatureC);
        Assert.Equal(21.5, JsonNode.Parse(sample.RoomTemperaturesJson)!["sensor.room"]!.GetValue<double>());
        Assert.NotEqual((int)DataQuality.Valid, RoomQuality(sample)["Quality"]!.GetValue<int>());
        Assert.False(ThermalReadinessService.HasRequiredTelemetry(sample, [new ThermalRoomConfig { EntityId = "sensor.room", IsCritical = true }]));
        Assert.False((await fixture.ReadinessAsync()).Single(x => x.Key == "telemetry-quality").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Collect_DisconnectedCacheNeverBecomesFreshValidTelemetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CollectAsync(0);
        fixture.Cache.EndSession(fixture.Session);
        await fixture.CollectAsync(5);
        var sample = await fixture.LatestAsync();

        Assert.Null(sample.OutsideTemperatureC);
        Assert.Null(sample.HeatOutputKw);
        Assert.Equal((int)DataQuality.Unavailable, RoomQuality(sample)["Quality"]!.GetValue<int>());
        Assert.Equal(21, JsonNode.Parse(sample.RoomTemperaturesJson)!["sensor.room"]!.GetValue<double>());
        Assert.False((await fixture.ReadinessAsync()).Single(x => x.Key == "ha-live").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Collect_ThreeDistinctBucketsExclude_ThreeNewMeasurementsRecover_WithSingleEvents()
    {
        await using var fixture = await Fixture.CreateAsync(state => state.EntityId == "sensor.room" ? state with { State = "unknown" } : state);
        for (var index = 0; index < 3; index++) await fixture.CollectAsync(0);
        Assert.False(RoomQuality(await fixture.LatestAsync())["Excluded"]!.GetValue<bool>());
        await fixture.CollectAsync(5);
        await fixture.CollectAsync(10);
        Assert.True(RoomQuality(await fixture.LatestAsync())["Excluded"]!.GetValue<bool>());

        fixture.UpdateRoom("21", 15);
        await fixture.CollectAsync(15);
        await fixture.CollectAsync(20);
        Assert.True(RoomQuality(await fixture.LatestAsync())["Excluded"]!.GetValue<bool>());
        fixture.UpdateRoom("21", 25);
        await fixture.CollectAsync(25);
        fixture.UpdateRoom("21", 30);
        await fixture.CollectAsync(30);
        Assert.False(RoomQuality(await fixture.LatestAsync())["Excluded"]!.GetValue<bool>());

        using var scope = fixture.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>().ThermalEvents
            .Where(x => x.Category == "DataQuality" && x.Message.Contains("sensor.room")).ToListAsync();
        Assert.Single(events.Where(x => x.Severity == "ActionRequired"));
        Assert.Single(events.Where(x => x.Severity == "Information"));
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Collect_NewConnectionDoesNotReuseOldCriticalFallback()
    {
        await using var fixture = await Fixture.CreateAsync(state => state.EntityId == "sensor.room" ? state with { State = "19" } : state);
        await fixture.CollectAsync(0);
        var revision = fixture.Now.AddSeconds(-10);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            (await db.HomeAssistantConnections.SingleAsync()).UpdatedAtUtc = revision;
            await db.SaveChangesAsync();
        }
        fixture.Cache.Invalidate("account-a", revision, true);
        var session = fixture.Cache.BeginSession("account-a", revision)!;
        fixture.Cache.BeginSnapshot(session);
        fixture.Cache.PublishSnapshot(session, Fixture.States(fixture.Now.AddMinutes(5))
            .Select(state => state.EntityId == "sensor.room" ? state with { State = "unknown" } : state));
        await fixture.CollectAsync(5);

        Assert.Equal(21.5, JsonNode.Parse((await fixture.LatestAsync()).RoomTemperaturesJson)!["sensor.room"]!.GetValue<double>());
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Collect_UnknownBackupHeaterDoesNotPublishCompressorCop()
    {
        await using var fixture = await Fixture.CreateAsync(state => state.EntityId == "binary_sensor.backup" ? state with { State = "unknown" } : state);
        await fixture.CollectAsync(0);
        var sample = await fixture.LatestAsync();
        Assert.NotNull(sample.HeatOutputKw);
        Assert.Null(sample.BackupHeaterActive);
        Assert.Null(sample.Cop);
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("0", 0d)]
    [InlineData("0.5", .5)]
    [InlineData("unknown", null)]
    public async Task Collect_RecordsMeasuredDeviationForCurveEvidenceWithoutIssuingWrites(string raw, double? expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "account-a", Role = ThermalEntityRoles.HeatingDeviation, EntityId = "number.heating_deviation", ExpectedUnit = "°C" });
            await db.SaveChangesAsync();
        }
        var deviation = new HomeAssistantState("number.heating_deviation", raw,
            new JsonObject { ["unit_of_measurement"] = "°C" }, fixture.Now, fixture.Now, fixture.Now);
        Assert.True(fixture.Cache.ApplyEvent(fixture.Session, new(deviation.EntityId, deviation, fixture.Now)));

        await fixture.CollectAsync(0);
        var quality = JsonNode.Parse((await fixture.LatestAsync()).QualityJson)!;

        Assert.Equal(expected, quality["heatingDeviationC"]?.GetValue<double>());
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("future-sample", "telemetry-fresh")]
    [InlineData("imported-sample", "telemetry-quality")]
    [InlineData("new-configuration", "ha-snapshot")]
    [InlineData("disabled", "ha-live")]
    public async Task Readiness_RejectsMisleadingRecentDataWithoutAnyControlServices(string fault, string key)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CollectAsync(0);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var sample = await db.ThermalTelemetrySamples.SingleAsync();
            if (fault == "future-sample") sample.TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            if (fault == "imported-sample")
            {
                var quality = JsonNode.Parse(sample.QualityJson)!.AsObject();
                quality["source"] = "HomeAssistantHistoryImport";
                sample.QualityJson = quality.ToJsonString();
            }
            if (fault == "new-configuration") (await db.HomeAssistantConnections.SingleAsync()).UpdatedAtUtc = fixture.Now.AddSeconds(-1);
            if (fault == "disabled") (await db.HomeAssistantConnections.SingleAsync()).TelemetryEnabled = false;
            await db.SaveChangesAsync();
        }

        var checks = await fixture.ReadinessAsync();

        Assert.False(checks.Single(x => x.Key == key).Passed);
        Assert.False(checks.Single(x => x.Key == "telemetry-quality").Passed);
        Assert.Null(fixture.Services.GetService<IHomeAssistantControlClient>());
        await fixture.AssertLegacyAsync();
    }

    private static JsonNode RoomQuality(ThermalTelemetrySample sample) => JsonNode.Parse(sample.QualityJson)!["rooms"]!["sensor.room"]!;

    private sealed class Fixture : IAsyncDisposable
    {
        internal ServiceProvider Services { get; }
        internal HomeAssistantStateCache Cache { get; } = new();
        internal HomeAssistantCacheSession Session { get; private set; } = null!;
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        private readonly HomeAssistantTelemetryCollector _collector;

        private Fixture()
        {
            var database = $"ha-collector-validation-{Guid.NewGuid():N}";
            Services = new ServiceCollection().AddDbContext<PrisstyrningDbContext>(options => options.UseInMemoryDatabase(database))
                .AddScoped<ThermalDiagnosticsService>().BuildServiceProvider();
            _collector = new(Services.GetRequiredService<IServiceScopeFactory>(), Cache, new SensorQualityTracker(), NullLogger<HomeAssistantTelemetryCollector>.Instance);
        }

        internal static async Task<Fixture> CreateAsync(Func<HomeAssistantState, HomeAssistantState>? adjust = null)
        {
            var fixture = new Fixture();
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var revision = fixture.Now.AddHours(-1);
            db.HomeAssistantConnections.Add(new HomeAssistantConnection { UserId = "account-a", BaseUrl = "https://ha.example.test", TelemetryEnabled = true, TelemetryTokenCiphertext = "unused-synthetic-ciphertext", UpdatedAtUtc = revision });
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", HeatPumpPowerSignVerified = true, UpdatedAtUtc = revision });
            db.ThermalRoomConfigs.AddRange(new ThermalRoomConfig { UserId = "account-a", EntityId = "sensor.room", IsCritical = true }, new ThermalRoomConfig { UserId = "account-a", EntityId = "sensor.other_room" });
            foreach (var (role, id, unit) in new[]
            {
                (ThermalEntityRoles.OutsideTemperature, "sensor.outside", "°C"),
                (ThermalEntityRoles.LeavingWaterTemperature, "sensor.lwt", "°C"),
                (ThermalEntityRoles.ReturnWaterTemperature, "sensor.rwt", "°C"),
                (ThermalEntityRoles.Flow, "sensor.flow", "l/min"),
                (ThermalEntityRoles.HeatPumpPower, "sensor.power", "kW"),
                (ThermalEntityRoles.BackupHeaterActive, "binary_sensor.backup", "bool")
            }) db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "account-a", Role = role, EntityId = id, ExpectedUnit = unit });
            await db.SaveChangesAsync();
            fixture.Session = fixture.Cache.BeginSession("account-a", revision)!;
            fixture.Cache.BeginSnapshot(fixture.Session);
            fixture.Cache.PublishSnapshot(fixture.Session, States(fixture.Now).Select(state => adjust?.Invoke(state) ?? state));
            return fixture;
        }

        internal Task CollectAsync(int minutes) => _collector.CollectAsync("account-a", CancellationToken.None, Now.AddMinutes(minutes));

        internal void UpdateRoom(string value, int minutes)
        {
            var state = States(Now.AddMinutes(minutes)).Single(x => x.EntityId == "sensor.room") with { State = value };
            Assert.True(Cache.ApplyEvent(Session, new(state.EntityId, state, state.LastUpdatedUtc)));
        }

        internal async Task<ThermalTelemetrySample> LatestAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>().ThermalTelemetrySamples.AsNoTracking().OrderByDescending(x => x.TimestampUtc).FirstAsync();
        }

        internal async Task<IReadOnlyList<ReadinessCheck>> ReadinessAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var connection = new HomeAssistantConnectionService(db, TestSecretProtector.Instance, new UnusedValidator(), Cache, new HomeAssistantConnectionChanges());
            return await new ThermalReadinessService(db, Cache, connection, ThermalCurrentModelTestData.Build)
                .EvaluateAsync("account-a", ControlMode.Shadow);
        }

        internal async Task AssertLegacyAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var site = await db.ThermalSiteConfigs.SingleAsync();
            Assert.Equal("Legacy", site.ControlMode);
            Assert.Equal("Legacy", site.DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        }

        internal static IEnumerable<HomeAssistantState> States(DateTimeOffset now) => new[]
        {
            ("sensor.room", "21", "°C"), ("sensor.other_room", "21.5", "°C"), ("sensor.outside", "5", "°C"),
            ("sensor.lwt", "35", "°C"), ("sensor.rwt", "30", "°C"), ("sensor.flow", "12", "l/min"),
            ("sensor.power", "2", "kW"), ("binary_sensor.backup", "off", "bool")
        }.Select(item => new HomeAssistantState(item.Item1, item.Item2, new JsonObject { ["unit_of_measurement"] = item.Item3 }, now, now, now));

        public async ValueTask DisposeAsync() { _collector.Dispose(); await Services.DisposeAsync(); }
    }

    private sealed class UnusedValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Readiness must not contact HA.");
    }
}
