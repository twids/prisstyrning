using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Api;

public sealed class HomeAssistantEntityCatalogApiTests
{
    [Fact]
    public async Task Catalog_RequiresAccountSession_AndRegistersNoIntegrationClients()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        using var response = await browser.Client.GetAsync("/api/home-assistant/entities");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Null(host.Services.GetService<IHomeAssistantTelemetryClient>());
        Assert.Null(host.Services.GetService<IHomeAssistantControlClient>());
        Assert.Null(host.Services.GetService<IEmhassClient>());
        Assert.Null(host.Services.GetService<SensorQualityTracker>());
        Assert.Null(host.Services.GetService<BatchRunner>());
    }

    [Fact]
    public async Task Catalog_UsesOnlySessionAccount_AndDoesNotReturnCredentialsOrArbitraryAttributes()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("account-a");
        await SeedAsync(host, "account-a", "unknown");
        await SeedAsync(host, "account-b", "99");
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        cache.Upsert("account-b", State("other-account-sensor", "99"));

        using var response = await browser.Client.GetAsync("/api/home-assistant/entities?userId=account-b");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var entity = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("sensor.room", entity.GetProperty("entityId").GetString());
        Assert.Equal("unknown", entity.GetProperty("state").GetString());
        Assert.Equal((int)DataQuality.Unavailable, entity.GetProperty("quality").GetInt32());
        Assert.Empty(entity.GetProperty("compatibleUnits").EnumerateArray());
        Assert.NotNull(entity.GetProperty("checkedAtUtc").GetString());
        Assert.DoesNotContain("other-account-sensor", text);
        Assert.DoesNotContain("ciphertext-must-not-be-returned", text);
        Assert.DoesNotContain("private-attribute", text);
        Assert.DoesNotContain("ha.example.test", text);
        Assert.DoesNotContain("account-b", text);

        for (var index = 0; index < 3; index++)
        {
            using var repeated = await browser.Client.GetAsync("/api/home-assistant/entities");
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        }
        await AssertUnchangedLegacyAsync(host, 2);
    }

    [Theory]
    [InlineData(3, DataQuality.Stale)]
    [InlineData(12, DataQuality.Valid)]
    public async Task Catalog_UsesAccountAgeThreshold_AndPreservesNumericEnumContract(int minutes, DataQuality expected)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a", "21.5", minutes);
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        cache.Upsert("account-a", State("sensor.room", "21.5") with { LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-4) });

        var entities = await browser.Client.GetFromJsonAsync<ThermalEntityStateDto[]>("/api/home-assistant/entities");
        var entity = Assert.Single(entities!);
        Assert.Equal(expected, entity.Quality);
        if (expected == DataQuality.Stale) Assert.Contains("3 minuter", entity.QualityReason);
        else
        {
            Assert.Equal(["°C"], entity.CompatibleUnits);
            Assert.True(entity.ValidUntilUtc > entity.CheckedAtUtc);
        }
        await AssertUnchangedLegacyAsync(host, 1);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("missing_token")]
    public async Task Catalog_NoActiveConnection_DoesNotExposeRetainedCache(string scenario)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a", "21.5");
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var connection = await db.HomeAssistantConnections.SingleAsync();
            if (scenario == "disabled") connection.TelemetryEnabled = false;
            else if (scenario == "deleted") db.Remove(connection);
            else connection.TelemetryTokenCiphertext = string.Empty;
            await db.SaveChangesAsync();
        });

        var entities = await browser.Client.GetFromJsonAsync<ThermalEntityStateDto[]>("/api/home-assistant/entities");
        Assert.Empty(entities!);
        Assert.Single(host.Services.GetRequiredService<IHomeAssistantStateCache>().Snapshot("account-a"));
        await AssertUnchangedLegacyAsync(host, 1);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("settings_changed")]
    [InlineData("no_snapshot")]
    public async Task Catalog_UnverifiedLiveConnection_ReturnsUnavailableUntilFreshSnapshot(string scenario)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a", "21.5", takeSnapshot: scenario != "no_snapshot");
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        if (scenario == "disconnected") cache.MarkDisconnected("account-a");
        if (scenario == "settings_changed") await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            (await db.HomeAssistantConnections.SingleAsync()).UpdatedAtUtc = cache.LastSnapshotUtcFor("account-a")!.Value.AddTicks(1);
            await db.SaveChangesAsync();
        });

        var before = Assert.Single((await browser.Client.GetFromJsonAsync<ThermalEntityStateDto[]>("/api/home-assistant/entities"))!);
        Assert.Equal(DataQuality.Unavailable, before.Quality);
        Assert.NotNull(before.QualityReason);
        Assert.Empty(before.CompatibleUnits!);

        await host.WithServicesAsync(async services =>
        {
            var revision = (await services.GetRequiredService<PrisstyrningDbContext>().HomeAssistantConnections.SingleAsync()).UpdatedAtUtc;
            var session = cache.BeginSession("account-a", revision)!;
            cache.BeginSnapshot(session);
            cache.PublishSnapshot(session, [State("sensor.room", "21.5")]);
        });
        var after = Assert.Single((await browser.Client.GetFromJsonAsync<ThermalEntityStateDto[]>("/api/home-assistant/entities"))!);
        Assert.Equal(DataQuality.Valid, after.Quality);
        Assert.Equal(["°C"], after.CompatibleUnits);
        await AssertUnchangedLegacyAsync(host, 1);
    }

    [Fact]
    public async Task Catalog_MalformedAttributes_DoNotBreakEntireHttpResponse()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a", "21.5");
        var malformed = State("sensor.bad_unit", "21.5");
        malformed.Attributes["unit_of_measurement"] = new JsonObject { ["private-attribute"] = 42 };
        malformed.Attributes["friendly_name"] = 42;
        host.Services.GetRequiredService<IHomeAssistantStateCache>().Upsert("account-a", malformed);

        var entities = await browser.Client.GetFromJsonAsync<ThermalEntityStateDto[]>("/api/home-assistant/entities");
        Assert.Equal(2, entities!.Length);
        var bad = entities.Single(entity => entity.EntityId == "sensor.bad_unit");
        Assert.Equal(DataQuality.Invalid, bad.Quality);
        Assert.Equal("sensor.bad_unit", bad.FriendlyName);
        Assert.Null(bad.Unit);
        Assert.Equal(DataQuality.Valid, entities.Single(entity => entity.EntityId == "sensor.room").Quality);
    }

    [Fact]
    public async Task Catalog_EmptyAccount_DoesNotProvisionInstallationOrUseDefaultAccountCache()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        host.Services.GetRequiredService<IHomeAssistantStateCache>().Replace("default", [State("sensor.room", "21.5")]);
        Assert.Empty((await browser.Client.GetFromJsonAsync<ThermalEntityStateDto[]>("/api/home-assistant/entities"))!);
        await AssertUnchangedLegacyAsync(host, 0);
        await host.WithServicesAsync(async services =>
            Assert.Empty(await services.GetRequiredService<PrisstyrningDbContext>().HomeAssistantConnections.ToListAsync()));
    }

    private static async Task SeedAsync(AccountApiTestHost host, string userId, string state, int staleAfterMinutes = 10, bool takeSnapshot = true)
    {
        var revision = DateTimeOffset.UtcNow.AddHours(-1);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.HomeAssistantConnections.Add(new HomeAssistantConnection
            {
                UserId = userId, BaseUrl = "https://ha.example.test", TelemetryEnabled = true,
                TelemetryTokenCiphertext = "ciphertext-must-not-be-returned",
                ControlTokenCiphertext = "control-ciphertext-must-not-be-returned", ControlEnabled = false,
                StaleAfterMinutes = staleAfterMinutes, UpdatedAtUtc = revision
            });
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = userId, ControlMode = "Legacy", DhwWriter = "Legacy" });
            await db.SaveChangesAsync();
        });
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        cache.BeginSession(userId, revision);
        if (takeSnapshot) cache.Replace(userId, [State("sensor.room", state)]);
        else cache.Upsert(userId, State("sensor.room", state));
        cache.MarkConnected(userId);
    }

    private static HomeAssistantState State(string entityId, string state) => new(
        entityId, state, new JsonObject { ["friendly_name"] = "Vardagsrum", ["unit_of_measurement"] = "°C", ["private-attribute"] = "not-for-catalog" },
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

    private static async Task AssertUnchangedLegacyAsync(AccountApiTestHost host, int installations)
    {
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var sites = await db.ThermalSiteConfigs.ToListAsync();
            Assert.Equal(installations, sites.Count);
            Assert.All(sites, site => { Assert.Equal("Legacy", site.ControlMode); Assert.Equal("Legacy", site.DhwWriter); });
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalControlStates.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
            Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
        });
        Assert.Equal(0, host.MutationCount);
    }
}
