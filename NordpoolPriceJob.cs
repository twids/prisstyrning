using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

internal class NordpoolPriceJob : IHostedService, IDisposable
{
    private readonly IConfiguration _cfg;
    private Timer? _timer;
    private readonly object _runLock = new();
    private bool _running;
    public NordpoolPriceJob(IConfiguration cfg) { _cfg = cfg; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run immediately
        _ = Task.Run(RunOnceSafe);
        var hoursCfg = _cfg["Price:Nordpool:RefreshHours"]; // optional override
        if (!int.TryParse(hoursCfg, out var hours) || hours <= 0) hours = 6;
        var period = TimeSpan.FromHours(hours);
        _timer = new Timer(_ => RunOnceSafe(), null, period, period);
        Console.WriteLine($"[NordpoolPriceJob] started interval={period}");
        return Task.CompletedTask;
    }

    private async void RunOnceSafe()
    {
        lock(_runLock)
        {
            if (_running) return; _running = true;
        }
        try { await RunOnceAsync(); }
        catch (Exception ex) { Console.WriteLine($"[NordpoolPriceJob] error {ex.Message}"); }
        finally { lock(_runLock) _running = false; }
    }

    private async Task RunOnceAsync()
    {
    var currency = _cfg["Price:Nordpool:Currency"] ?? "SEK";
    var page = _cfg["Price:Nordpool:PageId"];
        var defaultZone = _cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defaultZone };
        try
        {
            // Discover user zones by scanning token subdirs
            if (Directory.Exists("tokens"))
            {
                foreach (var userDir in Directory.GetDirectories("tokens"))
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

        Console.WriteLine($"[NordpoolPriceJob] fetching zones={string.Join(',', zones)} currency={currency}");
    var client = new NordpoolClient(currency, page);
        foreach (var zone in zones)
        {
            try
            {
                var (today, tomorrow) = await client.GetTodayTomorrowAsync(zone);
                NordpoolPersistence.Save(zone, today, tomorrow, _cfg["Storage:Directory"] ?? "data");
                if (string.Equals(zone, defaultZone, StringComparison.OrdinalIgnoreCase))
                {
                    PriceMemory.Set(today, tomorrow);
                }
                Console.WriteLine($"[NordpoolPriceJob] ok zone={zone} today={today.Count} tomorrow={tomorrow.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NordpoolPriceJob] zone={zone} error={ex.Message}");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    { _timer?.Change(Timeout.Infinite, 0); return Task.CompletedTask; }
    public void Dispose() => _timer?.Dispose();
}
