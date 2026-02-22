
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Dashboard;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Jobs;

// Constants for maintainability
const int MaxUserIdLength = 100;
const int MaxScheduleRawDisplayLength = 400;
const int DefaultListenPort = 5000;
const string UserCookieName = "ps_user";

// Register /api/user/settings endpoints after app is declared

var builder = WebApplication.CreateBuilder(args);
// Läser in en extra lokal override-fil (gemener) om den finns
builder.Configuration.AddJsonFile("appsettings.development.json", optional: true, reloadOnChange: true);
// Miljövariabler (tar över appsettings). Stöd både utan prefix och med prefix PRISSTYRNING_
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddEnvironmentVariables(prefix: "PRISSTYRNING_");
// Konfigurera så att appen kan lyssna på alla interfaces (0.0.0.0) istället för endast localhost
var portValue = Environment.GetEnvironmentVariable("PORT") ?? builder.Configuration["PORT"] ?? builder.Configuration["App:Port"];
if (!int.TryParse(portValue, out var listenPort)) listenPort = DefaultListenPort;
builder.WebHost.ConfigureKestrel(o =>
{
    // Rensar ev. default endpoints och lyssnar på angiven port på alla IP
    o.ListenAnyIP(listenPort);
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// PostgreSQL + EF Core
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=prisstyrning;Username=prisstyrning;Password=prisstyrning";
builder.Services.AddDbContext<PrisstyrningDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure HttpClientFactory with named clients
builder.Services.AddHttpClient("Nordpool", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
});

builder.Services.AddHttpClient("Daikin", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
});

builder.Services.AddHttpClient("HomeAssistant", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
});

builder.Services.AddHttpClient("Entsoe", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
});

// Register application services
builder.Services.AddScoped<BatchRunner>();

// Configure Hangfire with in-memory storage
builder.Services.AddHangfire(config => config
    .UseInMemoryStorage());
builder.Services.AddHangfireServer();

// Register repositories
builder.Services.AddScoped<UserSettingsRepository>();
builder.Services.AddScoped<AdminRepository>();
builder.Services.AddScoped<PriceRepository>();
builder.Services.AddScoped<ScheduleHistoryRepository>();
builder.Services.AddScoped<DaikinTokenRepository>();
builder.Services.AddScoped<FlexibleScheduleStateRepository>();
builder.Services.AddScoped<DaikinOAuthService>();
builder.Services.AddHostedService<JsonMigrationService>();

// Register job classes for dependency injection
builder.Services.AddTransient<NordpoolPriceHangfireJob>();
builder.Services.AddTransient<DaikinTokenRefreshHangfireJob>();
builder.Services.AddTransient<DailyPriceHangfireJob>();
builder.Services.AddTransient<InitialBatchHangfireJob>();
builder.Services.AddTransient<ScheduleUpdateHangfireJob>();

var app = builder.Build();

// Apply EF Core migrations on startup (with retry for container orchestration)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
    for (var attempt = 1; attempt <= 5; attempt++)
    {
        try
        {
            db.Database.Migrate();
            Console.WriteLine("[Startup] Database migrations applied successfully.");
            break;
        }
        catch (Exception ex) when (attempt < 5)
        {
            Console.WriteLine($"[Startup] Database migration attempt {attempt}/5 failed: {ex.Message}. Retrying in {attempt * 2}s...");
            Thread.Sleep(attempt * 2000);
        }
    }
}

// Configure Hangfire middleware
var hangfirePassword = builder.Configuration["Hangfire:DashboardPassword"];
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfirePasswordAuthorizationFilter(hangfirePassword, builder.Configuration) }
});

// Schedule recurring jobs
RecurringJob.AddOrUpdate<NordpoolPriceHangfireJob>("nordpool-price-job", 
    job => job.ExecuteAsync(), 
    "0 */6 * * *"); // Every 6 hours

