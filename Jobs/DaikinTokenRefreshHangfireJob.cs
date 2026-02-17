using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Hangfire;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that periodically scans token records and refreshes them proactively
/// so that normal requests rarely encounter an expired access token.
/// </summary>
public class DaikinTokenRefreshHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _refreshWindow;

    public DaikinTokenRefreshHangfireJob(IConfiguration cfg, IServiceScopeFactory scopeFactory)
    {
        _cfg = cfg;
        _scopeFactory = scopeFactory;
        // Allow override via config; default: refresh if < 5 min remains
        _refreshWindow = TimeSpan.FromMinutes(ParseInt(cfg["Daikin:ProactiveRefreshWindowMinutes"], 5));
    }

    [DisableConcurrentExecution(30)] // Prevent overlapping executions with 30s timeout
    public async Task ExecuteAsync()
    {
        Console.WriteLine($"[DaikinTokenRefreshHangfireJob] Starting scan with window={_refreshWindow}");
        
        using var scope = _scopeFactory.CreateScope();
        var tokenRepo = scope.ServiceProvider.GetRequiredService<DaikinTokenRepository>();
        var daikinOAuth = scope.ServiceProvider.GetRequiredService<DaikinOAuthService>();
        
        var userIds = await tokenRepo.GetAllUserIdsAsync();
        if (!userIds.Any()) return;

        foreach (var userId in userIds)
        {
            try
            {
                var token = await tokenRepo.LoadAsync(userId);
                if (token == null) continue;
                
                var remaining = token.ExpiresAtUtc - DateTimeOffset.UtcNow;
                if (remaining > _refreshWindow) continue;

                var before = remaining;
                var access = await daikinOAuth.RefreshIfNeededAsync(userId, _refreshWindow);
                if (access != null)
                {
                    Console.WriteLine($"[DaikinTokenRefreshHangfireJob] Refreshed token user={userId} beforeRemaining={(int)before.TotalSeconds}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DaikinTokenRefreshHangfireJob][Warn] userId={userId} {ex.Message}");
            }
        }
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var v) && v > 0 ? v : fallback;
}