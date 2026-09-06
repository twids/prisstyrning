using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Security;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Api;

public sealed class HomeAssistantHistoryPreviewApiTests
{
    private const string Path = "/api/home-assistant/history-preview";
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, HttpStatusCode.BadRequest)]
    public async Task Preview_RequiresSessionAndCsrf_BeforeContactingHa(bool signedIn, bool csrf, HttpStatusCode expected)
    {
        var client = new HistoryClient();
        await using var host = await AccountApiTestHost.CreateAsync(historyClient: client);
        using var browser = host.CreateBrowser();
        if (signedIn) await browser.SignInAsync();
        using var response = await SendAsync(browser, csrf);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Preview_UsesSessionAccount_NotBodyOrQueryIdentity_AndDoesNotWrite()
    {
        var client = new HistoryClient();
        await using var host = await AccountApiTestHost.CreateAsync(historyClient: client);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host);
        using var response = await SendAsync(browser, true, query: "?userId=account-b");
        response.EnsureSuccessStatusCode();
        var coverage = await response.Content.ReadFromJsonAsync<HomeAssistantHistoryCoverage>();
        Assert.Equal(3, coverage!.ExpectedSamples);
        Assert.Equal("sensor.account_a_room", Assert.Single(coverage.Sensors).EntityId);
        Assert.Equal(("account-a", "sensor.account_a_room"), Assert.Single(client.Requests));
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
            Assert.Empty(await db.ThermalModelVersions.ToListAsync());
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
            Assert.All(await db.ThermalSiteConfigs.ToListAsync(), site =>
            {
                Assert.Equal("Legacy", site.ControlMode);
                Assert.Equal("Legacy", site.DhwWriter);
            });
        });
    }

    [Fact]
    public async Task Preview_InvalidInterval_DoesNotContactHa()
    {
        var client = new HistoryClient();
        await using var host = await AccountApiTestHost.CreateAsync(historyClient: client);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host);
        using var response = await SendAsync(browser, true, to: From.AddDays(91));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Preview_HaFailure_DoesNotExposeDiagnosticsOrWritePartialData()
    {
        var client = new HistoryClient { Fail = true };
        await using var host = await AccountApiTestHost.CreateAsync(historyClient: client);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host);
        using var response = await SendAsync(browser, true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-ha-diagnostics", body);
        Assert.Contains("error", body);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
        });
    }

    private static async Task<HttpResponseMessage> SendAsync(AccountTestBrowser browser, bool csrf, string query = "", DateTimeOffset? to = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path + query)
        {
            Content = JsonContent.Create(new { fromUtc = From, toUtc = to ?? From.AddMinutes(10), userId = "account-b" })
        };
        if (csrf) request.Headers.Add(AccountAntiforgery.HeaderName, browser.CsrfToken);
        return await browser.Client.SendAsync(request);
    }

    private static Task SeedAsync(AccountApiTestHost host) => host.WithServicesAsync(async services =>
    {
        var db = services.GetRequiredService<PrisstyrningDbContext>();
        foreach (var (account, entity) in new[] { ("account-a", "sensor.account_a_room"), ("account-b", "sensor.account_b_room") })
        {
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = account });
            db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = account, EntityId = entity, Name = "Room" });
        }
        await db.SaveChangesAsync();
    });

    private sealed class HistoryClient : IHomeAssistantTelemetryClient
    {
        public List<(string UserId, string EntityId)> Requests { get; } = [];
        public bool Fail { get; init; }
        public Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            Requests.Add((userId, entityId));
            if (Fail) throw new HttpRequestException("private-ha-diagnostics");
            return Task.FromResult<IReadOnlyList<HomeAssistantState>>([new(entityId, "21", new JsonObject { ["unit_of_measurement"] = "°C" }, From, From, DateTimeOffset.UtcNow)]);
        }
        public Task<bool> TestConnectionAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HomeAssistantState?> GetStateAsync(string userId, string entityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(ResolvedHomeAssistantConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
