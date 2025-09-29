using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Hangfire;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that fetches Nordpool electricity prices and handles per-user auto-apply schedule
/// </summary>
public class NordpoolPriceHangfireJob
{
    private readonly IConfiguration _cfg;

    public NordpoolPriceHangfireJob(IConfiguration cfg)
    {
        _cfg = cfg;
    }

    [DisableConcurrentExecution(60)] // Prevent overlapping executions with 60s timeout
    public async Task ExecuteAsync()
    {
        var currency = _cfg["Price:Nordpool:Currency"] ?? "SEK";
        var page = _cfg["Price:Nordpool:PageId"];
        var defaultZone = _cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defaultZone };
        
        try
        {
            // Discover user zones by scanning token subdirs
            var tokensDirOuter = StoragePaths.GetTokensDir(_cfg);
            if (Directory.Exists(tokensDirOuter))
            {
                foreach (var userDir in Directory.GetDirectories(tokensDirOuter))
                {
                    try
                    {
                        var zoneFile = Path.Combine(userDir, "zone.txt");
                        if (File.Exists(zoneFile))
                        {
                            var z = File.ReadAllText(zoneFile).Trim();
                            if (UserSettingsService.IsValidZone(z)) zones.Add(z.Trim().ToUpperInvariant());
                        }
                    }
                    catch { }
                    if (zones.Count > 20) break; // safety cap
                }
            }
        }
        catch { }

        Console.WriteLine($"[NordpoolPriceHangfireJob] fetching zones={string.Join(',', zones)} currency={currency}");
        var client = new NordpoolClient(currency, page);
        
        foreach (var zone in zones)
        {
            try
            {
                var nordpoolDir = StoragePaths.GetNordpoolDir(_cfg);
                var file = NordpoolPersistence.GetLatestFile(zone, nordpoolDir);
                bool needUpdate = false;
                JsonArray? today = null;
                JsonArray? tomorrow = null;
                
                if (file != null && File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("today", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        today = JsonNode.Parse(tEl.GetRawText()) as JsonArray;
                    if (root.TryGetProperty("tomorrow", out var tmEl) && tmEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        tomorrow = JsonNode.Parse(tmEl.GetRawText()) as JsonArray;
                    // Kontroll: efter kl 13:00, om tomorrow saknas eller är tom, hämta ny data
                    var now = DateTimeOffset.Now;
                    if (now.Hour >= 13 && (tomorrow == null || tomorrow.Count == 0))
                    {
                        needUpdate = true;
                        Console.WriteLine($"[NordpoolPriceHangfireJob] saknar morgondagens priser för zone={zone}, hämtar...");
                    }
                }
                else
                {
                    needUpdate = true;
                }
                
                if (needUpdate)
                {
                    var fetched = await client.GetTodayTomorrowAsync(zone);
                    today = fetched.today;
                    tomorrow = fetched.tomorrow;
                    NordpoolPersistence.Save(zone, today, tomorrow, StoragePaths.GetNordpoolDir(_cfg));
                }
                
                if (string.Equals(zone, defaultZone, StringComparison.OrdinalIgnoreCase))
                {
                    PriceMemory.Set(today, tomorrow);
                }
                
                Console.WriteLine($"[NordpoolPriceHangfireJob] ok zone={zone} today={(today?.Count ?? 0)} tomorrow={(tomorrow?.Count ?? 0)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NordpoolPriceHangfireJob] zone={zone} error={ex.Message}");
            }
        }

        // Per-user auto-apply schedule
        var tokensDirInner = StoragePaths.GetTokensDir(_cfg);
        if (Directory.Exists(tokensDirInner))
        {
            var userDirs = Directory.GetDirectories(tokensDirInner);
            foreach (var userDir in userDirs)
            {
                try
                {
                    var userId = Path.GetFileName(userDir);
                    var userJsonPath = Path.Combine(userDir, "user.json");
                    if (!File.Exists(userJsonPath)) continue;
                    var json = await File.ReadAllTextAsync(userJsonPath);
                    var node = JsonNode.Parse(json) as JsonObject;
                    if (node == null) continue;
                    bool autoApply = bool.TryParse(node["AutoApplySchedule"]?.ToString(), out var aas) ? aas : false;
                    if (!autoApply) continue;
                    Console.WriteLine($"[NordpoolPriceHangfireJob] Auto-applying schedule for user {userId}");
                    try
                    {
                        await BatchRunner.RunBatchAsync(_cfg, userId, applySchedule: true, persist: true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NordpoolPriceHangfireJob] user={userId} auto-apply error: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NordpoolPriceHangfireJob] userdir={userDir} error: {ex.Message}");
                }
            }
        }
    }
}