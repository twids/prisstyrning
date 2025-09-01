using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);
// Läser in en extra lokal override-fil (gemener) om den finns
builder.Configuration.AddJsonFile("appsettings.development.json", optional: true, reloadOnChange: true);
// Miljövariabler (tar över appsettings). Stöd både utan prefix och med prefix PRISSTYRNING_
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddEnvironmentVariables(prefix: "PRISSTYRNING_");
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
// Static files
app.UseDefaultFiles();
app.UseStaticFiles();

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
                        today = JsonNode.Parse(tEl.GetRawText()) as JsonArray;
                    if (tomorrow == null && root.TryGetProperty("tomorrow", out var tmEl) && tmEl.ValueKind == JsonValueKind.Array)
                        tomorrow = JsonNode.Parse(tmEl.GetRawText()) as JsonArray;
                }
            }
        }
        catch { }
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
daikinAuthGroup.MapGet("/callback", async (IConfiguration cfg, string? code, string? state) => { if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return Results.BadRequest(new { error = "Missing code/state" }); var ok = await DaikinOAuthService.HandleCallbackAsync(cfg, code, state); return ok ? Results.Ok(new { message = "Authorization stored" }) : Results.BadRequest(new { error = "Auth failed" }); });
daikinAuthGroup.MapGet("/status", (IConfiguration cfg) => Results.Json(DaikinOAuthService.Status(cfg)));
daikinAuthGroup.MapPost("/refresh", async (IConfiguration cfg) => { var token = await DaikinOAuthService.RefreshIfNeededAsync(cfg); return token == null ? Results.BadRequest(new { error = "Refresh failed or not authorized" }) : Results.Ok(new { refreshed = true }); });
daikinAuthGroup.MapGet("/debug", (IConfiguration cfg) => Results.Json(new { status = DaikinOAuthService.Status(cfg), now = DateTimeOffset.UtcNow }));
daikinAuthGroup.MapPost("/revoke", async (IConfiguration cfg) => { var ok = await DaikinOAuthService.RevokeAsync(cfg); return ok ? Results.Ok(new { revoked = true }) : Results.BadRequest(new { error = "Revoke failed" }); });
daikinAuthGroup.MapGet("/introspect", async (IConfiguration cfg, bool refresh) => { var result = await DaikinOAuthService.IntrospectAsync(cfg, refresh); return result == null ? Results.BadRequest(new { error = "Not authorized" }) : Results.Json(result); });

// Schedule preview/apply
var scheduleGroup = app.MapGroup("/api/schedule").WithTags("Schedule");
scheduleGroup.MapGet("/preview", async () => await BatchRunner.GenerateSchedulePreview((IConfiguration)builder.Configuration));
scheduleGroup.MapPost("/apply", async () => { var result = await BatchRunner.RunBatchAsync(builder.Configuration, applySchedule:false, persist:true); return Results.Json(result); });

// Daikin data group
var daikinGroup = app.MapGroup("/api/daikin").WithTags("Daikin");
// Simple proxy for sites (needed by frontend Sites button)
daikinGroup.MapGet("/sites", async (IConfiguration cfg) =>
{
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
        var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen);
        var sitesJson = await client.GetSitesAsync();
        // Return raw JSON array/string from Daikin
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
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.Json(new { status="unauthorized", error="Not authorized" });
    try
    {
        var client = new DaikinApiClient(token, log:true);
        var json = await client.GetDevicesAsync("_ignored");
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
            // Include raw schedule node (first 400 chars) for debugging if present
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
            if (scheduleRaw!=null && scheduleRaw.Length>400) scheduleRaw = scheduleRaw.Substring(0,400)+"...";
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
daikinGroup.MapGet("/devices", async (IConfiguration cfg, string? siteId) =>
{
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen);
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
daikinGroup.MapGet("/gateway", async (IConfiguration cfg) =>
{
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
        var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen);
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
    var (token, _) = DaikinOAuthService.TryGetValidAccessToken(cfg);
    token ??= await DaikinOAuthService.RefreshIfNeededAsync(cfg);
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
        var client = new DaikinApiClient(token, log, logBody, bodyLen == 0 ? null : bodyLen);

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
        bool activated = false;
        if (!string.IsNullOrWhiteSpace(activateScheduleId))
        {
            try
            {
                await client.SetCurrentScheduleAsync(gatewayDeviceId, embeddedId, modeUsed, activateScheduleId);
                activated = true;
            }
            catch (Exception exAct)
            {
                return Results.BadRequest(new { error = "Put ok men aktivering misslyckades", activateError = exAct.Message });
            }
        }
        return Results.Ok(new { put = true, activated, activateScheduleId, modeUsed, requestedMode });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Kör initial batch (persist + ev. schedule) utan att exponera svar
// Initial batch now only fetches/prerenders (no auto apply)
_ = Task.Run(async () => await BatchRunner.RunBatchAsync((IConfiguration)builder.Configuration, applySchedule:false, persist:true));

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
    _ = BatchRunner.RunBatchAsync(_cfg, applySchedule:false, persist:true);
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
                await BatchRunner.RunBatchAsync(_cfg, applySchedule:false, persist:true);
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