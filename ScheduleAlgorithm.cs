using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public static class ScheduleAlgorithm
{
    public enum LogicType { PerDayOriginal, CrossDayCheapestLimited }

    /// <summary>
    /// Aggregates 15-minute price data into hourly averages with volatility tracking.
    /// If data is already hourly (24 or fewer entries per day), returns it as-is.
    /// If data has 15-minute resolution (more than 24 entries per day), averages each hour's 4 data points
    /// and includes min/max/volatility metadata for spike detection.
    /// </summary>
    private static JsonArray AggregateToHourly(JsonArray? rawData)
    {
        if (rawData == null || rawData.Count == 0)
            return new JsonArray();

        var result = new JsonArray();
        var entriesByHour = new Dictionary<(DateTime date, int hour), List<(DateTimeOffset start, decimal value)>>();

        // Group entries by date and hour
        foreach (var item in rawData)
        {
            if (item == null) continue;
            var startStr = item["start"]?.ToString();
            var valueStr = item["value"]?.ToString();
            if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
            if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;

            var key = (startTs.Date, startTs.Hour);
            if (!entriesByHour.ContainsKey(key))
                entriesByHour[key] = new List<(DateTimeOffset, decimal)>();
            entriesByHour[key].Add((startTs, val));
        }

        // For each hour, calculate average and volatility metrics
        foreach (var kvp in entriesByHour.OrderBy(x => x.Key.date).ThenBy(x => x.Key.hour))
        {
            var entries = kvp.Value;
            var avgValue = entries.Average(e => e.value);
            var minValue = entries.Min(e => e.value);
            var maxValue = entries.Max(e => e.value);
            var volatility = maxValue - minValue;
            var firstEntry = entries.OrderBy(e => e.start).First();
            
            // Use the hour's start time and averaged value
            var hourStart = new DateTimeOffset(kvp.Key.date.Year, kvp.Key.date.Month, kvp.Key.date.Day, 
                                                kvp.Key.hour, 0, 0, firstEntry.start.Offset);
            
            var hourObj = new JsonObject 
            { 
                ["start"] = hourStart.ToString("O"),
                ["value"] = avgValue.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
            };
            
            // Add volatility metadata if 15-min data (helps with spike detection)
            if (entries.Count > 1)
            {
                hourObj["min"] = minValue.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                hourObj["max"] = maxValue.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                hourObj["volatility"] = volatility.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            }
            
            result.Add(hourObj);
        }

        return result;
    }

    public static (JsonNode? schedulePayload, string message) Generate(
        JsonArray? rawToday,
        JsonArray? rawTomorrow,
        int comfortHoursDefault,
        double turnOffPercentile,
        int activationLimit,
        int maxComfortGapHours,
        IConfiguration config,
        DateTimeOffset? nowOverride = null,
        LogicType logic = LogicType.PerDayOriginal)
    {
        // Aggregate 15-minute price data to hourly averages if needed
        rawToday = AggregateToHourly(rawToday);
        rawTomorrow = AggregateToHourly(rawTomorrow);

        var now = nowOverride ?? DateTimeOffset.Now;
        var todayDate = now.Date;
        var tomorrowDate = todayDate.AddDays(1);
        var actionsCombined = new JsonObject();
    // comfortHoursDefault, turnOffPercentile, turnOffMaxConsec provided explicitly per-user
        double turnOffSpikeDeltaPct = double.TryParse(config["Schedule:TurnOffSpikeDeltaPct"], out var sd) ? Math.Clamp(sd, 1, 200) : 10;
        int turnOffNeighborWindow = int.TryParse(config["Schedule:TurnOffNeighborWindow"], out var nw) ? Math.Clamp(nw, 1, 4) : 2;
        decimal comfortNextHourMaxIncreasePct = decimal.TryParse(config["Schedule:ComfortNextHourMaxIncreasePct"], out var cni) ? Math.Clamp(cni, 0, 500) : 25m;
    // activationLimit provided explicitly

        if (logic == LogicType.CrossDayCheapestLimited)
        {
            // Merge today and tomorrow into a single list
            var allEntries = new List<(DateTimeOffset start, decimal value, string dayName)>();
            if (rawToday != null)
                foreach (var item in rawToday)
                {
                    if (item == null) continue;
                    var startStr = item["start"]?.ToString();
                    var valueStr = item["value"]?.ToString();
                    if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
                    if (startTs < now) continue;
                    if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
                    allEntries.Add((startTs, val, "today"));
                }
            if (rawTomorrow != null)
                foreach (var item in rawTomorrow)
                {
                    if (item == null) continue;
                    var startStr = item["start"]?.ToString();
                    var valueStr = item["value"]?.ToString();
                    if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
                    if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
                    allEntries.Add((startTs, val, "tomorrow"));
                }
            if (allEntries.Count == 0) return (null, "No schedule generated");
            // Select comfort hours: price below percentile threshold
            var sorted = allEntries.OrderBy(e => e.value).ToList();
            int maxActivationsPerDay = activationLimit;
            var percentileIdx = (int)Math.Floor(allEntries.Count * 0.2); // 20th percentile
            var priceThreshold = sorted[Math.Max(0, percentileIdx)].value;
            var comfortCandidates = allEntries.Where(e => e.value <= priceThreshold).OrderBy(e => e.start).ToList();
            // Always include the absolute cheapest hour
            var cheapest = sorted.First();
            var comfortHours = new List<(DateTimeOffset start, string dayName)> { (cheapest.start, cheapest.dayName) };
            foreach (var c in comfortCandidates)
            {
                if (c.start != cheapest.start)
                    comfortHours.Add((c.start, c.dayName));
            }
            // Group comfort hours into blocks per day
            var comfortBlocks = comfortHours
                .GroupBy(e => e.dayName)
                .Select(g => g.OrderBy(e => e.start.Hour).ToList())
                .ToDictionary(g => g.First().dayName, g => g);
            // Limit activations per day
            foreach (var day in comfortBlocks.Keys)
            {
                var blocks = comfortBlocks[day];
                if (blocks.Count > maxActivationsPerDay)
                    comfortBlocks[day] = blocks.Take(maxActivationsPerDay).ToList();
            }
            // Build actionsCombined
            foreach (var day in new[] { "today", "tomorrow" })
            {
                var dayObj = new JsonObject();
                var blocks = comfortBlocks.ContainsKey(day) ? comfortBlocks[day] : new List<(DateTimeOffset start, string dayName)>();
                foreach (var b in blocks)
                {
                    var key = new TimeSpan(b.start.Hour, 0, 0).ToString();
                    dayObj[key] = new JsonObject { ["domesticHotWaterTemperature"] = "comfort" };
                }
                actionsCombined[day] = dayObj;
            }
            return (new JsonObject { ["0"] = new JsonObject { ["actions"] = actionsCombined } }, "Schedule generated (cross-day, limited activations)");
        }

        // Restore original per-day AddDay logic
        void AddDay(JsonArray source, DateTimeOffset date, string weekdayName)
        {
            var entries = new List<(DateTimeOffset start, decimal value, decimal volatility)>();
            foreach (var item in source)
            {
                if (item == null) continue;
                var startStr = item["start"]?.ToString();
                var valueStr = item["value"]?.ToString();
                var volatilityStr = item["volatility"]?.ToString();
                if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
                if (startTs.Date != date) continue;
                if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
                
                // Extract volatility if available (0 if not present = stable hourly data)
                decimal volatility = 0;
                if (!string.IsNullOrEmpty(volatilityStr))
                    decimal.TryParse(volatilityStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out volatility);
                
                entries.Add((startTs, val, volatility));
            }
            if (entries.Count == 0) return;
            var byHour = entries.ToDictionary(e => e.start.Hour, e => e.value);
            var volatilityByHour = entries.ToDictionary(e => e.start.Hour, e => e.volatility);
            
            // IMPROVEMENT 1: Use volatility to penalize hours with spikes
            // Select comfort hours: prefer low price AND low volatility
            var orderedByPrice = entries
                .OrderBy(e => e.value)  // Primary: cheapest
                .ThenBy(e => e.volatility)  // Secondary: most stable (avoid spikes)
                .ToList();
            
            // IMPROVEMENT 2: Merge consecutive cheap hours into blocks
            // Instead of expanding from one hour, find all cheap hours and merge consecutive ones
            var cheapCandidates = orderedByPrice
                .Take(comfortHoursDefault * 2)  // Get more candidates than needed
                .OrderBy(e => e.start.Hour)  // Sort by time
                .ToList();
            
            // Find consecutive blocks of cheap hours
            var cheapBlocks = new List<(int start, int end, decimal avgPrice)>();
            if (cheapCandidates.Count > 0)
            {
                int blockStart = cheapCandidates[0].start.Hour;
                int blockEnd = blockStart;
                decimal blockPriceSum = cheapCandidates[0].value;
                int blockCount = 1;
                
                for (int i = 1; i < cheapCandidates.Count; i++)
                {
                    int hour = cheapCandidates[i].start.Hour;
                    if (hour == blockEnd + 1)
                    {
                        // Consecutive hour - extend block
                        blockEnd = hour;
                        blockPriceSum += cheapCandidates[i].value;
                        blockCount++;
                    }
                    else
                    {
                        // Gap - save current block and start new one
                        cheapBlocks.Add((blockStart, blockEnd, blockPriceSum / blockCount));
                        blockStart = hour;
                        blockEnd = hour;
                        blockPriceSum = cheapCandidates[i].value;
                        blockCount = 1;
                    }
                }
                // Save final block
                cheapBlocks.Add((blockStart, blockEnd, blockPriceSum / blockCount));
            }
            
            // Pick the cheapest block(s) that cover comfortHoursDefault hours
            var cheapestHours = new HashSet<int>();
            foreach (var block in cheapBlocks.OrderBy(b => b.avgPrice))
            {
                for (int h = block.start; h <= block.end && cheapestHours.Count < comfortHoursDefault; h++)
                {
                    cheapestHours.Add(h);
                }
                if (cheapestHours.Count >= comfortHoursDefault) break;
            }
            
            // Log if we detected and avoided volatile hours
            if (cheapestHours.Count > 0)
            {
                var avgVolatility = cheapestHours.Average(h => volatilityByHour.TryGetValue(h, out var v) ? (double)v : 0.0);
                if (avgVolatility > 0)
                {
                    Console.WriteLine($"[Schedule] Selected comfort hours {string.Join(",", cheapestHours.OrderBy(x => x))} with avg volatility {avgVolatility:F2}");
                }
            }
            var turnOffHours = new HashSet<int>();
            if (entries.Count >= 4)
            {
                var desc = entries.OrderByDescending(e => e.value).ToList();
                int idx = (int)Math.Floor(desc.Count * turnOffPercentile) - 1; if (idx < 0) idx = 0;
                var percentileThreshold = desc[idx].value;
                var candidateHours = new List<int>();
                foreach (var (start, value, volatility) in entries)
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
                candidateHours.Sort(); 
                foreach (var h in candidateHours)
                {
                    bool nearPeers = false;
                    for (int nh = h - turnOffNeighborWindow; nh <= h + turnOffNeighborWindow; nh++)
                    {
                        if (nh == h) continue; 
                        if (byHour.TryGetValue(nh, out var pv)) 
                        { 
                            var baseVal = byHour[h]; 
                            if (pv > 0 && (double)Math.Abs(baseVal - pv) / (double)pv * 100.0 < turnOffSpikeDeltaPct) 
                            { 
                                nearPeers = true; 
                                break; 
                            } 
                        }
                    }
                    // For consecutive expensive hours, allow them even if they have similar neighbors
                    // Only skip if nearPeers and it's not part of a consecutive block above percentile
                    if (nearPeers) 
                    {
                        // Check if neighbors are also above percentile - if so, allow this as part of a block
                        bool hasExpensiveNeighbors = false;
                        for (int nh = Math.Max(0, h - 1); nh <= Math.Min(23, h + 1); nh++)
                        {
                            if (nh == h) continue;
                            if (byHour.TryGetValue(nh, out var neighborPrice) && neighborPrice >= percentileThreshold)
                            {
                                hasExpensiveNeighbors = true;
                                break;
                            }
                        }
                        if (!hasExpensiveNeighbors) continue; // Skip isolated spikes with similar neighbors
                    }
                    if (cheapestHours.Contains(h)) continue; 
                    turnOffHours.Add(h);
                }
            }
            // Issue #53: ECO mode removed - only use COMFORT and TURN_OFF
            // Logic: comfort hours get "comfort", spike hours get "turn_off", rest default to "turn_off"
            
            int earliestHour = entries.Min(e => e.start.Hour); 
            int latestHour = entries.Max(e => e.start.Hour);
            int? comfortStart = null; 
            int? comfortEnd = null; 
            if (cheapestHours.Count > 0) 
            { 
                comfortStart = cheapestHours.Min(); 
                comfortEnd = cheapestHours.Max(); 
            }
            
            // Collect turn_off blocks (expensive spikes)
            (int start, int end)? turnOffBlock = null;
            if (turnOffHours.Count > 0)
            {
                var ordered = turnOffHours.OrderBy(h => h).ToList(); 
                int blockStart = ordered[0]; 
                int prev = ordered[0]; 
                var blocks = new List<(int start, int end)>();
                
                for (int i = 1; i < ordered.Count; i++) 
                { 
                    var h = ordered[i]; 
                    if (h == prev + 1) 
                    { 
                        prev = h; 
                        continue; 
                    } 
                    blocks.Add((blockStart, prev)); 
                    blockStart = h; 
                    prev = h; 
                }
                blocks.Add((blockStart, prev)); 
                
                // Filter out blocks that overlap with comfort hours
                if (comfortStart.HasValue && comfortEnd.HasValue) 
                    blocks = blocks.Where(b => b.end < comfortStart.Value || b.start > comfortEnd.Value).ToList();
                
                // No need to limit turn_off block length with 2-mode system - use all detected spikes
                
                // Pick the most expensive block
                if (blocks.Count > 0) 
                { 
                    decimal Score((int start, int end) b) 
                    { 
                        decimal sum = 0; 
                        int c = 0; 
                        for (int h = b.start; h <= b.end; h++) 
                        { 
                            if (entries.Any(e => e.start.Hour == h)) 
                            { 
                                sum += entries.First(e => e.start.Hour == h).value; 
                                c++; 
                            } 
                        } 
                        return c == 0 ? 0 : sum / c; 
                    } 
                    turnOffBlock = blocks.OrderByDescending(b => Score(b)).First(); 
                }
            }
            
            // IMPROVEMENT 3: Simplified segment building since Daikin only heats at mode START
            // Key insight: Heating happens when switching TO comfort, not continuously
            // Therefore, we only need transitions at comfort block boundaries and expensive spikes
            
            var segments = new List<(int hour, string state)>(); 
            
            // Start with turn_off as default (energy saving mode)
            segments.Add((earliestHour, "turn_off"));
            
            // Add comfort block (consolidated consecutive cheap hours)
            if (comfortStart.HasValue) 
            { 
                segments.Add((comfortStart.Value, "comfort")); 
                // Since heating only happens at START of comfort mode, we heat once
                // then tank maintains temperature naturally for the comfort duration
                
                // After comfort block ends, return to turn_off
                if (comfortEnd.HasValue && comfortEnd.Value < latestHour) 
                    segments.Add((comfortEnd.Value + 1, "turn_off")); 
            }
            
            // Add turn_off for expensive spike blocks (if distinct from comfort)
            if (turnOffBlock.HasValue)
            {
                bool overlapWithComfort = comfortStart.HasValue && comfortEnd.HasValue &&
                    turnOffBlock.Value.start <= comfortEnd.Value && turnOffBlock.Value.end >= comfortStart.Value;
                
                if (!overlapWithComfort)
                {
                    // Only add explicit turn_off if it's not already covered by default turn_off state
                    bool needsExplicitTurnOff = comfortStart.HasValue && 
                        turnOffBlock.Value.start > comfortStart.Value && 
                        turnOffBlock.Value.start <= comfortEnd.GetValueOrDefault(23);
                    
                    if (needsExplicitTurnOff)
                    {
                        segments.Add((turnOffBlock.Value.start, "turn_off"));
                    }
                }
            }
            
            segments = segments.OrderBy(s => s.hour).Distinct().ToList();
            
            // Limit total segments to activationLimit
            if (segments.Count > activationLimit)
            {
                // Remove turn_off segments that come after comfort to simplify schedule
                int idxPost = -1; 
                if (comfortEnd.HasValue) 
                    idxPost = segments.FindIndex(s => s.state == "turn_off" && s.hour == comfortEnd.Value + 1);
                
                if (idxPost > 0 && segments.Count > activationLimit) 
                    segments.RemoveAt(idxPost);
            }
            
            if (segments.Count > activationLimit)
            {
                // Remove turn_off block entirely if we still exceed limit
                int idxTO = segments.FindIndex(s => s.state == "turn_off" && s.hour > earliestHour);
                if (idxTO >= 0)
                {
                    segments.RemoveAt(idxTO);
                }
            }
            
            if (segments.Count > activationLimit)
            {
                // Final fallback: remove segments from the end until we're under limit
                for (int i = segments.Count - 1; i >= 0 && segments.Count > activationLimit; i--)
                {
                    if (segments[i].state == "turn_off" && segments[i].hour != earliestHour) 
                        segments.RemoveAt(i);
                }
            }
            var dayObj = new JsonObject(); foreach (var seg in segments.OrderBy(s => s.hour)) { var key = new TimeSpan(seg.hour, 0, 0).ToString(); dayObj[key] = new JsonObject { ["domesticHotWaterTemperature"] = seg.state }; }
            actionsCombined[weekdayName] = dayObj;
        }

        // Generate per-day schedules
    // Removed duplicate and unused todayHas/tomorrowHas
        if (rawToday is { Count: > 0 })
        {
            var weekdayName = now.Date.DayOfWeek.ToString().ToLower();
            AddDay(rawToday, todayDate, weekdayName);
        }
        if (rawTomorrow is { Count: > 0 })
        {
            var weekdayName = now.Date.AddDays(1).DayOfWeek.ToString().ToLower();
            AddDay(rawTomorrow, tomorrowDate, weekdayName);
        }

        bool todayHas = false; bool tomorrowHas = false;
        if (rawToday is { Count: > 0 })
        {
            foreach (var item in rawToday)
            {
                var startStr = item?["start"]?.ToString();
                if (DateTimeOffset.TryParse(startStr, out var ts) && ts.Date == todayDate) { todayHas = true; break; }
            }
            // AddDay is now replaced by the new logic above; do not call it anymore
        }
        if (rawTomorrow is { Count: > 0 })
        {
            foreach (var item in rawTomorrow)
            {
                var startStr = item?["start"]?.ToString();
                if (DateTimeOffset.TryParse(startStr, out var ts) && ts.Date == tomorrowDate) { tomorrowHas = true; break; }
            }
            // AddDay is now replaced by the new logic above; do not call it anymore
        }

        if (actionsCombined.Count == 0) return (null, "No schedule generated");
        
        // Validate MaxComfortGapHours constraint across today and tomorrow
        if (maxComfortGapHours > 0 && maxComfortGapHours < 72)
        {
            var allComfortHours = new List<DateTimeOffset>();
            
            // Collect all comfort hours from both days
            foreach (var prop in actionsCombined)
            {
                var dayName = prop.Key;
                if (prop.Value is JsonObject dayObj)
                {
                    // Determine the date for this day
                    DateTimeOffset? dayDate = null;
                    
                    if (rawToday != null && rawToday.Count > 0)
                    {
                        var firstTodayStr = rawToday[0]?["start"]?.ToString();
                        if (DateTimeOffset.TryParse(firstTodayStr, out var firstToday))
                        {
                            if (firstToday.DayOfWeek.ToString().Equals(dayName, StringComparison.OrdinalIgnoreCase))
                                dayDate = firstToday.Date;
                        }
                    }
                    
                    if (!dayDate.HasValue && rawTomorrow != null && rawTomorrow.Count > 0)
                    {
                        var firstTomorrowStr = rawTomorrow[0]?["start"]?.ToString();
                        if (DateTimeOffset.TryParse(firstTomorrowStr, out var firstTomorrow))
                        {
                            if (firstTomorrow.DayOfWeek.ToString().Equals(dayName, StringComparison.OrdinalIgnoreCase))
                                dayDate = firstTomorrow.Date;
                        }
                    }
                    
                    if (!dayDate.HasValue) continue;
                    
                    // Find all comfort hours in this day
                    foreach (var hourProp in dayObj)
                    {
                        if (hourProp.Value is JsonObject stateObj && 
                            stateObj["domesticHotWaterTemperature"]?.ToString() == "comfort")
                        {
                            if (TimeSpan.TryParse(hourProp.Key, out var timeOfDay))
                            {
                                allComfortHours.Add(dayDate.Value.Add(timeOfDay));
                            }
                        }
                    }
                }
            }
            
            // Sort comfort hours chronologically and check gaps
            allComfortHours = allComfortHours.OrderBy(h => h).ToList();
            
            if (allComfortHours.Count > 1)
            {
                for (int i = 1; i < allComfortHours.Count; i++)
                {
                    var gap = (allComfortHours[i] - allComfortHours[i - 1]).TotalHours;
                    if (gap > maxComfortGapHours)
                    {
                        Console.WriteLine($"[Schedule][Warning] Gap of {gap:F1}h between comfort periods exceeds MaxComfortGapHours={maxComfortGapHours}");
                        Console.WriteLine($"[Schedule][Warning] Gap from {allComfortHours[i-1]:yyyy-MM-dd HH:mm} to {allComfortHours[i]:yyyy-MM-dd HH:mm}");
                    }
                }
            }
        }
        
        // IMPROVEMENT 4: Cleaner future-hours filtering and action limiting
        // Since we've simplified segment building, we should have fewer actions anyway
        var currentHour = now.Hour;
        var todayDayName = now.Date.DayOfWeek.ToString().ToLower();
        var tomorrowDayName = now.Date.AddDays(1).DayOfWeek.ToString().ToLower();
        
        // Process today's schedule: keep only future hours
        if (actionsCombined[todayDayName] is JsonObject todayActions)
        {
            var futureActions = new JsonObject();
            foreach (var prop in todayActions)
            {
                if (TimeSpan.TryParse(prop.Key, out var timeOfDay) && timeOfDay.Hours >= currentHour)
                {
                    futureActions[prop.Key] = prop.Value?.DeepClone();
                }
            }
            
            if (futureActions.Count > 0)
            {
                actionsCombined[todayDayName] = futureActions;
                Console.WriteLine($"[Schedule] Today: {futureActions.Count} future action(s) from hour {currentHour}");
            }
            else
            {
                actionsCombined.Remove(todayDayName);
                Console.WriteLine($"[Schedule] Today: No future actions, schedule only tomorrow");
            }
        }
        
        // Log tomorrow's schedule
        if (actionsCombined[tomorrowDayName] is JsonObject tomorrowActions)
        {
            Console.WriteLine($"[Schedule] Tomorrow: {tomorrowActions.Count} action(s) scheduled");
        }
        
        string message;
        if (todayHas && tomorrowHas) message = "Schedule generated (today + tomorrow)";
        else if (todayHas) message = "Schedule generated (today)";
        else if (tomorrowHas) message = "Schedule generated (tomorrow)";
        else message = "Schedule generated";
        var root = new JsonObject { ["0"] = new JsonObject { ["actions"] = actionsCombined } };
        return (root, message);
    }

    // ─── Flexible Eco Scheduling ──────────────────────────────────────

    /// <summary>Result from the flexible eco scheduling algorithm.</summary>
    public sealed record FlexibleEcoResult(
        DateTimeOffset? ScheduledHourUtc,  // The hour we want to schedule eco, or null if no schedule needed
        string State,                       // "scheduled", "waiting", "no_prices"
        string Message                      // Human-readable explanation
    );

    /// <summary>
    /// Parses price data from raw JSON arrays into a flat list of hourly entries.
    /// Works with both 15-minute and hourly data. Deduplicates by hour
    /// (tomorrow's data wins when both arrays contain the same hour).
    /// Returns sorted by start time.
    /// </summary>
    internal static List<(DateTimeOffset Start, decimal Price)> ParseHourlyPrices(JsonArray? rawToday, JsonArray? rawTomorrow)
    {
        var byHour = new Dictionary<DateTimeOffset, decimal>();

        void AddEntries(JsonArray? raw)
        {
            if (raw == null) return;
            foreach (var item in raw)
            {
                if (item == null) continue;
                var startStr = item["start"]?.ToString();
                var valueStr = item["value"]?.ToString();
                if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
                if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;

                // Truncate to hour boundary
                var hourKey = new DateTimeOffset(startTs.Year, startTs.Month, startTs.Day,
                    startTs.Hour, 0, 0, startTs.Offset);
                byHour[hourKey] = val; // last write wins (tomorrow overwrites today for dupes)
            }
        }

        AddEntries(rawToday);
        AddEntries(rawTomorrow);

        return byHour
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>
    /// Determines when to schedule the next eco (DHW ~45°C) run within the flexibility window.
    /// Picks the cheapest available hour in the eco window from today/tomorrow price data.
    /// </summary>
    public static FlexibleEcoResult GenerateFlexibleEco(
        JsonArray? rawToday,
        JsonArray? rawTomorrow,
        DateTimeOffset lastEcoRun,
        int intervalHours,
        int flexibilityHours,
        DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;

        // Compute eco window
        var windowStart = lastEcoRun.AddHours(intervalHours - flexibilityHours);
        var windowEnd = lastEcoRun.AddHours(intervalHours + flexibilityHours);

        // If window hasn't opened yet, return waiting
        if (now < windowStart)
        {
            var hoursUntilOpen = (windowStart - now).TotalHours;
            return new FlexibleEcoResult(
                null,
                "waiting",
                $"Eco window opens in {hoursUntilOpen:F1} hours (at {windowStart:yyyy-MM-dd HH:00} UTC)");
        }

        // Parse all available prices
        var allPrices = ParseHourlyPrices(rawToday, rawTomorrow);

        // Filter to future hours within the eco window
        var effectiveStart = now > windowStart ? now : windowStart;
        var candidates = allPrices
            .Where(p => p.Start >= effectiveStart && p.Start < windowEnd)
            .ToList();

        if (candidates.Count == 0)
        {
            return new FlexibleEcoResult(
                null,
                "no_prices",
                "No price data available in eco window");
        }

        // Pick the cheapest hour
        var cheapest = candidates.OrderBy(p => p.Price).First();
        return new FlexibleEcoResult(
            cheapest.Start,
            "scheduled",
            $"Eco scheduled at {cheapest.Start:HH:00} ({cheapest.Price:F2} SEK/kWh)");
    }
}
