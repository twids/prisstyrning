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
            db.ThermalRoomConfigs.Add(new ThermalRoomConfig
            { UserId = "account-a", EntityId = "sensor.room", IsCritical = true });
            db.ThermalEntityConfigs.AddRange(ThermalModelTrainingDataTests.Entities.Select(entity => new ThermalEntityConfig
            {
                UserId = "account-a",
                Role = entity.Role,
                EntityId = entity.EntityId
            }));
            var model = Assert.Single(await ThermalCurrentModelTestData.SeedAsync(
                db, "account-a", DateTimeOffset.UtcNow, "2R2C"));
            if (!proven) model.MetricsJson = "{\"twoHourMaeC\":0.1,\"dayMaeC\":0.2}";
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
        var sourceValidation = modelJson.GetProperty("sourceValidation");
        Assert.True(sourceValidation.GetProperty("passed").GetBoolean());
        Assert.Equal("Current", sourceValidation.GetProperty("status").GetString());
        var provenance = modelJson.GetProperty("provenance");
        Assert.True(provenance.GetProperty("verifiable").GetBoolean());
        Assert.Equal("grey-box-2r2c-v1", provenance.GetProperty("algorithmVersion").GetString());
        Assert.Equal(489, provenance.GetProperty("observationCount").GetInt32());
        Assert.False(modelJson.TryGetProperty("sourceEvidenceJson", out _));
        Assert.DoesNotContain("sampleFingerprint", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configurationFingerprint", content, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task Models_RehashesHistoryAndReportsChangedWithoutExposingFingerprintsOrMutatingLegacy()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a" });
            db.ThermalRoomConfigs.Add(new ThermalRoomConfig
            { UserId = "account-a", EntityId = "sensor.room", IsCritical = true });
            db.ThermalEntityConfigs.AddRange(ThermalModelTrainingDataTests.Entities.Select(entity => new ThermalEntityConfig
            {
                UserId = "account-a",
                Role = entity.Role,
                EntityId = entity.EntityId
            }));
            await ThermalCurrentModelTestData.SeedAsync(db, "account-a", DateTimeOffset.UtcNow, "2R2C");
            (await db.ThermalTelemetrySamples.OrderBy(x => x.TimestampUtc).FirstAsync()).RoomTemperaturesJson =
                "{\"sensor.room\":21.6}";
            await db.SaveChangesAsync();
        });

        using var response = await browser.Client.GetAsync("/api/thermal/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var model = Assert.Single(document.RootElement.EnumerateArray());
        var source = model.GetProperty("sourceValidation");
        Assert.False(source.GetProperty("passed").GetBoolean());
        Assert.Equal("Changed", source.GetProperty("status").GetString());
        Assert.Equal("SourceChanged", model.GetProperty("validation").GetProperty("status").GetString());
        Assert.DoesNotContain("fingerprint", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account-a", content);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var site = await db.ThermalSiteConfigs.SingleAsync();
            Assert.Equal("Legacy", site.ControlMode);
            Assert.Equal("Legacy", site.DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
        });
    }
}
