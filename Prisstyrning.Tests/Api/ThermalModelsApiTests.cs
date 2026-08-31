using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Tests.Thermal;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Api;

public sealed class ThermalModelsApiTests
{
    [Fact]
    public async Task Models_RequiresSameSessionAndRegistersNoControlClient()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        using var response = await browser.Client.GetAsync("/api/thermal/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Null(host.Services.GetService<IHomeAssistantControlClient>());
        Assert.Null(host.Services.GetService<IHomeAssistantTelemetryClient>());
        Assert.Equal(0, host.MutationCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Models_PreservesFieldsAddsEvidenceAndNeverExposesOtherAccounts(bool proven)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalSiteConfigs.AddRange(new ThermalSiteConfig { UserId = "account-a" }, new ThermalSiteConfig { UserId = "account-b" });
            var model = ThermalModelEvidenceTests.ValidModel("2R2C", DateTimeOffset.UtcNow);
            if (!proven) model.MetricsJson = "{\"twoHourMaeC\":0.1,\"dayMaeC\":0.2}";
            db.ThermalModelVersions.Add(model);
            var other = ThermalModelEvidenceTests.ValidModel("COP", DateTimeOffset.UtcNow);
            other.UserId = "account-b";
            db.ThermalModelVersions.Add(other);
            await db.SaveChangesAsync();
        });

        using var response = await browser.Client.GetAsync("/api/thermal/models?userId=account-b");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var modelJson = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("2R2C", modelJson.GetProperty("modelType").GetString());
        Assert.True(modelJson.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.String, modelJson.GetProperty("parametersJson").ValueKind);
        Assert.Equal(JsonValueKind.String, modelJson.GetProperty("metricsJson").ValueKind);
        var evidence = modelJson.GetProperty("validation");
        Assert.Equal(proven, evidence.GetProperty("passed").GetBoolean());
        Assert.Equal(proven ? "Validated" : "Unproven", evidence.GetProperty("status").GetString());
        Assert.DoesNotContain("account-a", content);
        Assert.DoesNotContain("account-b", content);
        Assert.False(modelJson.TryGetProperty("userId", out _));
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.All(await db.ThermalModelVersions.ToListAsync(), version => Assert.True(version.IsActive));
            Assert.All(await db.ThermalSiteConfigs.ToListAsync(), site => { Assert.Equal("Legacy", site.ControlMode); Assert.Equal("Legacy", site.DhwWriter); });
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
        });
    }
}
