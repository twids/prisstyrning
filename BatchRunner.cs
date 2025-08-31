using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

internal static class BatchRunner
{
    public static async Task<object> GenerateSchedulePreview(IConfiguration config)
    {
        var res = await RunBatchAsync(config, applySchedule:false, persist:false);
        return new { res.schedulePayload, res.generated, res.message };
    }

    // Returnerar schedulePayload som JsonNode istället för sträng för att API-responsen ska ha ett inbäddat JSON-objekt
    public static async Task<(bool generated, JsonNode? schedulePayload, string message)> RunBatchAsync(IConfiguration config, bool applySchedule, bool persist)
    {
        var environment = config["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var haBaseUrl = config["HomeAssistant:BaseUrl"] ?? string.Empty;
        var haToken = config["HomeAssistant:Token"] ?? string.Empty;
        var haSensor = config["HomeAssistant:Sensor"] ?? string.Empty;
        var accessToken = config["Daikin:AccessToken"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            // Försök hämta från tokenfil (OAuth)
            var (tkn, _) = DaikinOAuthService.TryGetValidAccessToken(config);
            if (tkn == null)
            {
                // försök refresh
                tkn = await DaikinOAuthService.RefreshIfNeededAsync(config);
            }
            accessToken = tkn ?? string.Empty;
        }
        Console.WriteLine($"[Batch] Start env={environment} sensor={haSensor}");
        var homeAssistant = new HomeAssistantClient(haBaseUrl, haToken);
        await homeAssistant.TestConnectionAsync();

        JsonArray? rawToday = null;
        JsonArray? rawTomorrow = null;
        try
        {
            rawToday = await homeAssistant.GetRawPricesAsync(haSensor, "raw_today");
            rawTomorrow = await homeAssistant.GetRawPricesAsync(haSensor, "raw_tomorrow");
        }
        catch (Exception ex)
        {
            return (false, null, $"HA error: {ex.Message}");
        }

    // Uppdatera minnescache
    PriceMemory.Set(rawToday, rawTomorrow);

    string? dynamicSchedulePayload = null; // intern sträng för eventuell post till Daikin
        // Bygg schema för idag och (om finns) imorgon. Lägg in båda weekday-namnen i samma actions.
        var now = DateTimeOffset.Now;
        var todayDate = now.Date;
        var tomorrowDate = todayDate.AddDays(1);
        int comfortHoursDefault = 3;
        var actionsCombined = new JsonObject();

        void AddDay(JsonArray source, DateTimeOffset date, string weekdayName)
        {
            var entries = new List<(DateTimeOffset start, decimal value)>();
            foreach (var item in source)
            {
                if (item == null) continue;
                var startStr = item["start"]?.ToString();
                var valueStr = item["value"]?.ToString();
                if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
                if (startTs.Date != date) continue;
                if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
                // För dagens datum: ignorera historiska timmar äldre än 10 min för att undvika onödiga timmar
                if (date == todayDate && startTs < now.AddMinutes(-10)) continue;
                entries.Add((startTs, val));
            }
            if (entries.Count == 0) return;
            var comfortHours = comfortHoursDefault; // kan i framtiden hämtas från config
            var cheapestHours = entries.OrderBy(e => e.value).Take(comfortHours).Select(e => e.start.Hour).ToHashSet();
            var dayObj = new JsonObject();
            foreach (var e in entries.OrderBy(e => e.start))
            {
                var key = new TimeSpan(e.start.Hour, 0, 0).ToString();
                var tempState = cheapestHours.Contains(e.start.Hour) ? "comfort" : "eco";
                dayObj[key] = new JsonObject { ["domesticHotWaterTemperature"] = tempState };
            }
            actionsCombined[weekdayName] = dayObj;
        }

        // Generera alltid dagens schema om det finns, och lägg även till morgondagen om den finns
        bool todayHas = false;
        bool tomorrowHas = false;
        if (rawToday is { Count: > 0 })
        {
            // kontrollera att minst en entry är för idag
            foreach (var item in rawToday)
            {
                var startStr = item?["start"]?.ToString();
                if (DateTimeOffset.TryParse(startStr, out var ts) && ts.Date == todayDate) { todayHas = true; break; }
            }
            if (todayHas)
                AddDay(rawToday, todayDate, now.DayOfWeek.ToString().ToLower());
        }
        if (rawTomorrow is { Count: > 0 })
        {
            foreach (var item in rawTomorrow)
            {
                var startStr = item?["start"]?.ToString();
                if (DateTimeOffset.TryParse(startStr, out var ts) && ts.Date == tomorrowDate) { tomorrowHas = true; break; }
            }
            if (tomorrowHas)
                AddDay(rawTomorrow, tomorrowDate, tomorrowDate.DayOfWeek.ToString().ToLower());
        }
        if (actionsCombined.Count > 0)
        {
            var root = new JsonObject { ["0"] = new JsonObject { ["actions"] = actionsCombined } };
            dynamicSchedulePayload = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
                var snapshot = new
                {
                    fetchedAt = DateTimeOffset.UtcNow,
                    sensor = haSensor,
                    today = rawToday,
                    tomorrow = rawTomorrow,
                    schedulePreview = scheduleNode
                };
                var jsonOut = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                var filePath = Path.Combine(storageDir, $"prices-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(filePath, jsonOut);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persist] error: {ex.Message}");
            }
        }

    var applyEnabled = config["Daikin:ApplySchedule"] is string s && bool.TryParse(s, out var b) ? b : true;
    if (applySchedule && applyEnabled && !string.IsNullOrEmpty(dynamicSchedulePayload) && !string.IsNullOrEmpty(accessToken))
        {
            try
            {
                var daikin = new DaikinApiClient(accessToken);
                var sitesJson = await daikin.GetSitesAsync();
                using var doc = JsonDocument.Parse(sitesJson);
                var siteId = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0].GetProperty("id").GetString() : null;
                if (siteId != null)
                {
                    var devicesJson = await daikin.GetDevicesAsync(siteId);
                    using var dDoc = JsonDocument.Parse(devicesJson);
                    var deviceId = dDoc.RootElement.ValueKind == JsonValueKind.Array && dDoc.RootElement.GetArrayLength() > 0 ? dDoc.RootElement[0].GetProperty("id").GetString() : null;
                    if (deviceId != null)
                    {
                        await daikin.SetScheduleAsync(deviceId, dynamicSchedulePayload);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Schedule] apply failed: {ex.Message}");
            }
        }

        JsonNode? schedulePayloadNode = null;
        if (!string.IsNullOrEmpty(dynamicSchedulePayload))
        {
            try { schedulePayloadNode = JsonNode.Parse(dynamicSchedulePayload); } catch { /* lämna null om ogiltig */ }
        }
    string message;
    if (dynamicSchedulePayload == null) message = "No schedule generated";
    else if (todayHas && tomorrowHas) message = "Schedule generated (today + tomorrow)";
    else if (todayHas) message = "Schedule generated (today)";
    else if (tomorrowHas) message = "Schedule generated (tomorrow)";
    else message = "Schedule generated"; // fallback
    return (!string.IsNullOrEmpty(dynamicSchedulePayload), schedulePayloadNode, message);
    }
}
