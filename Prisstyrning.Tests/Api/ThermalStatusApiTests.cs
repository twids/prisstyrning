using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Api;

public sealed class ThermalStatusApiTests
{
    [Fact]
    public async Task FreshSnapshot_WithExcludedCriticalRoom_IsNotValidAndDoesNotChangeLegacy()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) });
            db.ThermalRoomConfigs.Add(new ThermalRoomConfig
            {
                UserId = "account-a", Name = "Vardagsrum", EntityId = "sensor.room", IsCritical = true
            });
            db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
            {
                UserId = "account-a", TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                RoomTemperaturesJson = "{\"sensor.room\":21.4}",
                QualityJson = "{\"rooms\":{\"sensor.room\":{\"Quality\":2,\"Excluded\":true}}}"
            });
            await db.SaveChangesAsync();
        });

        using var response = await browser.Client.GetAsync("/api/thermal/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = json.RootElement;
        // Preserve the existing numeric wire contract; the browser must translate it.
        Assert.Equal((int)ControlMode.Legacy, status.GetProperty("mode").GetInt32());
        Assert.Equal((int)DhwWriter.Legacy, status.GetProperty("dhwWriter").GetInt32());
        Assert.Equal((int)DataQuality.Invalid, status.GetProperty("overallDataQuality").GetInt32());
        Assert.Contains("1 ogiltiga eller exkluderade", status.GetProperty("dataQualityReason").GetString());
        Assert.Equal(0, status.GetProperty("currentLwtDeviationC").GetDouble());

        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var site = await db.ThermalSiteConfigs.SingleAsync();
            Assert.Equal("Legacy", site.ControlMode);
            Assert.Equal("Legacy", site.DhwWriter);
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Empty(await db.ThermalControlStates.ToListAsync());
            Assert.Empty(await db.ThermalEvents.ToListAsync());
        });
    }

    [Fact]
    public async Task Status_RequiresSessionAndDoesNotRegisterIntegrationClients()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        using var response = await browser.Client.GetAsync("/api/thermal/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Null(host.Services.GetService<IHomeAssistantControlClient>());
        Assert.Null(host.Services.GetService<IHomeAssistantTelemetryClient>());
        Assert.Null(host.Services.GetService<IEmhassClient>());
        Assert.Null(host.Services.GetService<BatchRunner>());
    }

    [Fact]
    public async Task EmptyAccount_HasUnavailableStatusWithoutCreatingAnInstallation()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        var status = await browser.Client.GetFromJsonAsync<ThermalStatusDto>("/api/thermal/status");
        Assert.NotNull(status);
        Assert.Equal(ControlMode.Legacy, status!.Mode);
        Assert.Equal(DhwWriter.Legacy, status.DhwWriter);
        Assert.Equal(DataQuality.Unavailable, status.OverallDataQuality);
        Assert.Null(status.LastTelemetryUtc);
        Assert.Null(status.PlanCreatedUtc);
        Assert.Null(status.PlanAgeMinutes);
        Assert.False(status.EmhassAvailable);
        Assert.False(status.ManualOverride);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.Empty(await db.ThermalSiteConfigs.ToListAsync());
            Assert.Empty(await db.ThermalControlStates.ToListAsync());
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
        });
    }

    [Fact]
    public async Task ActiveStatus_DoesNotPresentLatestShadowPlanAsNextControlInput()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ThermalSiteConfigs.Add(new() { UserId = "account-a", ControlMode = "LwtActive", UpdatedAtUtc = now });
            db.ThermalPlans.Add(new()
            {
                UserId = "account-a", CreatedAtUtc = now.AddMinutes(-1), ValidFromUtc = now.AddMinutes(-5),
                ValidUntilUtc = now.AddHours(1), Status = "Valid", IsShadow = true, SolverDurationMs = 100,
                ObjectiveCost = 1, Confidence = .8, Summary = "Syntetisk Shadow-plan"
            });
            await db.SaveChangesAsync();
        });

        var status = await browser.Client.GetFromJsonAsync<ThermalStatusDto>("/api/thermal/status");

        Assert.Equal(ControlMode.LwtActive, status!.Mode);
        Assert.Null(status.PlanCreatedUtc);
        Assert.Null(status.PlanAgeMinutes);
        Assert.Null(status.NextControlEventUtc);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact]
    public async Task Status_UsesOnlySignedInAccountsSamplesAndEnabledMappings()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a");
        await SeedAsync(host, "account-b", sample =>
        {
            sample.TimestampUtc = DateTimeOffset.UtcNow;
            sample.QualityJson = "{\"rooms\":{\"sensor.room\":{\"Quality\":2,\"Excluded\":true}}}";
        });
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalEntityConfigs.AddRange(
                new() { UserId = "account-b", Role = ThermalEntityRoles.Flow, EntityId = "sensor.other_flow" },
                new() { UserId = "account-a", Role = ThermalEntityRoles.Flow, EntityId = "sensor.disabled_flow", Enabled = false });
            await db.SaveChangesAsync();
        });

        var own = await browser.Client.GetFromJsonAsync<ThermalStatusDto>("/api/thermal/status?userId=account-b");
        Assert.Equal(DataQuality.Valid, own!.OverallDataQuality);
        Assert.Equal("Alla 1 aktiverade datakällor är giltiga i senaste insamlingen.", own.DataQualityReason);

        using var otherBrowser = host.CreateBrowser();
        await otherBrowser.SignInAsync("account-b");
        var other = await otherBrowser.Client.GetFromJsonAsync<ThermalStatusDto>("/api/thermal/status");
        Assert.Equal(DataQuality.Invalid, other!.OverallDataQuality);
        Assert.Contains("0/2", other.DataQualityReason);
        Assert.NotEqual(own.LastTelemetryUtc, other.LastTelemetryUtc);
    }

    [Theory]
    [InlineData("stale", DataQuality.Stale)]
    [InlineData("future", DataQuality.Invalid)]
    [InlineData("import", DataQuality.Unavailable)]
    [InlineData("malformed", DataQuality.Unavailable)]
    [InlineData("missing-value", DataQuality.Invalid)]
    [InlineData("changed-config", DataQuality.Unavailable)]
    public async Task Status_RejectsUnverifiableSnapshots(string scenario, DataQuality expected)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a", sample =>
        {
            switch (scenario)
            {
                case "stale": sample.TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-11); break;
                case "future": sample.TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(10); break;
                case "import": sample.QualityJson = "{\"source\":\"HomeAssistantHistoryImport\"," + sample.QualityJson[1..]; break;
                case "malformed": sample.QualityJson = "{bad"; break;
                case "missing-value": sample.RoomTemperaturesJson = "{}"; break;
            }
        });
        if (scenario == "changed-config")
            await host.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<PrisstyrningDbContext>();
                (await db.ThermalSiteConfigs.SingleAsync()).UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            });

        var status = await browser.Client.GetFromJsonAsync<ThermalStatusDto>("/api/thermal/status");
        Assert.Equal(expected, status!.OverallDataQuality);
        Assert.False(string.IsNullOrWhiteSpace(status.DataQualityReason));
        Assert.Equal(ControlMode.Legacy, status.Mode);
        Assert.Equal(DhwWriter.Legacy, status.DhwWriter);
    }

    [Fact]
    public async Task Recovery_IsValidOnlyWhenLatestSnapshotAlsoClearsExclusion()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStatus: true);
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await SeedAsync(host, "account-a");
        foreach (var (quality, excluded, expected) in new[]
                 { (2, true, DataQuality.Invalid), (0, true, DataQuality.Invalid), (0, false, DataQuality.Valid) })
        {
            await host.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<PrisstyrningDbContext>();
                var previous = await db.ThermalTelemetrySamples.Where(x => x.UserId == "account-a").MaxAsync(x => x.TimestampUtc);
                db.ThermalTelemetrySamples.Add(new()
                {
                    UserId = "account-a", TimestampUtc = previous.AddSeconds(1),
                    RoomTemperaturesJson = "{\"sensor.room\":20.4}",
                    QualityJson = JsonSerializer.Serialize(new
                    {
                        rooms = new Dictionary<string, object> { ["sensor.room"] = new { Quality = quality, Excluded = excluded } }
                    })
                });
                await db.SaveChangesAsync();
            });
            var status = await browser.Client.GetFromJsonAsync<ThermalStatusDto>("/api/thermal/status");
            Assert.Equal(expected, status!.OverallDataQuality);
        }
        Assert.Equal(0, host.MutationCount);
    }

    private static Task SeedAsync(AccountApiTestHost host, string userId, Action<ThermalTelemetrySample>? configure = null) =>
        host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalSiteConfigs.Add(new() { UserId = userId, UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1) });
            db.ThermalRoomConfigs.Add(new() { UserId = userId, EntityId = "sensor.room", Name = "Rum", IsCritical = true });
            var sample = new ThermalTelemetrySample
            {
                UserId = userId, TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                QualityJson = "{\"rooms\":{\"sensor.room\":{\"Quality\":0,\"Excluded\":false}}}",
                RoomTemperaturesJson = "{\"sensor.room\":21.4}"
            };
            configure?.Invoke(sample);
            db.ThermalTelemetrySamples.Add(sample);
            await db.SaveChangesAsync();
        });
}
