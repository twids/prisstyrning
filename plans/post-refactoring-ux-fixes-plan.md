## Plan: Post-Refactoring UX Fixes

Fix three critical UX regressions after the ECO removal + frontend rewrite: (1) Device IDs must be manually entered when applying schedules (automatic scheduler already auto-detects), (2) Schedule history is incorrectly saved on preview instead of on apply, (3) AutoApplySchedule setting doesn't persist when saved, (4) Retrieved current schedule only shows raw JSON instead of visual schedule grid.

**Phases: 4**

### 1. **Phase 1: Make Manual Apply Use Auto-Detection (Like Scheduled Apply)**
   - **Objective:** Make `/api/daikin/gateway/schedule/put` auto-detect device IDs when not provided, matching the behavior of the automatic scheduler (BatchRunner)
   - **Files/Functions to Modify/Create:**
     - [Program.cs](Program.cs) - Refactor device auto-detection logic into reusable helper method
     - [Program.cs](Program.cs) - Update `/api/daikin/gateway/schedule/put` to make gatewayDeviceId and embeddedId optional parameters
     - [frontend/src/types/api.ts](frontend/src/types/api.ts) - Make device ID fields optional in ApplyScheduleRequest
     - [frontend/src/pages/DashboardPage.tsx](frontend/src/pages/DashboardPage.tsx) - Remove device ID text fields, send only schedulePayload
     - [Prisstyrning.Tests/Api/EndpointIntegrationTests.cs](Prisstyrning.Tests/Api/EndpointIntegrationTests.cs) - Add test for apply with auto-detection
     - [Prisstyrning.Tests/Unit/DaikinApiTests.cs](Prisstyrning.Tests/Unit/DaikinApiTests.cs) (create) - Test auto-detection helper logic
   - **Tests to Write:**
     - Test_ApplySchedule_NoDeviceIds_AutoDetects_Success
     - Test_ApplySchedule_WithDeviceIds_UsesProvided
     - Test_ApplySchedule_NoAuth_Returns401
     - Test_AutoDetectDevice_PrefersDHW_Success
     - Test_AutoDetectDevice_NoDevices_ReturnsError
   - **Steps:**
     1. Write failing tests for apply endpoint with optional device IDs
     2. Extract device auto-detection logic from BatchRunner into reusable helper method in Program.cs
     3. Helper should: get first site → get first device OR use config overrides → find DHW embeddedId OR use override
     4. Update `/api/daikin/gateway/schedule/put` to check if gatewayDeviceId/embeddedId are provided
     5. If not provided: call auto-detection helper, use detected values
     6. If provided: use provided values (for future multi-device support)
     7. Run backend tests to verify auto-detection works
     8. Update frontend ApplyScheduleRequest to make device IDs optional
     9. Update DashboardPage to remove device ID text fields and state entirely
     10. Update applySchedule mutation to send only schedulePayload (no device IDs)
     11. Test manually: click Apply Schedule → auto-detects → succeeds
     12. Test manually: verify automatic scheduler still works (no regression)
     13. Run all tests to confirm no regressions

### 2. **Phase 2: Fix Schedule History - Only Save on Apply**
   - **Objective:** Move schedule history persistence from preview endpoint to apply endpoint so history only contains actually-applied schedules
   - **Files/Functions to Modify/Create:**
     - [Program.cs](Program.cs) - Change `/api/schedule/preview` persist flag from true to false
     - [Program.cs](Program.cs) - Add schedule history save to `/api/daikin/gateway/schedule/put` endpoint
     - [BatchRunner.cs](BatchRunner.cs) - Ensure persist flag is respected correctly
     - [Prisstyrning.Tests/Unit/BatchRunnerTests.cs](Prisstyrning.Tests/Unit/BatchRunnerTests.cs) (create) - Test persist flag behavior
     - [Prisstyrning.Tests/Integration/ScheduleHistoryIntegrationTests.cs](Prisstyrning.Tests/Integration/ScheduleHistoryIntegrationTests.cs) (create) - Test history only saved on apply
   - **Tests to Write:**
     - Test_SchedulePreview_DoesNotSaveHistory
     - Test_ScheduleApply_SavesHistory
     - Test_BatchRunner_PersistFalse_DoesNotSaveHistory
     - Test_BatchRunner_PersistTrue_SavesHistory
     - Integration_PreviewThenApply_OnlyOneHistoryEntry
   - **Steps:**
     1. Write failing tests for history persistence behavior (preview should not save, apply should save)
     2. Change `/api/schedule/preview` endpoint to call BatchRunner with `persist: false`
     3. Update `/api/daikin/gateway/schedule/put` to save schedule to history after successful PUT
     4. Run tests to verify preview doesn't save and apply does save
     5. Test manually: generate preview multiple times, verify no new history entries
     6. Test manually: apply schedule, verify it appears in history
     7. Run integration tests to verify end-to-end flow
     8. Clean up any test history entries from development

