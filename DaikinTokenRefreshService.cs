using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

/// <summary>
/// Background service that periodically scans token files and refreshes them proactively
/// so that normal requests rarely encounter an expired access token.
/// </summary>
internal sealed class DaikinTokenRefreshService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _refreshWindow;

    public DaikinTokenRefreshService(IConfiguration cfg)
    {
        _cfg = cfg;
        // Allow override via config; defaults: check every 5 minutes, refresh if < 5 min remains
        _interval = TimeSpan.FromMinutes(ParseInt(cfg["Daikin:RefreshScanMinutes"], 5));
        _refreshWindow = TimeSpan.FromMinutes(ParseInt(cfg["Daikin:ProactiveRefreshWindowMinutes"], 5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"[DaikinRefreshService] Started interval={_interval} window={_refreshWindow}");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndRefreshAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DaikinRefreshService][Error] {ex.Message}");
            }
            try { await Task.Delay(_interval, stoppingToken); } catch { }
        }
        Console.WriteLine("[DaikinRefreshService] Stopped");
    }

    private async Task ScanAndRefreshAsync(CancellationToken ct)
    {
        var tokenDir = StoragePaths.GetTokensDir(_cfg);
        if (!Directory.Exists(tokenDir)) return;

        // Find all daikin.json files (root and subdirectories)
        var files = Directory.EnumerateFiles(tokenDir, "daikin.json", SearchOption.AllDirectories)
            .Take(200) // safety cap
            .ToList();
        if (!files.Any()) return;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
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
                var access = await DaikinOAuthService.RefreshIfNeededAsync(_cfg, userId);
                if (access != null)
                {
                    Console.WriteLine($"[DaikinRefreshService] Refreshed token file={file} user={(userId??"(global)")} beforeRemaining={(int)before.TotalSeconds}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DaikinRefreshService][Warn] file={file} {ex.Message}");
            }
        }
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var v) && v > 0 ? v : fallback;
}