RecurringJob.AddOrUpdate<ScheduleUpdateHangfireJob>("schedule-update-job-midnight",
    job => job.ExecuteAsync(),
    "35 1 * * *", // Daily at 01:35 (1.5h after midnight, allows for price data availability)
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<ScheduleUpdateHangfireJob>("schedule-update-job-noon",
    job => job.ExecuteAsync(),
    "35 13 * * *", // Daily at 13:35 (1.5h after noon, ensures tomorrow's prices are available)
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<DaikinTokenRefreshHangfireJob>("daikin-token-refresh-job",
    job => job.ExecuteAsync(),
    "*/5 * * * *"); // Every 5 minutes

RecurringJob.AddOrUpdate<DailyPriceHangfireJob>("daily-price-job",
    job => job.ExecuteAsync(),
    "*/10 * * * *"); // Every 10 minutes

// Schedule initial batch job to run daily at 14:30
RecurringJob.AddOrUpdate<InitialBatchHangfireJob>("initial-batch-job",
    job => job.ExecuteAsync(),
    "30 14 * * *", // Daily at 14:30
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

// User settings endpoints
// Schedule history endpoint for frontend visualization
app.MapGet("/api/user/schedule-history", async (HttpContext ctx, ScheduleHistoryRepository historyRepo) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var entries = await historyRepo.LoadAsync(userId);
    var result = entries.Select(e => new {
        timestamp = e.Timestamp.ToString("o"),
        date = e.Timestamp.ToString("yyyy-MM-dd"),
        schedule = (JsonNode?)JsonNode.Parse(e.SchedulePayloadJson)
    });
    return Results.Json(result);
});
app.MapGet("/api/user/settings", async (HttpContext ctx, UserSettingsRepository settingsRepo) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var entity = await settingsRepo.GetOrCreateAsync(userId);
    return Results.Json(new { 
        ComfortHours = entity.ComfortHours, 
        TurnOffPercentile = entity.TurnOffPercentile, 
        AutoApplySchedule = entity.AutoApplySchedule, 
        MaxComfortGapHours = entity.MaxComfortGapHours 
    }, new JsonSerializerOptions { PropertyNamingPolicy = null });
});

app.MapPost("/api/user/settings", async (HttpContext ctx, UserSettingsRepository settingsRepo) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var body = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject;
    if (body == null) return Results.BadRequest(new { error = "Missing body" });
    string? rawCh = body["ComfortHours"]?.ToString();
    string? rawTp = body["TurnOffPercentile"]?.ToString();
    string? rawAas = body["AutoApplySchedule"]?.ToString();
    string? rawMcgh = body["MaxComfortGapHours"]?.ToString();
    var errors = new List<string>();
    int comfortHours = 3;
    if (!string.IsNullOrWhiteSpace(rawCh))
    { if (!int.TryParse(rawCh, out comfortHours) || comfortHours < 1 || comfortHours > 12) { errors.Add("ComfortHours must be an integer between 1 and 12"); comfortHours = 3; } }
    double turnOffPercentile = 0.9;
    if (!string.IsNullOrWhiteSpace(rawTp))
    { if (!double.TryParse(rawTp, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out turnOffPercentile) || turnOffPercentile < 0.5 || turnOffPercentile > 0.99) { errors.Add("TurnOffPercentile must be a number between 0.5 and 0.99"); turnOffPercentile = 0.9; } }
    bool autoApplySchedule = false;
    if (!string.IsNullOrWhiteSpace(rawAas))
    { if (!bool.TryParse(rawAas, out autoApplySchedule)) { errors.Add("AutoApplySchedule must be true or false"); autoApplySchedule = false; } }
    int maxComfortGapHours = 28;
    if (!string.IsNullOrWhiteSpace(rawMcgh))
    { if (!int.TryParse(rawMcgh, out maxComfortGapHours) || maxComfortGapHours < 1 || maxComfortGapHours > 72) { errors.Add("MaxComfortGapHours must be an integer between 1 and 72"); maxComfortGapHours = 28; } }
    if (errors.Count > 0) return Results.BadRequest(new { error = "Validation failed", errors });
    await settingsRepo.SaveSettingsAsync(userId, comfortHours, turnOffPercentile, autoApplySchedule, maxComfortGapHours);
    return Results.Json(new { ComfortHours = comfortHours, TurnOffPercentile = turnOffPercentile, AutoApplySchedule = autoApplySchedule, MaxComfortGapHours = maxComfortGapHours });
});

// Preload price memory from database
try
{
    using var preloadScope = app.Services.CreateScope();
    var priceRepo = preloadScope.ServiceProvider.GetRequiredService<PriceRepository>();
    var defaultZone = builder.Configuration["Price:Nordpool:DefaultZone"] ?? "SE3";
    var latestSnapshot = await priceRepo.GetLatestAsync(defaultZone);
    if (latestSnapshot != null)
    {
        var todayArr = JsonSerializer.Deserialize<JsonArray>(latestSnapshot.TodayPricesJson);
        var tomorrowArr = JsonSerializer.Deserialize<JsonArray>(latestSnapshot.TomorrowPricesJson);
        if (todayArr != null || tomorrowArr != null)
        {
            PriceMemory.Set(todayArr, tomorrowArr);
            Console.WriteLine($"[Startup] Preloaded price memory from database (zone={defaultZone}, date={latestSnapshot.Date})");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] DB preload failed: {ex.Message}");
}

if (app.Environment.IsDevelopment() || true)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// Static files
app.UseDefaultFiles();
app.UseStaticFiles();

// Simple per-user cookie (NOT secure auth – just isolation). In production replace with real auth (OIDC, etc.).
app.Use(async (ctx, next) =>
{
    const string cookieName = UserCookieName;
    if (!ctx.Request.Cookies.TryGetValue(cookieName, out var userId) || string.IsNullOrWhiteSpace(userId) || userId.Length < 8)
    {
        userId = Guid.NewGuid().ToString("N");
        ctx.Response.Cookies.Append(cookieName, userId, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true, Expires = DateTimeOffset.UtcNow.AddYears(1) });
    }
    ctx.Items[cookieName] = userId;
    await next();
});

