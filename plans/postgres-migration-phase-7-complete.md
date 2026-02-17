## Phase 7 Complete: Cleanup & Documentation

Deleted all 5 old file-based static services (StoragePaths, NordpoolPersistence, ScheduleHistoryPersistence, UserSettingsService, AdminService) and their 3 dedicated test files. Updated 3 test files to use EF Core InMemory DB repositories. Fixed last remaining production reference in BatchRunner.cs. Updated README.md to document PostgreSQL requirement.

**Files created/changed:**
- BatchRunner.cs (replaced UserSettingsService.LoadScheduleSettings with UserSettingsRepository)
- README.md (PostgreSQL docs, connection string config, updated docker-compose example, test count)
- Prisstyrning.Tests/Api/EndpointIntegrationTests.cs (5 tests rewritten to use InMemory DB repos)
- Prisstyrning.Tests/Integration/BatchRunnerIntegrationTests.cs (removed NordpoolPersistence.Save)
- Prisstyrning.Tests/Jobs/ScheduleUpdateJobTests.cs (removed file-based user.json and NordpoolPersistence)

**Files deleted:**
- StoragePaths.cs
- NordpoolPersistence.cs
- ScheduleHistoryPersistence.cs
- UserSettingsService.cs
- AdminService.cs (already absent)
- Prisstyrning.Tests/Unit/NordpoolPersistenceTests.cs
- Prisstyrning.Tests/ScheduleHistoryPersistenceTests.cs
- Prisstyrning.Tests/UserSettingsServiceTests.cs

**Functions created/changed:**
- BatchRunner.RunBatchAsync — loads settings from UserSettingsRepository via IServiceScopeFactory (fallback to config defaults)

**Tests created/changed:**
- EndpointIntegrationTests: GET_UserSettings_ReturnsCorrectDefaults, POST_UserSettings_ValidatesInput, GET_ScheduleHistory_ReturnsUserHistory, GET_PricesZone_ReturnsUserZone, POST_PricesZone_ValidatesZone
- BatchRunnerIntegrationTests: RunBatchAsync_WithValidPriceData_GeneratesSchedule
- ScheduleUpdateJobTests: ExecuteAsync_WithAutoApplyEnabled_GeneratesSchedules

**Review Status:** APPROVED

**Git Commit Message:**
```
chore: remove legacy file-based services and update docs

- Delete StoragePaths, NordpoolPersistence, ScheduleHistoryPersistence, UserSettingsService
- Delete corresponding test files (NordpoolPersistenceTests, ScheduleHistoryPersistenceTests, UserSettingsServiceTests)
- Update BatchRunner to load settings from UserSettingsRepository
- Rewrite 5 endpoint integration tests to use InMemory DB repositories
- Remove file-based setup from BatchRunner and ScheduleUpdate job tests
- Update README with PostgreSQL requirement, connection string config, and docker-compose example
```
