## Plan: Price Threshold Transparency & Smart Scheduling

Enhance the scheduling algorithm with cubic sliding threshold and price trend awareness, then expose computed thresholds to users through Settings and Dashboard UI so they can understand what prices trigger scheduling actions.

**Phases (6 phases)**

### Phase 1: Zone-level price cache + Cubic threshold + Trend factor
- **Objective:** Create a cached zone-level price data service; change sliding threshold from linear to cubic (x³); add trend factor; add price snapshot retention config
- **Files to Modify:** `HistoricalPriceAnalyzer.cs`
- **Tests:** Cubic curve values, trend factor (rising/falling/stable), cache hit/miss, ComputePercentile from cached array
- **Steps:**
  1. Add `ZonePriceCache` — a `ConcurrentDictionary<string, CachedZoneData>` with sorted prices array, computed timestamp, trend factor, max price, daily averages. TTL: 1 hour
  2. Add `GetOrComputeZoneDataAsync(repo, zone, lookbackDays=60)` — fetches once per zone per TTL, returns cached sorted prices
  3. Refactor `GetHistoricalStatsAsync` to use the cache: fetch zone data once, compute any percentile as O(1) array lookup
  4. Change `ComputeSlidingThreshold` to cubic (`progress³`)
  5. Add `ComputeTrendFactor` — 7d avg / 30d avg, clamped 0.5–2.0
  6. Extend `HistoricalPriceStats` with `TrendFactor` and `DailyAverages` (list of date+avg for trend visualization)

### Phase 2: Backend `/api/prices/threshold` + `/api/prices/trend` endpoints
- **Objective:** Expose price threshold and daily trend data via API
- **Files to Modify:** `Program.cs`
- **Tests:** Test endpoint returns expected shape, handles missing data gracefully
- **Steps:**
  1. `GET /api/prices/threshold?percentile=0.1` → `{ percentile, threshold, maxPrice, trendFactor, currency, lookbackDays }`
  2. `GET /api/prices/trend` → `{ zone, dailyAverages: [{ date, avgPrice }], trendFactor, lookbackDays }` — daily averages for 30 days
  3. Both use the zone-level cache, so 1000 concurrent users hit the same cached data

### Phase 3: Enrich `/api/user/flexible-state` with sliding threshold
- **Objective:** Add current cubic sliding threshold to flexible-state response
- **Files to Modify:** `Program.cs`
- **Steps:**
  1. Use cached zone data (O(1) per user) to compute trend-adjusted base + cubic sliding threshold
  2. Add `CurrentThreshold`, `BaseThreshold`, `TrendFactor`, `Currency` to response
  3. All per-user computation is trivial math — no DB queries per user

### Phase 4: Frontend API + hooks
- **Objective:** TypeScript types, API methods, React hooks
- **Files to Create/Modify:** `frontend-v2/src/types/api.ts`, `frontend-v2/src/api/client.ts`, new hooks
- **Steps:**
  1. Add `PriceThresholdResponse`, `PriceTrendResponse` types
  2. Extend `FlexibleState` with threshold/trend fields
  3. Add API methods and hooks (5min stale time, leverages backend cache)

### Phase 5: Settings UI — threshold display
- **Objective:** Show computed price next to percentile sliders with explanatory text
- **Files to Modify:** `frontend-v2/src/pages/SettingsPage.tsx`
- **Steps:**
  1. Both percentile sliders show "Currently ≈ X öre/kWh"
  2. Trend indicator next to Price Patience (↓ falling / → stable / ↑ rising)
  3. Help text: "Based on 60-day rolling price history — changes as new prices are recorded"

### Phase 6: Dashboard UI — threshold + trend visualization
- **Objective:** Show current price target, trend on dashboard; add reference line and trend chart
- **Files to Modify:** `frontend-v2/src/pages/DashboardPage.tsx`, `frontend-v2/src/components/PriceChart.tsx`, new `TrendChart.tsx`
- **Steps:**
  1. Flexible Status card: "Accepting prices ≤ X öre/kWh" + base target + trend indicator
  2. PriceChart: Add ReferenceLine at current threshold when inside a comfort window
  3. TrendChart: Small area chart showing 30-day daily averages below main price chart
