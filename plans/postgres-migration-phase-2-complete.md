## Phase 2 Complete: User Settings & Admin Repository

Replaced static `UserSettingsService` and `AdminService` with EF Core-backed `UserSettingsRepository` and `AdminRepository`. Updated all API endpoints in Program.cs, ScheduleUpdateHangfireJob, and NordpoolPriceHangfireJob to use the new DB-backed repositories. All 231 tests pass (227 passed, 4 skipped).

**Files created/changed:**
- Data/Repositories/UserSettingsRepository.cs (new)
- Data/Repositories/AdminRepository.cs (new)
- Program.cs (modified — DI registration, all user settings & zone endpoints)
- BatchRunner.cs (modified — using new UserScheduleSettings type)
- Jobs/ScheduleUpdateHangfireJob.cs (modified — IServiceScopeFactory, DB-backed auto-apply user discovery)
- Jobs/NordpoolPriceHangfireJob.cs (modified — IServiceScopeFactory, DB-backed zone discovery)
- Prisstyrning.Tests/Unit/Data/UserSettingsRepositoryTests.cs (new)
- Prisstyrning.Tests/Unit/Data/AdminRepositoryTests.cs (new)
- Prisstyrning.Tests/Jobs/ScheduleUpdateJobTests.cs (modified — InMemory DB + IServiceScopeFactory)
- Prisstyrning.Tests/Jobs/NordpoolPriceJobTests.cs (modified — InMemory DB + IServiceScopeFactory)

**Functions created/changed:**
- UserSettingsRepository: LoadScheduleSettingsAsync, GetUserZoneAsync, SetUserZoneAsync, GetOrCreateAsync, SaveSettingsAsync, GetAutoApplyUserIdsAsync, GetAllUserZonesAsync, IsValidZone, DeleteAsync
- AdminRepository: IsAdminAsync, GrantAdminAsync, RevokeAdminAsync, GetAdminUserIdsAsync, HasHangfireAccessAsync, GrantHangfireAccessAsync, RevokeHangfireAccessAsync, GetHangfireUserIdsAsync, CheckAdminAccess, IsValidUserId, DeleteAsync
- UserScheduleSettings record (in UserSettingsRepository.cs)
- NordpoolPriceHangfireJob: Updated constructor (IServiceScopeFactory), zone discovery via DB
- ScheduleUpdateHangfireJob: Updated constructor (IServiceScopeFactory), user discovery via DB

**Tests created/changed:**
- UserSettingsRepositoryTests: 30+ tests (CRUD, defaults, validation clamping, zone operations)
- AdminRepositoryTests: 25+ tests (admin/hangfire CRUD, access checks, validation)
- ScheduleUpdateJobTests: Updated to use InMemory DB seeding and IServiceScopeFactory
- NordpoolPriceJobTests: Updated to use InMemory DB seeding and IServiceScopeFactory

**Review Status:** APPROVED

**Git Commit Message:**
```
feat: add UserSettings and Admin EF Core repositories

- Create UserSettingsRepository replacing static UserSettingsService
- Create AdminRepository replacing static AdminService
- Update Program.cs endpoints to use DB-backed repositories
- Update Hangfire jobs with IServiceScopeFactory for DB access
- Add 55+ repository tests with InMemory database provider
```
