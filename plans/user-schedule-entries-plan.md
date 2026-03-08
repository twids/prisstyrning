## Plan: User Schedule Entries with Legionella Checkbox

Users can add manual schedule entries (comfort/eco) that persist and are respected by the batch runner. A checkbox (defaulting to checked) marks comfort entries as the last legionella comfort run, resetting the flexible comfort window. Entries can be added and removed.

**Phases: 5**

1. **Phase 1: UserScheduleEntry Entity and Repository**
    - **Objective:** Create a new `UserScheduleEntry` entity and repository for persisting user-defined schedule entries
    - **Files/Functions to Create:** `data/Entities/UserScheduleEntry.cs`, `data/Repositories/UserScheduleEntryRepository.cs`
    - **Files/Functions to Modify:** `data/PrisstyrningDbContext.cs` (add DbSet), `Program.cs` (register DI)
    - **Tests to Write:** `UserScheduleEntryRepositoryTests` — CRUD operations: add entry, list entries, remove entry, auto-cleanup of past entries
    - **Steps:**
        1. Write tests for adding, listing, removing, and auto-cleaning expired entries
        2. Create entity with fields: `Id` (int, PK auto), `UserId` (string), `ScheduledTimeUtc` (DateTimeOffset), `State` (string: "comfort"/"eco"), `CountsAsLegionella` (bool, default true), `CreatedAtUtc` (DateTimeOffset)
        3. Create repository with methods: `AddAsync`, `GetFutureEntriesAsync(userId)`, `RemoveAsync(userId, id)`, `CleanupPastEntriesAsync(userId)`
        4. Add `DbSet<UserScheduleEntry>` to DbContext and create EF migration
        5. Register repository in DI
        6. Run tests to confirm they pass

2. **Phase 2: Integrate User Entries into ComposeFlexibleSchedule**
    - **Objective:** Modify the schedule composition to overlay user entries onto the flexible schedule, so they aren't overwritten by batch runs
    - **Files/Functions to Modify:** `ScheduleAlgorithm.cs` → `ComposeFlexibleSchedule` (add `userEntries` parameter), `BatchRunner.cs` → `RunFlexibleBatchAsync` (fetch and pass entries)
    - **Tests to Write:** `ComposeFlexibleSchedule_WithUserEntries_IncludesThemInOutput`, `ComposeFlexibleSchedule_UserEntryOverridesAlgorithm`
    - **Steps:**
        1. Write tests that verify user entries appear in the composed Daikin schedule JSON
        2. Add `IReadOnlyList<UserScheduleEntry>?` parameter to `ComposeFlexibleSchedule`
        3. In `ComposeFlexibleSchedule`, overlay user entries after eco/comfort (highest priority)
        4. In `RunFlexibleBatchAsync`, fetch user entries from repository and pass them in
        5. Run tests

3. **Phase 3: Refactor Manual Comfort Endpoint to Use UserScheduleEntry**
    - **Objective:** Replace the one-shot manual comfort endpoint with one that creates a `UserScheduleEntry` and optionally marks it as legionella, then triggers a schedule recompose+apply
    - **Files/Functions to Modify:** `Program.cs` → `POST /api/schedule/comfort` endpoint
    - **Files/Functions to Create:** `POST /api/schedule/entries` (add), `GET /api/schedule/entries` (list), `DELETE /api/schedule/entries/{id}` (remove)
    - **Tests to Write:** `AddScheduleEntry_ValidInput_ReturnsCreated`, `RemoveScheduleEntry_ValidId_ReturnsOk`, `ListScheduleEntries_ReturnsFutureOnly`
    - **Steps:**
        1. Write tests for the new API endpoints
        2. Create `POST /api/schedule/entries` that accepts `{ time, state, countsAsLegionella }`, saves entry, and if legionella is true updates `LastComfortRunUtc` on the `FlexibleScheduleState`
        3. Create `GET /api/schedule/entries` returning future entries for the user
        4. Create `DELETE /api/schedule/entries/{id}` to remove an entry
        5. Update existing `POST /api/schedule/comfort` to delegate to the new entry creation (backward compat)
        6. After adding an entry, trigger a schedule recompose+apply to Daikin so the entry takes effect immediately
        7. Run tests

4. **Phase 4: Frontend — Schedule Entries UI**
    - **Objective:** Add a UI component for managing user schedule entries with a checkbox for legionella, replacing or augmenting the current manual comfort card
    - **Files/Functions to Create/Modify:** `frontend-v2/src/hooks/useScheduleEntries.ts`, `frontend-v2/src/api/client.ts`, `frontend-v2/src/pages/DashboardPage.tsx`
    - **Tests to Write:** N/A (frontend — manual visual testing)
    - **Steps:**
        1. Add API client methods: `addScheduleEntry`, `getScheduleEntries`, `removeScheduleEntry`
        2. Create `useScheduleEntries` hook with query + mutations
        3. Replace the Manual Comfort card with a "Schedule Entries" card that shows a list of upcoming entries with remove buttons, and a form to add new entries with: time picker, state selector (comfort/eco), and "Counts as legionella run" checkbox (default checked, only shown for comfort)
        4. Test manually in browser

5. **Phase 5: Cleanup and Edge Cases**
    - **Objective:** Handle past entry cleanup, batch job integration, and edge cases
    - **Files/Functions to Modify:** `BatchRunner.cs`, `Jobs/ScheduleUpdateHangfireJob.cs`
    - **Tests to Write:** `BatchRunner_CleansUpPastEntries`, `BatchRunner_UserEntryPreventsOverwrite`
    - **Steps:**
        1. Write tests for cleanup behavior
        2. In `RunFlexibleBatchAsync`, clean up past entries before composing the schedule
        3. Ensure batch job logs when user entries are included in the schedule
        4. Run all tests

**Open Questions:**
1. Should users be able to add eco entries too, or only comfort? → Planning for both comfort and eco.
2. Should there be a limit on how many entries a user can have? → Suggest max 10 future entries.
