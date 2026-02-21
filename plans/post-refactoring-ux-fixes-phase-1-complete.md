## Phase 1 Complete: Auto-Detect Device IDs for Manual Apply

Device IDs (gatewayDeviceId, embeddedId) are now automatically detected when applying schedules manually via the GUI, matching the behavior of the automatic scheduler.

**Files created/changed:**
- Program.cs (added AutoDetectDeviceAsync helper, updated PUT endpoint)
- Prisstyrning.Tests/Unit/DeviceAutoDetectionTests.cs (created, 5 new tests)
- frontend/src/types/api.ts (made device IDs optional)
- frontend/src/hooks/useApplySchedule.ts (made device IDs optional)
- frontend/src/pages/DashboardPage.tsx (removed device ID fields)

**Functions created/changed:**
- AutoDetectDeviceAsync() - New helper method for device auto-detection
- PUT /api/daikin/gateway/schedule/put - Now auto-detects device IDs when not provided
- DashboardPage.handleApplySchedule - Simplified to work without device IDs
- ApplyScheduleRequest interface - Device IDs now optional

**Tests created/changed:**
- Test_ConfigOverrides_AreRespected
- Test_ParseDeviceJson_FindsDHWManagementPoint
- Test_ParseDeviceJson_NoDHW_ReturnsNull
- Test_ParseSitesJson_ExtractsFirstSiteId
- Test_ParseDevicesJson_ExtractsFirstDeviceIdAndJson

**Review Status:** APPROVED

### Test Results
✅ Backend: 130/130 tests pass (4 skipped)
✅ Frontend: TypeScript compilation successful
✅ Application: Starts on port 5000 without errors
✅ BatchRunner: No regression in automatic scheduler

**Git Commit Message:**
```
feat: Auto-detect device IDs for manual schedule application

- Add AutoDetectDeviceAsync helper extracting BatchRunner's detection logic
- Make gatewayDeviceId and embeddedId optional in PUT /api/daikin/gateway/schedule/put
- Auto-detect device IDs when not provided (config overrides → first site → first device → DHW)
- Remove device ID text fields from Dashboard UI
- Add 5 unit tests for device auto-detection logic
- Ensure automatic scheduler continues working unchanged

Fixes manual schedule application regression after frontend rewrite where users
had to manually enter technical device IDs. Now works like automatic scheduler.
```
