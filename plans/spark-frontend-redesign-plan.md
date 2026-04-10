## Plan: Spark-Inspired Frontend Redesign

Replace the current MUI-based frontend with a Tailwind CSS + shadcn/ui design inspired by the Spark prototype, while preserving all existing functionality (API hooks, types, client, TimezoneContext). The new design uses Recharts, Phosphor Icons, glassmorphism cards, and a bottom-tab mobile navigation.

**Phases: 7**

1. **Phase 1: Tailwind + shadcn/ui Foundation**
    - **Objective:** Replace MUI with Tailwind CSS and shadcn/ui primitives. Set up build tooling, CSS theme, and base utilities.
    - **Files/Functions to Create:** `tailwind.config.js`, `postcss.config.js`, `src/styles/theme.css`, `src/lib/utils.ts`
    - **Files/Functions to Modify:** `package.json`, `src/main.tsx`, `index.html`
    - **Files to Remove:** `src/theme.ts`
    - **Tests to Write:** `build-smoke.test.ts` — app builds without MUI, Tailwind classes resolve
    - **Steps:**
        1. Install Tailwind CSS v4, postcss, autoprefixer, clsx, tailwind-merge, class-variance-authority
        2. Install shadcn/ui components: card, button, badge, slider, switch, label, select, separator, scroll-area, tabs, dialog, tooltip, table
        3. Create `src/lib/utils.ts` with `cn()` helper
        4. Create `src/styles/theme.css` with dark theme CSS variables adapted from Spark (green=comfort, blue=eco, red=turn_off)
        5. Update `index.html` to load IBM Plex Sans + Space Grotesk fonts
        6. Remove MUI, Emotion, and MUI X Charts from package.json
        7. Install recharts and @phosphor-icons/react
        8. Install sonner for toast notifications
        9. Verify build succeeds

2. **Phase 2: Layout + Navigation Shell**
    - **Objective:** Replace MUI AppBar with bottom-tab navigation matching the Spark mobile-first design.
    - **Files/Functions to Modify:** `src/components/Layout.tsx`, `src/App.tsx`
    - **Tests to Write:** `layout.test.tsx` — tabs render, navigation works, admin tab conditionally visible
    - **Steps:**
        1. Rewrite Layout.tsx with bottom tab bar using Phosphor icons (Lightning=Dashboard, GearSix=Settings, ClockCounterClockwise=History, ShieldCheck=Admin)
        2. Keep React Router for deep-linking
        3. Add pb-20 to main content for tab clearance
        4. Keep admin visibility check via useQuery(['admin-status'])
        5. Write navigation tests

3. **Phase 3: Dashboard View Redesign**
    - **Objective:** Redesign Dashboard with glassmorphism cards, Recharts AreaChart, horizontal HeatingTimeline, and action buttons.
    - **Files/Functions to Modify:** `src/pages/DashboardPage.tsx`, `src/components/PriceChart.tsx`, `src/components/AuthStatusChip.tsx`
    - **Files/Functions to Create:** `src/components/HeatingTimeline.tsx`, `src/components/ConnectionBadge.tsx`
    - **Tests to Write:** `dashboard.test.tsx` — chart renders, buttons trigger mutations, badge reflects auth
    - **Steps:**
        1. Rewrite PriceChart.tsx using Recharts AreaChart with gradient fill, keeping usePrices() hook
        2. Create HeatingTimeline.tsx — horizontal colored bar from SchedulePayload
        3. Create ConnectionBadge.tsx using shadcn Badge for Daikin status
        4. Rewrite DashboardPage.tsx with Spark layout: header, PriceChart card, HeatingTimeline card, action buttons
        5. Keep all existing hooks unchanged
        6. Keep Flexible Scheduling Status section
        7. Replace MUI Snackbar with sonner toast
        8. Rewrite ConfirmDialog using shadcn Dialog
        9. Write component tests

4. **Phase 4: TrendChart + Schedule History**
    - **Objective:** Migrate TrendChart to Recharts. Create standalone History page with Spark-style cards.
    - **Files/Functions to Modify:** `src/components/TrendChart.tsx`, `src/components/ScheduleHistoryList.tsx`
    - **Files/Functions to Create:** `src/pages/HistoryPage.tsx`
    - **Tests to Write:** `trend-chart.test.tsx`, `history.test.tsx`
    - **Steps:**
        1. Rewrite TrendChart to use Recharts AreaChart, keeping usePriceTrend() hook
        2. Create HistoryPage.tsx with Spark's card-based design using HeatingTimeline mini-previews
        3. Add History route to App.tsx
        4. Write tests for both components

5. **Phase 5: Settings Page Redesign**
    - **Objective:** Redesign Settings with shadcn/ui form controls matching Spark aesthetic.
    - **Files/Functions to Modify:** `src/pages/SettingsPage.tsx`
    - **Tests to Write:** `settings.test.tsx` — controls render, values change, save works
    - **Steps:**
        1. Replace MUI Slider/Switch/Select with shadcn equivalents
        2. Use card layout with Separator dividers
        3. Keep all settings fields including Flexible mode sub-settings
        4. Keep Regional Formatting section
        5. Keep Daikin auth management
        6. Keep all zones (SE1-4, NO1-5, DK1-2, FI)
        7. Replace Snackbar with sonner toast
        8. Write form tests

6. **Phase 6: Admin Page Redesign**
    - **Objective:** Restyle Admin page with Tailwind/shadcn while keeping full functionality.
    - **Files/Functions to Modify:** `src/pages/AdminPage.tsx`
    - **Tests to Write:** `admin.test.tsx` — login form, user table, toggles, delete confirmation
    - **Steps:**
        1. Replace MUI Table with shadcn Table
        2. Replace MUI Dialog with shadcn Dialog
        3. Replace MUI Switch with shadcn Switch
        4. Keep all mutation logic unchanged
        5. Write tests

7. **Phase 7: Cleanup + Polish**
    - **Objective:** Remove all MUI dependencies, clean up unused files, verify full build.
    - **Files to Modify:** `package.json`
    - **Files to Remove:** `src/theme.ts`, `src/components/ScheduleLegend.tsx`, `src/components/LoadingSkeleton.tsx` (rewrite if needed)
    - **Tests to Write:** `build-final.test.ts` — no MUI imports, clean build, all pages render
    - **Steps:**
        1. Remove @mui/material, @mui/icons-material, @mui/x-charts, @emotion/react, @emotion/styled
        2. Remove dead component files
        3. Run full build
        4. Verify all pages render correctly

**Resolved Questions**
1. Keep React Router for deep-linking ✓
2. Add sonner for toasts ✓
3. Keep JsonViewer component ✓
4. Keep all zones (SE, NO, DK, FI) ✓
