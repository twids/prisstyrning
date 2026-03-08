## Plan: Fix Eco Never Scheduled on First Run

The flexible eco algorithm never schedules an eco period because when `LastEcoRunUtc` is null (first run / no prior eco applied), the code defaults `lastEcoRun` to `now`. This pushes the eco window 15+ hours into the future every invocation, so the window never opens. The fix defaults `lastEcoRun` to `now - intervalHours` when null, ensuring the eco window is already open on first run.

**Phases: 2**

1. **Phase 1: Fix default lastEcoRun in BatchRunner**
    - **Objective:** When `LastEcoRunUtc` is null, default to `now - intervalHours` so the eco window is immediately open
    - **Files/Functions to Modify:** `BatchRunner.cs` → `RunFlexibleBatchAsync`, line ~125
    - **Tests to Write:** `FirstRunDefaultsEcoWindow_SchedulesEco` — verify that when `LastEcoRunUtc` is null, an eco is scheduled (not "waiting")
    - **Steps:**
        1. Write a test in `FlexibleEcoAlgorithmTests.cs` that simulates null `LastEcoRunUtc` by passing `now - intervalHours` as `lastEcoRun` and asserts eco is "scheduled"
        2. Write an integration-style test or unit test that verifies the BatchRunner defaulting logic produces an open window
        3. Change `BatchRunner.cs` line ~125 from `flexState.LastEcoRunUtc ?? now` to `flexState.LastEcoRunUtc ?? now.AddHours(-settings.EcoIntervalHours)`
        4. Run tests to confirm they pass

2. **Phase 2: Apply same fix to lastComfortRun**
    - **Objective:** Apply the same defaulting pattern to `lastComfortRun` for consistency (line ~126)
    - **Files/Functions to Modify:** `BatchRunner.cs` → `RunFlexibleBatchAsync`, line ~126
    - **Tests to Write:** `FirstRunDefaultsComfortWindow_SchedulesComfort` — verify comfort is scheduled when `LastComfortRunUtc` is null
    - **Steps:**
        1. Write a test that verifies comfort scheduling works when `LastComfortRunUtc` is null and defaults to `now - intervalDays * 24`
        2. Change `BatchRunner.cs` line ~126 from `flexState.LastComfortRunUtc ?? now` to `flexState.LastComfortRunUtc ?? now.AddDays(-settings.ComfortIntervalDays)`
        3. Run all tests to confirm everything passes

**Open Questions:** None — the fix is straightforward.
