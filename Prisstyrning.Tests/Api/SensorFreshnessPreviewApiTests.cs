using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Security;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Api;

public sealed class SensorFreshnessPreviewApiTests
{
    private const string Path = "/api/home-assistant/freshness-preview";

    [Theory]
    [InlineData(false, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, HttpStatusCode.BadRequest)]
    public async Task Preview_RequiresSessionAndCsrf(bool signedIn, bool csrf, HttpStatusCode expected)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        if (signedIn) await browser.SignInAsync();
        using var response = await Send(browser, csrf);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(false, DataQuality.Stale)]
    [InlineData(true, DataQuality.Valid)]
    public async Task Preview_LivenessIsAccountScopedAndNeverWrites(bool ownLiveness, DataQuality expected)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        var now = DateTimeOffset.UtcNow;
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.HomeAssistantConnections.Add(new() { UserId = "account-a", BaseUrl = "https://ha.example.test", TelemetryEnabled = true, TelemetryTokenCiphertext = "secret-never-return", UpdatedAtUtc = now.AddHours(-1) });
            db.ThermalSiteConfigs.Add(new() { UserId = "account-a" });
            await db.SaveChangesAsync();
        });
        var cache = host.Services.GetRequiredService<IHomeAssistantStateCache>();
        var raw = new HomeAssistantState("sensor.room", "21", new() { ["unit_of_measurement"] = "°C" }, now.AddHours(-3), now.AddHours(-3), now);
        var beat = new HomeAssistantState("sensor.seen", now.ToString("O"), new() { ["private_attribute"] = "secret-never-return" }, now, now, now);
        cache.BeginSession("account-a", now.AddHours(-1));
        cache.Replace("account-a", ownLiveness ? [raw, beat] : [raw]);
        cache.MarkConnected("account-a");
        cache.Replace("account-b", [beat]);
        using var response = await Send(browser, true);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SensorFreshnessPreview>();
        Assert.Equal(expected, result!.Quality);
        Assert.DoesNotContain("secret-never-return", await response.Content.ReadAsStringAsync());
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalTelemetrySamples.ToListAsync());
            Assert.Empty(await db.ThermalModelVersions.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
            Assert.All(await db.ThermalSiteConfigs.ToListAsync(), site => { Assert.Equal("Legacy", site.ControlMode); Assert.Equal("Legacy", site.DhwWriter); });
        });
        Assert.Null(host.Services.GetService<IHomeAssistantControlClient>());
        Assert.Null(host.Services.GetService<SensorQualityTracker>());
    }

    [Fact]
    public async Task Preview_CannotAssignLivenessToHydraulicSafetySignal()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeHomeAssistantEntities: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        using var response = await Send(browser, true, "flow");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(AccountTestBrowser browser, bool csrf, string role = "room")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path + "?userId=account-b")
        {
            Content = JsonContent.Create(new { entityId = "sensor.room", role, maximumReportAgeMinutes = 10, freshnessEntityId = "sensor.seen", userId = "account-b" })
        };
        if (csrf) request.Headers.Add(AccountAntiforgery.HeaderName, browser.CsrfToken);
        return browser.Client.SendAsync(request);
    }
}
