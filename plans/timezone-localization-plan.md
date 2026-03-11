## Plan: Frontend Timezone Localization & Settings

Add user-configurable timezone support so all timestamps display in the user's chosen timezone (defaulting to browser auto-detection). Only Nordpool-relevant timezones are offered. Schedule grid hours stay as price-zone hours; the price chart correctly localizes time axis labels.

**Phases (4 phases)**

1. **Phase 1: Backend — Add Timezone field to UserSettings**
    - **Objective:** Add a `Timezone` string property to `UserSettings` entity with default `"auto"`, update GET/POST `/api/user/settings` to accept/return/validate it, add EF Core migration.
    - **Files/Functions to Modify/Create:** `data/Entities/UserSettings.cs`, `Program.cs` (settings endpoints), new migration file
    - **Tests to Write:** `SettingsEndpoint_AcceptsTimezone`, `SettingsEndpoint_RejectsInvalidTimezone`
    - **Steps:**
        1. Write tests covering valid and invalid timezone values
        2. Add `Timezone` property (default `"auto"`) to `UserSettings.cs`
        3. Update POST `/api/user/settings` to validate Timezone against allowed list (auto + Nordpool IANA zones)
        4. Update GET response to include the Timezone field
        5. Create EF Core migration
        6. Run tests to confirm

2. **Phase 2: Frontend — Create TimezoneContext and update dateFormat.ts**
    - **Objective:** Create a React context providing the user's timezone preference, update formatting functions to accept a `timeZone` parameter, and expose timezone-aware hooks.
    - **Files/Functions to Modify/Create:** `frontend-v2/src/context/TimezoneContext.tsx` (new), `frontend-v2/src/dateFormat.ts`, `frontend-v2/src/types/api.ts`, `frontend-v2/src/App.tsx`
    - **Tests to Write:** N/A (React context)
    - **Steps:**
        1. Add `Timezone` to the `UserSettings` TypeScript interface
        2. Create `TimezoneContext` that reads timezone from user settings, falls back to `Intl.DateTimeFormat().resolvedOptions().timeZone`
        3. Update `formatDateTime`, `formatTime`, `formatDateTimeFull` in `dateFormat.ts` to accept optional `timeZone` param
        4. Export a `useFormatters()` hook from TimezoneContext that returns timezone-bound formatting functions
        5. Wrap App in `TimezoneProvider`

3. **Phase 3: Frontend — Update all timestamp displays**
    - **Objective:** Update every component displaying timestamps to use timezone-aware formatting from the context.
    - **Files/Functions to Modify/Create:** `PriceChart.tsx`, `DashboardPage.tsx`, `ScheduleHistoryList.tsx`, `AuthStatusBadge.tsx`, `AdminPage.tsx`
    - **Tests to Write:** N/A (visual)
    - **Steps:**
        1. Replace direct `formatDateTime`/`formatTime`/`formatDateTimeFull` calls with `useFormatters()` hook
        2. Ensure PriceChart x-axis labels use timezone-aware `formatTime`
        3. Verify all dates render correctly in non-default timezone

4. **Phase 4: Frontend — Add Timezone selector to Settings page**
    - **Objective:** Add a dropdown on SettingsPage with "Auto (browser)" and Nordpool-relevant IANA timezones, wired to the settings API.
    - **Files/Functions to Modify/Create:** `frontend-v2/src/pages/SettingsPage.tsx`
    - **Tests to Write:** N/A (UI)
    - **Steps:**
        1. Define Nordpool timezone list: `auto`, `Europe/Stockholm`, `Europe/Oslo`, `Europe/Copenhagen`, `Europe/Helsinki`
        2. Add timezone select dropdown to the settings form
        3. Wire save to existing updateSettings mutation
        4. Verify timezone changes reflect immediately

**Open Questions**
None — all resolved per user feedback.
