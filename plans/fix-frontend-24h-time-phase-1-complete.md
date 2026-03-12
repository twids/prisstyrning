## Phase 1 Complete: Nordpool regional formatting selector

Implemented a locale/timezone formatting system aligned with Nordpool regions and replaced forced time formatting behavior with user-selectable regional profiles. The frontend now consistently formats date/time through shared context-aware formatters and respects profile-specific localization preferences.

**Files created/changed:**
- frontend/src/context/TimezoneContext.tsx
- frontend/src/dateFormat.ts
- frontend/src/pages/SettingsPage.tsx
- frontend/src/components/PriceChart.tsx
- frontend/src/pages/DashboardPage.tsx
- frontend/src/pages/AdminPage.tsx
- plans/fix-frontend-24h-time-phase-1-complete.md

**Functions created/changed:**
- `getSystemTimezone`
- `getSystemLocale`
- `normalizeLocaleProfileKey`
- `useLocale`
- `useFormatters`
- `formatDateTime`
- `formatTime`
- `formatDateTimeFull`

**Tests created/changed:**
- No new automated frontend unit tests added in this phase.
- Validation executed:
  - `npm run build` in `frontend`
  - `dotnet restore`
  - `dotnet build --configuration Release --no-restore`
  - `dotnet test --configuration Release --no-build --verbosity normal`

**Review Status:** APPROVED with minor recommendations

**Git Commit Message:**
feat: add nordpool regional time profiles

- Add SE/NO/DK/FI regional locale-timezone formatting profiles
- Expose profile selector in settings with auto/system fallback
- Route chart/admin/dashboard date formatting through shared context
