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

    public static async Task<(bool generated, string? schedulePayload, string message)> RunBatchAsync(IConfiguration config, bool applySchedule, bool persist)
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

    string? dynamicSchedulePayload = null;
        if (rawToday is { Count: > 0 })
        {
            var entries = new List<(DateTimeOffset start, decimal value)>();
            foreach (var item in rawToday!)
            {
                if (item == null) continue;
                var startStr = item["start"]?.ToString();
                var valueStr = item["value"]?.ToString();
                if (DateTimeOffset.TryParse(startStr, out var startTs) && decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                {
                    if (startTs.Date == DateTimeOffset.Now.Date && startTs >= DateTimeOffset.Now.AddMinutes(-10))
                        entries.Add((startTs, val));
                }
            }
            if (entries.Count > 0)
            {
                int comfortHours = 3;
                var cheapest = entries.OrderBy(e => e.value).Take(comfortHours).Select(e => e.start.Hour).ToHashSet();
                var weekdayName = DateTimeOffset.Now.DayOfWeek.ToString().ToLower();
                var actionsObj = new JsonObject();
                var dayObj = new JsonObject();
                foreach (var e in entries.OrderBy(e => e.start))
                {
                    var key = new TimeSpan(e.start.Hour, 0, 0).ToString();
                    var tempState = cheapest.Contains(e.start.Hour) ? "comfort" : "eco";
                    dayObj[key] = new JsonObject { ["domesticHotWaterTemperature"] = tempState };
                }
                actionsObj[weekdayName] = dayObj;
                var root = new JsonObject { ["0"] = new JsonObject { ["actions"] = actionsObj } };
                dynamicSchedulePayload = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }

        if (persist)
        {
            try
            {
                var storageDir = config["Storage:Directory"] ?? "data";
                Directory.CreateDirectory(storageDir);
                var snapshot = new
                {
                    fetchedAt = DateTimeOffset.UtcNow,
                    sensor = haSensor,
                    today = rawToday,
                    tomorrow = rawTomorrow,
                    schedulePreview = dynamicSchedulePayload
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

        return (!string.IsNullOrEmpty(dynamicSchedulePayload), dynamicSchedulePayload, dynamicSchedulePayload == null ? "No schedule generated" : "Schedule generated");
    }
}
