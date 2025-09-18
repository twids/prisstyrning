
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;

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
builder.Services.AddHostedService<DailyPriceJob>();
builder.Services.AddHostedService<NordpoolPriceJob>();
builder.Services.AddHostedService<DaikinTokenRefreshService>();

var app = builder.Build();
// User settings endpoints
// Schedule history endpoint for frontend visualization
app.MapGet("/api/user/schedule-history", (HttpContext ctx) =>
{
    var userId = GetUserId(ctx);
    var history = ScheduleHistoryPersistence.Load(userId ?? "default", StoragePaths.GetBaseDir(builder.Configuration));
    var result = history.Select(e => new {
        timestamp = e?["timestamp"]?.ToString(),
        date = DateTimeOffset.TryParse(e?["timestamp"]?.ToString(), out var dt) ? dt.ToString("yyyy-MM-dd") : null,
        schedule = e?["schedule"]
    });
    return Results.Json(result);
});
app.MapGet("/api/user/settings", async (HttpContext ctx) =>
{
    var cfg = (IConfiguration)builder.Configuration;
    var userId = GetUserId(ctx) ?? "default";
    var path = StoragePaths.GetUserJsonPath(cfg, userId);
    if (!File.Exists(path)) return Results.Json(new { ComfortHours = 3, TurnOffPercentile = 0.9, TurnOffMaxConsecutive = 2, AutoApplySchedule = false });
    try
    {
        var json = await File.ReadAllTextAsync(path);
        var node = JsonNode.Parse(json) as JsonObject;
        int comfortHours = int.TryParse(node?["ComfortHours"]?.ToString(), out var ch) ? ch : 3;
        double turnOffPercentile = double.TryParse(node?["TurnOffPercentile"]?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tp) ? tp : 0.9;
        int turnOffMaxConsecutive = int.TryParse(node?["TurnOffMaxConsecutive"]?.ToString(), out var tmc) ? tmc : 2;
        bool autoApplySchedule = bool.TryParse(node?["AutoApplySchedule"]?.ToString(), out var aas) ? aas : false;
        return Results.Json(new { ComfortHours = comfortHours, TurnOffPercentile = turnOffPercentile, TurnOffMaxConsecutive = turnOffMaxConsecutive, AutoApplySchedule = autoApplySchedule });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[UserSettings] read error: {ex.Message}");
        return Results.Json(new { ComfortHours = 3, TurnOffPercentile = 0.9, TurnOffMaxConsecutive = 2, AutoApplySchedule = false });
    }
});

