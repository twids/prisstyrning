using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Api;

public sealed class HomeAssistantStatusApiTests
{
    [Fact]
    public async Task Status_RequiresVerifiedSession()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        using var response = await browser.Client.GetAsync("/api/home-assistant/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("NotConfigured", false, false)]
    [InlineData("Disabled", false, false)]
    [InlineData("Reloading", true, false)]
    [InlineData("Connecting", true, false)]
    [InlineData("Synchronizing", true, false)]
    [InlineData("Connected", true, true)]
    [InlineData("Reconnecting", true, false)]
    public async Task Status_DistinguishesConfigurationFromVerifiedSubscription(string phase, bool configured, bool connected)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        if (phase != "NotConfigured")
        {
            var revision = await SeedAsync(host, "account-a", telemetryEnabled: phase != "Disabled");
            var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
            if (phase is not ("Disabled" or "Reloading"))
            {
                var session = cache.BeginSession("account-a", revision)!;
                if (phase != "Connecting") cache.BeginSnapshot(session);
                if (phase is "Connected" or "Reconnecting") cache.PublishSnapshot(session, [State()]);
                if (phase == "Reconnecting") cache.EndSession(session);
            }
        }

        using var response = await browser.Client.GetAsync("/api/home-assistant/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var status = json.RootElement;
        Assert.Equal(phase, status.GetProperty("phase").GetString());
        Assert.Equal(configured, status.GetProperty("configured").GetBoolean());
        Assert.Equal(connected, status.GetProperty("connected").GetBoolean());
        Assert.DoesNotContain("private-ciphertext", text);
        Assert.DoesNotContain("ha.example.test", text);
        Assert.DoesNotContain("private-attribute", text);
        Assert.Null(host.Services.GetService<IHomeAssistantTelemetryClient>());
        Assert.Null(host.Services.GetService<IHomeAssistantControlClient>());
        Assert.Equal(0, host.MutationCount);
    }

    [Fact]
    public async Task Status_LateSnapshotFromOldRevision_IsNotCurrentEvenIfReceivedAfterSave()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        var currentRevision = await SeedAsync(host, "account-a");
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        var old = cache.BeginSession("account-a", currentRevision.AddMinutes(-1))!;
        cache.BeginSnapshot(old);
        cache.PublishSnapshot(old, [State()]);
        Assert.True(cache.LastSnapshotUtcFor("account-a") > currentRevision);

        var status = await browser.Client.GetFromJsonAsync<JsonObject>("/api/home-assistant/status");
        Assert.Equal("Reloading", status!["phase"]!.GetValue<string>());
        Assert.False(status["connected"]!.GetValue<bool>());
        Assert.Equal(0, status["cachedEntities"]!.GetValue<int>());
        Assert.Null(status["lastSnapshotUtc"]);
        Assert.Null(status["lastActivityUtc"]);
        var entities = await browser.Client.GetFromJsonAsync<JsonArray>("/api/home-assistant/entities");
        Assert.Equal(3, Assert.Single(entities!)!["quality"]!.GetValue<int>());
    }

    [Fact]
    public async Task Status_UsesOnlySessionAccount_AndReadsDoNotProvisionOrWrite()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("account-a");
        var revision = await SeedAsync(host, "account-b");
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        var other = cache.BeginSession("account-b", revision)!;
        cache.BeginSnapshot(other);
        cache.PublishSnapshot(other, [State()]);

        for (var index = 0; index < 3; index++)
        {
            var status = await browser.Client.GetFromJsonAsync<JsonObject>("/api/home-assistant/status?userId=account-b");
            Assert.Equal("NotConfigured", status!["phase"]!.GetValue<string>());
            Assert.False(status["connected"]!.GetValue<bool>());
            Assert.Equal(0, status["cachedEntities"]!.GetValue<int>());
        }
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.Equal("account-b", (await db.HomeAssistantConnections.SingleAsync()).UserId);
            Assert.Empty(await db.ThermalSiteConfigs.ToListAsync());
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
            Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
        });
        Assert.True(cache.IsConnected("account-b"));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("missing_token")]
    public async Task Status_NoActiveConnection_NeverShowsRetainedCacheAsConnected(string scenario)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        var revision = await SeedAsync(host, "account-a");
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        var session = cache.BeginSession("account-a", revision)!;
        cache.BeginSnapshot(session);
        cache.PublishSnapshot(session, [State()]);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var connection = await db.HomeAssistantConnections.SingleAsync();
            if (scenario == "deleted") db.Remove(connection);
            else if (scenario == "disabled") connection.TelemetryEnabled = false;
            else connection.TelemetryTokenCiphertext = string.Empty;
            await db.SaveChangesAsync();
        });
        var status = await browser.Client.GetFromJsonAsync<JsonObject>("/api/home-assistant/status");
        Assert.False(status!["configured"]!.GetValue<bool>());
        Assert.False(status["connected"]!.GetValue<bool>());
        Assert.Equal(0, status["cachedEntities"]!.GetValue<int>());
        Assert.Null(status["lastSnapshotUtc"]);
        Assert.Null(status["lastActivityUtc"]);
    }

    private static async Task<DateTimeOffset> SeedAsync(AccountApiTestHost host, string userId, bool telemetryEnabled = true)
    {
        var revision = DateTimeOffset.UtcNow.AddMinutes(-1);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.HomeAssistantConnections.Add(new HomeAssistantConnection
            {
                UserId = userId, BaseUrl = "https://ha.example.test", TelemetryEnabled = telemetryEnabled,
                TelemetryTokenCiphertext = "private-ciphertext", ControlTokenCiphertext = "private-control-ciphertext", UpdatedAtUtc = revision
            });
            await db.SaveChangesAsync();
        });
        return revision;
    }

    private static HomeAssistantState State() => new("sensor.room", "21",
        new JsonObject { ["unit_of_measurement"] = "°C", ["private-attribute"] = "not-for-status" },
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
