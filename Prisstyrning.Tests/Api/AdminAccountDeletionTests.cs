using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Api;

public class AdminAccountDeletionTests
{
    [Theory]
    [InlineData("Legacy")]
    [InlineData("Shadow")]
    [InlineData("LwtActive")]
    [InlineData("FullActive")]
    public async Task Delete_IsBlockedInEveryMode_WithoutChangingEitherAccount(string mode)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var administrator = await SignInAdministratorAsync(host);
        using var target = host.CreateBrowser();
        await target.SignInAsync("target-account");
        using var other = host.CreateBrowser();
        await other.SignInAsync("other-account");

        await SeedAccountsAsync(host, mode);
        var before = await SnapshotAsync(host);
        Assert.All(before.Values, rows => Assert.NotEmpty(rows));

        // Repeated/cached clients and direct API calls cannot bypass the UI guard.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await administrator.MutateAsync(
                HttpMethod.Delete, "/api/admin/users/target-account", administrator.CsrfToken);
            await AssertBlockedAsync(response);
        }

        AssertSnapshotsEqual(before, await SnapshotAsync(host));
        Assert.True(AdminService.IsAdmin(host.Configuration, "target-account"));
        Assert.True(AdminService.HasHangfireAccess(host.Configuration, "target-account"));
        Assert.True(AdminService.IsAdmin(host.Configuration, "other-account"));
        Assert.True(AdminService.HasHangfireAccess(host.Configuration, "other-account"));
        // A refused deletion is not a logout or an account disable operation.
        Assert.True((await target.ReadSessionAsync()).Authenticated);
        Assert.True((await other.ReadSessionAsync()).Authenticated);
    }

    [Fact]
    public async Task Delete_MissingAccount_DoesNotClaimSuccessOrCreateData()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var administrator = await SignInAdministratorAsync(host);
        var before = await SnapshotAsync(host);

        using var response = await administrator.MutateAsync(
            HttpMethod.Delete, "/api/admin/users/missing-account", administrator.CsrfToken);

        await AssertBlockedAsync(response);
        AssertSnapshotsEqual(before, await SnapshotAsync(host));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("history")]
    [InlineData("token")]
    public async Task Delete_LegacyOnlyRecord_IsPreservedWithoutCreatingAnAccount(string kind)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var administrator = await SignInAdministratorAsync(host);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            if (kind == "settings") db.UserSettings.Add(new UserSettings { UserId = "legacy-only", AutoApplySchedule = true });
            if (kind == "history") db.ScheduleHistory.Add(new ScheduleHistoryEntry { UserId = "legacy-only", Timestamp = DateTimeOffset.UtcNow });
            if (kind == "token") await services.GetRequiredService<DaikinTokenRepository>()
                .SaveAsync("legacy-only", "synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1));
            await db.SaveChangesAsync();
        });
        var before = await SnapshotAsync(host);

        using var response = await administrator.MutateAsync(
            HttpMethod.Delete, "/api/admin/users/legacy-only", administrator.CsrfToken);

        await AssertBlockedAsync(response);
        AssertSnapshotsEqual(before, await SnapshotAsync(host));
    }

    [Theory]
    [InlineData(false, false, "none", HttpStatusCode.Unauthorized)]
    [InlineData(true, false, "valid", HttpStatusCode.Unauthorized)]
    [InlineData(true, true, "none", HttpStatusCode.BadRequest)]
    [InlineData(true, true, "invalid", HttpStatusCode.BadRequest)]
    [InlineData(true, false, "none", HttpStatusCode.BadRequest)]
    public async Task Delete_StillRequiresSessionAdminAndCsrf(
        bool signedIn, bool admin, string csrf, HttpStatusCode expected)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        if (admin) await AdminService.GrantAdmin(host.Configuration, "caller");
        if (signedIn) await browser.SignInAsync("caller");
        await SeedAccountsAsync(host, "Legacy");
        var before = await SnapshotAsync(host);

        using var response = await browser.MutateAsync(HttpMethod.Delete, "/api/admin/users/target-account",
            csrf == "valid" ? browser.CsrfToken : csrf == "invalid" ? "not-a-csrf-token" : null);

        Assert.Equal(expected, response.StatusCode);
        AssertSnapshotsEqual(before, await SnapshotAsync(host));
        Assert.True(AdminService.IsAdmin(host.Configuration, "target-account"));
        Assert.True(AdminService.HasHangfireAccess(host.Configuration, "target-account"));
    }

    [Fact]
    public async Task Delete_AdminPasswordCannotBypassTheDeletionGuard()
    {
        await using var host = await AccountApiTestHost.CreateAsync(new() { ["Admin:Password"] = "synthetic-admin-password" });
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("caller");
        await SeedAccountsAsync(host, "Legacy");
        var before = await SnapshotAsync(host);

        using var response = await browser.MutateAsync(HttpMethod.Delete, "/api/admin/users/target-account",
            browser.CsrfToken, "synthetic-admin-password");

        await AssertBlockedAsync(response);
        AssertSnapshotsEqual(before, await SnapshotAsync(host));
        Assert.True(AdminService.IsAdmin(host.Configuration, "target-account"));
    }

    [Theory]
    [InlineData("http-admin")]
    [InlineData(" ")]
    [InlineData("invalid.id")]
    [InlineData("invalid:id")]
    public async Task Delete_SelfAndInvalidTargetsStillReturnBadRequest(string target)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var administrator = await SignInAdministratorAsync(host);
        var before = await SnapshotAsync(host);

        using var response = await administrator.MutateAsync(HttpMethod.Delete,
            $"/api/admin/users/{Uri.EscapeDataString(target)}", administrator.CsrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertSnapshotsEqual(before, await SnapshotAsync(host));
        Assert.True(AdminService.IsAdmin(host.Configuration, "http-admin"));
    }

    [Theory]
    [InlineData(100, HttpStatusCode.Conflict)]
    [InlineData(101, HttpStatusCode.BadRequest)]
    public async Task Delete_RetainsUserIdLengthBoundary(int length, HttpStatusCode expected)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var administrator = await SignInAdministratorAsync(host);

        using var response = await administrator.MutateAsync(HttpMethod.Delete,
            $"/api/admin/users/{new string('a', length)}", administrator.CsrfToken);

        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task AssertBlockedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Equal("account_deletion_unavailable", body.RootElement.GetProperty("code").GetString());
        Assert.Contains("Kontot har inte raderats eller ändrats", body.RootElement.GetProperty("error").GetString());
        Assert.False(body.RootElement.TryGetProperty("warnings", out _));
    }

    private static async Task<AccountTestBrowser> SignInAdministratorAsync(AccountApiTestHost host)
    {
        await AdminService.GrantAdmin(host.Configuration, "http-admin");
        var browser = host.CreateBrowser();
        await browser.SignInAsync("http-admin");
        return browser;
    }

    private static async Task SeedAccountsAsync(AccountApiTestHost host, string mode)
    {
        foreach (var userId in new[] { "target-account", "other-account" })
        {
            await AdminService.GrantAdmin(host.Configuration, userId);
            await AdminService.GrantHangfireAccess(host.Configuration, userId);
            await host.WithServicesAsync(async services =>
            {
                await services.GetRequiredService<DaikinTokenRepository>()
                    .SaveAsync(userId, "synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1));
                var db = services.GetRequiredService<PrisstyrningDbContext>();
                var now = DateTimeOffset.UtcNow;
                db.UserSettings.Add(new UserSettings { UserId = userId, AutoApplySchedule = true, ComfortHours = 5 });
                db.AdminRoles.Add(new AdminRole { UserId = userId, IsAdmin = true, HasHangfireAccess = true });
                db.ScheduleHistory.Add(new ScheduleHistoryEntry { UserId = userId, Timestamp = now, SchedulePayloadJson = "{\"test\":true}" });
                db.FlexibleScheduleStates.Add(new FlexibleScheduleState { UserId = userId, NextScheduledEcoUtc = now.AddHours(1) });
                db.DaikinInstallations.Add(new DaikinInstallation { UserId = userId, SiteId = userId, DeviceId = "test-device", DhwManagementPointEmbeddedId = "test-dhw" });
                db.HomeAssistantConnections.Add(new HomeAssistantConnection
                {
                    UserId = userId, BaseUrl = "https://ha.example.test", TelemetryTokenCiphertext = "synthetic-telemetry-ciphertext",
                    ControlTokenCiphertext = "synthetic-control-ciphertext", TelemetryEnabled = true, ControlEnabled = true,
                    HeatingDeviationEntityId = "number.test_deviation"
                });
                db.ThermalSiteConfigs.Add(new ThermalSiteConfig
                {
                    UserId = userId, ControlMode = mode, DhwWriter = mode == "FullActive" ? "Joint" : "Legacy",
                    DhwLeaseOwner = "synthetic-dhw-writer", DhwLeaseExpiresUtc = now.AddMinutes(5)
                });
                db.ThermalControlStates.Add(new ThermalControlState { UserId = userId, LeaseOwner = "synthetic-writer", LeaseExpiresUtc = now.AddMinutes(5), CurrentDeviationC = 0.5 });
                db.ThermalOptimizationJobs.Add(new ThermalOptimizationJob { Id = Guid.NewGuid(), UserId = userId, LeaseOwner = "synthetic-optimizer", Status = "Running" });
                db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = userId, EntityId = "sensor.room", Name = "Test room", IsCritical = true });
                db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = userId, Role = "lwt", EntityId = "sensor.test_lwt", ExpectedUnit = "°C" });
                db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample { UserId = userId, TimestampUtc = now, LeavingWaterTemperatureC = 32 });
                var plan = new ThermalPlan { UserId = userId, CreatedAtUtc = now };
                plan.Steps.Add(new ThermalPlanStep { StartUtc = now, EndUtc = now.AddMinutes(15), DhwReserved = true });
                db.ThermalPlans.Add(plan);
                db.ThermalModelVersions.Add(new ThermalModelVersion { UserId = userId, ModelType = "2R2C", CreatedAtUtc = now });
                db.ThermalEvents.Add(new ThermalEvent { UserId = userId, TimestampUtc = now, Message = "Synthetic retained audit event" });
                db.DhwCycles.Add(new DhwCycle { UserId = userId, TargetTemperatureC = 60, TargetVerificationCount = 2, TargetReachedUtc = now });
                db.ThermalControlCommands.Add(new ThermalControlCommand { UserId = userId, TimestampUtc = now, CommandType = "test", Target = "test", Outcome = "Accepted" });
                db.ThermalHourlyAggregates.Add(new ThermalHourlyAggregate { UserId = userId, HourUtc = now, HeatPumpEnergyKwh = 2 });
                if (!await db.PriceSnapshots.AnyAsync()) db.PriceSnapshots.Add(new PriceSnapshot { Zone = "SE3", Date = DateOnly.FromDateTime(now.UtcDateTime) });
                await db.SaveChangesAsync();
            });
        }
    }

    private static async Task<Dictionary<string, string[]>> SnapshotAsync(AccountApiTestHost host)
    {
        var snapshot = new Dictionary<string, string[]>();
        await host.WithServicesAsync(services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            foreach (var property in typeof(PrisstyrningDbContext).GetProperties()
                .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)))
            {
                var rows = new List<string>();
                foreach (var entity in (IQueryable)property.GetValue(db)!)
                {
                    // Include ALL mapped scalar fields, even JsonIgnore credentials,
                    // ownership and leases, without navigation cycles. All data here
                    // is synthetic and the host can never contact live integrations.
                    rows.Add(JsonSerializer.Serialize(db.Entry(entity).Properties
                        .OrderBy(field => field.Metadata.Name)
                        .ToDictionary(field => field.Metadata.Name, field => field.CurrentValue)));
                }
                snapshot.Add(property.Name, rows.OrderBy(row => row, StringComparer.Ordinal).ToArray());
            }
            return Task.CompletedTask;
        });
        return snapshot;
    }

    private static void AssertSnapshotsEqual(Dictionary<string, string[]> expected, Dictionary<string, string[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach (var (table, rows) in expected) Assert.Equal(rows, actual[table]);
    }
}
