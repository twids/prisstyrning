## Phase 6 Complete: Refactor Program.cs API Endpoints

Updated all remaining API endpoints in Program.cs and Jobs to inject IHttpClientFactory for NordpoolClient and DaikinApiClient instantiation. Main project now builds successfully with 0 compilation errors. Test project errors deferred to Phase 7.

**Files created/changed:**
- Program.cs
- Jobs/NordpoolPriceHangfireJob.cs

**Functions created/changed:**
- NordpoolPriceHangfireJob constructor (added IHttpClientFactory parameter)
- Program.cs: Updated 3 endpoints to inject IHttpClientFactory for NordpoolClient:
  - `/api/prices/_debug/fetch`
  - `/api/prices/_debug/raw`
  - NordpoolPriceHangfireJob.ExecuteAsync
- Program.cs: Updated 5 endpoints to inject IHttpClientFactory for DaikinApiClient:
  - `/api/daikin/sites`
  - `/api/daikin/gateway/schedule`
  - `/api/daikin/devices`
  - `/api/daikin/gateway`
  - `/api/daikin/gateway/schedule/put`

**Tests created/changed:**
- N/A (test updates in Phase 7)

**Build Status:**
- Main project: ✅ Build succeeded (0 errors)
- Test project: ❌ 10 test errors (expected - addressed in Phase 7)

**Review Status:** APPROVED

**Git Commit Message:**
```
refactor: Update all API endpoints to use IHttpClientFactory

- Updated NordpoolPriceHangfireJob to inject IHttpClientFactory
- Updated 3 Program.cs endpoints creating NordpoolClient instances
- Updated 5 Program.cs endpoints creating DaikinApiClient instances
- All client instantiations now use factory.CreateClient() pattern
- Main project builds successfully with 0 compilation errors
- Test failures expected and will be resolved in Phase 7
```
