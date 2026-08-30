using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using System.Text.Json;

namespace Prisstyrning.Security;

/// <summary>Existing admin contracts, shared with the isolated HTTP test host.</summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var adminGroup = endpoints.MapGroup("/api/admin").WithTags("Admin");

        adminGroup.MapGet("/status", (IConfiguration cfg, HttpContext c) =>
        {
            var userId = AccountAuthentication.UserId(c.User);
            var isAdmin = IsAdminRequest(c, cfg);
            return Results.Json(new { isAdmin, userId });
        });

        adminGroup.MapPost("/login", async (IConfiguration cfg, HttpContext c) =>
        {
            var userId = AccountAuthentication.UserId(c.User);
            var configuredPassword = cfg["Admin:Password"];
            if (string.IsNullOrEmpty(configuredPassword))
                return Results.Json(new { error = "No admin password configured" }, statusCode: 403);

            var password = c.Request.Headers["X-Admin-Password"].FirstOrDefault();
            if (string.IsNullOrEmpty(password) || !AdminService.SecureCompare(password, configuredPassword))
                return Results.Json(new { error = "Invalid admin password" }, statusCode: 401);

            if (!string.IsNullOrEmpty(userId))
                await AdminService.GrantAdmin(cfg, userId);

            return Results.Json(new { granted = true, userId });
        }).RequireRateLimiting("admin-login");

        adminGroup.MapGet("/users", async (IConfiguration cfg, HttpContext c, PrisstyrningDbContext db, UserSettingsRepository settingsRepo, DaikinTokenRepository tokenRepo, ScheduleHistoryRepository historyRepo) =>
        {
            if (!IsAdminRequest(c, cfg))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            var currentUserId = AccountAuthentication.UserId(c.User);

            // Include existing records even when no Daikin credential remains.
            // This is discovery only: do not create, merge or disable accounts.
            var userIds = await db.DaikinTokens.Select(account => account.UserId)
                .Union(db.UserSettings.Select(account => account.UserId))
                .Union(db.ScheduleHistory.Select(entry => entry.UserId))
                .Union(db.UserAccounts.Select(account => account.UserId))
                .ToListAsync(c.RequestAborted);

            var adminUserIds = AdminService.GetAdminUserIds(cfg);
            var hangfireUserIds = AdminService.GetHangfireUserIds(cfg);
            var users = new List<object>();

            foreach (var uid in userIds)
            {
                var settings = settingsRepo.LoadScheduleSettings(uid);
                var zone = settingsRepo.GetUserZone(uid);
                var token = await tokenRepo.LoadAsync(uid);
                var daikinAuthorized = token != null;
                string? daikinExpiresAtUtc = token?.ExpiresAtUtc.ToString("o");
                string? daikinSubject = token?.DaikinSubject;

                var historyCount = await historyRepo.CountAsync(uid);
                var hasScheduleHistory = historyCount > 0;
                int? scheduleCount = hasScheduleHistory ? historyCount : null;
                string? lastScheduleDate = null;
                if (hasScheduleHistory)
                {
                    var entries = await historyRepo.LoadAsync(uid);
                    lastScheduleDate = entries.FirstOrDefault()?.Timestamp.ToString("o");
                }

                users.Add(new
                {
                    userId = uid,
                    settings = new { settings.ComfortHours, settings.TurnOffPercentile, settings.MaxComfortGapHours },
                    zone,
                    daikinAuthorized,
                    daikinExpiresAtUtc,
                    daikinSubject,
                    hasScheduleHistory,
                    scheduleCount,
                    lastScheduleDate,
                    isAdmin = adminUserIds.Contains(uid),
                    hasHangfireAccess = hangfireUserIds.Contains(uid),
                    isCurrentUser = uid == currentUserId
                });
            }

            return Results.Json(new { users }, new JsonSerializerOptions { PropertyNamingPolicy = null });
        });

        adminGroup.MapPost("/users/{userId}/grant", async (IConfiguration cfg, HttpContext c, string userId) =>
        {
            if (!IsAdminRequest(c, cfg))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (!IsValidUserId(userId))
                return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

            await AdminService.GrantAdmin(cfg, userId);
            return Results.Json(new { granted = true, userId });
        });

        adminGroup.MapDelete("/users/{userId}/grant", async (IConfiguration cfg, HttpContext c, string userId) =>
        {
            if (!IsAdminRequest(c, cfg))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (!IsValidUserId(userId))
                return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

            var currentUserId = AccountAuthentication.UserId(c.User);
            if (userId == currentUserId)
                return Results.Json(new { error = "Cannot revoke your own admin access" }, statusCode: 400);

            await AdminService.RevokeAdmin(cfg, userId);
            return Results.Json(new { revoked = true, userId });
        });

        adminGroup.MapPost("/users/{userId}/hangfire", async (IConfiguration cfg, HttpContext c, string userId) =>
        {
            if (!IsAdminRequest(c, cfg))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (!IsValidUserId(userId))
                return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

            await AdminService.GrantHangfireAccess(cfg, userId);
            return Results.Json(new { granted = true, userId });
        });

        adminGroup.MapDelete("/users/{userId}/hangfire", async (IConfiguration cfg, HttpContext c, string userId) =>
        {
            if (!IsAdminRequest(c, cfg))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (!IsValidUserId(userId))
                return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

            await AdminService.RevokeHangfireAccess(cfg, userId);
            return Results.Json(new { revoked = true, userId });
        });

        adminGroup.MapDelete("/users/{userId}", async (IConfiguration cfg, HttpContext c, string userId, DaikinTokenRepository tokenRepo, ScheduleHistoryRepository historyRepo) =>
        {
            if (!IsAdminRequest(c, cfg))
                return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

            if (!IsValidUserId(userId))
                return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

            var currentUserId = AccountAuthentication.UserId(c.User);
            if (userId == currentUserId)
                return Results.Json(new { error = "Cannot delete your own user" }, statusCode: 400);

            var deleted = false;
            var warnings = new List<string>();

            // Delete tokens from database
            try
            {
                await tokenRepo.DeleteAsync(userId);
                deleted = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Admin] Failed to delete tokens for user {userId}: {ex.Message}");
                warnings.Add("Failed to delete tokens");
            }

            // Delete schedule history from database
            try
            {
                var historyDeleted = await historyRepo.DeleteAllOlderThanAsync(DateTimeOffset.MinValue);
                deleted = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Admin] Failed to delete schedule history for user {userId}: {ex.Message}");
                warnings.Add("Failed to delete schedule history");
            }

            // Remove from admin.json if present
            try
            {
                await AdminService.RevokeAdmin(cfg, userId);
                await AdminService.RevokeHangfireAccess(cfg, userId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Admin] Failed to update admin.json for user {userId}: {ex.Message}");
                warnings.Add("Failed to update admin configuration");
            }

            if (!deleted)
                return Results.Json(new { error = "User not found" }, statusCode: 404);

            return Results.Json(new { deleted = true, userId, warnings });
        });
        return adminGroup;
    }

    internal static bool IsAdminRequest(HttpContext ctx, IConfiguration cfg)
    {
        var userId = AccountAuthentication.UserId(ctx.User);
        var password = ctx.Request.Headers["X-Admin-Password"].FirstOrDefault();
        var (isAdmin, _) = AdminService.CheckAdminAccess(cfg, userId, password);
        return isAdmin;
    }

    private static bool IsValidUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 100)
            return false;
        return userId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }
}
