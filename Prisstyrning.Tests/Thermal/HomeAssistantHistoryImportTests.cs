using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantHistoryImportTests
{
    [Fact]
    public async Task History_ExternalCopUsesHistoricalOperatingPhase_NotAverageOrTodaysState()
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "test", HeatPumpPowerSignVerified = true });
        var readings = new[] { (ThermalEntityRoles.CopRealtime, "4.8", "COP"), (ThermalEntityRoles.CopAverage, "3.5", "COP"),
            (ThermalEntityRoles.DhwActive, "off", "bool"), (ThermalEntityRoles.DefrostActive, "off", "bool"),
            (ThermalEntityRoles.BackupHeaterActive, "off", "bool"), (ThermalEntityRoles.HeatPumpPower, "2", "kW") };
        db.ThermalEntityConfigs.AddRange(readings.Select(x => new ThermalEntityConfig { UserId = "test", Role = x.Item1,
            EntityId = "sensor." + x.Item1, ExpectedUnit = x.Item3 }));
        await db.SaveChangesAsync();
        var history = readings.ToDictionary(x => "sensor." + x.Item1,
            x => (IReadOnlyList<HomeAssistantState>)[State("sensor." + x.Item1, x.Item2, x.Item3, from)]);
        history["sensor." + ThermalEntityRoles.HeatPumpPower] = [State("sensor." + ThermalEntityRoles.HeatPumpPower, "2", "kW", from),
            State("sensor." + ThermalEntityRoles.HeatPumpPower, ".05", "kW", from.AddMinutes(5))];
        await new HomeAssistantHistoryImportService(db, new FakeHistoryClient(history)).ImportAsync("test", from, from.AddMinutes(5));
        var samples = await db.ThermalTelemetrySamples.OrderBy(x => x.TimestampUtc).ToArrayAsync();
        Assert.Equal(4.8, samples[0].Cop);
        Assert.Null(samples[1].Cop);
        Assert.Null(samples[0].HeatOutputKw); // No hidden flow/delta-T calculation was required for external COP.
        Assert.Equal("HeldWhileIdle", JsonNode.Parse(samples[1].QualityJson)!["entities"]![ThermalEntityRoles.CopRealtime]!["Usage"]!.GetValue<string>());
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Legacy", (await db.ThermalSiteConfigs.SingleAsync()).ControlMode);
    }

    [Fact]
    public async Task History_UsesOnlyContemporaneousLivenessAndPreservesOldMeasurementTimestamp()
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room", FreshnessEntityId = "sensor.seen" });
        await db.SaveChangesAsync();
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.room"] = [State("sensor.room", "21", "°C", from.AddHours(-2)) with { ReceivedAtUtc = DateTimeOffset.UtcNow }],
            ["sensor.seen"] = [State("sensor.seen", from.ToString("O"), "timestamp", from) with { ReceivedAtUtc = DateTimeOffset.UtcNow }]
        });
        var report = await new HomeAssistantHistoryImportService(db, client).PreviewAsync("test", from, from.AddMinutes(20));
        var sensor = Assert.Single(report.Sensors);
        Assert.Equal(3, sensor.Valid);
        Assert.Equal(2, sensor.Stale);
        Assert.Equal(0, sensor.Invalid);
        Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("unit", false)]
    [InlineData("unit", true)]
    [InlineData("value", false)]
    [InlineData("value", true)]
    public async Task PreviewAndImport_ContradictoryTimestamp_IsOrderIndependentAndIsolated(string conflict, bool reverse)
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room" });
        db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "test", EntityId = "sensor.outside", Role = ThermalEntityRoles.OutsideTemperature, ExpectedUnit = "°C" });
        await db.SaveChangesAsync();
        var valid = State("sensor.room", "21", "°C", from);
        var conflicting = conflict switch
        {
            "unit" => State("sensor.room", "21", "°F", from),
            "value" => valid with { State = "24" },
            _ => valid with { State = "unknown" }
        };
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.room"] = reverse ? [conflicting, valid] : [valid, conflicting],
            ["sensor.outside"] = [State("sensor.outside", "5", "°C", from)]
        });
        var service = new HomeAssistantHistoryImportService(db, client);
        var preview = await service.PreviewAsync("test", from, from.AddMinutes(10));
        Assert.Equal(0, preview.Sensors.Single(x => x.EntityId == "sensor.room").Valid);
        Assert.Contains("motstridiga uppgifter", preview.Sensors.Single(x => x.EntityId == "sensor.room").TimelineIssue);
        Assert.Equal(3, preview.Sensors.Single(x => x.EntityId == "sensor.outside").Valid);
        var result = await service.ImportAsync("test", from, from.AddMinutes(10));
        Assert.Contains("sensor.room", result.EntitiesWithoutHistory);
        Assert.All(await db.ThermalTelemetrySamples.ToListAsync(), sample =>
        {
            Assert.Equal("{}", sample.RoomTemperaturesJson);
            Assert.Equal(5, sample.OutsideTemperatureC);
        });
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
    }

    [Fact]
    public async Task Preview_IdenticalRepeatedObservation_DoesNotInvalidateTimeline()
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room" });
        await db.SaveChangesAsync();
        var value = State("sensor.room", "21", "°C", from);
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.room"] = [value, value with { ReceivedAtUtc = from.AddDays(1) }]
        });
        var report = await new HomeAssistantHistoryImportService(db, client).PreviewAsync("test", from, from.AddMinutes(10));
        Assert.Equal(3, Assert.Single(report.Sensors).Valid);
        Assert.Null(Assert.Single(report.Sensors).TimelineIssue);
    }

    [Theory]
    [InlineData(1, 2, 277)]
    [InlineData(2, 2, 289)]
    [InlineData(2, 1, 301)]
    public async Task Preview_DaylightSavingOffsets_CountUtcBucketsWithoutInventingMissingHours(int fromOffset, int toOffset, int expected)
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2025, 3, 30, 0, 0, 0, TimeSpan.FromHours(fromOffset));
        var to = new DateTimeOffset(2025, 3, 31, 0, 0, 0, TimeSpan.FromHours(toOffset));
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room" });
        await db.SaveChangesAsync();
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>());
        var report = await new HomeAssistantHistoryImportService(db, client).PreviewAsync("test", from, to);
        // Import's existing contract includes both boundary samples (92/96/100 quarters plus endpoint).
        Assert.Equal(expected, report.ExpectedSamples);
        Assert.Equal(TimeSpan.Zero, report.FromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, report.ToUtc.Offset);
        Assert.Equal(expected, Assert.Single(report.Sensors).Unavailable);
        Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
    }

    [Fact]
    public async Task Preview_UsesSameQualityAsImport_WithoutWritingOrReadingOtherAccounts()
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room", Name = "Vardagsrum" });
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "other", EntityId = "sensor.foreign" });
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.disabled", Enabled = false });
        db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "test", EntityId = "weather.home", Role = ThermalEntityRoles.WeatherForecast });
        db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample { UserId = "test", TimestampUtc = from, RoomTemperaturesJson = "{\"sensor.room\":18}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.room"] = [State("sensor.room", "21", "°C", from)]
        });
        var service = new HomeAssistantHistoryImportService(db, client);
        var preview = await service.PreviewAsync("test", from, from.AddMinutes(20));

        Assert.Equal(5, preview.ExpectedSamples);
        Assert.Equal(1, preview.ExistingSamples);
        var room = Assert.Single(preview.Sensors);
        Assert.Equal(3, room.Valid);
        Assert.Equal(2, room.Stale);
        Assert.Equal(0, room.Invalid + room.Unavailable);
        Assert.Equal(("test", "sensor.room"), Assert.Single(client.Requests));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Single(await db.ThermalTelemetrySamples.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ThermalEvents.ToListAsync());
        Assert.Empty(await db.ThermalModelVersions.ToListAsync());
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());

        var imported = await service.ImportAsync("test", from, from.AddMinutes(20));
        Assert.Equal(room, Assert.Single(imported.Coverage!.Sensors));
        Assert.Equal(4, imported.ImportedSamples);
        Assert.Equal(1, imported.ExistingSamplesPreserved);
    }

    [Theory]
    [InlineData("unknown", "°C", false, 0, 2)]
    [InlineData("999", "°C", false, 2, 0)]
    [InlineData("21", "bananas", false, 2, 0)]
    [InlineData("21", "°C", true, 0, 2)]
    public async Task Preview_SeparatesInvalidFromUnavailable(string value, string unit, bool missingTime, int invalid, int unavailable)
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room" });
        await db.SaveChangesAsync();
        var state = State("sensor.room", value, unit, from);
        if (missingTime) state = state with { LastUpdatedUtc = null };
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>> { ["sensor.room"] = [state] });
        var report = await new HomeAssistantHistoryImportService(db, client).PreviewAsync("test", from, from.AddMinutes(5));
        var sensor = Assert.Single(report.Sensors);
        Assert.Equal(invalid, sensor.Invalid);
        Assert.Equal(unavailable, sensor.Unavailable);
        Assert.Equal(0, sensor.Valid);
        Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
        Assert.Empty(await db.ThermalEvents.ToListAsync());
    }

    [Fact]
    public async Task Import_ResamplesToFiveMinutesAndPreservesExistingSnapshots()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase($"ha-history-{Guid.NewGuid():N}")
            .Options;
        await using var db = new PrisstyrningDbContext(options);
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "default" });
        db.ThermalEntityConfigs.Add(new ThermalEntityConfig
        {
            UserId = "default",
            Role = "outside_temperature",
            EntityId = "sensor.outside",
            ExpectedUnit = "°C",
            Enabled = true,
            MinimumValid = -50,
            MaximumValid = 50
        });
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig
        {
            UserId = "default",
            Name = "Vardagsrum",
            EntityId = "sensor.living",
            Enabled = true
        });
        db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
        {
            UserId = "default",
            TimestampUtc = from.AddMinutes(5),
            OutsideTemperatureC = -99
        });
        await db.SaveChangesAsync();

        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.outside"] = [State("sensor.outside", "68", "°F", from)],
            ["sensor.living"] = [State("sensor.living", "21.4", "°C", from)]
        });
        var importer = new HomeAssistantHistoryImportService(db, client);

        var result = await importer.ImportAsync("default", from, from.AddMinutes(10));

        Assert.Equal(2, result.ImportedSamples);
        Assert.Equal(1, result.ExistingSamplesPreserved);
        var samples = await db.ThermalTelemetrySamples.OrderBy(x => x.TimestampUtc).ToListAsync();
        Assert.Equal(3, samples.Count);
        Assert.NotNull(samples[0].OutsideTemperatureC);
        Assert.Equal(20d, samples[0].OutsideTemperatureC!.Value, 5);
        Assert.Equal(-99, samples[1].OutsideTemperatureC);
        Assert.Contains("sensor.living", samples[2].RoomTemperaturesJson);
        Assert.Single(await db.ThermalEvents.Where(x => x.Category == "HistoryImport").ToListAsync());
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("malformed-unit")]
    [InlineData("missing-time")]
    [InlineData("future-measurement")]
    [InlineData("wrong-entity")]
    public async Task Import_InvalidRoomIsIsolatedAndNeverInventsMeasurementTime(string fault)
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var room = State("sensor.room", "21", "°C", from);
        room = fault switch
        {
            "missing-time" => room with { LastUpdatedUtc = null },
            "future-measurement" => room with { LastUpdatedUtc = from.AddDays(1) },
            "wrong-entity" => room with { EntityId = "sensor.foreign" },
            "malformed-unit" => room with { Attributes = new JsonObject { ["unit_of_measurement"] = new JsonArray(42) } },
            _ => room with { State = fault }
        };
        db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "test", Role = ThermalEntityRoles.OutsideTemperature, EntityId = "sensor.outside", ExpectedUnit = "°C" });
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room", IsCritical = true });
        await db.SaveChangesAsync();
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.outside"] = [State("sensor.outside", "5", "°C", from)],
            ["sensor.room"] = [room]
        });

        var result = await new HomeAssistantHistoryImportService(db, client).ImportAsync("test", from, from.AddMinutes(10));

        Assert.Equal(3, result.ImportedSamples);
        foreach (var sample in await db.ThermalTelemetrySamples.ToListAsync())
        {
            Assert.Equal(5, sample.OutsideTemperatureC);
            Assert.Equal("{}", sample.RoomTemperaturesJson);
            var quality = JsonNode.Parse(sample.QualityJson)!;
            Assert.Equal("HomeAssistantHistoryImport", quality["source"]!.GetValue<string>());
            Assert.NotEqual(0, quality["rooms"]!["sensor.room"]!["quality"]!.GetValue<int>());
        }
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
    }

    [Fact]
    public async Task Import_UnknownAndLongGapsBreakCarryForward()
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room" });
        await db.SaveChangesAsync();
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.room"] = [State("sensor.room", "21", "°C", from), State("sensor.room", "unknown", "°C", from.AddMinutes(5)), State("sensor.room", "21", "°C", from.AddMinutes(10))]
        });

        await new HomeAssistantHistoryImportService(db, client).ImportAsync("test", from, from.AddMinutes(35));

        var samples = await db.ThermalTelemetrySamples.OrderBy(x => x.TimestampUtc).ToListAsync();
        Assert.Contains("sensor.room", samples[0].RoomTemperaturesJson);
        Assert.Equal("{}", samples[1].RoomTemperaturesJson);
        Assert.Contains("sensor.room", samples[4].RoomTemperaturesJson);
        Assert.Equal("{}", samples[5].RoomTemperaturesJson);
        var lastQuality = JsonNode.Parse(samples[^1].QualityJson)!["rooms"]!["sensor.room"]!;
        Assert.Equal((int)DataQuality.Stale, lastQuality["quality"]!.GetValue<int>());
        Assert.False(lastQuality["excluded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Import_RateAndExclusionAssessmentIncludesPreservedBucketsWithoutOverwritingThem()
    {
        await using var db = HistoryDatabase();
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "test", EntityId = "sensor.room" });
        db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample { UserId = "test", TimestampUtc = from.AddMinutes(5), RoomTemperaturesJson = "{\"sensor.room\":18}" });
        await db.SaveChangesAsync();
        var client = new FakeHistoryClient(new Dictionary<string, IReadOnlyList<HomeAssistantState>>
        {
            ["sensor.room"] = Enumerable.Range(0, 4).Select(i => State("sensor.room", i == 0 ? "21" : "25", "°C", from.AddMinutes(i * 5))).ToArray()
        });

        var result = await new HomeAssistantHistoryImportService(db, client).ImportAsync("test", from, from.AddMinutes(15));

        Assert.Equal(1, result.ExistingSamplesPreserved);
        var samples = await db.ThermalTelemetrySamples.OrderBy(x => x.TimestampUtc).ToListAsync();
        Assert.Equal("{\"sensor.room\":18}", samples[1].RoomTemperaturesJson);
        Assert.True(JsonNode.Parse(samples[^1].QualityJson)!["rooms"]!["sensor.room"]!["excluded"]!.GetValue<bool>());
        Assert.Equal("{}", samples[^1].RoomTemperaturesJson);
    }

    private static PrisstyrningDbContext HistoryDatabase() => new(new DbContextOptionsBuilder<PrisstyrningDbContext>()
        .UseInMemoryDatabase($"history-validation-{Guid.NewGuid():N}").Options);

    private static HomeAssistantState State(string entityId, string value, string unit, DateTimeOffset timestamp) => new(
        entityId,
        value,
        new JsonObject { ["unit_of_measurement"] = unit },
        timestamp,
        timestamp,
        timestamp);

    private sealed class FakeHistoryClient : IHomeAssistantTelemetryClient
    {
        public List<(string UserId, string EntityId)> Requests { get; } = [];
        private readonly IReadOnlyDictionary<string, IReadOnlyList<HomeAssistantState>> _history;

        public FakeHistoryClient(IReadOnlyDictionary<string, IReadOnlyList<HomeAssistantState>> history) => _history = history;
        public Task<bool> TestConnectionAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<HomeAssistantState?> GetStateAsync(string userId, string entityId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_history.TryGetValue(entityId, out var states) ? states.LastOrDefault() : null);
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HomeAssistantState>>(_history.Values.SelectMany(x => x).ToArray());
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(ResolvedHomeAssistantConnection connection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("History import must not start a live subscription.");
        public Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            Requests.Add((userId, entityId));
            return Task.FromResult(_history.TryGetValue(entityId, out var states) ? states : (IReadOnlyList<HomeAssistantState>)[]);
        }
    }
}
