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

        Console.WriteLine($"[NordpoolPriceJob] fetching zones={string.Join(',', zones)} currency={currency}");
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
                        Console.WriteLine($"[NordpoolPriceJob] saknar morgondagens priser för zone={zone}, hämtar...");
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
                Console.WriteLine($"[NordpoolPriceJob] ok zone={zone} today={(today!=null?today.Count:0)} tomorrow={(tomorrow!=null?tomorrow.Count:0)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NordpoolPriceJob] zone={zone} error={ex.Message}");
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
                        Console.WriteLine($"[NordpoolPriceJob] Auto-applying schedule for user {userId}");
                        try
                        {
                            await BatchRunner.RunBatchAsync(_cfg, userId, applySchedule: true, persist: true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[NordpoolPriceJob] user={userId} auto-apply error: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NordpoolPriceJob] userdir={userDir} error: {ex.Message}");
                    }
                }
            }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    { _timer?.Change(Timeout.Infinite, 0); return Task.CompletedTask; }
    public void Dispose() => _timer?.Dispose();
}
