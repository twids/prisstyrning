using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.HomeAssistant;

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
        public Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(_history.TryGetValue(entityId, out var states) ? states : (IReadOnlyList<HomeAssistantState>)[]);
    }
}