app.MapPost("/api/user/settings", async (HttpContext ctx) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var body = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject;
    if (body == null) return Results.BadRequest(new { error = "Missing body" });
    var cfg = (IConfiguration)builder.Configuration;
    var path = StoragePaths.GetUserJsonPath(cfg, userId);
    JsonObject node;
    if (File.Exists(path))
    {
        try { var jsonExisting = await File.ReadAllTextAsync(path); node = JsonNode.Parse(jsonExisting) as JsonObject ?? new JsonObject(); }
        catch { node = new JsonObject(); }
    }
    else node = new JsonObject();
    string? rawCh = body["ComfortHours"]?.ToString();
    string? rawTp = body["TurnOffPercentile"]?.ToString();
    string? rawTmc = body["TurnOffMaxConsecutive"]?.ToString();
    string? rawAas = body["AutoApplySchedule"]?.ToString();
    var errors = new List<string>();
    int comfortHours = 3;
    if (!string.IsNullOrWhiteSpace(rawCh))
    { if (!int.TryParse(rawCh, out comfortHours) || comfortHours < 1 || comfortHours > 12) { errors.Add("ComfortHours must be an integer between 1 and 12"); comfortHours = 3; } }
    int turnOffMaxConsecutive = 2;
    if (!string.IsNullOrWhiteSpace(rawTmc))
    { if (!int.TryParse(rawTmc, out turnOffMaxConsecutive) || turnOffMaxConsecutive < 1 || turnOffMaxConsecutive > 6) { errors.Add("TurnOffMaxConsecutive must be an integer between 1 and 6"); turnOffMaxConsecutive = 2; } }
    double turnOffPercentile = 0.9;
    if (!string.IsNullOrWhiteSpace(rawTp))
    { if (!double.TryParse(rawTp, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out turnOffPercentile) || turnOffPercentile < 0.5 || turnOffPercentile > 0.99) { errors.Add("TurnOffPercentile must be a number between 0.5 and 0.99"); turnOffPercentile = 0.9; } }
    bool autoApplySchedule = false;
    if (!string.IsNullOrWhiteSpace(rawAas))
    { if (!bool.TryParse(rawAas, out autoApplySchedule)) { errors.Add("AutoApplySchedule must be true or false"); autoApplySchedule = false; } }
    if (errors.Count > 0) return Results.BadRequest(new { error = "Validation failed", errors });
    node["ComfortHours"] = comfortHours;
    node["TurnOffPercentile"] = turnOffPercentile;
    node["TurnOffMaxConsecutive"] = turnOffMaxConsecutive;
    node["AutoApplySchedule"] = autoApplySchedule;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return Results.Ok(new { saved = true });
});

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
                todayArr = JsonSerializer.Deserialize<JsonArray>(tEl.GetRawText());
            }
            if (root.TryGetProperty("tomorrow", out var tmEl) && tmEl.ValueKind == JsonValueKind.Array)
            {
                tomorrowArr = JsonSerializer.Deserialize<JsonArray>(tmEl.GetRawText());
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
app.MapGet("/api/prices/_debug/fetch", async (HttpContext ctx, IConfiguration cfg) =>
{
    var userId = GetUserId(ctx);
    var zone = UserSettingsService.GetUserZone(cfg, userId) ?? "SE3";
    var dateStr = ctx.Request.Query["date"].FirstOrDefault();
    DateTime date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Today;
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var pageId = cfg["Price:Nordpool:PageId"];
    var client = new NordpoolClient(currency, pageId);
    var (prices, attempts) = await client.GetDailyPricesDetailedAsync(date, zone);
    return Results.Json(new { date = date.ToString("yyyy-MM-dd"), zone, priceCount = prices.Count, prices, attempts, currency, pageId, userId });
});
app.MapGet("/api/prices/_debug/raw", (HttpContext ctx, IConfiguration cfg) =>
{
    var dateStr = ctx.Request.Query["date"].FirstOrDefault();
    DateTime date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Today;
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var pageId = cfg["Price:Nordpool:PageId"];
    var client = new NordpoolClient(currency, pageId);
    return client.GetRawCandidateResponsesAsync(date);
});
pricesGroup.MapGet("/memory", () =>
{
    var (today, tomorrow, updated) = PriceMemory.Get();
    if (today == null && tomorrow == null) return Results.NotFound(new { message = "No prices in memory yet" });
    return Results.Json(new { updated, today, tomorrow });
});
// Per-user zone get/set
pricesGroup.MapGet("/zone", (HttpContext c, IConfiguration cfg) => {
    var userId = GetUserId(c); var zone = UserSettingsService.GetUserZone(cfg, userId); return Results.Json(new { zone });
});
pricesGroup.MapPost("/zone", async (HttpContext c, IConfiguration cfg) => {
    try {
        using var doc = await JsonDocument.ParseAsync(c.Request.Body);
        if (!doc.RootElement.TryGetProperty("zone", out var zEl)) return Results.BadRequest(new { error = "Missing zone" });
        var zone = zEl.GetString();
        if (!UserSettingsService.IsValidZone(zone)) return Results.BadRequest(new { error = "Invalid zone" });
        var userId = GetUserId(c);
        // Persist user zone inside user.json (merge/update existing)
        var userJsonPath = StoragePaths.GetUserJsonPath(builder.Configuration, userId ?? "");
        Directory.CreateDirectory(Path.GetDirectoryName(userJsonPath)!);
        JsonObject userNode;
        if (File.Exists(userJsonPath))
        {
            try { userNode = JsonNode.Parse(await File.ReadAllTextAsync(userJsonPath)) as JsonObject ?? new JsonObject(); }
            catch { userNode = new JsonObject(); }
        }
        else userNode = new JsonObject();
        userNode["Zone"] = zone;
        await File.WriteAllTextAsync(userJsonPath, userNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Results.Ok(new { saved = true, zone });
    } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
// Get latest persisted Nordpool snapshot for zone
pricesGroup.MapGet("/nordpool/latest", (HttpContext c, IConfiguration cfg, string? zone) => {
    zone ??= UserSettingsService.GetUserZone(cfg, GetUserId(c));
    var file = NordpoolPersistence.GetLatestFile(zone, cfg["Storage:Directory"] ?? "data");
    if (file == null) return Results.NotFound(new { error = "No snapshot" });
    return Results.File(file, "application/json");
});
pricesGroup.MapGet("/timeseries", (HttpContext ctx) =>
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
            var dir = builder.Configuration["Storage:Directory"] ?? "data";
            if (Directory.Exists(dir))
            {
                var file = Directory.GetFiles(dir, "prices-*.json").OrderByDescending(f => f).FirstOrDefault();
                if (file != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (today == null && root.TryGetProperty("today", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
                    {
                        // Convert JsonElement directly to JsonArray without re-parsing
                        var todayNode = JsonSerializer.Deserialize<JsonArray>(tEl.GetRawText());
                        today = todayNode;
                    }
                    if (tomorrow == null && root.TryGetProperty("tomorrow", out var tmEl) && tmEl.ValueKind == JsonValueKind.Array)
                    {
                        // Convert JsonElement directly to JsonArray without re-parsing
                        var tomorrowNode = JsonSerializer.Deserialize<JsonArray>(tmEl.GetRawText());
                        tomorrow = tomorrowNode;
                    }
                }
            }
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Timeseries] Failed to read fallback price file: {ex.Message}");
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
daikinAuthGroup.MapGet("/start", (IConfiguration cfg, HttpContext c) => { try { var url = DaikinOAuthService.GetAuthorizationUrl(cfg, c); return Results.Json(new { url }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
daikinAuthGroup.MapGet("/start-min", (IConfiguration cfg, HttpContext c) => { try { var url = DaikinOAuthService.GetMinimalAuthorizationUrl(cfg, c); return Results.Json(new { url }); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
daikinAuthGroup.MapGet("/callback", async (IConfiguration cfg, HttpContext c, string? code, string? state) =>
{
    var userId = GetUserId(c);
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        return Results.BadRequest(new { error = "Missing code/state" });
    var ok = await DaikinOAuthService.HandleCallbackAsync(cfg, code, state, userId);
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
daikinAuthGroup.MapGet("/status", async (IConfiguration cfg, HttpContext c) => {
    var userId = GetUserId(c);
    var raw = DaikinOAuthService.Status(cfg, userId); // anonymous object { authorized, expiresAtUtc, ... }
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
            await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId, TimeSpan.FromMinutes(5));
            raw = DaikinOAuthService.Status(cfg, userId);
        }
    }
    catch { }
    return Results.Json(raw);
});
daikinAuthGroup.MapPost("/refresh", async (IConfiguration cfg, HttpContext c) => { var userId = GetUserId(c); var token = await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId); return token == null ? Results.BadRequest(new { error = "Refresh failed or not authorized" }) : Results.Ok(new { refreshed = true }); });
daikinAuthGroup.MapGet("/debug", (IConfiguration cfg, HttpContext c) => { var userId = GetUserId(c); return Results.Json(new { status = DaikinOAuthService.Status(cfg, userId), userId, now = DateTimeOffset.UtcNow }); });
daikinAuthGroup.MapPost("/revoke", async (IConfiguration cfg, HttpContext c) => { var userId = GetUserId(c); var ok = await DaikinOAuthService.RevokeAsync(cfg, userId); return ok ? Results.Ok(new { revoked = true }) : Results.BadRequest(new { error = "Revoke failed" }); });
daikinAuthGroup.MapGet("/introspect", async (IConfiguration cfg, HttpContext c, bool refresh) => { var userId = GetUserId(c); var result = await DaikinOAuthService.IntrospectAsync(cfg, userId, refresh); return result == null ? Results.BadRequest(new { error = "Not authorized" }) : Results.Json(result); });

// Schedule preview/apply
var scheduleGroup = app.MapGroup("/api/schedule").WithTags("Schedule");
scheduleGroup.MapGet("/preview", async (HttpContext c) => {
    var cfg = (IConfiguration)builder.Configuration;
    var userId = GetUserId(c);
    var zone = UserSettingsService.GetUserZone(cfg, userId);
    // Ensure we have prices (if memory empty try fetch)
    var (memToday, memTomorrow, _) = PriceMemory.GetReadOnly();
    JsonArray? today = memToday; JsonArray? tomorrow = memTomorrow;
    if (today == null || today.Count == 0)
    {
        try 
        { 
            var np = new NordpoolClient(cfg["Price:Nordpool:Currency"] ?? "SEK", cfg["Price:Nordpool:PageId"]); 
            var fetched = await np.GetTodayTomorrowAsync(zone); 
            today = fetched.today; 
            tomorrow = fetched.tomorrow; 
            PriceMemory.Set(today, tomorrow); 
            NordpoolPersistence.Save(zone, today, tomorrow, cfg["Storage:Directory"] ?? "data"); 
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Schedule] Failed to fetch prices for zone {zone}: {ex.Message}");
        }
    }
    // Läs per-user inställningar från user.json
    var comfortHours = cfg["Schedule:ComfortHours"];
    var turnOffPercentile = cfg["Schedule:TurnOffPercentile"];
    var turnOffMaxConsecutive = cfg["Schedule:TurnOffMaxConsecutive"];
    try {
        var userJsonPath = StoragePaths.GetUserJsonPath(builder.Configuration, userId ?? "default");
        if (System.IO.File.Exists(userJsonPath)) {
            var json = System.IO.File.ReadAllText(userJsonPath);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (node != null) {
                comfortHours = node["ComfortHours"]?.ToString() ?? comfortHours;
                turnOffPercentile = node["TurnOffPercentile"]?.ToString() ?? turnOffPercentile;
                turnOffMaxConsecutive = node["TurnOffMaxConsecutive"]?.ToString() ?? turnOffMaxConsecutive;
            }
        }
    } catch {}
    // Skapa en temporär config med per-user värden
    var configDict = new Dictionary<string, string?> {
        { "Schedule:ComfortHours", comfortHours },
        { "Schedule:TurnOffPercentile", turnOffPercentile },
        { "Schedule:TurnOffMaxConsecutive", turnOffMaxConsecutive }
    };
    var configUser = new ConfigurationBuilder().AddInMemoryCollection(configDict).AddConfiguration(cfg).Build();
    var userSettings = UserSettingsService.LoadScheduleSettings(cfg, userId);
    int activationLimit = int.TryParse(cfg["Schedule:MaxActivationsPerDay"], out var mpd) ? Math.Clamp(mpd, 1, 24) : 4;
    var (payload, msg) = ScheduleAlgorithm.Generate(
        today,
        tomorrow,
        userSettings.ComfortHours,
        userSettings.TurnOffPercentile,
        userSettings.TurnOffMaxConsecutive,
        activationLimit,
        configUser,
        null,
        ScheduleAlgorithm.LogicType.PerDayOriginal);
    return Results.Json(new { schedulePayload = payload, generated = payload != null, message = msg, zone });
});
scheduleGroup.MapPost("/apply", async (HttpContext ctx) => { 
    var userId = GetUserId(ctx); 
    var result = await BatchRunner.RunBatchAsync(builder.Configuration, userId, applySchedule:false, persist:true); 
    return Results.Json(new { generated = result.generated, schedulePayload = result.schedulePayload, message = result.message }); 
});

// Daikin data group
var daikinGroup = app.MapGroup("/api/daikin").WithTags("Daikin");
// Simple proxy for sites (needed by frontend Sites button) – user-scoped
daikinGroup.MapGet("/sites", async (IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg, userId);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var sitesJson = await client.GetSitesAsync();
        return Results.Content(sitesJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});
// Simplified current schedule via gateway-devices
daikinGroup.MapGet("/gateway/schedule", async (IConfiguration cfg, HttpContext ctx) =>
{
    var deviceId = ctx.Request.Query["deviceId"].FirstOrDefault();
    var embeddedIdQuery = ctx.Request.Query["embeddedId"].FirstOrDefault();
    Console.WriteLine($"[GatewaySchedule] start deviceId={deviceId} embeddedIdQuery={embeddedIdQuery}");
    var userId = GetUserId(ctx);
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg, userId);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId);
    if (token == null) return Results.Json(new { status="unauthorized", error="Not authorized" });
    try
    {
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(token, log:true, baseApiOverride:baseApi);
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
daikinGroup.MapGet("/devices", async (IConfiguration cfg, HttpContext c, string? siteId) =>
{
    var userId = GetUserId(c);
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg, userId);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
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
daikinGroup.MapGet("/gateway", async (IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg, userId);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var devicesJson = await client.GetDevicesAsync("_ignored");
        return Results.Content(devicesJson, "application/json");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});


// PUT (upload) a schedule payload to a gateway device management point + optionally activate a scheduleId (mode auto-detect if omitted or 'auto')
daikinGroup.MapPost("/gateway/schedule/put", async (IConfiguration cfg, HttpContext ctx) =>
{
    var userId = GetUserId(ctx);
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg, userId);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg, userId);
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
        if (string.IsNullOrWhiteSpace(gatewayDeviceId) || string.IsNullOrWhiteSpace(embeddedId) || schedulePayloadNode == null)
            return Results.BadRequest(new { error = "gatewayDeviceId, embeddedId och schedulePayload krävs" });

        // Serialize schedule payload exactly as provided
        var schedulePayloadJson = schedulePayloadNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);

        string modeUsed = requestedMode;
        if (modeUsed == "auto" || string.IsNullOrWhiteSpace(modeUsed))
        {
            // Fetch devices to detect mode
            try
            {
                var devicesJson = await client.GetDevicesAsync("_ignored");
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
    // Activation step removed: only PUT schedule, do not activate
    return Results.Ok(new { put = true, activateScheduleId, modeUsed, requestedMode });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Kör initial batch (persist + ev. schedule) utan att exponera svar
// Initial batch now only fetches/prerenders (no auto apply)
_ = Task.Run(async () => await BatchRunner.RunBatchAsync((IConfiguration)builder.Configuration, null, applySchedule:false, persist:true));

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
    _ = BatchRunner.RunBatchAsync(_cfg, null, applySchedule:false, persist:true);
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
                await BatchRunner.RunBatchAsync(_cfg, null, applySchedule:false, persist:true);
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