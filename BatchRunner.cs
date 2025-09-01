using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

internal enum HourState { None, Comfort, Eco, TurnOff }
internal record ClassifiedHour(int Hour, decimal Price, HourState State);
internal record GeneratedSegments(List<(int hour,string state)> Segments);

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
    // Konfigurerbara parametrar
    int comfortHoursDefault = int.TryParse(config["Schedule:ComfortHours"], out var ch) ? Math.Clamp(ch, 1, 12) : 3;
    int turnOffMaxConsec = int.TryParse(config["Schedule:TurnOffMaxConsecutive"], out var moc) ? Math.Clamp(moc, 1, 6) : 2;
    double turnOffPercentile = double.TryParse(config["Schedule:TurnOffPercentile"], out var tp) ? Math.Clamp(tp, 0.5, 0.99) : 0.9; // top 10% default
    double turnOffSpikeDeltaPct = double.TryParse(config["Schedule:TurnOffSpikeDeltaPct"], out var sd) ? Math.Clamp(sd, 1, 200) : 10; // minst 10% dyrare än grannfönstrets snitt
    int turnOffNeighborWindow = int.TryParse(config["Schedule:TurnOffNeighborWindow"], out var nw) ? Math.Clamp(nw, 1, 4) : 2; // +-2 timmar
    decimal comfortNextHourMaxIncreasePct = decimal.TryParse(config["Schedule:ComfortNextHourMaxIncreasePct"], out var cni) ? Math.Clamp(cni, 0, 500) : 25m; // om nästa timme är > denna % dyrare än billigaste -> stopp
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
            // COMFORT: starta på absolut billigaste timmen och försök bara bygga framåt i tid.
            var byHour = entries.ToDictionary(e => e.start.Hour, e => e.value);
            var orderedByPrice = entries.OrderBy(e => e.value).ToList();
            var cheapestHours = new HashSet<int>();
            if (orderedByPrice.Count > 0)
            {
                var first = orderedByPrice[0];
                var baseHour = first.start.Hour;
                var basePrice = first.value;
                cheapestHours.Add(baseHour);
                var maxLen = comfortHoursDefault;
                var nextHour = baseHour + 1;
                while (cheapestHours.Count < maxLen && byHour.TryGetValue(nextHour, out var nextPrice))
                {
                    if (comfortNextHourMaxIncreasePct <= 0) break; // konfig satt till 0 => bara billigaste timmen
                    var increasePct = basePrice == 0 ? 0 : (nextPrice - basePrice) / (basePrice) * 100m;
                    if (increasePct > comfortNextHourMaxIncreasePct) break; // för dyrt nästa timme
                    cheapestHours.Add(nextHour);
                    nextHour++;
                }
            }

            // TURN_OFF kandidater: identifiera topp-priser (percentil) och spikes relativt grannar
            var turnOffHours = new HashSet<int>();
            if (entries.Count >= 4) // behöver några datapunkter för att få mening
            {
                var desc = entries.OrderByDescending(e => e.value).ToList();
                int idx = (int)Math.Floor(desc.Count * turnOffPercentile) - 1;
                if (idx < 0) idx = 0;
                var percentileThreshold = desc[idx].value;
                // För varje timme: jämför mot genomsnitt av grannfönster
                // byHour redan skapad ovan
                var candidateHours = new List<int>();
                foreach (var (start, value) in entries)
                {
                    if (value < percentileThreshold) continue; // inte bland de dyraste
                    // Beräkna medel för grannar inom +-turnOffNeighborWindow (exkl. själv)
                    decimal sum = 0; int count = 0;
                    for (int h = start.Hour - turnOffNeighborWindow; h <= start.Hour + turnOffNeighborWindow; h++)
                    {
                        if (h == start.Hour) continue;
                        if (byHour.TryGetValue(h, out var v)) { sum += v; count++; }
                    }
                    if (count == 0) continue; // inga grannar
                    var avg = sum / count;
                    if (avg == 0) continue;
                    var spikePct = (double)((value - avg) / avg) * 100.0;
                    if (spikePct >= turnOffSpikeDeltaPct)
                    {
                        candidateHours.Add(start.Hour);
                    }
                }
                // Begränsa max antal i följd och undvik att slå ut timmar runt som är nästan lika dyra
                candidateHours.Sort();
                int consec = 0; int prev = -10;
                foreach (var h in candidateHours)
                {
                    if (h == prev + 1) consec++; else consec = 1;
                    prev = h;
                    if (consec > turnOffMaxConsec) continue; // bryter kedjan
                    // Kontroll: om timmar ±2 (window) är nästan lika dyra (< turnOffSpikeDeltaPct diff) -> avstå
                    bool nearPeers = false;
                    for (int nh = h - turnOffNeighborWindow; nh <= h + turnOffNeighborWindow; nh++)
                    {
                        if (nh == h) continue;
                        if (byHour.TryGetValue(nh, out var pv))
                        {
                            var baseVal = byHour[h];
                            if (pv > 0 && (double)Math.Abs(baseVal - pv) / (double)pv * 100.0 < turnOffSpikeDeltaPct)
                            { nearPeers = true; break; }
                        }
                    }
                    if (nearPeers) continue; // inte en tydlig spike
                    // Undvik att markera timmar som redan är comfort
                    if (cheapestHours.Contains(h)) continue;
                    turnOffHours.Add(h);
                }
            }

            // Ny komprimering: Max 4 actions och turn_off får aldrig gälla längre än 2h utan reaktivering.
            int earliestHour = entries.Min(e=>e.start.Hour);
            int latestHour = entries.Max(e=>e.start.Hour);
            int? comfortStart = null; int? comfortEnd = null; // inclusive
            if (cheapestHours.Count>0) { comfortStart = cheapestHours.Min(); comfortEnd = cheapestHours.Max(); }

            // Hitta turn_off kandidatblock (utanför comfort). Varje block max 2h.
            (int start,int end)? turnOffBlock = null; // inclusive end
            if (turnOffHours.Count>0)
            {
                var ordered = turnOffHours.OrderBy(h=>h).ToList();
                int blockStart = ordered[0]; int prev = ordered[0];
                var blocks = new List<(int start,int end)>();
                for (int i=1;i<ordered.Count;i++)
                {
                    var h = ordered[i];
                    if (h==prev+1) { prev = h; continue; }
                    blocks.Add((blockStart, prev));
                    blockStart = h; prev=h;
                }
                blocks.Add((blockStart, prev));
                // filtrera ut block som overlappar comfort
                if (comfortStart.HasValue && comfortEnd.HasValue)
                    blocks = blocks.Where(b => b.end < comfortStart.Value || b.start > comfortEnd.Value).ToList();
                // Begränsa varje block till max 2h längd genom att kapa slut
                blocks = blocks.Select(b => (b.start, end: b.start + Math.Min(1, b.end - b.start))) // max 2h => length (end-start+1) <=2
                               .ToList();
                // välj block med högsta genomsnittspris (använd entries dictionary)
                if (blocks.Count>0)
                {
                    decimal Score((int start,int end) b){ decimal sum=0; int c=0; for(int h=b.start;h<=b.end;h++){ if (entries.Any(e=>e.start.Hour==h)) { sum += entries.First(e=>e.start.Hour==h).value; c++; } } return c==0?0:sum/c; }
                    turnOffBlock = blocks.OrderByDescending(b=>Score(b)).First();
                }
            }

            // Försök placera turn_off före comfort annars efter.
            bool turnOffBeforeComfort = false;
            if (turnOffBlock.HasValue && comfortStart.HasValue)
            {
                turnOffBeforeComfort = turnOffBlock.Value.end < comfortStart.Value;
            }
            // Om comfort saknas spelar placeringen ingen roll.

            var segments = new List<(int hour,string state)>(); // will be compressed to <=4
            void AddSegment(int hour,string state){ if (!segments.Any(s=>s.hour==hour)) segments.Add((hour,state)); else { // ersätt bara om olika state och senare logik kräver
                    for(int i=0;i<segments.Count;i++){ if (segments[i].hour==hour){ segments[i]=(hour,state); break; } }
                }}

            AddSegment(earliestHour, "eco");

            if (turnOffBlock.HasValue && turnOffBeforeComfort)
            {
                AddSegment(turnOffBlock.Value.start, "turn_off");
                int reActHour = turnOffBlock.Value.end + 1; // reaktivera efter block (max 2h block redan garanterad)
                if (!comfortStart.HasValue || reActHour < comfortStart.Value)
                    AddSegment(reActHour, "eco");
            }

            if (comfortStart.HasValue)
            {
                AddSegment(comfortStart.Value, "comfort");
                if (comfortEnd.HasValue && comfortEnd.Value < latestHour)
                {
                    AddSegment(comfortEnd.Value + 1, "eco");
                }
            }

            if (turnOffBlock.HasValue && !turnOffBeforeComfort)
            {
                // place after comfort (or only block if no comfort)
                AddSegment(turnOffBlock.Value.start, "turn_off");
                int reActHour = turnOffBlock.Value.end + 1;
                if (reActHour <= latestHour)
                {
                    AddSegment(reActHour, "eco");
                }
            }

            // Sort & normalize segments
            segments = segments.OrderBy(s=>s.hour).ToList();
            // Säkra att inga turn_off sträckor >2h (om comfort/eco borttogs senare)
            for(int i=0;i<segments.Count;i++)
            {
                if (segments[i].state=="turn_off")
                {
                    int startH = segments[i].hour;
                    int nextH = (i+1<segments.Count) ? segments[i+1].hour : latestHour+1; // exclusive
                    if (nextH - startH > 2) // >2h betyder >2 timmars varaktighet
                    {
                        // Infoga reaktivation efter 2h om plats finns, annars ta bort turn_off
                        int reactHour = startH + 2;
                        if (segments.Count < 4)
                        {
                            segments.Insert(i+1, (reactHour, "eco"));
                        }
                        else
                        {
                            // ta bort turn_off blocket
                            segments.RemoveAt(i); i--; continue;
                        }
                    }
                }
            }
            // Trimma till max 4 segment enligt prioritetsordning: baseline eco, comfort start, turn_off + react eco, post comfort eco.
            // Om fler än 4: ta bort post-comfort eco först, sedan turn_off (båda segmenten), sist react eco.
            if (segments.Count > 4)
            {
                // ta bort eco efter comfort (om det finns och inte baseline)
                int idxPostComfortEco = -1;
                if (comfortEnd.HasValue)
                {
                    idxPostComfortEco = segments.FindIndex(s=>s.state=="eco" && s.hour==comfortEnd.Value+1);
                }
                if (idxPostComfortEco>0 && segments.Count>4) { segments.RemoveAt(idxPostComfortEco); }
            }
            if (segments.Count > 4)
            {
                // ta bort turn_off + dess reactivation
                int idxTO = segments.FindIndex(s=>s.state=="turn_off");
                if (idxTO>=0)
                {
                    // ev. reactivation direkt efter?
                    if (idxTO+1 < segments.Count && segments[idxTO+1].state=="eco")
                        segments.RemoveAt(idxTO+1);
                    segments.RemoveAt(idxTO);
                }
            }
            if (segments.Count > 4)
            {
                // fallback: ta bort sista eco (förutom baseline)
                for(int i=segments.Count-1;i>=0 && segments.Count>4;i--)
                {
                    if (segments[i].state=="eco" && segments[i].hour!=earliestHour)
                        segments.RemoveAt(i);
                }
            }
            // Bygg dayObj
            var dayObj = new JsonObject();
            foreach (var seg in segments.OrderBy(s=>s.hour))
            {
                var key = new TimeSpan(seg.hour,0,0).ToString();
                dayObj[key] = new JsonObject { ["domesticHotWaterTemperature"] = seg.state };
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
            // Fyll alla övriga veckodagar som inte byggts med en default 10:00 comfort
            string[] allDays = new[]{"monday","tuesday","wednesday","thursday","friday","saturday","sunday"};
            foreach (var dName in allDays)
            {
                if (!actionsCombined.ContainsKey(dName))
                {
                    var dayObj = new JsonObject();
                    dayObj["10:00:00"] = new JsonObject { ["domesticHotWaterTemperature"] = "comfort" };
                    actionsCombined[dName] = dayObj;
                }
            }
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
                var targetName = $"prices-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
                var tmpPath = Path.Combine(storageDir, targetName + ".tmp");
                var finalPath = Path.Combine(storageDir, targetName);
                const int maxAttempts = 5;
                for (int attempt=1; attempt<=maxAttempts; attempt++)
                {
                    try
                    {
                        await File.WriteAllTextAsync(tmpPath, jsonOut);
                        // atomic-ish replace
                        if (File.Exists(finalPath)) File.Delete(finalPath);
                        File.Move(tmpPath, finalPath);
                        Console.WriteLine($"[Persist] wrote {finalPath} (attempt {attempt})");
                        break;
                    }
                    catch (IOException ioex) when (attempt < maxAttempts)
                    {
                        Console.WriteLine($"[Persist] retry {attempt} IO: {ioex.Message}");
                        await Task.Delay(100 * attempt);
                    }
                }
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
                var daikin = new DaikinApiClient(token, log, logBody, snipLen == 0 ? null : snipLen);
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
            var refreshed = await DaikinOAuthService.RefreshIfNeededAsync(config);
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
    string message;
    if (dynamicSchedulePayload == null) message = "No schedule generated";
    else if (todayHas && tomorrowHas) message = "Schedule generated (today + tomorrow)";
    else if (todayHas) message = "Schedule generated (today)";
    else if (tomorrowHas) message = "Schedule generated (tomorrow)";
    else message = "Schedule generated"; // fallback
    // Berika message med apply-resultat om schema fanns
    if (dynamicSchedulePayload != null)
    {
        message += scheduleApplied ? " | Applied" : applySchedule ? " | Apply failed" : string.Empty;
        if (scheduleApplied && appliedSite != null && appliedDevice != null)
            message += $" (site={appliedSite} device={appliedDevice})";
        else if (!scheduleApplied && applyAttempts > 0)
            message += $" (attempts={applyAttempts}{(lastApplyError!=null?" error='"+lastApplyError+"'":"")})";
    }
    return (!string.IsNullOrEmpty(dynamicSchedulePayload), schedulePayloadNode, message);
    }
}
