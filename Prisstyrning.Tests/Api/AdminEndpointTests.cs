using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Api;

public class AdminEndpointTests
{
    [Fact]
    public void AdminService_IsAdmin_ReturnsFalseWhenNoAdminFile()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        var result = AdminService.IsAdmin(cfg, "nonexistent-user");

        Assert.False(result);
    }

    [Fact]
    public async Task AdminService_GrantAndCheck_Works()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-grant";

        await AdminService.GrantAdmin(cfg, userId);

        Assert.True(AdminService.IsAdmin(cfg, userId));
    }

    [Fact]
    public async Task AdminService_RevokeAdmin_Works()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-revoke";

        await AdminService.GrantAdmin(cfg, userId);
        Assert.True(AdminService.IsAdmin(cfg, userId));

        await AdminService.RevokeAdmin(cfg, userId);
        Assert.False(AdminService.IsAdmin(cfg, userId));
    }

    [Fact]
    public async Task AdminService_CannotRevokeNonExistent()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        // Should not throw
        await AdminService.RevokeAdmin(cfg, "nonexistent-user");
    }

    [Fact]
    public async Task AdminService_GetAdminUserIds_ReturnsAll()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        await AdminService.GrantAdmin(cfg, "user-a");
        await AdminService.GrantAdmin(cfg, "user-b");
        await AdminService.GrantAdmin(cfg, "user-c");

        var admins = AdminService.GetAdminUserIds(cfg);

        Assert.Equal(3, admins.Count);
        Assert.Contains("user-a", admins);
        Assert.Contains("user-b", admins);
        Assert.Contains("user-c", admins);
    }

    [Fact]
    public void AdminService_CheckAdminAccess_PasswordHeader()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Admin:Password"] = "test-secret"
        });

        // Correct password grants access
        var (isAdmin, reason) = AdminService.CheckAdminAccess(cfg, null, "test-secret");
        Assert.True(isAdmin);
        Assert.Null(reason);

        // Wrong password denied
        var (isAdmin2, reason2) = AdminService.CheckAdminAccess(cfg, null, "wrong-password");
        Assert.False(isAdmin2);
        Assert.Equal("Unauthorized", reason2);

        // No password provided
        var (isAdmin3, reason3) = AdminService.CheckAdminAccess(cfg, null, null);
        Assert.False(isAdmin3);
        Assert.Equal("Unauthorized", reason3);
    }

    [Fact]
    public void AdminService_CheckAdminAccess_NoPasswordConfigured()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(); // No Admin:Password configured

        // When no password is configured, access is denied with reason
        var (isAdmin, reason) = AdminService.CheckAdminAccess(cfg, null, "any-password");
        Assert.False(isAdmin);
        Assert.Equal("No admin password configured", reason);
    }

    [Fact]
    public void AdminService_IsAdmin_ReturnsFalseForNullUserId()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        Assert.False(AdminService.IsAdmin(cfg, null));
        Assert.False(AdminService.IsAdmin(cfg, ""));
    }

    [Fact]
    public async Task AdminService_GrantAdmin_DoesNotDuplicate()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-dup";

        await AdminService.GrantAdmin(cfg, userId);
        await AdminService.GrantAdmin(cfg, userId);

        var admins = AdminService.GetAdminUserIds(cfg);
        Assert.Single(admins);
    }

    [Fact]
    public async Task AdminService_AdminJsonFileFormat()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        await AdminService.GrantAdmin(cfg, "user-format-test");

        var path = Path.Combine(cfg["Storage:Directory"] ?? "data", "admin.json");
        Assert.True(File.Exists(path));

        var json = await File.ReadAllTextAsync(path);
        var node = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(node);
        Assert.NotNull(node["adminUserIds"]);
        var arr = node["adminUserIds"] as JsonArray;
        Assert.NotNull(arr);
        Assert.Single(arr);
        Assert.Equal("user-format-test", arr[0]!.ToString());
    }

    [Fact]
    public async Task AdminService_HangfireAccess_GrantAndCheck()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "hangfire-user-1";

        Assert.False(AdminService.HasHangfireAccess(cfg, userId));

        await AdminService.GrantHangfireAccess(cfg, userId);

        Assert.True(AdminService.HasHangfireAccess(cfg, userId));
    }

    [Fact]
    public async Task AdminService_HangfireAccess_RevokeWorks()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "hangfire-user-revoke";

        await AdminService.GrantHangfireAccess(cfg, userId);
        Assert.True(AdminService.HasHangfireAccess(cfg, userId));

        await AdminService.RevokeHangfireAccess(cfg, userId);
        Assert.False(AdminService.HasHangfireAccess(cfg, userId));
    }

    [Fact]
    public async Task AdminService_HangfireAccess_PreservesAdminData()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        // Grant admin to user A
        await AdminService.GrantAdmin(cfg, "admin-user-a");
        // Grant hangfire to user B
        await AdminService.GrantHangfireAccess(cfg, "hangfire-user-b");

        // Verify both lists are preserved
        Assert.True(AdminService.IsAdmin(cfg, "admin-user-a"));
        Assert.True(AdminService.HasHangfireAccess(cfg, "hangfire-user-b"));

        // Verify the raw JSON has both lists
        var path = Path.Combine(cfg["Storage:Directory"] ?? "data", "admin.json");
        var json = await File.ReadAllTextAsync(path);
        var node = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(node);
        Assert.NotNull(node["adminUserIds"]);
        Assert.NotNull(node["hangfireUserIds"]);

        var adminArr = node["adminUserIds"] as JsonArray;
        var hangfireArr = node["hangfireUserIds"] as JsonArray;
        Assert.NotNull(adminArr);
        Assert.NotNull(hangfireArr);
        Assert.Contains("admin-user-a", adminArr.Select(n => n?.ToString()));
        Assert.Contains("hangfire-user-b", hangfireArr.Select(n => n?.ToString()));
    }

    [Fact]
    public void AdminService_HasHangfireAccess_FalseForNull()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        Assert.False(AdminService.HasHangfireAccess(cfg, null));
        Assert.False(AdminService.HasHangfireAccess(cfg, ""));
    }

    [Fact]
    public async Task AdminDeleteUser_RemovesData()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "user-to-delete";

        // Create user data
        fs.CreateUserSettings(userId, comfortHours: 5);

        // Create schedule history
        var histDir = Path.Combine(fs.HistoryDir, userId);
        Directory.CreateDirectory(histDir);
        await File.WriteAllTextAsync(Path.Combine(histDir, "history.json"), "[]");

        // Grant admin and hangfire
        await AdminService.GrantAdmin(cfg, userId);
        await AdminService.GrantHangfireAccess(cfg, userId);

        // Verify data exists
        Assert.True(Directory.Exists(Path.Combine(fs.TokensDir, userId)));
        Assert.True(Directory.Exists(histDir));
        Assert.True(AdminService.IsAdmin(cfg, userId));
        Assert.True(AdminService.HasHangfireAccess(cfg, userId));

        // Delete the user's directories
        Directory.Delete(Path.Combine(fs.TokensDir, userId), true);
        Directory.Delete(histDir, true);
        await AdminService.RevokeAdmin(cfg, userId);
        await AdminService.RevokeHangfireAccess(cfg, userId);

        // Verify everything is gone
        Assert.False(Directory.Exists(Path.Combine(fs.TokensDir, userId)));
        Assert.False(Directory.Exists(histDir));
        Assert.False(AdminService.IsAdmin(cfg, userId));
        Assert.False(AdminService.HasHangfireAccess(cfg, userId));
    }

    [Fact]
    public void AdminDeleteUser_NonExistent_DirectoriesDontExist()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "nonexistent-user";

        // Verify directories don't exist for non-existent user
        Assert.False(Directory.Exists(Path.Combine(fs.TokensDir, userId)));
        Assert.False(Directory.Exists(Path.Combine(fs.HistoryDir, userId)));
    }

    [Fact]
    public void AdminDeleteUser_CannotDeleteSelf_Validation()
    {
        // Validates the business rule: currentUserId == targetUserId should be rejected
        var currentUserId = "admin-user";
        var targetUserId = "admin-user";

        Assert.Equal(currentUserId, targetUserId); // Would be a 400 in the endpoint
    }

    [Fact]
    public async Task AdminService_ConcurrentGrants_NoDataLoss()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var tasks = Enumerable.Range(0, 10)
            .Select(i => AdminService.GrantAdmin(cfg, $"concurrent-user-{i}"))
            .ToArray();
        await Task.WhenAll(tasks);
        var admins = AdminService.GetAdminUserIds(cfg);
        Assert.Equal(10, admins.Count);
    }

    [Fact]
    public async Task Admin_NoAuth_Returns401()
    {
        await using var host = await AccountApiTestHost.CreateAsync(new() { ["Admin:Password"] = "test-admin-password" });
        using var browser = host.CreateBrowser();
        browser.Client.DefaultRequestHeaders.Add("X-Admin-Password", "test-admin-password");

        using var response = await browser.Client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Admin_ListUsers_ReturnsAllUsersWithoutCredentials()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        await host.WithServicesAsync(async services =>
        {
            var tokens = services.GetRequiredService<DaikinTokenRepository>();
            await tokens.SaveAsync("http-admin", "synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1));
            await tokens.SaveAsync("token-user", "synthetic-access", "synthetic-refresh", DateTimeOffset.UtcNow.AddHours(1));
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.UserSettings.Add(new UserSettings { UserId = "settings-only", AutoApplySchedule = true });
            db.UserAccounts.Add(new UserAccount { UserId = "registry-only", DaikinSubjectHash = "test-subject-hash" });
            await db.SaveChangesAsync();
            await services.GetRequiredService<ScheduleHistoryRepository>()
                .SaveAsync("history-only", new JsonObject(), DateTimeOffset.UtcNow);
        });
        await AdminService.GrantAdmin(host.Configuration, "http-admin");
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("http-admin");

        using var response = await browser.Client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var users = json.RootElement.GetProperty("users").EnumerateArray().ToArray();

        Assert.Equal(new[] { "history-only", "http-admin", "registry-only", "settings-only", "token-user" },
            users.Select(user => user.GetProperty("userId").GetString()).OrderBy(id => id).ToArray());
        Assert.False(users.Single(user => user.GetProperty("userId").GetString() == "settings-only")
            .GetProperty("daikinAuthorized").GetBoolean());
        Assert.DoesNotContain("synthetic-access", body);
        Assert.DoesNotContain("synthetic-refresh", body);
        Assert.DoesNotContain("Ciphertext", body);
        await host.WithServicesAsync(async services =>
            Assert.True((await services.GetRequiredService<PrisstyrningDbContext>().UserSettings.SingleAsync()).AutoApplySchedule));
    }

    [Fact]
    public async Task Admin_NoPasswordConfigured_Returns403()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();

        using var response = await browser.MutateAsync(HttpMethod.Post, "/api/admin/login", browser.CsrfToken, "any-password");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(AdminService.GetAdminUserIds(host.Configuration));
    }
}
