using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that fetches Nordpool electricity prices for all configured zones
/// </summary>
public class NordpoolPriceHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly IServiceScopeFactory _scopeFactory;

    public NordpoolPriceHangfireJob(IConfiguration cfg, IServiceScopeFactory scopeFactory)
    {
        _cfg = cfg;
        _scopeFactory = scopeFactory;
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
            // Discover user zones from database
            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<UserSettingsRepository>();
            var userZones = await settingsRepo.GetAllUserZonesAsync();
            foreach (var z in userZones)
            {
                if (UserSettingsRepository.IsValidZone(z)) zones.Add(z.Trim().ToUpperInvariant());
                if (zones.Count > 20) break; // safety cap
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
    }
}