using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class LwtClimateControlTests
{
    private const string Actuator = "climate.bridge0_lwt_deviation_heating";
    private const string Feedback = "sensor.bridge0_lwt_deviation_heating";

    [Fact]
    public async Task Climate_WritesOnlyTemperatureAndVerifiesSeparateSensor()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Client.SetHeatingDeviationAsync("account-a", 1);
        Assert.Equal("/api/services/climate/set_temperature", fixture.Path);
        using var payload = JsonDocument.Parse(fixture.Payload!);
        Assert.Equal(2, payload.RootElement.EnumerateObject().Count());
        Assert.Equal(Actuator, payload.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("heat", fixture.Cache.Snapshot("account-a").Single(x => x.EntityId == Actuator).State);
    }

    [Theory]
    [InlineData("Legacy")]
    [InlineData("Shadow")]
    public async Task InactiveModes_NeverWriteEvenZero(string mode)
    {
        await using var fixture = await Fixture.Create(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 0));
        Assert.Null(fixture.Path);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(-.5)]
    public async Task Climate_RejectsValuesBetweenAdvertisedSteps(double value)
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", value));
        Assert.Null(fixture.Path);
    }

    [Fact]
    public async Task Climate_TargetEchoDoesNotProveHardwareFeedback()
    {
        await using var fixture = await Fixture.Create();
        fixture.ReportFeedback = false;
        using var timeout = new CancellationTokenSource();
        fixture.AfterPost = timeout.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 1, timeout.Token));
        Assert.NotNull(fixture.Path);
    }

    [Theory]
    [InlineData("°F", "0")]
    [InlineData("°C", "unavailable")]
    [InlineData("°C", "NaN")]
    public async Task Climate_RejectsInvalidFeedbackBeforePosting(string unit, string value)
    {
        await using var fixture = await Fixture.Create();
        fixture.Cache.Upsert("account-a", Sensor(value, unit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 1));
        Assert.Null(fixture.Path);
    }

    [Fact]
    public async Task Climate_RequiresAccountOwnedFeedbackMapping()
    {
        await using var fixture = await Fixture.Create();
        var mapping = await fixture.Db.ThermalEntityConfigs.SingleAsync();
        mapping.UserId = "account-b";
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 1));
        Assert.Null(fixture.Path);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2)]
    public async Task Climate_RejectsNonfiniteOrOutOfBoundsRequests(double value)
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", value));
        Assert.Null(fixture.Path);
    }

    [Fact]
    public async Task Climate_RejectsStaleFeedbackBeforePosting()
    {
        await using var fixture = await Fixture.Create();
        fixture.Cache.Upsert("account-a", Sensor("0") with { ReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-11) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 1));
        Assert.Null(fixture.Path);
    }

    [Fact]
    public async Task Climate_ZeroRollbackAllowedWhenDeploymentSwitchDisabled()
    {
        await using var fixture = await Fixture.Create(allowLwt: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 1));
        Assert.Null(fixture.Path);
        await fixture.Client.SetHeatingDeviationAsync("account-a", 0);
        Assert.NotNull(fixture.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Climate_ZeroRollbackCanRecoverMissingFeedbackButRequiresFreshConfirmation(bool stale)
    {
        await using var fixture = await Fixture.Create(allowLwt: false);
        fixture.Cache.Upsert("account-a", stale
            ? Sensor("0") with { ReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-11) }
            : Sensor("unavailable"));
        await fixture.Client.SetHeatingDeviationAsync("account-a", 0);
        Assert.NotNull(fixture.Path);
        using var payload = JsonDocument.Parse(fixture.Payload!);
        Assert.Equal(0, payload.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Climate_ZeroWithUnavailableFeedbackDoesNotTreatHttpSuccessAsConfirmation()
    {
        await using var fixture = await Fixture.Create(allowLwt: false);
        fixture.Cache.Upsert("account-a", Sensor("unavailable"));
        fixture.ReportFeedback = false;
        using var timeout = new CancellationTokenSource();
        fixture.AfterPost = timeout.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 0, timeout.Token));
        Assert.NotNull(fixture.Path);
    }

    [Fact]
    public async Task Climate_ZeroDoesNotBypassActuatorCapabilityValidation()
    {
        await using var fixture = await Fixture.Create(allowLwt: false);
        fixture.Cache.Upsert("account-a", Sensor("unavailable"));
        var actuator = Climate();
        actuator.Attributes["min_temp"] = 7;
        fixture.Cache.Upsert("account-a", actuator);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.SetHeatingDeviationAsync("account-a", 0));
        Assert.Null(fixture.Path);
    }

    [Theory]
    [InlineData("climate.")]
    [InlineData("climate.name/service")]
    [InlineData("climate.name.other")]
    [InlineData("sensor.bridge0_lwt_deviation_heating")]
    public void ActuatorAllowlist_RejectsOtherDomainsAndInvalidIds(string id) => Assert.False(LwtControlBinding.IsActuator(id));

    [Fact]
    public void Capabilities_RejectOrdinaryThermostatAndStaleOrMissingMetadata()
    {
        var state = Climate();
        Assert.Equal(1, LwtControlBinding.Step(Actuator, state, DateTimeOffset.UtcNow, 1));
        Assert.Null(LwtControlBinding.Step(Actuator, state with { ReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-11) }, DateTimeOffset.UtcNow, 1));
        state.Attributes["min_temp"] = 7;
        Assert.Null(LwtControlBinding.Step(Actuator, state, DateTimeOffset.UtcNow, 1));
        state.Attributes["min_temp"] = -10;
        state.Attributes.Remove("target_temp_step");
        Assert.Null(LwtControlBinding.Step(Actuator, state, DateTimeOffset.UtcNow, 1));
    }

    [Fact]
    public async Task Readiness_AcceptsSeparateFeedbackButNotUnavailableSensor()
    {
        await using var fixture = await Fixture.Create("Shadow");
        var readiness = new ThermalReadinessService(fixture.Db, fixture.Cache, fixture.Connections, ThermalCurrentModelTestData.Build);
        Assert.True((await readiness.EvaluateAsync("account-a", ControlMode.LwtActive)).Single(x => x.Key == "p1p2-control").Passed);
        fixture.Cache.Upsert("account-a", Sensor("unavailable"));
        Assert.False((await readiness.EvaluateAsync("account-a", ControlMode.LwtActive)).Single(x => x.Key == "p1p2-control").Passed);
        Assert.Equal("Shadow", (await fixture.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
        Assert.Null(fixture.Path);
    }

    [Fact]
    public async Task Configuration_CannotReplaceActiveFeedbackSensor()
    {
        await using var fixture = await Fixture.Create();
        var service = new ThermalDataService(fixture.Db, new ThermalInstallationRegistry(fixture.Db));
        var draft = await service.GetConfigAsync("account-a");
        draft.Entities[0].EntityId = "sensor.other";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateConfigAsync("account-a", draft));
        Assert.Equal(Feedback, (await fixture.Db.ThermalEntityConfigs.SingleAsync()).EntityId);
        Assert.Null(fixture.Path);
    }

    private static HomeAssistantState Climate() => new(Actuator, "heat",
        new JsonObject { ["temperature"] = 0, ["current_temperature"] = 21, ["min_temp"] = -10, ["max_temp"] = 10, ["target_temp_step"] = 1 },
        DateTimeOffset.UtcNow.AddHours(-4), DateTimeOffset.UtcNow.AddHours(-4), DateTimeOffset.UtcNow);
    private static HomeAssistantState Sensor(string value, string unit = "°C") => new(Feedback, value,
        new JsonObject { ["unit_of_measurement"] = unit }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Fixture : IAsyncDisposable
    {
        public PrisstyrningDbContext Db { get; } = new(new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public HomeAssistantStateCache Cache { get; } = new();
        public HomeAssistantControlClient Client { get; private set; } = null!;
        public HomeAssistantConnectionService Connections { get; private set; } = null!;
        public string? Path { get; private set; }
        public string? Payload { get; private set; }
        public bool ReportFeedback { get; set; } = true;
        public Action? AfterPost { get; set; }
        private HttpClient _http = null!;

        public static async Task<Fixture> Create(string mode = "LwtActive", bool allowLwt = true)
        {
            var f = new Fixture();
            var connections = new HomeAssistantConnectionService(f.Db, TestSecretProtector.Instance, new Endpoint(), f.Cache, new HomeAssistantConnectionChanges());
            f.Connections = connections;
            await connections.SaveAsync("account-a", new("https://ha.example.se", "synthetic-telemetry", "synthetic-control", true, true, Actuator, 10));
            f.Db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", ControlMode = mode, ActiveDeviationLimitC = 1 });
            f.Db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "account-a", Role = ThermalEntityRoles.HeatingDeviation, EntityId = Feedback, ExpectedUnit = "°C", Enabled = true });
            await f.Db.SaveChangesAsync();
            f.Cache.Upsert("account-a", Climate());
            f.Cache.Upsert("account-a", Sensor("0"));
            f._http = new HttpClient(new Handler(async request =>
            {
                f.Path = request.RequestUri!.AbsolutePath;
                f.Payload = await request.Content!.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(f.Payload);
                var value = payload.RootElement.GetProperty("temperature").GetDouble();
                var echo = Climate();
                echo.Attributes["temperature"] = value;
                f.Cache.Upsert("account-a", echo);
                if (f.ReportFeedback) f.Cache.Upsert("account-a", Sensor(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                f.AfterPost?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            f.Client = new(new Factory(f._http), connections, f.Cache, f.Db,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Thermal:AllowLwtActive"] = allowLwt.ToString() }).Build());
            return f;
        }
        public async ValueTask DisposeAsync() { _http.Dispose(); await Db.DisposeAsync(); }
    }
    private sealed class Endpoint : IHomeAssistantEndpointValidator
    { public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(new Uri(value)); }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
