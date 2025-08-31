using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
// Läser in en extra lokal override-fil (gemener) om den finns
builder.Configuration.AddJsonFile("appsettings.development.json", optional: true, reloadOnChange: true);
// Konfigurera så att appen kan lyssna på alla interfaces (0.0.0.0) istället för endast localhost
var portValue = Environment.GetEnvironmentVariable("PORT") ?? builder.Configuration["PORT"] ?? builder.Configuration["App:Port"];
if (!int.TryParse(portValue, out var listenPort)) listenPort = 5000;
builder.WebHost.ConfigureKestrel(o =>
{
    // Rensar ev. default endpoints och lyssnar på angiven port på alla IP
    o.ListenAnyIP(listenPort);
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHostedService<DailyPriceJob>();

var app = builder.Build();

// Försök förladda minnescache från senaste persistenta fil så /api/prices/memory inte ger 404 direkt vid start
try
{
    var preloadDir = builder.Configuration["Storage:Directory"] ?? "data";
    if (Directory.Exists(preloadDir))
    {
        var latest = Directory.GetFiles(preloadDir, "prices-*.json").OrderByDescending(f => f).FirstOrDefault();
        if (latest != null)
        {
            var json = await File.ReadAllTextAsync(latest);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonArray? todayArr = null;
            JsonArray? tomorrowArr = null;
            if (root.TryGetProperty("today", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            {
                todayArr = JsonNode.Parse(tEl.GetRawText()) as JsonArray;
            }
            if (root.TryGetProperty("tomorrow", out var tmEl) && tmEl.ValueKind == JsonValueKind.Array)
            {
                tomorrowArr = JsonNode.Parse(tmEl.GetRawText()) as JsonArray;
            }
            if (todayArr != null || tomorrowArr != null)
            {
                PriceMemory.Set(todayArr, tomorrowArr);
                Console.WriteLine("[Startup] Preloaded price memory from latest file");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] Preload failed: {ex.Message}");
}

if (app.Environment.IsDevelopment() || true)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// Static files (UI)
app.UseDefaultFiles();
app.UseStaticFiles();
// Root handled by default file (index.html)

// Prices group
var pricesGroup = app.MapGroup("/api/prices").WithTags("Prices");
pricesGroup.MapGet("/latest", () =>
{
    var dir = builder.Configuration["Storage:Directory"] ?? "data";
    if (!Directory.Exists(dir)) return Results.NotFound();
    var file = Directory.GetFiles(dir, "prices-*.json").OrderByDescending(f => f).FirstOrDefault();
    if (file == null) return Results.NotFound();
    return Results.File(file, "application/json");
});
pricesGroup.MapGet("/memory", () =>
{
    var (today, tomorrow, updated) = PriceMemory.Get();
    if (today == null && tomorrow == null) return Results.NotFound(new { message = "No prices in memory yet" });
    return Results.Json(new { updated, today, tomorrow });
});
pricesGroup.MapGet("/timeseries", (HttpContext ctx) =>
{
    var source = ctx.Request.Query["source"].ToString(); // "latest" för att tvinga läsning från fil
    var (memToday, memTomorrow, updated) = PriceMemory.Get();
    JsonArray? today = memToday;
    JsonArray? tomorrow = memTomorrow;

    bool forceLatest = string.Equals(source, "latest", StringComparison.OrdinalIgnoreCase);
    if (forceLatest || today == null || (today.Count < 24 && DateTimeOffset.Now.Hour < 23))
    {
        try
        {
            var dir = builder.Configuration["Storage:Directory"] ?? "data";
            if (Directory.Exists(dir))
            {
                var file = Directory.GetFiles(dir, "prices-*.json").OrderByDescending(f => f).FirstOrDefault();
                if (file != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (today == null && root.TryGetProperty("today", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                        today = JsonNode.Parse(tEl.GetRawText()) as JsonArray;
                    if (tomorrow == null && root.TryGetProperty("tomorrow", out var tmEl) && tmEl.ValueKind == JsonValueKind.Array)
                        tomorrow = JsonNode.Parse(tmEl.GetRawText()) as JsonArray;
                }
            }
        }
        catch { /* ignore */ }
    }

    var items = new List<(DateTimeOffset start, decimal value, string day)>();
    void Add(JsonArray? arr, string label)
    {
        if (arr == null) return;
        foreach (var n in arr)
        {
            if (n == null) continue;
            var startStr = n["start"]?.ToString();
            var valueStr = n["value"]?.ToString();
            if (!DateTimeOffset.TryParse(startStr, out var ts)) continue;
            if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
            items.Add((ts, val, label));
        }
    }
    Add(today, "today");
    Add(tomorrow, "tomorrow");
    var ordered = items.OrderBy(i => i.start).Select(i => new { start = i.start, value = i.value, day = i.day }).ToList();
    return Results.Json(new { updated, count = ordered.Count, items = ordered, source = forceLatest ? "latest" : "memory" });
});

// Daikin OAuth group
var daikinAuthGroup = app.MapGroup("/auth/daikin").WithTags("Daikin Auth");
daikinAuthGroup.MapGet("/start", (IConfiguration cfg, HttpContext ctx) =>
{
    try { var url = DaikinOAuthService.GetAuthorizationUrl(cfg, ctx); return Results.Json(new { url }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
daikinAuthGroup.MapGet("/start-min", (IConfiguration cfg, HttpContext ctx) =>
{
    try { var url = DaikinOAuthService.GetMinimalAuthorizationUrl(cfg, ctx); return Results.Json(new { url }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
daikinAuthGroup.MapGet("/callback", async (IConfiguration cfg, string? code, string? state) =>
{
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return Results.BadRequest(new { error = "Missing code/state" });
    var ok = await DaikinOAuthService.HandleCallbackAsync(cfg, code, state);
    return ok ? Results.Ok(new { message = "Authorization stored" }) : Results.BadRequest(new { error = "Auth failed" });
});
daikinAuthGroup.MapGet("/status", (IConfiguration cfg) => Results.Json(DaikinOAuthService.Status(cfg)));
daikinAuthGroup.MapPost("/refresh", async (IConfiguration cfg) =>
{
    var token = await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    return token == null ? Results.BadRequest(new { error = "Refresh failed or not authorized" }) : Results.Ok(new { refreshed = true });
});
    daikinAuthGroup.MapGet("/debug", (IConfiguration cfg) =>
    {
        var status = DaikinOAuthService.Status(cfg);
        return Results.Json(new { status, now = DateTimeOffset.UtcNow });
    });
    daikinAuthGroup.MapPost("/revoke", async (IConfiguration cfg) =>
    {
        var ok = await DaikinOAuthService.RevokeAsync(cfg);
        return ok ? Results.Ok(new { revoked = true }) : Results.BadRequest(new { error = "Revoke failed or not authorized" });
    });
    daikinAuthGroup.MapGet("/introspect", async (IConfiguration cfg, bool refresh) =>
    {
        var result = await DaikinOAuthService.IntrospectAsync(cfg, refresh);
        return result == null ? Results.BadRequest(new { error = "Not authorized" }) : Results.Json(result);
    });

// Schedule group
var scheduleGroup = app.MapGroup("/api/schedule").WithTags("Schedule");
scheduleGroup.MapGet("/preview", async () => await BatchRunner.GenerateSchedulePreview((IConfiguration)builder.Configuration));
scheduleGroup.MapPost("/apply", async () =>
{
    var result = await BatchRunner.RunBatchAsync(builder.Configuration, applySchedule:true, persist:true);
    return Results.Json(result);
});

// Daikin data group
var daikinGroup = app.MapGroup("/api/daikin").WithTags("Daikin");
daikinGroup.MapGet("/sites", async (IConfiguration cfg) =>
{
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        var client = new DaikinApiClient(token);
        var json = await client.GetSitesAsync();
        return Results.Content(json, "application/json");
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
daikinGroup.MapGet("/devices", async (IConfiguration cfg, string? siteId) =>
{
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        var client = new DaikinApiClient(token);
        if (string.IsNullOrWhiteSpace(siteId))
        {
            var sitesJson = await client.GetSitesAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(sitesJson);
            siteId = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0].GetProperty("id").GetString() : null;
            if (siteId == null) return Results.BadRequest(new { error = "No site found" });
        }
        var devicesJson = await client.GetDevicesAsync(siteId);
        return Results.Content(devicesJson, "application/json");
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
daikinGroup.MapGet("/schedule", async (IConfiguration cfg, string deviceId) =>
{
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        var client = new DaikinApiClient(token);
        var schedule = await client.GetScheduleAsync(deviceId);
        return Results.Content(schedule, "application/json");
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Kör initial batch (persist + ev. schedule) utan att exponera svar
_ = Task.Run(async () => await BatchRunner.RunBatchAsync((IConfiguration)builder.Configuration, applySchedule:true, persist:true));

await app.RunAsync();


public class DailyPriceJob : IHostedService, IDisposable
{
    private Timer? _timer;
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _cfg;
    public DailyPriceJob(IServiceProvider sp, IConfiguration cfg)
    { _sp = sp; _cfg = cfg; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Kör direkt om ingen fil finns
        var dir = _cfg["Storage:Directory"] ?? "data";
        Directory.CreateDirectory(dir);
        var hasAny = Directory.GetFiles(dir, "prices-*.json").Length > 0;
    _ = BatchRunner.RunBatchAsync(_cfg, applySchedule:true, persist:true);
        // Start timer (check var 10:e minut om klockan passerat 14:00 och dagens tomorrow saknas)
        _timer = new Timer(Check, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        return Task.CompletedTask;
    }

    private async void Check(object? state)
    {
        try
        {
            var now = DateTimeOffset.Now;
            if (now.Hour == 14 && now.Minute < 10) // första 10 min efter 14
            {
                await BatchRunner.RunBatchAsync(_cfg, applySchedule:true, persist:true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DailyJob] error: {ex.Message}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    { _timer?.Change(Timeout.Infinite, 0); return Task.CompletedTask; }
    public void Dispose() => _timer?.Dispose();
}