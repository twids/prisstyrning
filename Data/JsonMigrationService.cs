using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Data;

public class JsonMigrationService : IHostedService
{
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _baseDir;
    private readonly string _defaultZone;

    public JsonMigrationService(IConfiguration config, IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _scopeFactory = scopeFactory;
        _baseDir = config["Storage:Directory"] ?? "data";
        _defaultZone = config["Price:Nordpool:DefaultZone"] ?? "SE3";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[JsonMigration] Starting JSON-to-DB migration check...");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();

        await MigrateUserSettings(db);
        await MigrateDaikinTokens(db);
        await MigratePriceSnapshotsNewFormat(db);
        await MigratePriceSnapshotsOldFormat(db);
        await MigrateScheduleHistory(db);

        Console.WriteLine("[JsonMigration] Migration check complete.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ─── User Settings ───────────────────────────────────────────────

    private async Task MigrateUserSettings(PrisstyrningDbContext db)
    {
        var tokensDir = Path.Combine(_baseDir, "tokens");
        if (!Directory.Exists(tokensDir)) return;

        foreach (var userDir in Directory.GetDirectories(tokensDir))
        {
            var filePath = Path.Combine(userDir, "user.json");
            if (!File.Exists(filePath)) continue;

            var userId = Path.GetFileName(userDir);
            try
            {
                var existing = await db.UserSettings.FindAsync(userId);
                if (existing != null)
                {
                    Console.WriteLine($"[JsonMigration] UserSettings for '{userId}' already exists, skipping.");
                    DeleteFile(filePath);
                    continue;
                }

                var json = await File.ReadAllTextAsync(filePath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var settings = new UserSettings
                {
                    UserId = userId,
                    ComfortHours = GetIntOrDefault(root, "ComfortHours", 3),
                    TurnOffPercentile = GetDoubleOrDefault(root, "TurnOffPercentile", 0.9),
                    MaxComfortGapHours = GetIntOrDefault(root, "MaxComfortGapHours", 28),
                    AutoApplySchedule = false,
                    Zone = GetStringOrDefault(root, "zone", "SE3")
                };

                db.UserSettings.Add(settings);
                await db.SaveChangesAsync();
                Console.WriteLine($"[JsonMigration] Migrated UserSettings for '{userId}'.");
                DeleteFile(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JsonMigration] ERROR migrating UserSettings for '{userId}': {ex.Message}");
            }
        }
    }

    // ─── Daikin Tokens ───────────────────────────────────────────────

    private async Task MigrateDaikinTokens(PrisstyrningDbContext db)
    {
        var tokensDir = Path.Combine(_baseDir, "tokens");

        // Anonymous token at {baseDir}/tokens/daikin.json
        var anonPath = Path.Combine(tokensDir, "daikin.json");
        if (File.Exists(anonPath))
        {
            await MigrateSingleDaikinToken(db, anonPath, "anonymous");
        }

        // Per-user tokens at {baseDir}/tokens/{userId}/daikin.json
        if (!Directory.Exists(tokensDir)) return;

        foreach (var userDir in Directory.GetDirectories(tokensDir))
        {
            var filePath = Path.Combine(userDir, "daikin.json");
            if (!File.Exists(filePath)) continue;

            var userId = Path.GetFileName(userDir);
            await MigrateSingleDaikinToken(db, filePath, userId);
        }
    }

    private async Task MigrateSingleDaikinToken(PrisstyrningDbContext db, string filePath, string userId)
    {
        try
        {
            var existing = await db.DaikinTokens.FindAsync(userId);
            if (existing != null)
            {
                Console.WriteLine($"[JsonMigration] DaikinToken for '{userId}' already exists, skipping.");
                DeleteFile(filePath);
                return;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var token = new DaikinToken
            {
                UserId = userId,
                AccessToken = GetStringOrDefault(root, "access_token", ""),
                RefreshToken = GetStringOrDefault(root, "refresh_token", ""),
                ExpiresAtUtc = DateTimeOffset.Parse(GetStringOrDefault(root, "expires_at_utc", DateTimeOffset.MinValue.ToString("O")))
            };

            db.DaikinTokens.Add(token);
            await db.SaveChangesAsync();
            Console.WriteLine($"[JsonMigration] Migrated DaikinToken for '{userId}'.");
            DeleteFile(filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JsonMigration] ERROR migrating DaikinToken for '{userId}': {ex.Message}");
        }
    }

    // ─── Price Snapshots (new format) ────────────────────────────────

    private async Task MigratePriceSnapshotsNewFormat(PrisstyrningDbContext db)
    {
        var nordpoolDir = Path.Combine(_baseDir, "nordpool");
        if (!Directory.Exists(nordpoolDir)) return;

        foreach (var zoneDir in Directory.GetDirectories(nordpoolDir))
        {
            var zone = Path.GetFileName(zoneDir);
            foreach (var filePath in Directory.GetFiles(zoneDir, "prices-*.json"))
            {
                await MigrateSinglePriceSnapshot(db, filePath, zone, isNewFormat: true);
            }
        }
    }

    // ─── Price Snapshots (old format) ────────────────────────────────

    private async Task MigratePriceSnapshotsOldFormat(PrisstyrningDbContext db)
    {
        // Old format files are at {baseDir}/prices-*.json
        if (!Directory.Exists(_baseDir)) return;

        foreach (var filePath in Directory.GetFiles(_baseDir, "prices-*.json"))
        {
            await MigrateSinglePriceSnapshot(db, filePath, _defaultZone, isNewFormat: false);
        }
    }

    private async Task MigrateSinglePriceSnapshot(PrisstyrningDbContext db, string filePath, string zone, bool isNewFormat)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Determine savedAt
            DateTimeOffset savedAt;
            if (isNewFormat && root.TryGetProperty("savedAt", out var savedAtProp))
            {
                savedAt = DateTimeOffset.Parse(savedAtProp.GetString()!);
            }
            else if (root.TryGetProperty("fetchedAt", out var fetchedAtProp))
            {
                savedAt = DateTimeOffset.Parse(fetchedAtProp.GetString()!);
            }
            else
            {
                savedAt = DateTimeOffset.UtcNow;
            }

            // Use zone from JSON if present (new format), otherwise use parameter
            if (isNewFormat && root.TryGetProperty("zone", out var zoneProp))
            {
                zone = zoneProp.GetString() ?? zone;
            }

            // Extract date from first today entry's start field
            DateOnly date;
            if (root.TryGetProperty("today", out var todayArray) &&
                todayArray.ValueKind == JsonValueKind.Array &&
                todayArray.GetArrayLength() > 0)
            {
                var firstEntry = todayArray[0];
                if (firstEntry.TryGetProperty("start", out var startProp))
                {
                    var startDto = DateTimeOffset.Parse(startProp.GetString()!);
                    date = DateOnly.FromDateTime(startDto.DateTime);
                }
                else
                {
                    date = DateOnly.FromDateTime(savedAt.UtcDateTime);
                }
            }
            else
            {
                date = DateOnly.FromDateTime(savedAt.UtcDateTime);
            }

            // Check idempotency — same zone+date+savedAt
            var alreadyExists = await db.PriceSnapshots
                .AnyAsync(p => p.Zone == zone && p.Date == date && p.SavedAtUtc == savedAt);
            if (alreadyExists)
            {
                Console.WriteLine($"[JsonMigration] PriceSnapshot for {zone}/{date} already exists, skipping.");
                DeleteFile(filePath);
                return;
            }

            // Serialize arrays as strings
            var todayJson = root.TryGetProperty("today", out var tp)
                ? tp.GetRawText()
                : "[]";
            var tomorrowJson = root.TryGetProperty("tomorrow", out var tmProp)
                ? tmProp.GetRawText()
                : "[]";

            var snapshot = new PriceSnapshot
            {
                Zone = zone,
                Date = date,
                SavedAtUtc = savedAt,
                TodayPricesJson = todayJson,
                TomorrowPricesJson = tomorrowJson
            };

            db.PriceSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            Console.WriteLine($"[JsonMigration] Migrated PriceSnapshot {zone}/{date} from {Path.GetFileName(filePath)}.");
            DeleteFile(filePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JsonMigration] ERROR migrating PriceSnapshot from '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    // ─── Schedule History ────────────────────────────────────────────

    private async Task MigrateScheduleHistory(PrisstyrningDbContext db)
    {
        var historyBaseDir = Path.Combine(_baseDir, "schedule_history");
        if (!Directory.Exists(historyBaseDir)) return;

        foreach (var userDir in Directory.GetDirectories(historyBaseDir))
        {
            var filePath = Path.Combine(userDir, "history.json");
            if (!File.Exists(filePath)) continue;

            var userId = Path.GetFileName(userDir);
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var entries = JsonDocument.Parse(json).RootElement;

                if (entries.ValueKind != JsonValueKind.Array)
                {
                    Console.WriteLine($"[JsonMigration] Schedule history for '{userId}' is not an array, skipping.");
                    continue;
                }

                var existingCount = await db.ScheduleHistory.CountAsync(s => s.UserId == userId);
                if (existingCount > 0)
                {
                    Console.WriteLine($"[JsonMigration] ScheduleHistory for '{userId}' already has entries, skipping.");
                    DeleteFile(filePath);
                    continue;
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    var timestamp = entry.TryGetProperty("timestamp", out var tsProp)
                        ? DateTimeOffset.Parse(tsProp.GetString()!)
                        : DateTimeOffset.UtcNow;

                    var schedulePayload = entry.TryGetProperty("schedule", out var schedProp)
                        ? schedProp.GetRawText()
                        : "{}";

                    db.ScheduleHistory.Add(new ScheduleHistoryEntry
                    {
                        UserId = userId,
                        Timestamp = timestamp,
                        SchedulePayloadJson = schedulePayload
                    });
                }

                await db.SaveChangesAsync();
                Console.WriteLine($"[JsonMigration] Migrated {entries.GetArrayLength()} ScheduleHistory entries for '{userId}'.");
                DeleteFile(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JsonMigration] ERROR migrating ScheduleHistory for '{userId}': {ex.Message}");
            }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            // Try to clean up empty parent directory
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JsonMigration] Warning: could not delete '{path}': {ex.Message}");
        }
    }

    private static int GetIntOrDefault(JsonElement el, string prop, int def)
    {
        if (el.TryGetProperty(prop, out var val) && val.TryGetInt32(out var i)) return i;
        return def;
    }

    private static double GetDoubleOrDefault(JsonElement el, string prop, double def)
    {
        if (el.TryGetProperty(prop, out var val) && val.TryGetDouble(out var d)) return d;
        return def;
    }

    private static string GetStringOrDefault(JsonElement el, string prop, string def)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString() ?? def;
        return def;
    }
}
