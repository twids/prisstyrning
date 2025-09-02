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
        var accessToken = config["Daikin:AccessToken"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            var (tkn, _) = DaikinOAuthService.TryGetValidAccessToken(config);
            if (tkn == null) tkn = await DaikinOAuthService.RefreshIfNeededAsync(config);
            accessToken = tkn ?? string.Empty;
        }
        var zone = config["Price:Nordpool:DefaultZone"] ?? "SE3";
        var currency = config["Price:Nordpool:Currency"] ?? "SEK";
        Console.WriteLine($"[Batch] Start env={environment} zone={zone} source=Nordpool");
        JsonArray? rawToday = null; JsonArray? rawTomorrow = null;
        try
        {
            var np = new NordpoolClient(currency);
            var fetched = await np.GetTodayTomorrowAsync(zone);
            rawToday = fetched.today; rawTomorrow = fetched.tomorrow;
            PriceMemory.Set(rawToday, rawTomorrow);
            NordpoolPersistence.Save(zone, rawToday, rawTomorrow, config["Storage:Directory"] ?? "data");
        }
        catch (Exception ex)
        {
            return (false, null, $"Nordpool error: {ex.Message}");
        }

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

    // (Refactor hooks present but original algorithm restored below for stability.)
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
            if (date == todayDate && startTs < now.AddMinutes(-10)) continue;
            entries.Add((startTs, val));
        }
        if (entries.Count == 0) return;
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
                if (comfortNextHourMaxIncreasePct <= 0) break;
                var increasePct = basePrice == 0 ? 0 : (nextPrice - basePrice) / (basePrice) * 100m;
                if (increasePct > comfortNextHourMaxIncreasePct) break;
                cheapestHours.Add(nextHour);
                nextHour++;
            }
        }
        var turnOffHours = new HashSet<int>();
        if (entries.Count >= 4)
        {
            var desc = entries.OrderByDescending(e => e.value).ToList();
            int idx = (int)Math.Floor(desc.Count * turnOffPercentile) - 1;
            if (idx < 0) idx = 0;
            var percentileThreshold = desc[idx].value;
            var candidateHours = new List<int>();
            foreach (var (start, value) in entries)
            {
                if (value < percentileThreshold) continue;
                decimal sum = 0; int count = 0;
                for (int h = start.Hour - turnOffNeighborWindow; h <= start.Hour + turnOffNeighborWindow; h++)
                {
                    if (h == start.Hour) continue;
                    if (byHour.TryGetValue(h, out var v)) { sum += v; count++; }
                }
                if (count == 0) continue;
                var avg = sum / count; if (avg == 0) continue;
                var spikePct = (double)((value - avg) / avg) * 100.0;
                if (spikePct >= turnOffSpikeDeltaPct) candidateHours.Add(start.Hour);
            }
            candidateHours.Sort(); int consec = 0; int prev = -10;
            foreach (var h in candidateHours)
            {
                if (h == prev + 1) consec++; else consec = 1; prev = h; if (consec > turnOffMaxConsec) continue;
                bool nearPeers = false;
                for (int nh = h - turnOffNeighborWindow; nh <= h + turnOffNeighborWindow; nh++)
                { if (nh == h) continue; if (byHour.TryGetValue(nh, out var pv)) { var baseVal = byHour[h]; if (pv > 0 && (double)Math.Abs(baseVal - pv) / (double)pv * 100.0 < turnOffSpikeDeltaPct) { nearPeers = true; break; } } }
                if (nearPeers) continue; if (cheapestHours.Contains(h)) continue; turnOffHours.Add(h);
            }
        }
        int earliestHour = entries.Min(e=>e.start.Hour); int latestHour = entries.Max(e=>e.start.Hour);
        int? comfortStart = null; int? comfortEnd = null; if (cheapestHours.Count>0){ comfortStart=cheapestHours.Min(); comfortEnd=cheapestHours.Max(); }
        (int start,int end)? turnOffBlock = null;
        if (turnOffHours.Count>0)
        {
            var ordered = turnOffHours.OrderBy(h=>h).ToList(); int blockStart=ordered[0]; int prev=ordered[0]; var blocks=new List<(int start,int end)>();
            for(int i=1;i<ordered.Count;i++){ var h=ordered[i]; if(h==prev+1){ prev=h; continue; } blocks.Add((blockStart,prev)); blockStart=h; prev=h; }
            blocks.Add((blockStart,prev)); if(comfortStart.HasValue && comfortEnd.HasValue) blocks=blocks.Where(b=> b.end < comfortStart.Value || b.start > comfortEnd.Value).ToList();
            blocks=blocks.Select(b=> (b.start, end: b.start + Math.Min(1, b.end - b.start))).ToList();
            if(blocks.Count>0){ decimal Score((int start,int end) b){ decimal sum=0; int c=0; for(int h=b.start;h<=b.end;h++){ if(entries.Any(e=>e.start.Hour==h)) { sum += entries.First(e=>e.start.Hour==h).value; c++; } } return c==0?0:sum/c; } turnOffBlock=blocks.OrderByDescending(b=>Score(b)).First(); }
        }
        bool turnOffBeforeComfort=false; if(turnOffBlock.HasValue && comfortStart.HasValue) turnOffBeforeComfort = turnOffBlock.Value.end < comfortStart.Value;
        var segments=new List<(int hour,string state)>(); void AddSegment(int hour,string state){ if(!segments.Any(s=>s.hour==hour)) segments.Add((hour,state)); else { for(int i=0;i<segments.Count;i++){ if(segments[i].hour==hour){ segments[i]=(hour,state); break; } } } }
        AddSegment(earliestHour,"eco"); if(turnOffBlock.HasValue && turnOffBeforeComfort){ AddSegment(turnOffBlock.Value.start,"turn_off"); int reActHour=turnOffBlock.Value.end+1; if(!comfortStart.HasValue || reActHour < comfortStart.Value) AddSegment(reActHour,"eco"); }
        if(comfortStart.HasValue){ AddSegment(comfortStart.Value,"comfort"); if(comfortEnd.HasValue && comfortEnd.Value < latestHour) AddSegment(comfortEnd.Value+1,"eco"); }
        if(turnOffBlock.HasValue && !turnOffBeforeComfort){ AddSegment(turnOffBlock.Value.start,"turn_off"); int reActHour=turnOffBlock.Value.end+1; if(reActHour<=latestHour) AddSegment(reActHour,"eco"); }
        segments = segments.OrderBy(s=>s.hour).ToList();
        for(int i=0;i<segments.Count;i++){ if(segments[i].state=="turn_off"){ int start=segments[i].hour; int next=(i+1<segments.Count)? segments[i+1].hour : latestHour+1; if(next-start>2){ int react=start+2; if(segments.Count<4) segments.Insert(i+1,(react,"eco")); else { segments.RemoveAt(i); i--; continue; } } } }
        if(segments.Count>4){ int idxPost=-1; if(comfortEnd.HasValue) idxPost=segments.FindIndex(s=>s.state=="eco" && s.hour==comfortEnd.Value+1); if(idxPost>0 && segments.Count>4) segments.RemoveAt(idxPost); }
        if(segments.Count>4){ int idxTO=segments.FindIndex(s=>s.state=="turn_off"); if(idxTO>=0){ if(idxTO+1<segments.Count && segments[idxTO+1].state=="eco") segments.RemoveAt(idxTO+1); segments.RemoveAt(idxTO); } }
        if(segments.Count>4){ for(int i=segments.Count-1;i>=0 && segments.Count>4;i--){ if(segments[i].state=="eco" && segments[i].hour!=earliestHour) segments.RemoveAt(i); } }
        var dayObj=new JsonObject(); foreach(var seg in segments.OrderBy(s=>s.hour)){ var key=new TimeSpan(seg.hour,0,0).ToString(); dayObj[key]= new JsonObject{ ["domesticHotWaterTemperature"]=seg.state }; }
        actionsCombined[weekdayName]=dayObj;
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
                // Prisschema ska inte sparas med tid i namnet längre
                // Om du vill spara schema, använd prices-YYYYMMDD-ZON.json via NordpoolPersistence
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