string? GetUserId(HttpContext c) 
{
    if (c.Items.TryGetValue(UserCookieName, out var v) && v is string userId)
    {
        // Validate that the userId is a proper GUID format or sanitized string
        if (string.IsNullOrWhiteSpace(userId)) return null;
        if (userId.Length > MaxUserIdLength) return null; // Reasonable length limit
        
        // Only allow alphanumeric characters, hyphens, and underscores (matching DaikinOAuthService.SanitizeUser logic)
        if (userId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
            return userId;
    }
    return null;
}

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

// Diagnostic endpoint for Nordpool fetch debugging
app.MapGet("/api/prices/_debug/fetch", async (IHttpClientFactory httpClientFactory, HttpContext ctx, IConfiguration cfg, UserSettingsRepository settingsRepo) =>
{
    var userId = GetUserId(ctx);
    var zone = await settingsRepo.GetUserZoneAsync(userId) ?? "SE3";
    var dateStr = ctx.Request.Query["date"].FirstOrDefault();
    DateTime date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Today;
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var pageId = cfg["Price:Nordpool:PageId"];
    var client = new NordpoolClient(httpClientFactory.CreateClient("Nordpool"), currency, pageId);
    var (prices, attempts) = await client.GetDailyPricesDetailedAsync(date, zone);
    return Results.Json(new { date = date.ToString("yyyy-MM-dd"), zone, priceCount = prices.Count, prices, attempts, currency, pageId, userId });
});
app.MapGet("/api/prices/_debug/raw", (IHttpClientFactory httpClientFactory, HttpContext ctx, IConfiguration cfg) =>
{
    var dateStr = ctx.Request.Query["date"].FirstOrDefault();
    DateTime date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Today;
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var pageId = cfg["Price:Nordpool:PageId"];
    var client = new NordpoolClient(httpClientFactory.CreateClient("Nordpool"), currency, pageId);
    return client.GetRawCandidateResponsesAsync(date);
});
pricesGroup.MapGet("/memory", () =>
{
    var (today, tomorrow, updated) = PriceMemory.Get();
    if (today == null && tomorrow == null) return Results.NotFound(new { message = "No prices in memory yet" });
    return Results.Json(new { updated, today, tomorrow });
});
// Per-user zone get/set
pricesGroup.MapGet("/zone", async (HttpContext c, UserSettingsRepository settingsRepo) => {
    var userId = GetUserId(c); var zone = await settingsRepo.GetUserZoneAsync(userId); return Results.Json(new { zone });
});
pricesGroup.MapPost("/zone", async (HttpContext c, UserSettingsRepository settingsRepo) => {
    try {
        using var doc = await JsonDocument.ParseAsync(c.Request.Body);
        if (!doc.RootElement.TryGetProperty("zone", out var zEl)) return Results.BadRequest(new { error = "Missing zone" });
        var zone = zEl.GetString();
        if (!UserSettingsRepository.IsValidZone(zone)) return Results.BadRequest(new { error = "Invalid zone" });
        var userId = GetUserId(c);
        await settingsRepo.SetUserZoneAsync(userId, zone!);
        return Results.Ok(new { saved = true, zone });
    } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
// Get latest persisted Nordpool snapshot for zone
pricesGroup.MapGet("/nordpool/latest", async (HttpContext c, IConfiguration cfg, UserSettingsRepository settingsRepo, PriceRepository priceRepo, string? zone) => {
    zone ??= settingsRepo.GetUserZone(GetUserId(c));
    var snapshot = await priceRepo.GetLatestAsync(zone);
    if (snapshot == null) return Results.NotFound(new { error = "No snapshot" });
    return Results.Json(new { zone = snapshot.Zone, date = snapshot.Date.ToString("yyyy-MM-dd"), savedAt = snapshot.SavedAtUtc, today = JsonSerializer.Deserialize<JsonArray>(snapshot.TodayPricesJson), tomorrow = JsonSerializer.Deserialize<JsonArray>(snapshot.TomorrowPricesJson) });
});
pricesGroup.MapGet("/timeseries", async (HttpContext ctx, PriceRepository priceRepo, IConfiguration cfg) =>
{
    var source = ctx.Request.Query["source"].ToString();
    var (memToday, memTomorrow, updated) = PriceMemory.Get();
    JsonArray? today = memToday;
    JsonArray? tomorrow = memTomorrow;
    bool forceLatest = string.Equals(source, "latest", StringComparison.OrdinalIgnoreCase);
    if (forceLatest || today == null || (today.Count < 24 && DateTimeOffset.Now.Hour < 23))
    {
        try
        {
            var zone = cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
            var snapshot = await priceRepo.GetLatestAsync(zone);
            if (snapshot != null)
            {
                if (today == null)
                    today = JsonSerializer.Deserialize<JsonArray>(snapshot.TodayPricesJson);
                if (tomorrow == null)
                    tomorrow = JsonSerializer.Deserialize<JsonArray>(snapshot.TomorrowPricesJson);
            }
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Timeseries] Failed to read price data from DB: {ex.Message}");
        }
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

// Auth group
var daikinAuthGroup = app.MapGroup("/auth/daikin").WithTags("Daikin Auth");
daikinAuthGroup.MapGet("/start", (DaikinOAuthService daikinOAuth, HttpContext c) => { try { var url = daikinOAuth.GetAuthorizationUrl(c); return Results.Json(new { url }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
daikinAuthGroup.MapGet("/start-min", (DaikinOAuthService daikinOAuth, HttpContext c) => { try { var url = daikinOAuth.GetMinimalAuthorizationUrl(c); return Results.Json(new { url }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
daikinAuthGroup.MapGet("/callback", async (DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c, string? code, string? state) =>
{
    var userId = GetUserId(c);
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        return Results.BadRequest(new { error = "Missing code/state" });
    var result = await daikinOAuth.HandleCallbackWithSubjectAsync(code, state, userId);
    var ok = result.Success;
    // If we got a stable OIDC subject, remap the user to a deterministic userId
    if (ok && !string.IsNullOrEmpty(result.Subject))
    {
        var stableUserId = DaikinOAuthService.DeriveUserId(result.Subject);
        if (userId != stableUserId)
        {
            // Migrate data from the old browser-random userId to the deterministic one.
            if (!string.IsNullOrEmpty(userId))
                await daikinOAuth.MigrateUserDataAsync(userId, stableUserId);
            // Update the cookie to the deterministic userId
            c.Response.Cookies.Append(
                UserCookieName,
                stableUserId,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = c.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    IsEssential = true,
                    Expires = DateTimeOffset.UtcNow.AddYears(1)
                });
            Console.WriteLine($"[DaikinOAuth][Callback] Remapped userId={userId} -> {stableUserId}");
        }
    }
    // Secure redirect handling to avoid open redirect vulnerabilities.
    var configured = cfg["Daikin:PostAuthRedirect"];
    string finalBase;
    if (string.IsNullOrWhiteSpace(configured))
    {
        finalBase = "/"; // fallback
    }
    else if (configured.StartsWith('/'))
    {
        // Relative path within this application. Disallow protocol-relative '//' by forcing single leading slash.
        finalBase = configured.StartsWith("//") ? "/" : configured;
    }
    else if (Uri.TryCreate(configured, UriKind.Absolute, out var abs))
    {
        // Allow only https and hosts in optional allowlist
        var allowedHostsCfg = cfg["Daikin:AllowedRedirectHosts"] ?? string.Empty; // comma-separated
        var allowedHosts = allowedHostsCfg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Select(h => h.ToLowerInvariant())
                                          .ToHashSet();
        var hostOk = allowedHosts.Count == 0 ? true : allowedHosts.Contains(abs.Host.ToLowerInvariant());
        if (abs.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && hostOk)
        {
            finalBase = abs.GetLeftPart(UriPartial.Path); // drop any existing query to control params we add
        }
        else
        {
            finalBase = "/"; // unsafe absolute -> fallback
        }
    }
    else
    {
        finalBase = "/"; // invalid format
    }
    // Helper: append/replace daikinAuth param safely (supports relative URLs)
    static string AddOrReplaceQueryParam(string url, string key, string value)
    {
        url = url.TrimEnd('?', '&');
        var qIndex = url.IndexOf('?');
        if (qIndex < 0)
        {
            return QueryHelpers.AddQueryString(url, key, value);
        }
        var basePart = url.Substring(0, qIndex);
        var queryPart = url.Substring(qIndex + 1);
        var parsed = QueryHelpers.ParseQuery(queryPart);
        // Rebuild without existing key (case-insensitive)
        var rebuilt = basePart;
        foreach (var kv in parsed)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            // Preserve all values for duplicate query parameters
            foreach (var val in kv.Value)
            {
                rebuilt = QueryHelpers.AddQueryString(rebuilt, kv.Key, val ?? string.Empty);
            }
        }
        rebuilt = QueryHelpers.AddQueryString(rebuilt, key, value);
        return rebuilt;
    }
    var dest = AddOrReplaceQueryParam(finalBase, "daikinAuth", ok ? "ok" : "fail");
    Console.WriteLine($"[DaikinOAuth][Callback] Redirecting userId={userId} success={ok} to={dest}");
    return Results.Redirect(dest, false);
});
daikinAuthGroup.MapGet("/status", async (DaikinOAuthService daikinOAuth, HttpContext c) => {
    var userId = GetUserId(c);
    var raw = await daikinOAuth.StatusAsync(userId); // anonymous object { authorized, expiresAtUtc, ... }
    try
    {
        // Use reflection to read properties safely
        var t = raw.GetType();
        var authProp = t.GetProperty("authorized");
        var expProp = t.GetProperty("expiresAtUtc");
        var authorized = authProp?.GetValue(raw) as bool?;
        var expiresAt = expProp?.GetValue(raw) as DateTimeOffset?;
        if (authorized == true && expiresAt != null && expiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            await daikinOAuth.RefreshIfNeededAsync(userId, TimeSpan.FromMinutes(5));
            raw = await daikinOAuth.StatusAsync(userId);
        }
    }
    catch { }
    return Results.Json(raw);
});
daikinAuthGroup.MapPost("/refresh", async (DaikinOAuthService daikinOAuth, HttpContext c) => { var userId = GetUserId(c); var token = await daikinOAuth.RefreshIfNeededAsync(userId); return token == null ? Results.BadRequest(new { error = "Refresh failed or not authorized" }) : Results.Ok(new { refreshed = true }); });
daikinAuthGroup.MapGet("/debug", async (DaikinOAuthService daikinOAuth, HttpContext c) => { var userId = GetUserId(c); return Results.Json(new { status = await daikinOAuth.StatusAsync(userId), userId, now = DateTimeOffset.UtcNow }); });
daikinAuthGroup.MapPost("/revoke", async (DaikinOAuthService daikinOAuth, HttpContext c) => { var userId = GetUserId(c); var ok = await daikinOAuth.RevokeAsync(userId); return ok ? Results.Ok(new { revoked = true }) : Results.BadRequest(new { error = "Revoke failed" }); });
daikinAuthGroup.MapGet("/introspect", async (DaikinOAuthService daikinOAuth, HttpContext c, bool refresh) => { var userId = GetUserId(c); var result = await daikinOAuth.IntrospectAsync(userId, refresh); return result == null ? Results.BadRequest(new { error = "Not authorized" }) : Results.Json(result); });

// Schedule preview/apply
var scheduleGroup = app.MapGroup("/api/schedule").WithTags("Schedule");
scheduleGroup.MapGet("/preview", async (HttpContext c, UserSettingsRepository settingsRepo, BatchRunner batchRunner, IServiceScopeFactory scopeFactory) => {
    var cfg = (IConfiguration)builder.Configuration;
    var userId = GetUserId(c);
    
    // Preview should NOT persist to history - only apply should persist
    var (generated, schedulePayload, message) = await batchRunner.RunBatchAsync(cfg, userId, applySchedule: false, persist: false, scopeFactory);
    var zone = await settingsRepo.GetUserZoneAsync(userId);
    
    return Results.Json(new { schedulePayload, generated, message, zone });
});
scheduleGroup.MapPost("/apply", async (BatchRunner batchRunner, HttpContext ctx, IServiceScopeFactory scopeFactory) => await HandleApplyScheduleAsync(batchRunner, ctx, builder.Configuration, scopeFactory));

// Admin group
var adminGroup = app.MapGroup("/api/admin").WithTags("Admin");

bool IsAdminRequest(HttpContext ctx, IConfiguration cfg)
{
    var userId = GetUserId(ctx);
    var password = ctx.Request.Headers["X-Admin-Password"].FirstOrDefault();
    var (isAdmin, _) = AdminService.CheckAdminAccess(cfg, userId, password);
    return isAdmin;
}

static bool IsValidUserId(string? userId)
{
    if (string.IsNullOrWhiteSpace(userId) || userId.Length > 100)
        return false;
    return userId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}

adminGroup.MapGet("/status", (IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var isAdmin = IsAdminRequest(c, cfg);
    return Results.Json(new { isAdmin, userId });
});

adminGroup.MapPost("/login", async (IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var configuredPassword = cfg["Admin:Password"];
    if (string.IsNullOrEmpty(configuredPassword))
        return Results.Json(new { error = "No admin password configured" }, statusCode: 403);

    var password = c.Request.Headers["X-Admin-Password"].FirstOrDefault();
    if (string.IsNullOrEmpty(password) || password != configuredPassword)
        return Results.Json(new { error = "Invalid admin password" }, statusCode: 401);

    if (!string.IsNullOrEmpty(userId))
        await AdminService.GrantAdmin(cfg, userId);

    return Results.Json(new { granted = true, userId });
});

adminGroup.MapGet("/users", async (IConfiguration cfg, HttpContext c, UserSettingsRepository settingsRepo, DaikinTokenRepository tokenRepo, ScheduleHistoryRepository historyRepo) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    var currentUserId = GetUserId(c);

    // Get all known user IDs from DB (tokens + settings + history)
    var tokenUserIds = await tokenRepo.GetAllUserIdsAsync();
    var userIds = new HashSet<string>(tokenUserIds);

    var adminUserIds = AdminService.GetAdminUserIds(cfg);
    var hangfireUserIds = AdminService.GetHangfireUserIds(cfg);
    var users = new List<object>();

    foreach (var uid in userIds)
    {
        var settings = settingsRepo.LoadScheduleSettings(uid);
        var zone = settingsRepo.GetUserZone(uid);
        var token = await tokenRepo.LoadAsync(uid);
        var daikinAuthorized = token != null;
        string? daikinExpiresAtUtc = token?.ExpiresAtUtc.ToString("o");
        string? daikinSubject = token?.DaikinSubject;

        var historyCount = await historyRepo.CountAsync(uid);
        var hasScheduleHistory = historyCount > 0;
        int? scheduleCount = hasScheduleHistory ? historyCount : null;
        string? lastScheduleDate = null;
        if (hasScheduleHistory)
        {
            var entries = await historyRepo.LoadAsync(uid);
            lastScheduleDate = entries.FirstOrDefault()?.Timestamp.ToString("o");
        }

        users.Add(new
        {
            userId = uid,
            settings = new { settings.ComfortHours, settings.TurnOffPercentile, settings.MaxComfortGapHours },
            zone,
            daikinAuthorized,
            daikinExpiresAtUtc,
            daikinSubject,
            hasScheduleHistory,
            scheduleCount,
            lastScheduleDate,
            isAdmin = adminUserIds.Contains(uid),
            hasHangfireAccess = hangfireUserIds.Contains(uid),
            isCurrentUser = uid == currentUserId
        });
    }

    return Results.Json(new { users });
});

adminGroup.MapPost("/users/{userId}/grant", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    await AdminService.GrantAdmin(cfg, userId);
    return Results.Json(new { granted = true, userId });
});

adminGroup.MapDelete("/users/{userId}/grant", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    var currentUserId = GetUserId(c);
    if (userId == currentUserId)
        return Results.Json(new { error = "Cannot revoke your own admin access" }, statusCode: 400);

    await AdminService.RevokeAdmin(cfg, userId);
    return Results.Json(new { revoked = true, userId });
});

adminGroup.MapPost("/users/{userId}/hangfire", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    await AdminService.GrantHangfireAccess(cfg, userId);
    return Results.Json(new { granted = true, userId });
});

adminGroup.MapDelete("/users/{userId}/hangfire", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    await AdminService.RevokeHangfireAccess(cfg, userId);
    return Results.Json(new { revoked = true, userId });
});

adminGroup.MapDelete("/users/{userId}", async (IConfiguration cfg, HttpContext c, string userId, DaikinTokenRepository tokenRepo, ScheduleHistoryRepository historyRepo) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    var currentUserId = GetUserId(c);
    if (userId == currentUserId)
        return Results.Json(new { error = "Cannot delete your own user" }, statusCode: 400);

    var deleted = false;
    var warnings = new List<string>();

    // Delete tokens from database
    try
    {
        await tokenRepo.DeleteAsync(userId);
        deleted = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] Failed to delete tokens for user {userId}: {ex.Message}");
        warnings.Add("Failed to delete tokens");
    }

    // Delete schedule history from database
    try
    {
        var historyDeleted = await historyRepo.DeleteAllOlderThanAsync(DateTimeOffset.MinValue);
        deleted = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] Failed to delete schedule history for user {userId}: {ex.Message}");
        warnings.Add("Failed to delete schedule history");
    }

    // Remove from admin.json if present
    try
    {
        await AdminService.RevokeAdmin(cfg, userId);
        await AdminService.RevokeHangfireAccess(cfg, userId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] Failed to update admin.json for user {userId}: {ex.Message}");
        warnings.Add("Failed to update admin configuration");
    }

    if (!deleted)
        return Results.Json(new { error = "User not found" }, statusCode: 404);

    return Results.Json(new { deleted = true, userId, warnings });
});

// Daikin data group
var daikinGroup = app.MapGroup("/api/daikin").WithTags("Daikin");
// Simple proxy for sites (needed by frontend Sites button) – user-scoped
// Extracted method for /apply endpoint logic
async Task<IResult> HandleApplyScheduleAsync(BatchRunner batchRunner, HttpContext ctx, IConfiguration configuration, IServiceScopeFactory scopeFactory)
{
    var userId = GetUserId(ctx);
    var result = await batchRunner.RunBatchAsync(configuration, userId, applySchedule: false, persist: true, scopeFactory);
    return Results.Json(new { generated = result.generated, schedulePayload = result.schedulePayload, message = result.message });
}
daikinGroup.MapGet("/sites", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var sitesJson = await client.GetSitesAsync();
        return Results.Content(sitesJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
// Simplified current schedule via gateway-devices
daikinGroup.MapGet("/gateway/schedule", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext ctx) =>
{
    var deviceId = ctx.Request.Query["deviceId"].FirstOrDefault();
    var embeddedIdQuery = ctx.Request.Query["embeddedId"].FirstOrDefault();
    Console.WriteLine($"[GatewaySchedule] start deviceId={deviceId} embeddedIdQuery={embeddedIdQuery}");
    var userId = GetUserId(ctx);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.Json(new { status="unauthorized", error="Not authorized" });
    try
    {
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log:true, baseApiOverride:baseApi);
    var json = await client.GetDevicesCachedAsync("_ignored", TimeSpan.FromSeconds(10));
        if (string.IsNullOrWhiteSpace(json)) return Results.Json(new { status="error", error="Empty gateway-devices" });
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Results.Json(new { status="error", error="Unexpected root" });
        JsonElement? dev = null;
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            if (deviceId == null) { dev = d; break; }
            if (d.TryGetProperty("id", out var idEl) && idEl.GetString() == deviceId) { dev = d; break; }
        }
        if (dev == null) return Results.Json(new { status="error", error="Device not found", requestedDeviceId=deviceId });

        // Helper to extract schedule container and metadata from a schedule node (supports DHW schedule.value nesting)
        (bool ok, JsonElement container, string? detectedMode, string? currentScheduleId) Extract(JsonElement scheduleNode)
        {
            string? curId = null; string? mode = null; JsonElement container = default;
            if (scheduleNode.ValueKind != JsonValueKind.Object) return (false, container, null, null);
            // If node has a 'value' object (DHW often wraps data) prefer descending once
            if (scheduleNode.TryGetProperty("value", out var valueNode) && valueNode.ValueKind==JsonValueKind.Object)
            {
                scheduleNode = valueNode;
            }
            // Primary: modes collection
            if (scheduleNode.TryGetProperty("modes", out var modesRoot) && modesRoot.ValueKind==JsonValueKind.Object)
            {
                foreach (var mProp in modesRoot.EnumerateObject())
                {
                    var mVal = mProp.Value;
                    if (mode==null && mVal.TryGetProperty("schedules", out var schTest) && schTest.ValueKind==JsonValueKind.Object)
                    { mode = mProp.Name; container = schTest; }
                    if (mVal.TryGetProperty("currentSchedule", out var curObj) && curObj.TryGetProperty("value", out var curValEl))
                    { curId = curValEl.GetString(); }
                }
                if (mode!=null) return (true, container, mode, curId);
            }
            // Direct schedules property
            if (scheduleNode.TryGetProperty("schedules", out var direct) && direct.ValueKind==JsonValueKind.Object)
            {
                return (true, direct, mode ?? "heating", curId);
            }
            // Heuristic: some DHW variants expose scheduleNode.{waterHeating|domesticHotWaterHeating|dhw}.schedules
            foreach (var prop in scheduleNode.EnumerateObject())
            {
                var pVal = prop.Value;
                if (pVal.ValueKind==JsonValueKind.Object && pVal.TryGetProperty("schedules", out var scheds) && scheds.ValueKind==JsonValueKind.Object)
                {
                    // detect currentSchedule if present
                    if (pVal.TryGetProperty("currentSchedule", out var curObj) && curObj.TryGetProperty("value", out var curValEl))
                        curId = curValEl.GetString();
                    mode = prop.Name;
                    return (true, scheds, mode, curId);
                }
            }
            return (false, container, null, null);
        }

        string? embeddedId=null; string? mpType=null; JsonElement schedulesContainer=default; string? detectedMode=null; string? currentScheduleId=null; List<string> candidateEmbeddedIds=new();
        if (dev.Value.TryGetProperty("managementPoints", out var mps) && mps.ValueKind==JsonValueKind.Array)
        {
            var mpList = mps.EnumerateArray().ToList();
            foreach (var mp in mpList)
            {
                if (mp.TryGetProperty("embeddedId", out var embElAll)) { var v=embElAll.GetString(); if (v!=null) candidateEmbeddedIds.Add(v); }
            }
            Func<IEnumerable<JsonElement>, (string? emb,string? type, JsonElement container,string? mode,string? cur)> tryPick = (source) =>
            {
                foreach (var mp in source)
                {
                    if (!mp.TryGetProperty("embeddedId", out var embEl)) continue; var embVal = embEl.GetString();
                    if (embeddedIdQuery!=null && embVal != embeddedIdQuery) continue;
                    if (!mp.TryGetProperty("managementPointType", out var typeEl)) continue; var typeStr = typeEl.GetString();
                    if (!mp.TryGetProperty("schedule", out var scheduleNode)) continue;
                    var ex = Extract(scheduleNode);
                    if (!ex.ok) continue;
                    return (embVal, typeStr, ex.container, ex.detectedMode, ex.currentScheduleId);
                }
                return (null,null,default(JsonElement),null,null);
            };
            // Priority order: requested embeddedId -> domesticHotWaterTank -> climateControl -> anything with schedule
            (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList.Where(mp=>mp.TryGetProperty("managementPointType", out var t1) && t1.GetString()=="domesticHotWaterTank"));
            if (embeddedId==null)
                (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList.Where(mp=>mp.TryGetProperty("managementPointType", out var t1) && t1.GetString()=="climateControl"));
            if (embeddedId==null)
                (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList);
            // If user explicitly requested embeddedId but we picked different, try forcing exact
            if (embeddedIdQuery!=null && embeddedId!=embeddedIdQuery)
            {
                (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList.Where(mp=> mp.TryGetProperty("embeddedId", out var e2) && e2.GetString()==embeddedIdQuery));
            }
        }
        if (embeddedId==null)
        {
            return Results.Json(new { status="error", error="No schedule", requestedEmbeddedId=embeddedIdQuery, candidateEmbeddedIds });
        }
        if (schedulesContainer.ValueKind!=JsonValueKind.Object)
        {
            // Include raw schedule node (first few chars) for debugging if present
            string? scheduleRaw = null;
            try
            {
                if (dev.Value.TryGetProperty("managementPoints", out var mps2) && mps2.ValueKind==JsonValueKind.Array)
                {
                    foreach (var mp in mps2.EnumerateArray())
                    {
                        if (mp.TryGetProperty("embeddedId", out var eId) && eId.GetString()==(embeddedIdQuery??embeddedId))
                        {
                            if (mp.TryGetProperty("schedule", out var sNode)) scheduleRaw = sNode.GetRawText();
                            break;
                        }
                    }
                }
            } catch {}
            if (scheduleRaw!=null && scheduleRaw.Length > MaxScheduleRawDisplayLength) 
                scheduleRaw = scheduleRaw.Substring(0, MaxScheduleRawDisplayLength)+"...";
            return Results.Json(new { status="error", error="No schedules container", embeddedId, requestedEmbeddedId=embeddedIdQuery, candidateEmbeddedIds, scheduleRaw });
        }
        string? chosen = currentScheduleId; Dictionary<string, JsonElement> dict=new();
        foreach (var p in schedulesContainer.EnumerateObject()) { dict[p.Name]=p.Value; if (chosen==null) chosen=p.Name; }
        JsonObject? payload=null;
        if (chosen!=null && dict.TryGetValue(chosen, out var sch) && sch.TryGetProperty("actions", out var acts))
        {
            var root=new JsonObject(); var sObj=new JsonObject(); sObj["actions"]=JsonNode.Parse(acts.GetRawText()); root[chosen]=sObj; payload=root;
        }
        var id = dev.Value.TryGetProperty("id", out var idEl2)? idEl2.GetString():null;
        Console.WriteLine($"[GatewaySchedule] ok deviceId={id} embeddedId={embeddedId} chosen={chosen} detectedMode={detectedMode}");
        return Results.Json(new { status=payload==null?"warning":"ok", deviceId=id, embeddedId, mpType, currentScheduleId, chosenScheduleId=chosen, schedulePayload=payload, schedules=dict.Keys, detectedMode, requestedEmbeddedId=embeddedIdQuery, candidateEmbeddedIds });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GatewaySchedule][Exception] {ex.GetType().Name} {ex.Message}");
        return Results.Json(new { status="error", error=ex.Message });
    }
});
daikinGroup.MapGet("/devices", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c, string? siteId) =>
{
    var userId = GetUserId(c);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
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
// Simplified gateway devices proxy: return raw array from Daikin
daikinGroup.MapGet("/gateway", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var devicesJson = await client.GetDevicesAsync("_ignored");
        return Results.Content(devicesJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});


// PUT (upload) a schedule payload to a gateway device management point + optionally activate a scheduleId (mode auto-detect if omitted or 'auto')
daikinGroup.MapPost("/gateway/schedule/put", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext ctx, ScheduleHistoryRepository historyRepo) =>
{
    var userId = GetUserId(ctx);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        // Parse JSON body
        JsonNode? body = await JsonNode.ParseAsync(ctx.Request.Body);
        if (body == null) return Results.BadRequest(new { error = "Missing body" });
        string? gatewayDeviceId = body["gatewayDeviceId"]?.ToString();
        string? embeddedId = body["embeddedId"]?.ToString();
    string requestedMode = body["mode"]?.ToString() ?? "auto"; // 'auto' triggers detection
        JsonNode? schedulePayloadNode = body["schedulePayload"];
        string? activateScheduleId = body["activateScheduleId"]?.ToString();
        if (schedulePayloadNode == null)
            return Results.BadRequest(new { error = "schedulePayload is required" });

        // Serialize schedule payload exactly as provided
        var schedulePayloadJson = schedulePayloadNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);

        // Auto-detect device IDs if not provided
        string? siteId = null;
        if (string.IsNullOrWhiteSpace(gatewayDeviceId) || string.IsNullOrWhiteSpace(embeddedId))
        {
            // Check for config overrides
            var overrideSite = cfg["Daikin:SiteId"];
            var overrideDevice = cfg["Daikin:DeviceId"];
            var overrideEmbedded = cfg["Daikin:ManagementPointEmbeddedId"];

            string? detectedSite = null;
            string? detectedDevice = null;
            string? detectedEmbedded = null;

            // Detect site
            if (!string.IsNullOrWhiteSpace(overrideSite))
                detectedSite = overrideSite;
            else
            {
                var sitesJson = await client.GetSitesAsync();
                detectedSite = DeviceAutoDetection.GetFirstSiteId(sitesJson);
                if (detectedSite != null)
                    Console.WriteLine($"[SchedulePut] Auto-detected site: {detectedSite}");
            }

            if (detectedSite == null)
                return Results.BadRequest(new { error = "Could not auto-detect site. No Daikin sites found." });

            siteId = detectedSite;

            // Detect device
            if (!string.IsNullOrWhiteSpace(overrideDevice))
                detectedDevice = overrideDevice;
            else
            {
                var devicesJson = await client.GetDevicesAsync(detectedSite);
                var (deviceId, deviceJsonRaw) = DeviceAutoDetection.GetFirstDevice(devicesJson);
                detectedDevice = deviceId;
                
                // Also detect embedded ID from the device
                if (!string.IsNullOrWhiteSpace(overrideEmbedded))
                    detectedEmbedded = overrideEmbedded;
                else if (deviceJsonRaw != null)
                {
                    detectedEmbedded = DeviceAutoDetection.FindDhwEmbeddedId(deviceJsonRaw);
                    if (detectedEmbedded != null)
                        Console.WriteLine($"[SchedulePut] Auto-detected DHW embeddedId: {detectedEmbedded}");
                }
                
                if (detectedDevice != null)
                    Console.WriteLine($"[SchedulePut] Auto-detected device: {detectedDevice}");
            }

            if (detectedDevice == null)
                return Results.BadRequest(new { error = "Could not auto-detect device. No Daikin devices found." });

            if (detectedEmbedded == null)
                return Results.BadRequest(new { error = "Could not auto-detect DHW management point. No domesticHotWaterTank found on device." });

            // Use detected values if not provided in request
            gatewayDeviceId ??= detectedDevice;
            embeddedId ??= detectedEmbedded;
        }

        string modeUsed = requestedMode;
        if (modeUsed == "auto" || string.IsNullOrWhiteSpace(modeUsed))
        {
            // Fetch devices to detect mode (need site ID)
            if (siteId == null)
            {
                // If we didn't auto-detect above, we need to get the site
                var overrideSite = cfg["Daikin:SiteId"];
                if (!string.IsNullOrWhiteSpace(overrideSite))
                    siteId = overrideSite;
                else
                {
                    var sitesJson = await client.GetSitesAsync();
                    using var siteDoc = JsonDocument.Parse(sitesJson);
                    if (siteDoc.RootElement.ValueKind == JsonValueKind.Array && siteDoc.RootElement.GetArrayLength() > 0)
                    {
                        siteId = siteDoc.RootElement[0].GetProperty("id").GetString();
                    }
                }
            }

            try
            {
                var devicesJson = siteId != null ? await client.GetDevicesAsync(siteId) : "[]";
                using var doc = JsonDocument.Parse(devicesJson);
                if (doc.RootElement.ValueKind==JsonValueKind.Array)
                {
                    foreach (var d in doc.RootElement.EnumerateArray())
                    {
                        if (d.TryGetProperty("id", out var idEl) && idEl.GetString()==gatewayDeviceId)
                        {
                            if (d.TryGetProperty("managementPoints", out var mps) && mps.ValueKind==JsonValueKind.Array)
                            {
                                foreach (var mp in mps.EnumerateArray())
                                {
                                    if (mp.TryGetProperty("embeddedId", out var emb2) && emb2.GetString()==embeddedId)
                                    {
                                        if (mp.TryGetProperty("schedule", out var schNode))
                                        {
                                            if (schNode.TryGetProperty("modes", out var modesNode) && modesNode.ValueKind==JsonValueKind.Object)
                                            {
                                                // prefer heating, waterHeating, cooling order
                                                string[] pref = new[]{"heating","waterHeating","cooling","dhw","domesticHotWaterHeating"};
                                                var available = modesNode.EnumerateObject().Select(o=>o.Name).ToList();
                                                var picked = pref.FirstOrDefault(p=>available.Contains(p)) ?? available.FirstOrDefault();
                                                if (picked!=null) modeUsed = picked; else modeUsed = "heating";
                                            }
                                            else if (schNode.TryGetProperty("schedules", out _))
                                            {
                                                modeUsed = "heating"; // generic
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exDetect)
            {
                Console.WriteLine($"[SchedulePut] mode auto-detect failed: {exDetect.Message}");
                modeUsed = "heating"; // fallback
            }
        }

        await client.PutSchedulesAsync(gatewayDeviceId, embeddedId, modeUsed, schedulePayloadJson);
    
    // Save to schedule history
    if (schedulePayloadNode is JsonObject scheduleObj && !string.IsNullOrWhiteSpace(userId))
    {
        try
        {
            await historyRepo.SaveAsync(userId, scheduleObj, DateTimeOffset.UtcNow);
            Console.WriteLine($"[SchedulePut] Saved schedule to history for user {userId}");
        }
        catch (Exception exHist)
        {
            Console.WriteLine($"[SchedulePut] Failed to save history for user {userId}: {exHist.Message}");
        }
    }
    
    // Activation step removed: only PUT schedule, do not activate
    return Results.Ok(new { put = true, activateScheduleId, modeUsed, requestedMode });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// SPA fallback: serve index.html for client-side routes (excluding /api and /auth)
app.MapFallback(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path.Value ?? "";
    
    // Don't intercept API or auth endpoints
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || 
        path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Not Found");
        return;
    }
    
    // Serve index.html for SPA routes like /settings, /history, etc.
    var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(indexPath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Frontend not built. Run: cd frontend && npm run build");
    }
});

await app.RunAsync();

// Hangfire dashboard authorization filter with password protection
public class HangfirePasswordAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly string? _password;
    private readonly IConfiguration _cfg;

    public HangfirePasswordAuthorizationFilter(string? password, IConfiguration cfg)
    {
        _password = password;
        _cfg = cfg;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        // Check 1: Cookie-based access via admin.json hangfireUserIds
        // Note: must match UserCookieName constant in top-level statements
        var userId = httpContext.Request.Cookies["ps_user"];
        // Validate cookie value using shared validation logic
        if (AdminService.IsValidUserId(userId))
        {
            if (AdminService.HasHangfireAccess(_cfg, userId))
                return true;

            // Check 2: Also allow admins
            if (AdminService.IsAdmin(_cfg, userId))
                return true;
        }

        // Check 3: Original Basic Auth password check
        if (string.IsNullOrWhiteSpace(_password))
        {
            httpContext.Response.StatusCode = 401;
            httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
            return false;
        }

        var authHeader = httpContext.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.StatusCode = 401;
            httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
            return false;
        }

        try
        {
            var encodedCredentials = authHeader.Substring(6).Trim();
            var decodedCredentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
            var parts = decodedCredentials.Split(':', 2);

            if (parts.Length == 2)
            {
                var providedPassword = parts[1];
                if (providedPassword == _password)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Invalid authorization header format
        }

        // Authentication failed
        httpContext.Response.StatusCode = 401;
        httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
        return false;
    }
}