using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data.Repositories;

internal enum HourState { None, Comfort, Eco, TurnOff }
internal record ClassifiedHour(int Hour, decimal Price, HourState State);
internal record GeneratedSegments(List<(int hour,string state)> Segments);

internal class BatchRunner
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DaikinOAuthService _daikinOAuthService;

    public BatchRunner(IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuthService)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _daikinOAuthService = daikinOAuthService ?? throw new ArgumentNullException(nameof(daikinOAuthService));
    }

    public async Task<object> GenerateSchedulePreview(IConfiguration config, IServiceScopeFactory? scopeFactory = null)
    {
        var res = await RunBatchAsync(config, null, applySchedule:false, persist:false, scopeFactory);
        return new { res.schedulePayload, res.generated, res.message };
    }

    // Overload that uses user-specific settings from DB and unified ScheduleAlgorithm
    public async Task<(bool generated, JsonNode? schedulePayload, string message)> RunBatchAsync(IConfiguration config, string? userId, bool applySchedule, bool persist, IServiceScopeFactory? scopeFactory = null)
    {
        UserScheduleSettings settings;
        if (scopeFactory != null)
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<UserSettingsRepository>();
            settings = await repo.LoadScheduleSettingsAsync(userId);
        }
        else
        {
            // Fall back to config defaults when no DI available
            int ch = int.TryParse(config["Schedule:ComfortHours"], out var _ch) ? Math.Clamp(_ch, 1, 12) : 3;
            double tp = double.TryParse(config["Schedule:TurnOffPercentile"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _tp) ? Math.Clamp(_tp, 0.5, 0.99) : 0.9;
            int mcgh = int.TryParse(config["Schedule:MaxComfortGapHours"], out var _mcgh) ? Math.Clamp(_mcgh, 1, 72) : 28;
            settings = new UserScheduleSettings(ch, tp, mcgh);
        }
        int activationLimit = int.TryParse(config["Schedule:MaxActivationsPerDay"], out var mpd) ? Math.Clamp(mpd, 1, 24) : 4;
        var (generated, schedulePayload, message) = await RunBatchInternalAsync(config, new UserScheduleSettings(settings.ComfortHours, settings.TurnOffPercentile, settings.MaxComfortGapHours), activationLimit, applySchedule, persist, userId);
        
        // Enhanced logging for history persistence
        if (!persist)
        {
            Console.WriteLine($"[BatchRunner] History NOT saved: persist=false (userId={userId ?? "null"})");
        }
        else if (string.IsNullOrWhiteSpace(userId))
        {
            Console.WriteLine($"[BatchRunner] History NOT saved: userId is null or empty");
        }
        else if (!generated || schedulePayload == null)
        {
            Console.WriteLine($"[BatchRunner] History NOT saved: schedule generation failed (userId={userId})");
        }
        else if (generated && schedulePayload is JsonObject payload)
        {
            // Fire and forget async save - only when persist is true
            _ = SaveHistoryAsync(userId, payload, config, scopeFactory);
        }
        
        return (generated, schedulePayload, message);
    }

    // Returnerar schedulePayload som JsonNode istället för sträng för att API-responsen ska ha ett inbäddat JSON-objekt
    private async Task<(bool generated, JsonNode? schedulePayload, string message)> RunBatchInternalAsync(IConfiguration config, UserScheduleSettings settings, int activationLimit, bool applySchedule, bool persist, string? userId)
    {
        var environment = config["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var accessToken = config["Daikin:AccessToken"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            var (tkn, _) = await _daikinOAuthService.TryGetValidAccessTokenAsync(userId);
            if (tkn == null) tkn = await _daikinOAuthService.RefreshIfNeededAsync(userId);
            accessToken = tkn ?? string.Empty;
        }
        var zone = config["Price:Nordpool:DefaultZone"] ?? "SE3";
        var currency = config["Price:Nordpool:Currency"] ?? "SEK";
        Console.WriteLine($"[Batch] Start env={environment} zone={zone} source=Nordpool");
        JsonArray? rawToday = null; JsonArray? rawTomorrow = null;
        try
        {
            var np = new NordpoolClient(_httpClientFactory.CreateClient("Nordpool"), currency);
            var fetched = await np.GetTodayTomorrowAsync(zone);
            rawToday = fetched.today; rawTomorrow = fetched.tomorrow;
            PriceMemory.Set(rawToday, rawTomorrow);
        }
        catch (Exception ex)
        {
            return (false, null, $"Nordpool error: {ex.Message}");
        }

    string? dynamicSchedulePayload = null;
        var (schedulePayload, scheduleMessage) = ScheduleAlgorithm.Generate(rawToday, rawTomorrow,
            settings.ComfortHours, settings.TurnOffPercentile, activationLimit,
            settings.MaxComfortGapHours, config, null, ScheduleAlgorithm.LogicType.PerDayOriginal);
        if (schedulePayload != null)
        {
            dynamicSchedulePayload = schedulePayload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        if (persist)
        {
            try
            {
                var storageDir = config["Storage:Directory"] ?? "data";
                Directory.CreateDirectory(storageDir);
                JsonNode? scheduleNode = null;
                if (!string.IsNullOrEmpty(dynamicSchedulePayload))
                {
                    try { scheduleNode = JsonNode.Parse(dynamicSchedulePayload); } catch { /* ignorerar parse-fel */ }
                }
                // Price persistence is handled by NordpoolPriceHangfireJob via PriceRepository
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persist] error: {ex.Message}");
            }
        }

    var applyEnabled = config["Daikin:ApplySchedule"] is string s && bool.TryParse(s, out var b) ? b : true;
    string? appliedSite = null; string? appliedDevice = null; bool scheduleApplied = false; int applyAttempts = 0; string? lastApplyError = null;
    if (applySchedule && applyEnabled && !string.IsNullOrEmpty(dynamicSchedulePayload) && !string.IsNullOrEmpty(accessToken))
    {
        async Task<bool> TryApplyAsync(string token, bool isRetry)
        {
            try
            {
                bool log = (config["Daikin:Http:Log"] ?? config["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                bool logBody = (config["Daikin:Http:LogBody"] ?? config["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                int.TryParse(config["Daikin:Http:BodySnippetLength"], out var snipLen);
                var daikin = new DaikinApiClient(_httpClientFactory.CreateClient("Daikin"), token, log, logBody, snipLen == 0 ? null : snipLen);
                // Overrides via config (optional)
                var overrideSite = config["Daikin:SiteId"];
                var overrideDevice = config["Daikin:DeviceId"];
                var overrideEmbedded = config["Daikin:ManagementPointEmbeddedId"]; // e.g. "2"
                var scheduleMode = config["Daikin:ScheduleMode"] ?? "heating"; // heating / cooling / any
                if (!string.IsNullOrWhiteSpace(overrideSite)) appliedSite = overrideSite;
                if (appliedSite == null)
                {
                    var sitesJson = await daikin.GetSitesAsync();
                    using var doc = JsonDocument.Parse(sitesJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        appliedSite = doc.RootElement[0].GetProperty("id").GetString();
                        Console.WriteLine($"[Schedule] sites count={doc.RootElement.GetArrayLength()} pickedSite={appliedSite}");
                    }
                }
                if (appliedSite == null) { Console.WriteLine("[Schedule] no site found"); return false; }
                if (!string.IsNullOrWhiteSpace(overrideDevice)) appliedDevice = overrideDevice;
                string? pickedDeviceJson = null; // hold raw JSON of picked device to avoid disposed JsonDocument issues
                if (appliedDevice == null)
                {
                    var devicesJson = await daikin.GetDevicesAsync(appliedSite);
                    using (var dDoc = JsonDocument.Parse(devicesJson))
                    {
                        if (dDoc.RootElement.ValueKind == JsonValueKind.Array && dDoc.RootElement.GetArrayLength() > 0)
                        {
                            var elem = dDoc.RootElement[0];
                            appliedDevice = elem.GetProperty("id").GetString();
                            pickedDeviceJson = elem.GetRawText();
                            Console.WriteLine($"[Schedule] devices count={dDoc.RootElement.GetArrayLength()} pickedDevice={appliedDevice}");
                        }
                    }
                }
                if (appliedDevice == null) { Console.WriteLine("[Schedule] no device found"); return false; }

                // Determine embeddedId (management point) for domesticHotWaterTank
                string? embeddedId = overrideEmbedded;
                if (embeddedId == null)
                {
                    if (pickedDeviceJson == null)
                    {
                        // fetch again to locate the selected device
                        var devicesJson = await daikin.GetDevicesAsync(appliedSite);
                        using (var dDoc = JsonDocument.Parse(devicesJson))
                        {
                            if (dDoc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in dDoc.RootElement.EnumerateArray())
                                {
                                    if (item.TryGetProperty("id", out var idEl) && idEl.GetString() == appliedDevice)
                                    { pickedDeviceJson = item.GetRawText(); break; }
                                }
                            }
                        }
                    }
                    if (pickedDeviceJson != null)
                    {
                        using (var devDoc = JsonDocument.Parse(pickedDeviceJson))
                        {
                            if (devDoc.RootElement.TryGetProperty("managementPoints", out var mpArray) && mpArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var mp in mpArray.EnumerateArray())
                                {
                                    if (mp.TryGetProperty("managementPointType", out var mpt) && mpt.GetString() == "domesticHotWaterTank" && mp.TryGetProperty("embeddedId", out var emb))
                                    { embeddedId = emb.GetString(); Console.WriteLine($"[Schedule] found DHW embeddedId={embeddedId}"); break; }
                                }
                            }
                        }
                    }
                }
                if (embeddedId == null)
                {
                    Console.WriteLine("[Schedule] no domesticHotWaterTank management point found – attempting legacy DHW endpoint");
                    await daikin.LegacySetDhwScheduleAsync(appliedDevice, dynamicSchedulePayload);
                    Console.WriteLine($"[Schedule] legacy apply OK site={appliedSite} device={appliedDevice} retry={isRetry}");
                    return true;
                }

                // PUT full schedules then enable current schedule (scheduleId 0)
                Console.WriteLine($"[Schedule] applying schedule bytes={dynamicSchedulePayload.Length} mode={scheduleMode}");
                await daikin.PutSchedulesAsync(appliedDevice, embeddedId, scheduleMode, dynamicSchedulePayload);
                await daikin.SetCurrentScheduleAsync(appliedDevice, embeddedId, scheduleMode, "0");
                Console.WriteLine($"[Schedule] apply OK site={appliedSite} device={appliedDevice} embedded={embeddedId} mode={scheduleMode} retry={isRetry}");
                return true;
            }
            catch (HttpRequestException hre) when (hre.Message.Contains("401") && !isRetry)
            {
                Console.WriteLine("[Schedule] 401 Unauthorized on first attempt – will try refresh and retry once.");
                return false;
            }
            catch (HttpRequestException hre)
            {
                lastApplyError = hre.Message;
                Console.WriteLine($"[Schedule] apply HTTP error (retry={isRetry}): {hre.Message}");
                return false;
            }
            catch (Exception ex)
            {
                lastApplyError = ex.Message;
                Console.WriteLine($"[Schedule] apply failed (retry={isRetry}): {ex.Message}");
                return false;
            }
        }

        // First attempt
        applyAttempts++;
        scheduleApplied = await TryApplyAsync(accessToken, false);
        if (!scheduleApplied)
        {
            // Try refresh if possible
            var refreshed = await _daikinOAuthService.RefreshIfNeededAsync(userId);
            if (!string.IsNullOrEmpty(refreshed) && refreshed != accessToken)
            {
                applyAttempts++;
                scheduleApplied = await TryApplyAsync(refreshed, true);
            }
        }
    }

        JsonNode? schedulePayloadNode = null;
        if (!string.IsNullOrEmpty(dynamicSchedulePayload))
        {
            try { schedulePayloadNode = JsonNode.Parse(dynamicSchedulePayload); } catch { /* lämna null om ogiltig */ }
        }
    string message = schedulePayload == null ? "No schedule generated" : scheduleMessage;
    // Berika message med apply-resultat om schema fanns
    if (dynamicSchedulePayload != null)
    {
        message += scheduleApplied ? " | Applied" : applySchedule ? " | Apply failed" : string.Empty;
        if (scheduleApplied && appliedSite != null && appliedDevice != null)
            message += $" (site={appliedSite} device={appliedDevice})";
        else if (!scheduleApplied && applyAttempts > 0)
            message += $" (attempts={applyAttempts}{(lastApplyError!=null?" error='"+lastApplyError+"'":"")})";
    }
    return (schedulePayload != null, schedulePayload, message);
    }

    // Helper method for fire-and-forget history save with error handling
    private async Task SaveHistoryAsync(string userId, JsonObject payload, IConfiguration config, IServiceScopeFactory? scopeFactory)
    {
        try 
        {
            if (scopeFactory == null)
            {
                Console.WriteLine($"[BatchRunner] History NOT saved: no scope factory (userId={userId})");
                return;
            }
            using var scope = scopeFactory.CreateScope();
            var historyRepo = scope.ServiceProvider.GetRequiredService<ScheduleHistoryRepository>();
            await historyRepo.SaveAsync(userId, payload, DateTimeOffset.UtcNow);
            Console.WriteLine($"[BatchRunner] Saved schedule history for user {userId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BatchRunner] Failed to save schedule history for user {userId}: {ex.Message}");
        }
    }
}
