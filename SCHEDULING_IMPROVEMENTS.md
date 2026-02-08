# Scheduling Improvements for Daikin Altherma 3 with 15-Minute Nordpool Pricing

**Date:** 2026-02-08  
**Context:** Analysis of current hourly-averaged scheduling vs. potential 15-minute optimized scheduling

## Executive Summary

The current implementation aggregates 15-minute Nordpool prices to hourly averages, losing 75% of price data granularity. This results in suboptimal heating schedules that miss short price spikes and don't leverage the Daikin Altherma 3's thermal inertia capabilities. Implementing 15-minute aware scheduling could reduce electricity costs by an estimated **15-25%** while improving DHW availability.

---

## Current Implementation Analysis

### Data Flow
```
Nordpool API (96 data points/day, 15-min intervals)
    ↓
AggregateToHourly() - Line 10, ScheduleAlgorithm.cs
    ↓
24 hourly averages
    ↓
Hourly comfort/turn_off decisions
    ↓
Max 4-8 mode changes per day (2x 12-hour windows)
```

### Critical Issues

#### 1. **Granularity Loss** (High Impact)
```csharp
// Current: ScheduleAlgorithm.cs, line 10-54
private static JsonArray AggregateToHourly(JsonArray? rawData)
{
    // Averages four 15-minute prices into one hourly price
    var avgValue = entries.Average(e => e.value);
}
```

**Problem:** A 15-minute spike of 2.50 SEK/kWh at 14:15-14:30 within an otherwise cheap hour (avg 0.80 SEK/kWh) is invisible after averaging to 0.93 SEK/kWh.

**Real-world impact:**
- **Winter peak prices:** 14:00-15:00 might average 1.20 SEK/kWh, but contain a 15-minute spike at 3.50 SEK/kWh (14:15-14:30)
- **Current system:** Heats during entire hour (1.5 kWh × 1.20 = 1.80 SEK)
- **Optimized:** Skip 15-minute spike, heat rest of hour (1.125 kWh × 0.85 = 0.96 SEK - **46% savings**)

#### 2. **Thermal Inertia Ignored** (Medium-High Impact)

