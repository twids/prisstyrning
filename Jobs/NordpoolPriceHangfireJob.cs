using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that fetches Nordpool electricity prices for all configured zones.
/// Runs daily at 13:00 Europe/Stockholm time (CET/CEST, when day-ahead prices
/// are published) with exponential backoff retries if tomorrow's prices are not
/// yet available.
/// </summary>
internal class NordpoolPriceHangfireJob
{
    internal const int MaxRetryAttempts = 5;
    internal const double RetryBaseMinutes = 5; // 5, 10, 20, 40, 80 min

    private readonly IConfiguration _cfg;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public NordpoolPriceHangfireJob(
        IConfiguration cfg,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IBackgroundJobClient backgroundJobClient)
    {
        _cfg = cfg;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _backgroundJobClient = backgroundJobClient;
    }

    /// <summary>Entry point for the recurring 13:00 schedule.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync()
    {
        await FetchPricesAsync(attempt: 0);
    }

    /// <summary>Entry point for exponential-backoff retries scheduled by the main run.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RetryFetchAsync(int attempt)
    {
        await FetchPricesAsync(attempt);
    }

    private async Task FetchPricesAsync(int attempt)
    {
        var currency = _cfg["Price:Nordpool:Currency"] ?? "SEK";
        var defaultZone = _cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defaultZone };
        
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<UserSettingsRepository>();
            var userZones = await settingsRepo.GetAllUserZonesAsync();
            foreach (var z in userZones)
            {
                if (UserSettingsRepository.IsValidZone(z)) zones.Add(z.Trim().ToUpperInvariant());
                if (zones.Count > 20) break;
            }
        }
        catch { }

        var label = attempt > 0 ? $"retry #{attempt}" : "initial";
        Console.WriteLine($"[NordpoolPriceHangfireJob] ({label}) fetching zones={string.Join(',', zones)} currency={currency}");
        var client = new NordpoolClient(_httpClientFactory.CreateClient("Nordpool"), currency);

        bool anyMissingTomorrow = false;

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
                    if (tomorrow == null || tomorrow.Count == 0)
                    {
                        needUpdate = true;
                        Console.WriteLine($"[NordpoolPriceHangfireJob] tomorrow prices missing for zone={zone}, fetching...");
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

                if (tomorrow == null || tomorrow.Count == 0)
                    anyMissingTomorrow = true;
                
                if (string.Equals(zone, defaultZone, StringComparison.OrdinalIgnoreCase))
                {
                    PriceMemory.Set(today, tomorrow);
                }
                
                Console.WriteLine($"[NordpoolPriceHangfireJob] ok zone={zone} today={(today?.Count ?? 0)} tomorrow={(tomorrow?.Count ?? 0)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NordpoolPriceHangfireJob] zone={zone} error={ex.Message}");
                anyMissingTomorrow = true; // treat errors as missing, so we retry
            }
        }

        // Schedule retry with exponential backoff if tomorrow's prices are still missing
        if (anyMissingTomorrow && attempt < MaxRetryAttempts)
        {
            var delayMinutes = RetryBaseMinutes * Math.Pow(2, attempt);
            Console.WriteLine($"[NordpoolPriceHangfireJob] tomorrow prices incomplete, scheduling retry #{attempt + 1} in {delayMinutes:F0}min");
            _backgroundJobClient.Schedule<NordpoolPriceHangfireJob>(
                j => j.RetryFetchAsync(attempt + 1), TimeSpan.FromMinutes(delayMinutes));
        }
        else if (anyMissingTomorrow)
        {
            Console.WriteLine($"[NordpoolPriceHangfireJob] tomorrow prices still missing after {MaxRetryAttempts} retries, giving up until next scheduled run");
        }

        // Cleanup old price snapshots (only on initial run, not retries)
        if (attempt == 0)
        {
            try
            {
                var configuredRetentionDays = _cfg.GetValue("Price:RetentionDays", 90);
                const int maxRetentionDays = 365;
                var retentionDays = configuredRetentionDays <= 0
                    ? 90
                    : Math.Min(configuredRetentionDays, maxRetentionDays);
                var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-retentionDays));
                using var cleanupScope = _scopeFactory.CreateScope();
                var cleanupRepo = cleanupScope.ServiceProvider.GetRequiredService<PriceRepository>();
                var deleted = await cleanupRepo.DeleteOlderThanAsync(cutoff);
                if (deleted > 0)
                    Console.WriteLine($"[NordpoolPriceHangfireJob] cleaned up {deleted} price snapshots older than {cutoff}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NordpoolPriceHangfireJob] price cleanup error: {ex.Message}");
            }
        }
    }
}