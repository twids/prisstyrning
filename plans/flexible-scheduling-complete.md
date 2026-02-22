## Plan Complete: Flexible Eco and Comfort Scheduling

Implemented GitHub issue #74: an optional "Flexible" scheduling mode alongside the existing "Classic" mode for Daikin DHW temperature control. The system schedules eco runs at configurable intervals (6-36h) picking the cheapest electricity hour, and comfort/legionella runs at longer intervals (7-90d) using a sliding historical price threshold that relaxes as the window progresses. Comfort runs are re-optimized when cheaper prices arrive in subsequent batches.

**Phases Completed:** 8 of 8
1. ✅ Phase 1: Data Model & Migration
2. ✅ Phase 2: Historical Price Percentile Service
3. ✅ Phase 3: Flexible Eco Scheduling Algorithm
4. ✅ Phase 4: Flexible Comfort Scheduling Algorithm
5. ✅ Phase 5: Schedule Composition & BatchRunner Integration
6. ✅ Phase 6: API Endpoints Update
7. ✅ Phase 7: Frontend Settings UI
8. ✅ Phase 8: Dashboard Comfort/Eco Status

**All Files Created/Modified:**
- data/Entities/FlexibleScheduleState.cs (NEW)
- data/Entities/UserSettings.cs
- data/PrisstyrningDbContext.cs
- data/Repositories/FlexibleScheduleStateRepository.cs (NEW)
- data/Repositories/UserSettingsRepository.cs
- data/Migrations/PrisstyrningDbContextModelSnapshot.cs
- HistoricalPriceAnalyzer.cs (NEW)
- ScheduleAlgorithm.cs
- BatchRunner.cs
- Program.cs
- frontend/src/types/api.ts
- frontend/src/api/client.ts
- frontend/src/hooks/useFlexibleState.ts (NEW)
- frontend/src/pages/SettingsPage.tsx
- frontend/src/pages/DashboardPage.tsx
- frontend/src/components/ScheduleGrid.tsx
- frontend/src/components/ScheduleLegend.tsx
- wwwroot/ (rebuilt)

**Key Functions/Classes Added:**
- `FlexibleScheduleState` entity with LastEcoRunUtc, LastComfortRunUtc, NextScheduledComfortUtc
- `FlexibleScheduleStateRepository` with state tracking and scheduled comfort management
- `HistoricalPriceAnalyzer` with ComputePercentile, GetHistoricalStatsAsync, ComputeSlidingThreshold
- `ScheduleAlgorithm.GenerateFlexibleEco()` — cheapest hour in eco window
- `ScheduleAlgorithm.GenerateFlexibleComfort()` — sliding threshold with re-optimization
- `ScheduleAlgorithm.ComposeFlexibleSchedule()` — Daikin-compatible JSON composition
- `BatchRunner.RunFlexibleBatchAsync()` — end-to-end flexible scheduling pipeline
- Frontend: Scheduling Mode toggle, Flexible Settings panel, Dashboard status with progress bar

**Test Coverage:**
- Total tests written: 49 (across 5 test files)
- All backend tests passing: ✅ (356 total: 352 passed, 4 skipped)
- Frontend build: ✅

**Recommendations for Next Steps:**
- Add a Hangfire recurring job specifically tuned for flexible mode (e.g., run every 2h instead of at fixed times)
- Consider adding a "test run" button in the UI that previews what the flexible algorithm would schedule without applying
- Add notification/logging when comfort re-optimization occurs (rescheduled to cheaper hour)
- Consider adding frontend unit tests with Vitest for the new settings form validation