**Daikin Altherma 3 DHW Tank Characteristics:**
- Volume: 180-300 liters (typical residential)
- Temperature: 50-60°C (comfort), 35-40°C (eco - removed in Issue #53)
- Heat loss: ~0.5-1.0°C per hour (well-insulated modern tanks)
- **Effective hot water availability: 6-12 hours after heating stops**

**Current algorithm doesn't leverage this:**
```csharp
// ScheduleAlgorithm.cs, line 258-265
// After comfort block, immediately returns to turn_off
if (comfortEnd.HasValue && comfortEnd.Value < latestHour) 
    AddSegment(comfortEnd.Value + 1, "turn_off");
```

**Problem:** If comfort period is 03:00-06:00, system turns OFF at 07:00, but tank stays hot until ~13:00-18:00. No need for immediate turn-off decision.

#### 3. **No Preheating Strategy** (Medium Impact)

**Missing opportunity:**
- Cheap prices often occur at night (00:00-06:00)
- Peak DHW demand is morning (06:00-09:00) and evening (17:00-21:00)
- **Current:** Might heat 03:00-06:00 (cheapest), but peak usage at 07:00-09:00 when tank has started cooling
- **Better:** Heat 05:00-08:00 (still cheap) + leverage thermal inertia through morning peak

#### 4. **Excessive Mode Changes** (Low-Medium Impact)

**Current behavior:**
- Up to 8 mode changes per day (2 schedules × 4 actions each)
- Heat pumps work best with **longer continuous runs** (better COP, less wear)
- Frequent on/off cycling:
  - Reduces lifespan (compressor stress)
  - Lower average COP during startup phases
  - Increased acoustic noise from frequent compressor starts

---

## Proposed Improvements

### **Priority 1: 15-Minute Price Awareness (High ROI)**

#### Implementation Strategy

**Option A: Enhanced Hourly Decisions** (Easier, 60% of benefit)
Keep hourly scheduling but use 15-minute data for smarter decisions:

```csharp
// New method in ScheduleAlgorithm.cs
private static (decimal min, decimal max, decimal avg, decimal volatility) AnalyzeHourPrices(
    JsonArray rawData, int hour, DateTime date)
{
    var hourPrices = rawData
        .Where(item => ParseTimestamp(item) is DateTimeOffset ts && 
                       ts.Hour == hour && ts.Date == date)
        .Select(item => ParsePrice(item))
        .ToList();
    
    if (hourPrices.Count == 0) return (0, 0, 0, 0);
    
    return (
        min: hourPrices.Min(),
        max: hourPrices.Max(),
        avg: hourPrices.Average(),
        volatility: hourPrices.Max() - hourPrices.Min()
    );
}

// Enhanced comfort hour selection
private static HashSet<int> SelectComfortHours(
    JsonArray rawData, DateTime date, int targetHours)
{
    var hourAnalysis = Enumerable.Range(0, 24)
        .Select(h => new {
            Hour = h,
            Metrics = AnalyzeHourPrices(rawData, h, date)
        })
        .Where(h => h.Metrics.avg > 0)
        .OrderBy(h => h.Metrics.avg) // Prioritize low average
        .ThenBy(h => h.Metrics.volatility) // Prefer stable hours
        .Take(targetHours * 2) // Get candidates
        .ToList();
    
    // Favor hours without extreme spikes
    var selected = hourAnalysis
        .Where(h => h.Metrics.max < h.Metrics.avg * 1.5m) // No >50% spikes
        .OrderBy(h => h.Metrics.avg)
        .Take(targetHours)
        .Select(h => h.Hour)
        .ToHashSet();
    
    return selected;
}

// Enhanced turn-off detection
private static HashSet<int> DetectExpensiveHours(
    JsonArray rawData, DateTime date, double percentile)
{
    var hourAnalysis = Enumerable.Range(0, 24)
        .Select(h => new {
            Hour = h,
            Metrics = AnalyzeHourPrices(rawData, h, date)
        })
        .Where(h => h.Metrics.avg > 0)
        .ToList();
    
    var threshold = hourAnalysis
        .OrderByDescending(h => h.Metrics.max)
        .Skip((int)(hourAnalysis.Count * (1 - percentile)))
        .First().Metrics.max;
    
    // Mark hours with ANY 15-minute interval above threshold
    return hourAnalysis
        .Where(h => h.Metrics.max > threshold)
        .Select(h => h.Hour)
        .ToHashSet();
}
```

**Benefits:**
- Avoids heating during hours with extreme 15-minute spikes
- Prefers stable, consistently cheap hours
- **Estimated savings: 10-15% vs. current hourly averaging**
- Low implementation complexity (reuse existing hourly framework)

---

**Option B: Sub-Hourly Scheduling** (Harder, 90% of benefit)
Generate 15-minute precision schedules (if Daikin API supports):

```csharp
// Theoretical - requires Daikin API investigation
public static JsonNode GenerateQuarterHourSchedule(
    JsonArray raw15MinData, 
    int comfortQuarters, // e.g., 12 quarters = 3 hours
    double turnOffPercentile)
{
    // Select cheapest 15-minute intervals across the day
    var sorted = raw15MinData
        .Select(item => new {
            Timestamp = ParseTimestamp(item),
            Price = ParsePrice(item)
        })
        .OrderBy(x => x.Price)
        .ToList();
    
    var comfortIntervals = sorted
        .Take(comfortQuarters)
        .OrderBy(x => x.Timestamp)
        .ToList();
    
    // Merge consecutive intervals into comfort blocks
    var blocks = MergeConsecutiveIntervals(comfortIntervals);
    
    // Generate Daikin schedule with 15-minute precision
    // NOTE: Need to verify Daikin API supports this format
    return BuildDaikinSchedule(blocks);
}
```

**Requirements:**
- Research Daikin ONECTA API capabilities for sub-hourly schedules
- If not supported, **not feasible** without custom hardware integration

**Estimated savings:** 20-25% vs. current (if API supports)

---

### **Priority 2: Thermal Inertia Modeling (Medium ROI)**

#### Implementation: Predictive Heat Retention

```csharp
// New class: ThermalModel.cs
public class DhwTankThermalModel
{
    private const decimal HEAT_LOSS_PER_HOUR = 0.7m; // °C/hour (typical)
    private const decimal COMFORT_TEMP = 55.0m; // °C
    private const decimal MIN_USABLE_TEMP = 42.0m; // °C (minimum for shower)
    
    /// <summary>
    /// Calculates how long tank will stay above minimum usable temperature
    /// </summary>
    public static int CalculateHotWaterDurationHours(
        decimal currentTemp, 
        decimal ambientTemp = 20.0m)
    {
        if (currentTemp < MIN_USABLE_TEMP) return 0;
        
        // Simplified Newton's cooling law
        decimal tempDrop = currentTemp - MIN_USABLE_TEMP;
        decimal hoursToMinTemp = tempDrop / HEAT_LOSS_PER_HOUR;
        
        return (int)Math.Ceiling(hoursToMinTemp);
    }
    
    /// <summary>
    /// Predicts optimal heating timing before expected usage
    /// </summary>
    public static int CalculatePreheatingAdvanceHours(
        DateTimeOffset usageTime,
        decimal targetTemp = COMFORT_TEMP)
    {
        // Heat pump typically takes 1-2 hours to heat full tank from cold
        // Add buffer for thermal stabilization
        return 2; // Conservative estimate
    }
}

// Integration with ScheduleAlgorithm
public static (JsonNode? schedulePayload, string message) GenerateWithThermalAwareness(
    JsonArray? rawToday,
    JsonArray? rawTomorrow,
    int comfortHoursDefault,
    double turnOffPercentile,
    int activationLimit,
    int maxComfortGapHours,
    IConfiguration config,
    DateTimeOffset? nowOverride = null,
    int[]? expectedUsageHours = null) // NEW: e.g., [7, 8, 18, 19, 20]
{
    expectedUsageHours ??= new[] { 7, 8, 18, 19, 20 }; // Default morning/evening peaks
    
    // Find cheapest hours that align with preheating windows
    var preheatingWindows = expectedUsageHours
        .Select(usageHour => new {
            UsageHour = usageHour,
            OptimalHeatingStart = usageHour - ThermalModel.CalculatePreheatingAdvanceHours(
                DateTimeOffset.Now.Date.AddHours(usageHour))
        })
        .ToList();
    
    // Prioritize cheap hours that fall within preheating windows
    // ... (modify comfort hour selection logic)
}
```

**Benefits:**
- **Reduced comfort hours needed:** 3 hours → 2 hours (if timed correctly)
- **Better DHW availability:** Hot water available during actual usage times
- **Estimated savings: 5-10%** (fewer heating hours)

---

### **Priority 3: Reduce Mode Change Frequency (Low-Medium ROI)**

#### Implementation: Penalty for Excessive Switching

```csharp
// Add to ScheduleAlgorithm.cs
private static List<(int hour, string state)> OptimizeForLongerRuns(
    List<(int hour, string state)> segments,
    int minRunDurationHours = 2)
{
    // Merge short segments into longer runs
    var optimized = new List<(int hour, string state)>();
    
    for (int i = 0; i < segments.Count; i++)
    {
        var current = segments[i];
        
        // Look ahead to see if next segment is too short
        if (i + 1 < segments.Count)
        {
            var next = segments[i + 1];
            int duration = next.hour - current.hour;
            
            if (duration < minRunDurationHours && 
                current.state == "comfort" && 
                next.state == "turn_off")
            {
                // Extend comfort period by 1 hour instead of frequent switching
                optimized.Add((current.hour, current.state));
                i++; // Skip next segment
                continue;
            }
        }
        
        optimized.Add(current);
    }
    
    return optimized;
}
```

**Benefits:**
- Longer heat pump run times → better COP (2-5% efficiency gain)
- Reduced compressor wear (longer lifespan)
- Lower acoustic noise (fewer starts)

---

## Implementation Roadmap

### **Phase 1: Quick Wins (1-2 weeks)**

1. **Keep 15-minute data, enhance hourly decisions** (Priority 1, Option A)
   - Modify `AnalyzeHourPrices()` to detect intra-hour volatility
   - Adjust comfort hour selection to avoid spikey hours
   - **Expected savings: 10-15%**

2. **Add thermal inertia grace period** (Priority 2, simple version)
   - Don't immediately turn_off after comfort period
   - Allow 2-3 hour "coast" period leveraging tank heat
   - **Expected savings: 3-5%**

### **Phase 2: Moderate Complexity (4-6 weeks)**

3. **Implement preheating strategy** (Priority 2)
   - User-configurable expected usage hours (default: 7-9, 18-21)
   - Shift comfort hours to align with usage peaks
   - **Expected savings: 5-8%**

4. **Optimize for longer runs** (Priority 3)
   - Penalty function for excessive mode changes
   - Merge short segments
   - **Expected savings: 2-5% (efficiency), lifespan extension**

### **Phase 3: Advanced (8-12 weeks, if API supports)**

5. **Sub-hourly scheduling** (Priority 1, Option B)
   - Research Daikin ONECTA API for 15-minute schedule support
   - If supported, implement quarter-hour precision
   - **Expected savings: Additional 5-10% beyond hourly optimization**

6. **Machine learning for usage prediction** (Future)
   - Learn household DHW usage patterns over time
   - Adaptive preheating timing
   - **Expected savings: 3-7% (optimized preheating)**

---

## Testing Strategy

### A/B Testing Framework

```csharp
// Add to appsettings.json
"Schedule": {
  "AlgorithmVersion": "enhanced-15min", // or "legacy-hourly-avg"
  "EnableThermalModel": true,
  "EnablePreheating": true,
  "ExpectedUsageHours": [7, 8, 18, 19, 20],
  "MinHeatPumpRunHours": 2,
  "TankHeatLossPerHour": 0.7
}

// Logging for comparison
Console.WriteLine($"[Schedule][Algorithm={version}] Cost estimate: " +
    $"{CalculateDailyCost(schedule, prices):F2} SEK");
```

### Metrics to Track

1. **Cost savings:**
   - Total kWh purchased
   - Average price per kWh
   - Daily/weekly/monthly cost comparison

2. **DHW availability:**
   - User-reported hot water availability
   - Tank temperature logs (if available via API)

3. **Heat pump efficiency:**
   - Average COP (if measurable)
   - Number of compressor starts per day
   - Total runtime hours

---

## Configuration Recommendations

### New Settings

```json
{
  "Schedule": {
    "Use15MinutePrices": true,
    "MaxIntraHourVolatility": 0.50,
    
    "ThermalModel": {
      "Enabled": true,
      "TankVolumeLiters": 180,
      "HeatLossPerHour": 0.7,
      "ComfortTemperature": 55.0,
      "MinUsableTemperature": 42.0
    },
    
    "Preheating": {
      "Enabled": true,
      "ExpectedUsageHours": [7, 8, 18, 19, 20],
      "PreheatingAdvanceHours": 2
    },
    
    "HeatPump": {
      "MinRunDurationHours": 2,
      "PreferLongerRuns": true
    }
  }
}
```

---

## API Investigation Required

### Daikin ONECTA API Questions

Before implementing sub-hourly scheduling, verify:

1. **Schedule resolution:**
   - Does API accept 15-minute intervals?
   - What's the minimum interval: 15min, 30min, 60min?
   - Current assumption: 60-minute based on TimeSpan keys like "02:00:00"

2. **Action limits:**
   - Current: 4 actions per 12-hour window (8/day)
   - Does this limit apply to 15-minute actions?
   - Would 12 quarterly actions count as 12 or 3 (per hour)?

3. **Schedule format:**
```csharp
// Current hourly format
{
  "0": {
    "actions": {
      "monday": {
        "03:00:00": { "domesticHotWaterTemperature": "comfort" },
        "06:00:00": { "domesticHotWaterTemperature": "turn_off" }
      }
    }
  }
}

// Hypothetical 15-minute format (to verify)
{
  "0": {
    "actions": {
      "monday": {
        "03:00:00": { "domesticHotWaterTemperature": "comfort" },
        "03:15:00": { "domesticHotWaterTemperature": "turn_off" },  // Valid?
        "03:30:00": { "domesticHotWaterTemperature": "comfort" }   // Valid?
      }
    }
  }
}
```

**Action items:**
- Review [Daikin API documentation](https://developer.cloud.daikineurope.com/docs)
- Test quarter-hour schedule submission via `/gateway-devices/{id}/management-points/{embeddedId}/schedule`
- If not supported, contact Daikin developer support for roadmap

---

## Expected Overall Impact

### Conservative Estimate
- Enhanced hourly with 15-min awareness: **10-15% savings**
- Thermal inertia modeling: **3-5% savings**
- Preheating optimization: **3-5% savings**
- Longer runs optimization: **2-3% efficiency**

**Total: 18-28% cost reduction vs. current hourly averaging**

### Aggressive Estimate (if sub-hourly supported)
- Sub-hourly scheduling: **20-25% savings**
- All other optimizations: **8-13% additional**

**Total: 28-38% cost reduction**

### Real-world Example
- Current monthly cost: 400 SEK (winter, northern Sweden)
- After optimization: **288-328 SEK**
- **Annual savings: 864-1,344 SEK per household**

For 10,000 users: **8.6-13.4 million SEK annual savings**

---

## References & Further Reading

1. **Heat Pump Thermal Dynamics:**
   - "Coefficient of Performance in Residential Heat Pumps" (IEA HPT Annex 52)
   - Typical Altherma 3 COP: 2.5-4.0 depending on outdoor temperature

2. **DHW Tank Heat Loss:**
   - European Standard EN 12897 (tank standby losses)
   - Modern A-rated tanks: 0.5-0.8°C/hour heat loss

3. **Nordpool Market:**
   - Intraday price volatility studies
   - 15-minute spikes can be 200-500% above hourly average during winter peaks

4. **Daikin ONECTA API:**
   - [Developer Portal](https://developer.cloud.daikineurope.com/)
   - Current API documentation silent on sub-hourly scheduling

---

## Next Steps

1. **Immediate:** Implement Priority 1A (enhanced hourly with 15-min awareness)
2. **Week 2:** Deploy A/B testing framework with cost tracking
3. **Week 4:** Analyze 2 weeks of data, present findings
4. **Month 2:** Implement thermal inertia and preheating if Priority 1A shows >10% savings
5. **Month 3:** Research Daikin API sub-hourly capabilities (contact Daikin if needed)

---

**Document Version:** 1.0  
**Last Updated:** 2026-02-08  
**Next Review:** After Phase 1 implementation (4 weeks)
