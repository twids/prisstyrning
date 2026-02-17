using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that fetches Nordpool electricity prices for all configured zones
/// </summary>
internal class NordpoolPriceHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public NordpoolPriceHangfireJob(IConfiguration cfg, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory)
    {
        _cfg = cfg;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
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
        var client = new NordpoolClient(_httpClientFactory.CreateClient("Nordpool"), currency, page);
        
        foreach (var zone in zones)
        {
            try
            {
                using var zoneScope = _scopeFactory.CreateScope();
                var priceRepo = zoneScope.ServiceProvider.GetRequiredService<PriceRepository>();
                bool needUpdate = false;
                JsonArray? today = null;
                JsonArray? tomorrow = null;
                
                var todayDate = DateOnly.FromDateTime(DateTime.UtcNow);
                var snapshot = await priceRepo.GetByDateAsync(zone, todayDate);
                if (snapshot != null)
                {
                    today = System.Text.Json.JsonSerializer.Deserialize<JsonArray>(snapshot.TodayPricesJson);
                    tomorrow = System.Text.Json.JsonSerializer.Deserialize<JsonArray>(snapshot.TomorrowPricesJson);
                    // After 13:00, if tomorrow data is missing, fetch new data
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
                    await priceRepo.SaveSnapshotAsync(zone, todayDate, today ?? new JsonArray(), tomorrow ?? new JsonArray());
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