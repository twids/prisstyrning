## Plan: Backend Testing & Frontend Rewrite

This plan addresses backend testing gaps, fixes the schedule history bug, and rewrites the frontend using Material UI + React + TypeScript + Vite. The work is structured to support multiple PRs and avoid merge conflicts.

**Phases: 10**

### 1. **Phase 1: Backend Testing Foundation - History & Settings (TDD)**
   - **Objective:** Write tests first for the broken history feature and user settings validation, then fix the bugs
   - **Files/Functions to Modify/Create:**
     - [Prisstyrning.Tests/ScheduleHistoryPersistenceTests.cs](Prisstyrning.Tests/ScheduleHistoryPersistenceTests.cs) (create)
     - [Prisstyrning.Tests/UserSettingsServiceTests.cs](Prisstyrning.Tests/UserSettingsServiceTests.cs) (create)
     - [Prisstyrning.Tests/Fixtures/TestDataFactory.cs](Prisstyrning.Tests/Fixtures/TestDataFactory.cs) (create)
     - [Prisstyrning.Tests/Fixtures/TempFileSystem.cs](Prisstyrning.Tests/Fixtures/TempFileSystem.cs) (create)
     - [Program.cs](Program.cs) line 482 - Fix `persist: false` → `persist: true`
     - [ScheduleHistoryPersistence.cs](ScheduleHistoryPersistence.cs) - Fix JSON node parent exception if needed
   - **Tests to Write:**
     - SaveAsync_WithValidPayload_CreatesHistoryFile
     - SaveAsync_WithExistingHistory_AppendsNewEntry
     - SaveAsync_WithRetentionDays_RemovesOldEntries
     - SaveAsync_WithJsonNodeAlreadyHasParent_ClonesCorrectly (THE BUG TEST)
     - LoadAsync_WithNoHistory_ReturnsEmptyArray
     - LoadAsync_WithValidHistory_ReturnsAllEntries
     - SaveAsync_WithCorruptFile_StartsFromScratch
     - LoadAsync_WithMultipleUsers_ReturnsCorrectUserData
     - UserSettings: LoadScheduleSettings_WithDefaults_ReturnsGlobalConfig
     - UserSettings: LoadScheduleSettings_WithUserOverrides_ReturnsUserConfig
     - UserSettings: SetUserZone_WithValidZone_PersistsCorrectly
     - UserSettings: IsValidZone_WithAllNordicZones_ReturnsTrue
   - **Steps:**
     1. Create test fixtures (TestDataFactory, TempFileSystem) for isolated test directories
     2. Write failing tests for ScheduleHistoryPersistence (10 tests)
     3. Run tests - verify they fail (validates bug exists)
     4. Fix [Program.cs](Program.cs#L482) to change `persist: false` to `persist: true`
     5. Fix [ScheduleHistoryPersistence.cs](ScheduleHistoryPersistence.cs) to deep clone JSON nodes if needed
     6. Run tests - verify they pass
     7. Write UserSettingsService tests (8 tests)
     8. Fix any validation issues discovered
     9. Run full test suite - ensure all tests pass (`dotnet test`)
     10. Verify manually: Start app, generate schedule, check history appears

---

### 2. **Phase 2: Backend Testing - Integration Layer (OAuth & Batch)**
   - **Objective:** Test the OAuth flow and BatchRunner orchestration with mocked external APIs
   - **Files/Functions to Modify/Create:**
     - [Prisstyrning.Tests/Integration/DaikinOAuthServiceIntegrationTests.cs](Prisstyrning.Tests/Integration/DaikinOAuthServiceIntegrationTests.cs) (create)
     - [Prisstyrning.Tests/Integration/BatchRunnerIntegrationTests.cs](Prisstyrning.Tests/Integration/BatchRunnerIntegrationTests.cs) (create)
     - [Prisstyrning.Tests/Fixtures/MockHttpMessageHandler.cs](Prisstyrning.Tests/Fixtures/MockHttpMessageHandler.cs) (create)
   - **Tests to Write:**
     - OAuth: GetAuthorizationUrl_WithValidConfig_GeneratesCorrectUrl
     - OAuth: HandleCallbackAsync_WithValidState_ExchangesTokenSuccessfully
     - OAuth: HandleCallbackAsync_WithInvalidState_ReturnsFalse
     - OAuth: RefreshIfNeededAsync_WithExpiredToken_RefreshesToken
     - OAuth: TryGetValidAccessToken_WithExpiredToken_ReturnsNull
     - OAuth: SaveTokens_CreatesTokenFileWithCorrectStructure
     - OAuth: PKCE_GeneratesValidCodeChallengeAndVerifier
     - OAuth: MultiUser_IsolatesTokensCorrectly
     - Batch: RunBatchAsync_WithValidPriceData_GeneratesSchedule
     - Batch: RunBatchAsync_WithPersistTrue_SavesHistory
     - Batch: RunBatchAsync_WithApplyScheduleTrue_CallsDaikinAPI
     - Batch: RunBatchAsync_WithUserSettings_UsesUserOverrides
   - **Steps:**
     1. Create MockHttpMessageHandler for intercepting HTTP requests
     2. Write OAuth service tests (12 tests) with mocked token endpoint
     3. Run tests - verify OAuth flow correctness
     4. Write BatchRunner integration tests (10 tests) with mocked Nordpool/Daikin APIs
     5. Run tests - verify orchestration logic
     6. Refactor any issues discovered (token refresh timing, error handling)
     7. Run full test suite - ensure all tests pass
     8. Verify code coverage: ScheduleHistoryPersistence 90%+, UserSettingsService 90%+, DaikinOAuthService 80%+

---

### 3. **Phase 3: Backend Testing - Comprehensive Coverage**
   - **Objective:** Complete test coverage for persistence, Hangfire jobs, and API endpoints
   - **Files/Functions to Modify/Create:**
     - [Prisstyrning.Tests/Unit/NordpoolPersistenceTests.cs](Prisstyrning.Tests/Unit/NordpoolPersistenceTests.cs) (create)
     - [Prisstyrning.Tests/Unit/PriceMemoryTests.cs](Prisstyrning.Tests/Unit/PriceMemoryTests.cs) (create)
     - [Prisstyrning.Tests/Integration/NordpoolClientIntegrationTests.cs](Prisstyrning.Tests/Integration/NordpoolClientIntegrationTests.cs) (create)
     - [Prisstyrning.Tests/Jobs/ScheduleUpdateJobTests.cs](Prisstyrning.Tests/Jobs/ScheduleUpdateJobTests.cs) (create)
     - [Prisstyrning.Tests/Jobs/DaikinTokenRefreshJobTests.cs](Prisstyrning.Tests/Jobs/DaikinTokenRefreshJobTests.cs) (create)
     - [Prisstyrning.Tests/Jobs/NordpoolPriceJobTests.cs](Prisstyrning.Tests/Jobs/NordpoolPriceJobTests.cs) (create)
     - [Prisstyrning.Tests/Api/EndpointIntegrationTests.cs](Prisstyrning.Tests/Api/EndpointIntegrationTests.cs) (create)
   - **Tests to Write:**
     - NordpoolPersistence: Save_LoadLatest_ReturnsCorrectFile (6 tests)
     - PriceMemory: Set_Get_ReturnsDefensiveCopies (5 tests)
     - NordpoolClient: GetDailyPricesAsync_WithValidDate_ReturnsPriceArray (8 tests mocked)
     - ScheduleUpdateJob: ExecuteAsync_WithAutoApplyEnabled_GeneratesSchedules (5 tests)
     - DaikinTokenRefreshJob: ExecuteAsync_WithExpiredToken_RefreshesToken (4 tests)
     - NordpoolPriceJob: ExecuteAsync_FetchesPricesForAllZones (3 tests)
     - API Endpoints: 12 integration tests covering critical paths
   - **Steps:**
     1. Write Nordpool persistence and client tests (14 tests)
     2. Write PriceMemory thread-safety tests (5 tests)
     3. Write Hangfire job tests (12 tests total for 3 jobs)
     4. Write API endpoint integration tests (12 tests) using WebApplicationFactory
     5. Run full test suite - verify all tests pass
     6. Generate coverage report - target 80%+ overall backend coverage
     7. Document any untested edge cases in ROADMAP.md

---

### 4. **Phase 4: Backend Refactoring & Code Quality**
   - **Objective:** Improve backend code quality, structured logging, and configuration management
   - **Files/Functions to Modify/Create:**
     - [Program.cs](Program.cs) - Replace Console.WriteLine with ILogger
     - [BatchRunner.cs](BatchRunner.cs) - Add structured logging
     - [DaikinApiClient.cs](DaikinApiClient.cs) - Use HttpClientFactory
     - [NordpoolClient.cs](NordpoolClient.cs) - Use HttpClientFactory
     - [appsettings.json](appsettings.json) - Add retention period configuration
     - Create Options classes for Daikin, Schedule, Storage configuration
   - **Tests to Write:**
     - Options validation tests for new configuration classes
     - Logging integration tests (verify logs emitted correctly)
   - **Steps:**
     1. Create strongly-typed Options classes (DaikinOptions, ScheduleOptions, StorageOptions)
     2. Inject IOptions<T> into services instead of IConfiguration
     3. Replace all Console.WriteLine with ILogger calls (use LogLevel.Information, LogLevel.Error)
     4. Refactor DaikinApiClient to accept IHttpClientFactory
     5. Refactor NordpoolClient to accept IHttpClientFactory
     6. Add configuration for history retention period (make 7-day default configurable)
     7. Run tests - ensure refactoring didn't break functionality
     8. Update ROADMAP.md to mark technical debt items as resolved
     9. Verify application runs with structured logging: `dotnet run --configuration Release`

---

### 5. **Phase 5: Frontend Foundation (Vite + MUI + React + TypeScript)**
   - **Objective:** Initialize new frontend project with Material UI, React Router, and API client
   - **Files/Functions to Modify/Create:**
     - [frontend/package.json](frontend/package.json) (create)
     - [frontend/vite.config.ts](frontend/vite.config.ts) (create)
     - [frontend/tsconfig.json](frontend/tsconfig.json) (create)
     - [frontend/src/main.tsx](frontend/src/main.tsx) (create)
     - [frontend/src/App.tsx](frontend/src/App.tsx) (create)
     - [frontend/src/routes.tsx](frontend/src/routes.tsx) (create)
     - [frontend/src/theme.ts](frontend/src/theme.ts) (create)
     - [frontend/src/api/client.ts](frontend/src/api/client.ts) (create)
     - [frontend/src/types/api.ts](frontend/src/types/api.ts) (create)
     - [frontend/.gitignore](frontend/.gitignore) (create)
   - **Tests to Write:**
     - No tests in this phase - focus on infrastructure setup
   - **Steps:**
     1. Create `frontend/` directory at repository root
     2. Run `npm create vite@latest frontend -- --template react-ts`
     3. Install dependencies: @mui/material, @emotion/react, @emotion/styled, @mui/x-charts, react-router-dom, @tanstack/react-query
     4. Set up project structure (api/, components/, hooks/, pages/, types/, utils/)
     5. Configure Vite proxy to backend: `/api` → `http://localhost:5000/api`
     6. Create MUI dark theme matching current UI design
     7. Set up React Router with basic Layout component
     8. Create TypeScript API type definitions (all interfaces from Oracle research)
     9. Implement ApiClient class with all backend endpoints
     10. Create skeleton pages (DashboardPage, SettingsPage, NotFoundPage)
     11. Test frontend starts: `cd frontend && npm run dev` → verify localhost:5173 loads

---

### 6. **Phase 6: Frontend Core Features (Auth, Prices, Schedule Preview)**
   - **Objective:** Implement authentication, price visualization, and schedule preview generation
   - **Files/Functions to Modify/Create:**
     - [frontend/src/hooks/useAuth.ts](frontend/src/hooks/useAuth.ts) (create)
     - [frontend/src/hooks/usePrices.ts](frontend/src/hooks/usePrices.ts) (create)
     - [frontend/src/hooks/useSchedulePreview.ts](frontend/src/hooks/useSchedulePreview.ts) (create)
     - [frontend/src/components/AuthStatusChip.tsx](frontend/src/components/AuthStatusChip.tsx) (create)
     - [frontend/src/components/PriceChart.tsx](frontend/src/components/PriceChart.tsx) (create)
     - [frontend/src/components/ScheduleGrid.tsx](frontend/src/components/ScheduleGrid.tsx) (create)
     - [frontend/src/pages/DashboardPage.tsx](frontend/src/pages/DashboardPage.tsx) (update)
     - [frontend/src/components/Layout.tsx](frontend/src/components/Layout.tsx) (create)
   - **Tests to Write:**
     - Component tests with React Testing Library (auth flow, chart rendering, grid display)
   - **Steps:**
     1. Implement useAuth hook with React Query (refetch every 60s)
     2. Create AuthStatusChip component (MUI Chip showing authorized/unauthorized)
     3. Wire up OAuth start button and callback URL parameter handling
     4. Implement usePrices hook with auto-refetch every 5 minutes
     5. Create PriceChart component using MUI X Charts LineChart
     6. Add today/tomorrow series with distinct colors (#4FC3F7, #FFB74D)
     7. Implement useSchedulePreview mutation hook
     8. Create custom ScheduleGrid component with hour cells and color coding
     9. Parse schedule payload and render 24x2 grid (today + tomorrow)
     10. Add current hour highlighting with MUI theme primary color
     11. Create schedule legend (Comfort/Eco/Turn Off)
     12. Assemble DashboardPage with Auth, Price, and Schedule sections
     13. Test all features: Start backend, start frontend, verify auth flow, price chart, schedule generation

---

### 7. **Phase 7: Frontend Schedule Management (Apply, History, Current)**
   - **Objective:** Implement schedule application to Daikin, history display, and current schedule retrieval
   - **Files/Functions to Modify/Create:**
     - [frontend/src/hooks/useApplySchedule.ts](frontend/src/hooks/useApplySchedule.ts) (create)
     - [frontend/src/hooks/useScheduleHistory.ts](frontend/src/hooks/useScheduleHistory.ts) (create)
     - [frontend/src/hooks/useCurrentSchedule.ts](frontend/src/hooks/useCurrentSchedule.ts) (create)
     - [frontend/src/hooks/useGatewayDevices.ts](frontend/src/hooks/useGatewayDevices.ts) (create)
     - [frontend/src/components/ScheduleHistoryList.tsx](frontend/src/components/ScheduleHistoryList.tsx) (create)
     - [frontend/src/components/ConfirmDialog.tsx](frontend/src/components/ConfirmDialog.tsx) (create)
     - [frontend/src/components/JsonViewer.tsx](frontend/src/components/JsonViewer.tsx) (create)
     - [frontend/src/pages/DashboardPage.tsx](frontend/src/pages/DashboardPage.tsx) (update)
   - **Tests to Write:**
     - Component tests for schedule history, apply flow, current schedule display
   - **Steps:**
     1. Implement useApplySchedule mutation with device auto-fill logic
     2. Create ConfirmDialog component (MUI Dialog) for apply confirmation
     3. Wire up "Apply Schedule" button with loading and error states
     4. Add MUI Snackbar for success/error messages
     5. Implement useScheduleHistory hook with local date formatting
     6. Create ScheduleHistoryList with MUI Accordion (expandable entries)
     7. Display historical schedules with timestamps and schedule grids
     8. Implement useCurrentSchedule hook for retrieving active schedule
     9. Add "Retrieve Current Schedule" button with loading state
     10. Display current schedule in separate card with ScheduleGrid
     11. Create JsonViewer component for raw data display (MUI Accordion)
     12. Test schedule workflow: Generate → Apply → Verify in history → Retrieve current

---

### 8. **Phase 8: Frontend Settings & Polish**
   - **Objective:** Complete settings page and add UX polish (responsive design, error handling, animations)
   - **Files/Functions to Modify/Create:**
     - [frontend/src/hooks/useUserSettings.ts](frontend/src/hooks/useUserSettings.ts) (create)
     - [frontend/src/hooks/useZone.ts](frontend/src/hooks/useZone.ts) (create)
     - [frontend/src/pages/SettingsPage.tsx](frontend/src/pages/SettingsPage.tsx) (create)
     - [frontend/src/components/ErrorBoundary.tsx](frontend/src/components/ErrorBoundary.tsx) (create)
     - [frontend/src/components/LoadingSpinner.tsx](frontend/src/components/LoadingSpinner.tsx) (create)
     - All components - Add responsive breakpoints and loading skeletons
   - **Tests to Write:**
     - Settings form validation tests
     - Error boundary tests
     - Responsive design tests (viewport resizing)
   - **Steps:**
     1. Implement useUserSettings hook (get and save mutations)
     2. Create Settings page layout with MUI Cards
     3. Add MUI Sliders for ComfortHours (1-12) and TurnOffPercentile (0.5-0.99)
     4. Add TextFields for MaxConsecutiveTurnOff and MaxComfortGapHours
     5. Add MUI Switch for AutoApplySchedule toggle
     6. Implement form validation with error messages
     7. Wire up Save button with loading state and success feedback
     8. Implement useZone hook for zone selection persistence
     9. Create zone select dropdown (MUI Select) with SE3/SE2/NO5 options
     10. Add Danger Zone card with Token Revocation (double-confirmation dialog)
     11. Create ErrorBoundary component for graceful error handling
     12. Add MUI Skeleton loading states to all data-dependent components
     13. Make layout responsive with MUI breakpoints (xs, sm, md, lg, xl)
     14. Add ARIA labels and keyboard navigation for accessibility
     15. Test on mobile viewport (Chrome DevTools) - verify responsive design
     16. Test error scenarios (network errors, invalid data) - verify error boundaries

---

### 9. **Phase 9: Integration Testing & Production Build**
   - **Objective:** End-to-end testing, production build optimization, and deployment preparation
   - **Files/Functions to Modify/Create:**
     - [frontend/vite.config.ts](frontend/vite.config.ts) - Configure production build to output to `../wwwroot`
     - [.github/workflows/build.yml](.github/workflows/build.yml) - Add frontend build step
     - [Dockerfile](Dockerfile) - Add frontend build stage
     - [frontend/src/utils/analytics.ts](frontend/src/utils/analytics.ts) (optional - usage tracking)
   - **Tests to Write:**
     - E2E tests with Playwright (optional but recommended)
   - **Steps:**
     1. Configure Vite build output: `build.outDir: '../wwwroot'`
     2. Test production build: `cd frontend && npm run build`
     3. Verify output in `wwwroot/` directory
     4. Start backend with production build: `cd .. && dotnet run --configuration Release`
     5. Test all features with production build (localhost:5000)
     6. Update GitHub Actions workflow to build frontend before backend
     7. Add frontend build stage to Dockerfile (multi-stage build)
     8. Test Docker image builds successfully
     9. Run full test suite (backend + frontend): `dotnet test && cd frontend && npm test`
     10. Manual end-to-end testing checklist:
         - OAuth flow (start, callback, status display)
         - Price chart loads with today/tomorrow data
         - Schedule generation works
         - Schedule application to Daikin succeeds
         - Schedule history persists and displays correctly
         - Current schedule retrieval works
         - Settings save and apply correctly
         - Token revocation works
     11. Performance testing: Lighthouse audit (target: Performance 90+, Accessibility 95+)
     12. Browser compatibility testing (Chrome, Firefox, Safari, Edge)

---

### 10. **Phase 10: Documentation & Migration**
   - **Objective:** Update documentation, create migration guide, and prepare for production deployment
   - **Files/Functions to Modify/Create:**
     - [README.md](README.md) - Update with frontend build instructions
     - [ROADMAP.md](ROADMAP.md) - Mark completed items, add new technical debt
     - [frontend/README.md](frontend/README.md) (create) - Frontend-specific documentation
     - [MIGRATION.md](MIGRATION.md) (create) - Guide for upgrading from old frontend
     - [CHANGELOG.md](CHANGELOG.md) (create) - Version history and breaking changes
   - **Tests to Write:**
     - No tests - documentation only
   - **Steps:**
     1. Update [README.md](README.md) with frontend development instructions
     2. Document frontend architecture in [frontend/README.md](frontend/README.md)
     3. Create MIGRATION.md with upgrade instructions and rollback procedure
     4. Document breaking changes (if any) in CHANGELOG.md
     5. Update environment variable documentation (no changes expected)
     6. Create PR template with testing checklist
     7. Update ROADMAP.md: Mark resolved items (console logging, options classes), add new items (E2E tests, WebSocket support, analytics)
     8. Create deployment guide for production cutover
     9. Document multiple PR strategy in MIGRATION.md
     10. Create backup of old `wwwroot/` before final merge

---

## Multiple PR Strategy

**To avoid merge conflicts and enable incremental review:**

**PR #1: Backend Testing & Bug Fixes (Phases 1-3)**
- Branch: `feature/backend-testing`
- Focus: Test infrastructure, history bug fix, comprehensive test coverage
- Safe to merge: No breaking changes, only additions

**PR #2: Backend Refactoring (Phase 4)**
- Branch: `feature/backend-refactoring`
- Focus: Structured logging, Options classes, HttpClientFactory
- Rebase on PR #1 after merge

**PR #3: Frontend Foundation (Phase 5)**
- Branch: `feature/frontend-foundation`
- Focus: Vite setup, MUI theme, routing, API client
- Independent from backend changes - can be parallel

**PR #4: Frontend Core Features (Phases 6-7)**
- Branch: `feature/frontend-core`
- Focus: Auth, prices, schedule management
- Rebase on PR #3 after merge

**PR #5: Frontend Complete (Phase 8)**
- Branch: `feature/frontend-polish`
- Focus: Settings page, responsive design, error handling
- Rebase on PR #4 after merge

**PR #6: Production Ready (Phases 9-10)**
- Branch: `feature/production-build`
- Focus: Build pipeline, Docker, documentation, migration guide
- Merge all previous PRs first

---

## Open Questions

1. **Testing Priority** - Should we write ALL tests first (Phases 1-3) before any refactoring (Phase 4)? Or interleave testing and refactoring? *Recommended: All tests first for safety* **✅ APPROVED**

2. **History Retention** - Current 7-day retention seems aggressive. Should we increase to 30 days? Make it configurable? *Recommended: Default 30 days, configurable via appsettings.json* **✅ APPROVED**

3. **Frontend Directory** - Should the new frontend live in `frontend/` subdirectory or replace `wwwroot/` entirely during development? *Recommended: Separate `frontend/` dir during development, build to `wwwroot/` for production* **✅ APPROVED - Already working this way**

4. **Migration Strategy** - Should we keep old frontend files for rollback, or delete them after new frontend is verified? *Recommended: Keep backup in git history only, clean removal after 1 week in production*

5. **E2E Testing** - Should we invest in Playwright/Cypress for E2E tests in Phase 9? *Recommended: Yes, but as separate follow-up PR after Phase 10* **✅ APPROVED**

6. **Multiple PRs vs Single PR** - Would you prefer 6 separate PRs (as outlined above) or fewer larger PRs? *Recommended: 6 PRs for easier review and incremental deployment* **✅ APPROVED - 6 PRs**
