## Plan: Frontend V2 - MUI-Free Modern Redesign

Create a v2 of the Prisstyrning frontend that removes Material UI entirely, uses Tailwind CSS v4 with Headless UI for accessible components and automatic day/night theming, replaces `@mui/x-charts` with the lightweight `recharts` library, and serves at `/v2` alongside the existing frontend. All existing functionality is preserved identically.

**Phases (7 phases)**

1. **Phase 1: V2 Project Scaffold & Routing**
    - **Objective:** Set up the new frontend project with all dependencies, build config, and backend integration
    - **Files/Functions to Create:** `frontend-v2/package.json`, `frontend-v2/vite.config.ts`, `frontend-v2/tsconfig.json`, `frontend-v2/tsconfig.node.json`, `frontend-v2/index.html`, `frontend-v2/src/main.tsx`, `frontend-v2/src/App.tsx`
    - **Files to Copy:** `src/api/client.ts`, `src/types/api.ts`, all `src/hooks/*.ts`
    - **Files to Modify:** `Program.cs` (add `/v2` static files and SPA fallback)
    - **Tests to Write:** N/A (scaffold phase)
    - **Steps:**
        1. Create `frontend-v2/` directory with `package.json` containing React, react-router-dom, @tanstack/react-query, recharts, date-fns, Tailwind CSS v4, @headlessui/react
        2. Configure Vite to build to `wwwroot-v2/`, proxy `/api` and `/auth` to port 5000
        3. Copy API client, types, and all hooks verbatim
        4. Create `main.tsx` and `App.tsx` with router shell
        5. Update `Program.cs` to serve `wwwroot-v2/` at `/v2` with SPA fallback

2. **Phase 2: CSS Design System & Auto Theme**
    - **Objective:** Build a complete design system using Tailwind CSS v4 with automatic light/dark theme switching
    - **Files/Functions to Create:** `src/styles/app.css`, `src/context/ThemeContext.tsx`
    - **Tests to Write:** N/A (styling phase)
    - **Steps:**
        1. Configure Tailwind CSS v4 with `@tailwindcss/vite` plugin and custom theme colors
        2. Implement auto-detection via `prefers-color-scheme` and time-of-day (6AM–6PM = light) with `dark:` class strategy
        3. Style components using Tailwind utility classes with dark mode variants
        4. Apply modern design with subtle shadows, rounded corners, smooth transitions

3. **Phase 3: Shared Components**
    - **Objective:** Build all reusable UI components without MUI
    - **Files/Functions to Create:** `Layout.tsx`, `ConfirmDialog.tsx`, `Toast.tsx`, `ToastContext.tsx`, `ErrorBoundary.tsx`, `LoadingSkeleton.tsx`, `AuthStatusBadge.tsx`, `JsonViewer.tsx`
    - **Tests to Write:** N/A (component phase)
    - **Steps:**
        1. Build Layout with responsive navbar and navigation links
        2. Create ConfirmDialog using native HTML `<dialog>` element
        3. Create Toast notification system with context provider
        4. Port ErrorBoundary, LoadingSkeleton, AuthStatusBadge, JsonViewer without MUI

4. **Phase 4: PriceChart & ScheduleGrid Components**
    - **Objective:** Port data visualization components using recharts and CSS grid
    - **Files/Functions to Create:** `PriceChart.tsx`, `ScheduleGrid.tsx`, `ScheduleLegend.tsx`, `ScheduleHistoryList.tsx`
    - **Tests to Write:** N/A (component phase)
    - **Steps:**
        1. Port PriceChart using recharts LineChart (replaces @mui/x-charts)
        2. Port ScheduleGrid as pure CSS grid with custom properties for state colors
        3. Port ScheduleLegend and ScheduleHistoryList with CSS-only accordion

5. **Phase 5: Dashboard Page**
    - **Objective:** Port DashboardPage with all functionality
    - **Files/Functions to Create:** `pages/DashboardPage.tsx`
    - **Tests to Write:** N/A (page port)
    - **Steps:**
        1. Port auth section, price chart, schedule preview section
        2. Port manual comfort run, flexible scheduling status
        3. Port apply schedule section, schedule history
        4. Wire up Toast for notifications (replaces MUI Snackbar)

6. **Phase 6: Settings & Admin Pages**
    - **Objective:** Port SettingsPage and AdminPage with all functionality
    - **Files/Functions to Create:** `pages/SettingsPage.tsx`, `pages/AdminPage.tsx`, `pages/NotFoundPage.tsx`
    - **Tests to Write:** N/A (page port)
    - **Steps:**
        1. Port SettingsPage with custom CSS sliders, selects, switches
        2. Port AdminPage with HTML table, toggle switches, delete dialog
        3. Port NotFoundPage

7. **Phase 7: Build Integration & Polish**
    - **Objective:** Ensure the v2 frontend builds and is served correctly
    - **Files/Functions to Modify:** Build scripts, responsive design fixes
    - **Tests to Write:** N/A (integration phase)
    - **Steps:**
        1. Verify build and static file serving at `/v2`
        2. Add keyboard navigation and ARIA attributes
        3. Final responsive design verification
        4. Ensure all pages and interactions work end-to-end

**Open Questions:** None
