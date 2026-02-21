## Plan: Flexible Eco & Comfort DHW Scheduling

Add an optional "Flexible" scheduling mode (Issue #74) alongside the existing "Classic" mode. Flexible mode re-introduces the Daikin-native `eco` state and uses interval-based scheduling with price optimization. Eco runs at configurable intervals (6–36h) timed to the cheapest available price. Comfort runs at configurable intervals (days) with a **sliding historical price threshold** that becomes progressively more lenient as the comfort window advances — making it increasingly likely to schedule the further into the window we get. Even at the deadline day, we have ~36h of available prices to pick the cheapest from (never forced to a single hour). Historical percentiles are computed from 60 days of stored `PriceSnapshot` data.

**Phases: 8**

1. **Phase 1: Data Model & Migration**
    - **Objective:** Extend `UserSettings` with flexible scheduling fields and create a `FlexibleScheduleState` entity to track last eco/comfort run times. Add EF Core migration.
    - **Files/Functions to Modify/Create:**
        - `data/Entities/UserSettings.cs` — add `SchedulingMode` (string, "Classic"/"Flexible"), `EcoIntervalHours` (int, default 24), `EcoFlexibilityHours` (int, default 12), `ComfortIntervalDays` (int, default 21), `ComfortFlexibilityDays` (int, default 7), `ComfortEarlyPercentile` (double, default 0.10)
        - `data/Entities/FlexibleScheduleState.cs` — new entity with `UserId` (PK), `LastEcoRunUtc` (DateTimeOffset?), `LastComfortRunUtc` (DateTimeOffset?), `NextScheduledComfortUtc` (DateTimeOffset?) for re-optimization tracking
        - `data/PrisstyrningDbContext.cs` — add `DbSet<FlexibleScheduleState>`
        - `data/Repositories/FlexibleScheduleStateRepository.cs` — new repo with `GetOrCreateAsync`, `UpdateEcoRunAsync`, `UpdateComfortRunAsync` (clears NextScheduledComfortUtc), `ScheduleComfortRunAsync`, `ClearScheduledComfortAsync`
        - `data/Repositories/UserSettingsRepository.cs` — update `LoadScheduleSettingsAsync` to include new fields, update `UserScheduleSettings` record
        - EF migration via `dotnet ef migrations add AddFlexibleScheduling`
    - **Tests to Write:**
        - `FlexibleScheduleStateRepositoryTests` — GetOrCreate returns defaults, UpdateEcoRun persists, UpdateComfortRun persists
        - `UserSettingsRepositoryTests` — LoadScheduleSettings returns flexible fields with correct defaults and clamping
    - **Steps:**
        1. Write tests for FlexibleScheduleStateRepository (GetOrCreate, UpdateEco, UpdateComfort)
        2. Write tests for UserSettingsRepository loading new flexible fields
        3. Run tests — verify they fail (entities/repos don't exist yet)
        4. Create `FlexibleScheduleState` entity
        5. Update `UserSettings` entity with new properties
        6. Update `PrisstyrningDbContext` with new DbSet
        7. Create `FlexibleScheduleStateRepository`
        8. Update `UserSettingsRepository` and `UserScheduleSettings` record
        9. Generate EF migration
        10. Run tests — verify they pass

2. **Phase 2: Historical Price Percentile Service**
    - **Objective:** Create a service that computes price percentiles from the last 60 days of stored `PriceSnapshot` data. This is used by the comfort algorithm to determine if a current price is "historically cheap" enough to trigger an early comfort run.
    - **Files/Functions to Modify/Create:**
        - `HistoricalPriceAnalyzer.cs` — new static class with `ComputePercentile(IEnumerable<decimal> prices, double percentile)` and `GetHistoricalThreshold(PriceRepository repo, string zone, double percentile, int lookbackDays = 60)`
    - **Tests to Write:**
        - `HistoricalPriceAnalyzerTests` — empty list returns null, single value returns that value, 100 values returns correct percentile, handles missing days gracefully
    - **Steps:**
        1. Write tests for `ComputePercentile` with various edge cases
        2. Write tests for `GetHistoricalThreshold` using mocked/in-memory PriceRepository
        3. Run tests — verify they fail
        4. Implement `HistoricalPriceAnalyzer` with percentile computation using sorted array interpolation
        5. Run tests — verify they pass

3. **Phase 3: Flexible Eco Scheduling Algorithm**
    - **Objective:** Add eco scheduling logic to `ScheduleAlgorithm`. Given a last eco run time and interval/flexibility settings, determine the eco scheduling window and pick the cheapest hour within it from available prices.
    - **Files/Functions to Modify/Create:**
        - `ScheduleAlgorithm.cs` — add `GenerateFlexibleEco(JsonArray? rawToday, JsonArray? rawTomorrow, DateTimeOffset lastEcoRun, int intervalHours, int flexibilityHours, DateTimeOffset? nowOverride)` returning `FlexibleEcoResult` with scheduled eco hour (if any), state, and message
        - `ScheduleAlgorithm.cs` — add `FlexibleEcoResult` record
    - **Tests to Write:**
        - `FlexibleEcoAlgorithmTests` — no prices returns no schedule, eco window not yet open returns no schedule, eco window open picks cheapest hour, eco at deadline forces cheapest available, eco respects flexibility range, null lastEcoRun treated as "now"
    - **Steps:**
        1. Write tests for all eco scheduling scenarios (window not open, window open with cheap hour, deadline forcing)
        2. Run tests — verify they fail
        3. Implement `GenerateFlexibleEco` in ScheduleAlgorithm
        4. Run tests — verify they pass

4. **Phase 4: Flexible Comfort Scheduling Algorithm**
    - **Objective:** Add comfort scheduling logic with a **sliding price threshold** and **re-optimization**. The comfort window spans from `lastComfortRun + intervalDays - flexibilityDays` to `lastComfortRun + intervalDays + flexibilityDays`. A "window progress" (0.0 at window start → 1.0 at window end) controls the effective threshold: at progress=0 use the user's configured percentile (e.g., 10th — very strict, only exceptionally cheap prices), at progress=1.0 use ~100th percentile (accept any price). The effective threshold is linearly interpolated: `effectivePercentile = userPercentile + (1.0 - userPercentile) * progress`. This means comfort is increasingly likely to be scheduled the further into the window we get. Even at the deadline day, we still pick the **cheapest hour** from available ~36h prices — never the exact deadline hour unless it happens to be cheapest. **Re-optimization**: If a comfort hour has been scheduled (`NextScheduledComfortUtc`) but hasn't run yet, and new price data reveals a cheaper hour within the window, the algorithm reschedules to the cheaper hour. Once the scheduled hour has passed, `LastComfortRunUtc` is updated and `NextScheduledComfortUtc` is cleared.
    - **Files/Functions to Modify/Create:**
        - `ScheduleAlgorithm.cs` — add `GenerateFlexibleComfort(JsonArray? rawToday, JsonArray? rawTomorrow, DateTimeOffset lastComfortRun, int intervalDays, int flexibilityDays, decimal? historicalBaseThreshold, double comfortEarlyPercentile, DateTimeOffset? nowOverride)` returning `FlexibleComfortResult` with `ScheduledHourUtc`, `EffectivePercentile`, `WindowProgress`, and `Message`
        - `ScheduleAlgorithm.cs` — add `FlexibleComfortResult` record
        - `HistoricalPriceAnalyzer.cs` — add `ComputeSlidingThreshold(decimal? baseThreshold, double userPercentile, double windowProgress)` to compute the effective threshold at current window position
    - **Tests to Write:**
        - `FlexibleComfortAlgorithmTests` — comfort window not yet open returns no schedule, early in window only triggers if price below strict threshold, mid-window triggers at moderate threshold, near deadline triggers at lenient threshold, at deadline picks cheapest from available prices, sliding percentile computes correctly at 0%/50%/100% progress, null lastComfortRun treated as "now"
    - **Steps:**
        1. Write tests for sliding threshold computation at various progress levels
        2. Write tests for all comfort scheduling scenarios with sliding logic
        3. Run tests — verify they fail
        4. Implement sliding threshold in `HistoricalPriceAnalyzer`
        5. Implement `GenerateFlexibleComfort` in ScheduleAlgorithm using sliding threshold
        6. Run tests — verify they pass

5. **Phase 5: Flexible Schedule Composition & BatchRunner Integration**
    - **Objective:** Compose eco and comfort results into a single Daikin-compatible schedule payload. Integrate with BatchRunner so flexible mode is used when `SchedulingMode == "Flexible"`. Update state tracking after schedule apply.
    - **Files/Functions to Modify/Create:**
        - `ScheduleAlgorithm.cs` — add `ComposeFlexibleSchedule(FlexibleEcoResult? eco, FlexibleComfortResult? comfort, DateTimeOffset now)` that produces the combined Daikin JSON (using "eco", "comfort", and "turn_off" states)
        - `BatchRunner.cs` — update `RunBatchAsync` to check scheduling mode and branch to flexible path, call `HistoricalPriceAnalyzer`, call `GenerateFlexibleEco` + `GenerateFlexibleComfort`, compose, and apply. After apply, update `FlexibleScheduleState` timestamps.
        - `Jobs/ScheduleUpdateHangfireJob.cs` — ensure it passes through mode correctly
    - **Tests to Write:**
        - `FlexibleScheduleCompositionTests` — eco-only schedule, comfort-only schedule, combined eco+comfort, neither (all turn_off), correct Daikin JSON format with weekday keys
        - `BatchRunnerFlexibleTests` — flexible mode calls flexible algorithm, classic mode unchanged
    - **Steps:**
        1. Write tests for schedule composition (eco-only, comfort-only, combined)
        2. Write tests for BatchRunner mode switching
        3. Run tests — verify they fail
        4. Implement `ComposeFlexibleSchedule`
        5. Update BatchRunner with mode branching and state tracking
        6. Update ScheduleUpdateHangfireJob if needed
        7. Run tests — verify they pass

6. **Phase 6: API Endpoints Update**
    - **Objective:** Update GET/POST `/api/user/settings` to expose new flexible scheduling fields. Add GET `/api/user/flexible-state` for last-run times and next-window info.
    - **Files/Functions to Modify/Create:**
        - `Program.cs` — update settings GET to return new fields, update settings POST to validate and save new fields (EcoIntervalHours 6–36, EcoFlexibilityHours 1–18, ComfortIntervalDays 7–90, ComfortFlexibilityDays 1–30, ComfortEarlyPercentile 0.01–0.50), add GET `/api/user/flexible-state`
    - **Tests to Write:**
        - Integration-style tests or manual validation of endpoint responses
    - **Steps:**
        1. Update GET settings endpoint to include all new fields
        2. Update POST settings endpoint with validation for new fields
        3. Add GET `/api/user/flexible-state` endpoint
        4. Test endpoints manually or with curl

7. **Phase 7: Frontend Settings UI**
    - **Objective:** Add mode toggle and flexible scheduling controls to the settings page.
    - **Files/Functions to Modify/Create:**
        - `frontend/src/pages/SettingsPage.tsx` — add scheduling mode toggle (Classic/Flexible), conditional section for flexible settings with sliders for eco interval, eco flexibility, comfort interval days, comfort flexibility days, comfort early percentile
        - `frontend/src/types/api.ts` — update UserSettings type with new fields
        - `frontend/src/hooks/useUserSettings.ts` — update hook to handle new fields
    - **Tests to Write:**
        - TypeScript type correctness verified by build
    - **Steps:**
        1. Update TypeScript types for new settings fields
        2. Update useUserSettings hook
        3. Add mode toggle to SettingsPage
        4. Add conditional flexible settings section with sliders
        5. Build frontend and verify no errors

8. **Phase 8: Dashboard Comfort/Eco Run History**
    - **Objective:** Visualize historical comfort and eco run timestamps on the dashboard so users can see when the last runs occurred and when the next window opens.
    - **Files/Functions to Modify/Create:**
        - `Program.cs` — add GET `/api/user/flexible-state` endpoint returning `LastEcoRunUtc`, `LastComfortRunUtc`, next eco/comfort window start/end, and window progress
        - `frontend/src/pages/Dashboard.tsx` — add a "Flexible Scheduling" card/section showing last eco run, last comfort run, next windows, and progress indicator
        - `frontend/src/hooks/useFlexibleState.ts` — new hook for fetching flexible state data
        - `frontend/src/types/api.ts` — add `FlexibleState` type
    - **Tests to Write:**
        - TypeScript type correctness verified by build
    - **Steps:**
        1. Add `FlexibleState` type to api.ts
        2. Create `useFlexibleState` hook
        3. Add flexible scheduling history card to Dashboard
        4. Display last eco/comfort run times, next window, and progress bar
        5. Build frontend and verify rendering

**Open Questions:** None — all resolved via user input.
