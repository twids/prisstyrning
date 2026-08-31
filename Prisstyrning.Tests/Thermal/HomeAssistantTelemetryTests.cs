using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public class HomeAssistantTelemetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("1000", "W", "kW", 1)]
    [InlineData("1", "m³/h", "l/min", 16.6666667)]
    [InlineData("68", "°F", "°C", 20)]
    [InlineData("125", "öre/kWh", "SEK/kWh", 1.25)]
    [InlineData("36", "km/h", "m/s", 10)]
    public void Normalize_ConvertsSupportedUnits(string value, string source, string target, double expected)
    {
        var state = State("sensor.test", value, source);
        var result = SensorValueNormalizer.Normalize(state, target);
        Assert.Equal(DataQuality.Valid, result.Quality);
        Assert.Equal(expected, result.Value!.Value, 5);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    public void Normalize_MarksUnavailableStates(string value)
    {
        var result = SensorValueNormalizer.Normalize(State("sensor.test", value, "°C"), "°C");
        Assert.Equal(DataQuality.Unavailable, result.Quality);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Normalize_RejectsMissingUnitInsteadOfGuessingTheScale()
    {
        var state = State("sensor.power", "1000", string.Empty);

        var result = SensorValueNormalizer.Normalize(state, "kW");

        Assert.Equal(DataQuality.Invalid, result.Quality);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Tracker_ExcludesAfterThreeInvalidAndRecoversAfterThreeValid()
    {
        var tracker = new SensorQualityTracker();
        var rules = new SensorValidationRules(5, 35, 3, TimeSpan.FromMinutes(10));
        SensorAssessment assessment = null!;
        for (var index = 0; index < 3; index++)
            assessment = tracker.Assess("sensor.room", State("sensor.room", "99", "°C"), new(99, null, "°C", DataQuality.Valid, null), rules, Now.AddMinutes(index * 5));
        Assert.True(assessment.Excluded);
        Assert.True(assessment.BecameExcluded);

        for (var index = 0; index < 3; index++)
            assessment = tracker.Assess("sensor.room", State("sensor.room", "21", "°C", Now.AddMinutes((index + 3) * 5)), new(21, null, "°C", DataQuality.Valid, null), rules, Now.AddMinutes((index + 3) * 5));
        Assert.False(assessment.Excluded);
        Assert.True(assessment.BecameRecovered);
    }

    [Fact]
    public void Tracker_MarksOldLastUpdatedAsStale()
    {
        var tracker = new SensorQualityTracker();
        var state = State("sensor.room", "21", "°C", Now.AddMinutes(-11));
        var result = tracker.Assess("sensor.room", state, new(21, null, "°C", DataQuality.Valid, null),
            new SensorValidationRules(5, 35, 3, TimeSpan.FromMinutes(10)), Now);
        Assert.Equal(DataQuality.Stale, result.Quality);
    }

    [Fact]
    public void RepresentativeError_UsesConfiguredBaseTargetAndRoomOffsets()
    {
        var rooms = new[]
        {
            new ThermalRoomConfig { EntityId = "sensor.room_1", TargetOffsetC = 0, Weight = 1 },
            new ThermalRoomConfig { EntityId = "sensor.room_2", TargetOffsetC = 1, Weight = 3 }
        };
        var values = new Dictionary<string, double>
        {
            ["sensor.room_1"] = 20,
            ["sensor.room_2"] = 22
        };

        var error = HomeAssistantTelemetryCollector.CalculateRepresentativeError(rooms, values, 20);

        Assert.Equal(0.75, error);
    }

    [Fact]
    public void StateCache_TracksSocketLivenessSeparatelyFromTheStartSnapshot()
    {
        var cache = new HomeAssistantStateCache();
        cache.Replace([State("sensor.room", "21", "°C")]);

        Assert.NotNull(cache.LastSnapshotUtc);
        Assert.False(cache.Connected);

        cache.MarkConnected();
        Assert.True(cache.Connected);
        Assert.NotNull(cache.LastActivityUtc);

        cache.MarkDisconnected();
        Assert.False(cache.Connected);
        Assert.NotNull(cache.LastSnapshotUtc);
    }

    [Theory]
    [InlineData("ftp://home-assistant.local")]
    [InlineData("http://user:password@home-assistant.local")]
    [InlineData("not-a-url")]
    public void BaseUrl_RejectsUnsupportedOrCredentialBearingAddresses(string value)
    {
        Assert.False(HomeAssistantTelemetryClient.IsSupportedBaseUrl(value));
    }

    [Theory]
    [InlineData("sensor.deviation")]
    [InlineData("number.deviation/value")]
    [InlineData("number.deviation value")]
    [InlineData("")]
    public void ControlAllowlist_RejectsAnythingExceptASimpleNumberEntity(string entityId)
    {
        Assert.False(HomeAssistantControlClient.IsAllowedNumberEntity(entityId));
    }

    [Fact]
    public void ControlVerification_RequiresAFreshMatchingState()
    {
        var sentAt = Now;
        var matching = State("number.deviation", "1.0", "°C", sentAt) with { ReceivedAtUtc = sentAt.AddMilliseconds(1) };
        var stale = matching with { ReceivedAtUtc = sentAt.AddMilliseconds(-1) };
        var wrong = matching with { State = "0.5" };

        Assert.True(HomeAssistantControlClient.IsVerifiedState(matching, 1, sentAt));
        Assert.False(HomeAssistantControlClient.IsVerifiedState(stale, 1, sentAt));
        Assert.False(HomeAssistantControlClient.IsVerifiedState(wrong, 1, sentAt));
    }

    [Fact]
    public async Task ControlClient_PostsOnlyAllowlistedServiceAndVerifiesTheObservedValue()
    {
        await using var db = new PrisstyrningDbContext(
            new DbContextOptionsBuilder<PrisstyrningDbContext>()
                .UseInMemoryDatabase($"ha-control-{Guid.NewGuid():N}")
                .Options);
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig
        {
            UserId = "control-user",
            ControlMode = "LwtActive",
            ActiveDeviationLimitC = 1
        });
        await db.SaveChangesAsync();
        var connections = CreateConnectionService(db);
        await connections.SaveAsync("control-user", new UpdateHomeAssistantConnectionRequest(
            "https://ha.example.se",
            "telemetry-secret",
            "control-secret",
            TelemetryEnabled: true,
            ControlEnabled: true,
            HeatingDeviationEntityId: "number.heating_deviation",
            StaleAfterMinutes: 10));

        var cache = new HomeAssistantStateCache();
        string? path = null;
        string? authorization = null;
        var handler = new StubHandler(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            authorization = request.Headers.Authorization?.ToString();
            cache.Upsert("control-user", State("number.heating_deviation", "1", "°C") with { ReceivedAtUtc = DateTimeOffset.UtcNow });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var client = new HomeAssistantControlClient(
            new StubFactory(new HttpClient(handler)),
            connections,
            cache,
            db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Thermal:AllowLwtActive"] = "true"
            }).Build());

        await client.SetHeatingDeviationAsync("control-user", 1);

        Assert.Equal("/api/services/number/set_value", path);
        Assert.Equal("Bearer control-secret", authorization);
    }

    [Fact]
    public async Task ControlClient_DeploymentKillSwitchBlocksNonZeroWrite()
    {
        await using var db = new PrisstyrningDbContext(
            new DbContextOptionsBuilder<PrisstyrningDbContext>()
                .UseInMemoryDatabase($"ha-control-kill-switch-{Guid.NewGuid():N}")
                .Options);
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig
        {
            UserId = "control-user",
            ControlMode = "LwtActive",
            ActiveDeviationLimitC = 1
        });
        await db.SaveChangesAsync();

        var client = new HomeAssistantControlClient(
            new StubFactory(new HttpClient(new StubHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))),
            CreateConnectionService(db),
            new HomeAssistantStateCache(),
            db,
            new ConfigurationBuilder().Build());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SetHeatingDeviationAsync("control-user", 0.5));

        Assert.Contains("kill switch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_DoesNotCountSyntheticCriticalRoomFallbackAsValidTelemetry()
    {
        var sample = new ThermalTelemetrySample
        {
            OutsideTemperatureC = 0,
            LeavingWaterTemperatureC = 35,
            ReturnWaterTemperatureC = 30,
            FlowLitresPerMinute = 12,
            RoomTemperaturesJson = "{\"sensor.critical\":21}",
            QualityJson = "{\"rooms\":{\"sensor.critical\":{\"quality\":2,\"excluded\":true}}}"
        };
        var rooms = new[] { new ThermalRoomConfig { EntityId = "sensor.critical", IsCritical = true } };

        Assert.False(ThermalReadinessService.HasRequiredTelemetry(sample, rooms));
    }

    [Fact]
    public void WeatherForecast_NormalizesTemperatureAndWindFromHaAttributes()
    {
        var state = new HomeAssistantState(
            "weather.home",
            "sunny",
            new JsonObject
            {
                ["temperature_unit"] = "°F",
                ["wind_speed_unit"] = "km/h",
                ["forecast"] = new JsonArray
                {
                    new JsonObject { ["datetime"] = Now.AddHours(1).ToString("O"), ["temperature"] = 68, ["wind_speed"] = 36 },
                    new JsonObject { ["datetime"] = Now.AddHours(2).ToString("O"), ["temperature"] = 69.8, ["wind_speed"] = 18 }
                }
            },
            Now,
            Now,
            Now);

        var result = HomeAssistantWeatherForecastParser.Parse(state, Now);

        Assert.Equal(DataQuality.Valid, result.Quality);
        Assert.Equal(20d, result.Points[0].TemperatureC);
        Assert.NotNull(result.Points[0].WindSpeedMps);
        Assert.Equal(10d, result.Points[0].WindSpeedMps!.Value, 5);
    }

    private static HomeAssistantState State(string entityId, string value, string unit, DateTimeOffset? updated = null) => new(
        entityId,
        value,
        new JsonObject { ["unit_of_measurement"] = unit, ["friendly_name"] = "Test" },
        updated ?? Now,
        updated ?? Now,
        Now);

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    private static HomeAssistantConnectionService CreateConnectionService(PrisstyrningDbContext db) =>
        new(db, TestSecretProtector.Instance, new AcceptingEndpointValidator(), new HomeAssistantStateCache(), new HomeAssistantConnectionChanges());

    private sealed class AcceptingEndpointValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri(value));
    }
}
