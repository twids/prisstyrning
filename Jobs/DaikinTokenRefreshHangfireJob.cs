using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Hangfire;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that periodically scans token files and refreshes them proactively
/// so that normal requests rarely encounter an expired access token.
/// </summary>
public class DaikinTokenRefreshHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly DaikinOAuthService _daikinOAuthService;
    private readonly TimeSpan _refreshWindow;

    public DaikinTokenRefreshHangfireJob(IConfiguration cfg, DaikinOAuthService daikinOAuthService)
    {
        _cfg = cfg;
        _daikinOAuthService = daikinOAuthService;
        // Allow override via config; default: refresh if < 5 min remains
        _refreshWindow = TimeSpan.FromMinutes(ParseInt(cfg["Daikin:ProactiveRefreshWindowMinutes"], 5));
    }

    [DisableConcurrentExecution(30)] // Prevent overlapping executions with 30s timeout
    public async Task ExecuteAsync()
    {
        Console.WriteLine($"[DaikinTokenRefreshHangfireJob] Starting scan with window={_refreshWindow}");
        
        var tokenDir = StoragePaths.GetTokensDir(_cfg);
        if (!Directory.Exists(tokenDir)) return;

        // Find all daikin.json files (root and subdirectories)
        var files = Directory.EnumerateFiles(tokenDir, "daikin.json", SearchOption.AllDirectories)
            .Take(200) // safety cap
            .ToList();
        if (!files.Any()) return;

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("expires_at_utc", out var expProp)) continue;
                if (!DateTimeOffset.TryParse(expProp.GetString(), out var expiresAt)) continue;
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                if (remaining > _refreshWindow) continue; // still plenty of time

                // Derive userId from directory name (if nested). The DaikinOAuthService already knows how to map; we mimic its per-user dir scheme.
                string? userId = null;
                var dir = Path.GetDirectoryName(file)!;
                // if parent isn't the base tokens directory, treat directory name as userId
                var parent = Path.GetFullPath(tokenDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dirFull = Path.GetFullPath(dir);
                if (!string.Equals(dirFull, parent, StringComparison.OrdinalIgnoreCase))
                {
                    var userFolder = Path.GetFileName(dirFull);
                    userId = userFolder; // Already sanitized when created
                }
                var before = remaining;
                var access = await _daikinOAuthService.RefreshIfNeededAsync(_cfg, userId);
                if (access != null)
                {
                    Console.WriteLine($"[DaikinTokenRefreshHangfireJob] Refreshed token file={file} user={(userId ?? "(global)")} beforeRemaining={(int)before.TotalSeconds}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DaikinTokenRefreshHangfireJob][Warn] file={file} {ex.Message}");
            }
        }
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var v) && v > 0 ? v : fallback;
}