### 3. **Phase 3: Fix AutoApplySchedule Setting Persistence**
   - **Objective:** Ensure AutoApplySchedule setting is properly returned after save so frontend displays updated value correctly
   - **Files/Functions to Modify/Create:**
     - [Program.cs](Program.cs) - Change POST `/api/user/settings` to return the saved settings object instead of just `{saved: true}`
     - [frontend/src/hooks/useUserSettings.ts](frontend/src/hooks/useUserSettings.ts) - Verify mutation onSuccess properly updates cache
     - [Prisstyrning.Tests/Api/EndpointIntegrationTests.cs](Prisstyrning.Tests/Api/EndpointIntegrationTests.cs) - Add test for settings endpoint response
   - **Tests to Write:**
     - Test_SaveUserSettings_ReturnsUpdatedSettings
     - Test_SaveAutoApplySchedule_True_ReturnsTrue
     - Test_SaveAutoApplySchedule_False_ReturnsFalse
   - **Steps:**
     1. Write failing test: POST settings, verify response contains updated settings
     2. Update POST `/api/user/settings` to return full settings object after save
     3. Run backend tests to verify response format
     4. Update frontend SaveSettingsResponse type if needed
     5. Test manually: toggle AutoApplySchedule, verify it stays toggled
     6. Test manually: save other settings, verify they all persist
     7. Run all tests to ensure no regressions

### 4. **Phase 4: Display Retrieved Schedule Visually with Optional JSON View**
   - **Objective:** Show retrieved current schedule in visual ScheduleGrid format (like generated previews) with toggle to view raw JSON for advanced users
   - **Files/Functions to Modify/Create:**
     - [frontend/src/hooks/useCurrentSchedule.ts](frontend/src/hooks/useCurrentSchedule.ts) - Transform API response to SchedulePayload format
     - [frontend/src/pages/DashboardPage.tsx](frontend/src/pages/DashboardPage.tsx) - Display retrieved schedule in ScheduleGrid + add JSON toggle
5. Schedule parsing: Should schedule parsing happen in backend (C#) or frontend (TypeScript)? **Suggestion:** Frontend - keeps backend thin and allows easy format evolution
6. Toggle persistence: Should we remember user's preference (grid vs JSON view) in localStorage? **Suggestion:** No - default to grid view each time (users rarely need JSON)
7. Empty schedule: How to handle device with no schedule? **Suggestion:** Show friendly message "No schedule currently active on device" instead of empty grid
     - [frontend/src/components/ScheduleViewer.tsx](frontend/src/components/ScheduleViewer.tsx) (create) - Reusable component for schedule display with JSON toggle
     - [frontend/src/utils/scheduleParser.ts](frontend/src/utils/scheduleParser.ts) (create) - Parse Daikin API schedule format to SchedulePayload format
     - [Prisstyrning.Tests/Unit/ScheduleParserTests.cs](Prisstyrning.Tests/Unit/ScheduleParserTests.cs) (create if backend parsing) - Test schedule format parsing
   - **Tests to Write:**
     - Test_ParseDaikinSchedule_ValidFormat_ReturnsSchedulePayload
     - Test_ParseDaikinSchedule_MultipleSchedules_SelectsActive
     - Test_ParseDaikinSchedule_InvalidFormat_ReturnsNull
     - Component test for ScheduleViewer toggle behavior
   - **Steps:**
     1. Write failing tests for schedule format parsing
     2. Create scheduleParser utility to convert Daikin API response to SchedulePayload format
     3. Handle schedule ID selection (if multiple schedules, pick the active one)
     4. Create ScheduleViewer component with props: schedule, showToggle (default true)
     5. ScheduleViewer shows ScheduleGrid by default with "Show JSON" toggle button
     6. Clicking toggle switches to JsonViewer, button text becomes "Show Schedule"
     7. Update DashboardPage to use ScheduleViewer for currentSchedule.data
     8. Test manually: retrieve schedule → see visual grid → toggle → see JSON → toggle back
     9. Verify schedule grid shows correct comfort/turn_off states
     10. Test with no current schedule (device never had schedule applied)
     11. Run component tests for toggle behavior
     12. Consider reusing ScheduleViewer for schedule history display (future enhancement)

### Open Questions
1. Device detection fallback: If auto-detection fails (no DHW device found), should we show a technical error or user-friendly message? **Suggestion:** User-friendly: "Could not find Daikin DHW device. Please check your Daikin authorization."
2. Config overrides: Should we honor existing `Daikin:DeviceId` and `Daikin:ManagementPointEmbeddedId` config for manual apply too? **Suggestion:** Yes - use same logic as BatchRunner for consistency
3. Settings response: Should return format be `{saved: true, settings: {...}}` or just `{...settings}`? **Suggestion:** Just return settings directly for consistency with GET endpoint
4. Multi-device support: Should we keep device IDs as optional parameters for future multi-device scenarios? **Suggestion:** Yes - if provided, use them; otherwise auto-detect (gradual enhancement path)
