using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public static class ScheduleAlgorithm
{
    public enum LogicType { PerDayOriginal, CrossDayCheapestLimited }

    public static (JsonNode? schedulePayload, string message) Generate(
        JsonArray? rawToday,
        JsonArray? rawTomorrow,
        IConfiguration config,
        DateTimeOffset? nowOverride = null,
        LogicType logic = LogicType.PerDayOriginal)
    {
        var now = nowOverride ?? DateTimeOffset.Now;
        var todayDate = now.Date;
        var tomorrowDate = todayDate.AddDays(1);
        var actionsCombined = new JsonObject();
    int comfortHoursDefault = int.TryParse(config["Schedule:ComfortHours"], out var ch) ? Math.Clamp(ch, 1, 12) : 3;
        int turnOffMaxConsec = int.TryParse(config["Schedule:TurnOffMaxConsecutive"], out var moc) ? Math.Clamp(moc, 1, 6) : 2;
        double turnOffPercentile = double.TryParse(config["Schedule:TurnOffPercentile"], out var tp) ? Math.Clamp(tp, 0.5, 0.99) : 0.9;
        double turnOffSpikeDeltaPct = double.TryParse(config["Schedule:TurnOffSpikeDeltaPct"], out var sd) ? Math.Clamp(sd, 1, 200) : 10;
        int turnOffNeighborWindow = int.TryParse(config["Schedule:TurnOffNeighborWindow"], out var nw) ? Math.Clamp(nw, 1, 4) : 2;
        decimal comfortNextHourMaxIncreasePct = decimal.TryParse(config["Schedule:ComfortNextHourMaxIncreasePct"], out var cni) ? Math.Clamp(cni, 0, 500) : 25m;
    int activationLimit = int.TryParse(config["Schedule:MaxActivationsPerDay"], out var mpd) ? Math.Clamp(mpd, 1, 24) : 6; // new configurable activation limit (default 6)

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
            var entries = new List<(DateTimeOffset start, decimal value)>();
            foreach (var item in source)
            {
                if (item == null) continue;
                var startStr = item["start"]?.ToString();
                var valueStr = item["value"]?.ToString();
                if (!DateTimeOffset.TryParse(startStr, out var startTs)) continue;
                if (startTs.Date != date) continue;
                if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
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
                int idx = (int)Math.Floor(desc.Count * turnOffPercentile) - 1; if (idx < 0) idx = 0;
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
                    {
                        if (nh == h) continue; if (byHour.TryGetValue(nh, out var pv)) { var baseVal = byHour[h]; if (pv > 0 && (double)Math.Abs(baseVal - pv) / (double)pv * 100.0 < turnOffSpikeDeltaPct) { nearPeers = true; break; } }
                    }
                    if (nearPeers) continue; if (cheapestHours.Contains(h)) continue; turnOffHours.Add(h);
                }
            }
            int earliestHour = entries.Min(e => e.start.Hour); int latestHour = entries.Max(e => e.start.Hour);
            int? comfortStart = null; int? comfortEnd = null; if (cheapestHours.Count > 0) { comfortStart = cheapestHours.Min(); comfortEnd = cheapestHours.Max(); }
            (int start, int end)? turnOffBlock = null;
            if (turnOffHours.Count > 0)
            {
                var ordered = turnOffHours.OrderBy(h => h).ToList(); int blockStart = ordered[0]; int prev = ordered[0]; var blocks = new List<(int start, int end)>();
                for (int i = 1; i < ordered.Count; i++) { var h = ordered[i]; if (h == prev + 1) { prev = h; continue; } blocks.Add((blockStart, prev)); blockStart = h; prev = h; }
                blocks.Add((blockStart, prev)); if (comfortStart.HasValue && comfortEnd.HasValue) blocks = blocks.Where(b => b.end < comfortStart.Value || b.start > comfortEnd.Value).ToList();
                blocks = blocks.Select(b => (b.start, end: b.start + Math.Min(1, b.end - b.start))).ToList();
                if (blocks.Count > 0) { decimal Score((int start, int end) b) { decimal sum = 0; int c = 0; for (int h = b.start; h <= b.end; h++) { if (entries.Any(e => e.start.Hour == h)) { sum += entries.First(e => e.start.Hour == h).value; c++; } } return c == 0 ? 0 : sum / c; } turnOffBlock = blocks.OrderByDescending(b => Score(b)).First(); }
            }
            bool turnOffBeforeComfort = false; if (turnOffBlock.HasValue && comfortStart.HasValue) turnOffBeforeComfort = turnOffBlock.Value.end < comfortStart.Value;
            var segments = new List<(int hour, string state)>(); void AddSegment(int hour, string state) { if (!segments.Any(s => s.hour == hour)) segments.Add((hour, state)); else { for (int i = 0; i < segments.Count; i++) { if (segments[i].hour == hour) { segments[i] = (hour, state); break; } } } }
            AddSegment(earliestHour, "eco"); if (turnOffBlock.HasValue && turnOffBeforeComfort) { AddSegment(turnOffBlock.Value.start, "turn_off"); int reActHour = turnOffBlock.Value.end + 1; if (!comfortStart.HasValue || reActHour < comfortStart.Value) AddSegment(reActHour, "eco"); }
            if (comfortStart.HasValue) { AddSegment(comfortStart.Value, "comfort"); if (comfortEnd.HasValue && comfortEnd.Value < latestHour) AddSegment(comfortEnd.Value + 1, "eco"); }
            if (turnOffBlock.HasValue && !turnOffBeforeComfort) { AddSegment(turnOffBlock.Value.start, "turn_off"); int reActHour = turnOffBlock.Value.end + 1; if (reActHour <= latestHour) AddSegment(reActHour, "eco"); }
            segments = segments.OrderBy(s => s.hour).ToList();
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].state == "turn_off")
                {
                    int start = segments[i].hour;
                    int next = (i + 1 < segments.Count) ? segments[i + 1].hour : latestHour + 1;
                    if (next - start > 2)
                    {
                        int react = start + 2;
                        if (segments.Count < activationLimit)
                            segments.Insert(i + 1, (react, "eco"));
                        else
                        {
                            segments.RemoveAt(i); i--; continue;
                        }
                    }
                }
            }
            if (segments.Count > activationLimit)
            {
                int idxPost = -1; if (comfortEnd.HasValue) idxPost = segments.FindIndex(s => s.state == "eco" && s.hour == comfortEnd.Value + 1);
                if (idxPost > 0 && segments.Count > activationLimit) segments.RemoveAt(idxPost);
            }
            if (segments.Count > activationLimit)
            {
                int idxTO = segments.FindIndex(s => s.state == "turn_off");
                if (idxTO >= 0)
                {
                    if (idxTO + 1 < segments.Count && segments[idxTO + 1].state == "eco") segments.RemoveAt(idxTO + 1);
                    segments.RemoveAt(idxTO);
                }
            }
            if (segments.Count > activationLimit)
            {
                for (int i = segments.Count - 1; i >= 0 && segments.Count > activationLimit; i--)
                {
                    if (segments[i].state == "eco" && segments[i].hour != earliestHour) segments.RemoveAt(i);
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
        string message;
        if (todayHas && tomorrowHas) message = "Schedule generated (today + tomorrow)";
        else if (todayHas) message = "Schedule generated (today)";
        else if (tomorrowHas) message = "Schedule generated (tomorrow)";
        else message = "Schedule generated";
        var root = new JsonObject { ["0"] = new JsonObject { ["actions"] = actionsCombined } };
        return (root, message);
    }
}
