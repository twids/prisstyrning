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
        Assert.True(lastQuality["excluded"]!.GetValue<bool>());
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
        private readonly IReadOnlyDictionary<string, IReadOnlyList<HomeAssistantState>> _history;

        public FakeHistoryClient(IReadOnlyDictionary<string, IReadOnlyList<HomeAssistantState>> history) => _history = history;
        public Task<bool> TestConnectionAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<HomeAssistantState?> GetStateAsync(string userId, string entityId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_history.TryGetValue(entityId, out var states) ? states.LastOrDefault() : null);
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HomeAssistantState>>(_history.Values.SelectMany(x => x).ToArray());
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(ResolvedHomeAssistantConnection connection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("History import must not start a live subscription.");
        public Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(_history.TryGetValue(entityId, out var states) ? states : (IReadOnlyList<HomeAssistantState>)[]);
    }
}
