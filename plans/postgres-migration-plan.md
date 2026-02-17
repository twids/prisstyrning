## Plan: PostgreSQL + EF Core Migration

Migrate all file-based JSON persistence (user settings, admin roles, prices, schedule history, Daikin tokens) to PostgreSQL with EF Core. Add a startup migrator that imports existing JSON files into the database and deletes them on success. Clean break — no JSON fallback mode. PriceMemory stays in-memory as a cache. PKCE state stays in-memory (ephemeral).

**Phases (7)**

### 1. Phase 1: Infrastructure & Entity Models
- **Objective:** Add PostgreSQL + EF Core packages, define all entity models, create `PrisstyrningDbContext`, generate the initial EF Core migration, and add PostgreSQL to `docker-compose.example.yml`.
- **Files/Functions to Create:**
  - `Data/PrisstyrningDbContext.cs` — DbContext with DbSets for all entities
  - `Data/Entities/UserSettings.cs` — UserId (PK), ComfortHours, TurnOffPercentile, MaxComfortGapHours, AutoApplySchedule, Zone
  - `Data/Entities/AdminRole.cs` — UserId (PK), IsAdmin, HasHangfireAccess
  - `Data/Entities/PriceSnapshot.cs` — Id (auto), Zone, Date (DateOnly), SavedAtUtc, TodayPricesJson (jsonb string), TomorrowPricesJson (jsonb string)
  - `Data/Entities/ScheduleHistoryEntry.cs` — Id (auto), UserId, Timestamp, SchedulePayloadJson (jsonb string)
  - `Data/Entities/DaikinToken.cs` — UserId (PK), AccessToken, RefreshToken, ExpiresAtUtc
  - `Data/Migrations/` — Initial EF Core migration
  - Update `Prisstyrning.csproj` — Add `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.InMemory` (for tests)
  - Update `appsettings.json` — Add `ConnectionStrings:DefaultConnection`
  - Update `docker-compose.example.yml` — Add PostgreSQL service + connection string env var
  - Update `Program.cs` — Register `PrisstyrningDbContext` in DI, call `Database.Migrate()` on startup
- **Tests to Write:**
  - `DbContextCreationTests` — Verify DbContext can be created with in-memory provider, all DbSets are accessible
  - `EntityValidationTests` — Verify entity configurations (key, required fields, jsonb columns)
- **Steps:**
  1. Write entity model tests that verify entity configurations and DbContext creation
  2. Run tests — they fail
  3. Add NuGet packages, create entity classes and DbContext
  4. Register DbContext in DI, add connection string config, update docker-compose
  5. Generate initial EF Core migration via `dotnet ef migrations add InitialCreate`
  6. Run tests — they pass

### 2. Phase 2: User Settings & Admin Repository
- **Objective:** Replace static `UserSettingsService` and `AdminService` with injected EF Core-backed services. Update all API endpoints in Program.cs to use the new services.
- **Files/Functions to Modify/Create:**
  - Create `Data/Repositories/UserSettingsRepository.cs` — Injectable service wrapping EF Core CRUD for `UserSettings`
  - Create `Data/Repositories/AdminRepository.cs` — Injectable service wrapping EF Core CRUD for `AdminRole`
  - Modify `Program.cs` — Register repositories in DI, replace all `UserSettingsService.*` and `AdminService.*` calls with injected repository calls
  - Modify `BatchRunner.cs` — Replace `UserSettingsService.LoadScheduleSettings()` calls
  - Modify `Jobs/ScheduleUpdateHangfireJob.cs` — Replace settings access
- **Tests to Write:**
  - `UserSettingsRepositoryTests` — CRUD operations, defaults for missing users, validation clamping, zone operations
  - `AdminRepositoryTests` — Grant/revoke admin, grant/revoke hangfire, check access, password fallback
- **Steps:**
  1. Write repository tests using in-memory DbContext
  2. Run tests — they fail
  3. Implement `UserSettingsRepository` and `AdminRepository`
  4. Run tests — they pass
  5. Update Program.cs endpoints and BatchRunner/Jobs to use injected repositories
  6. Update existing integration tests
  7. Run full test suite

### 3. Phase 3: Price Data Repository
- **Objective:** Replace `NordpoolPersistence` with an EF Core-backed price repository. Keep `PriceMemory` as an in-memory cache but populate it from the database on startup instead of from files. Store prices as jsonb blobs (not normalized rows). Supports all Nordpool zones (SE1-4, NO1-9, DK1-2, FI, EE, LV, LT) via the Zone column.
- **Files/Functions to Modify/Create:**
  - Create `Data/Repositories/PriceRepository.cs` — Save/load price snapshots, query by zone+date
  - Modify `PriceMemory.cs` — Add method to load from DB on startup (keep in-memory caching behavior intact)
  - Modify `Program.cs` — Replace `NordpoolPersistence` calls and file-based price loading at startup
  - Modify `NordpoolClient.cs` / `Jobs/NordpoolPriceHangfireJob.cs` — Save to DB instead of files
  - Modify `Jobs/DailyPriceHangfireJob.cs` — Update price persistence calls
- **Tests to Write:**
  - `PriceRepositoryTests` — Save snapshot, load latest by zone, load by date range, multi-zone isolation, handle no data
- **Steps:**
  1. Write price repository tests
  2. Run tests — they fail
  3. Implement `PriceRepository`
  4. Run tests — they pass
  5. Update NordpoolPriceHangfireJob, DailyPriceHangfireJob, and Program.cs startup preload
  6. Run full test suite

### 4. Phase 4: Schedule History Repository
- **Objective:** Replace `ScheduleHistoryPersistence` with an EF Core-backed repository with retention policy support.
- **Files/Functions to Modify/Create:**
  - Create `Data/Repositories/ScheduleHistoryRepository.cs` — Save, load by user, retention cleanup
  - Modify `Program.cs` — Replace `ScheduleHistoryPersistence` calls in endpoints
  - Modify `BatchRunner.cs` — Replace history save calls
- **Tests to Write:**
  - `ScheduleHistoryRepositoryTests` — Save, load, append, retention cutoff, multi-user isolation
- **Steps:**
  1. Write schedule history repository tests
  2. Run tests — they fail
  3. Implement `ScheduleHistoryRepository`
  4. Run tests — they pass
  5. Update endpoints, BatchRunner, and Hangfire jobs
  6. Run full test suite

### 5. Phase 5: Daikin Token Repository
- **Objective:** Replace file-based token storage in `DaikinOAuthService` with EF Core-backed `DaikinTokenRepository`. Refactor `DaikinOAuthService` from static to injectable. Keep PKCE `_stateToVerifier` in-memory (ephemeral, short-lived).
- **Files/Functions to Modify/Create:**
  - Create `Data/Repositories/DaikinTokenRepository.cs` — Save/load/delete tokens by userId
  - Modify `DaikinOAuthService.cs` — Inject repository, remove file I/O, convert from static to instance class. Keep `_stateToVerifier` in-memory.
  - Modify `Program.cs` — Register `DaikinOAuthService` in DI, update all Daikin endpoint calls
  - Modify `Jobs/DaikinTokenRefreshHangfireJob.cs` — Use injected service
- **Tests to Write:**
  - `DaikinTokenRepositoryTests` — Save, load, delete, upsert, expiry check
- **Steps:**
  1. Write token repository tests
  2. Run tests — they fail
  3. Implement `DaikinTokenRepository`
  4. Refactor `DaikinOAuthService` to use injected repository
  5. Run tests — they pass
  6. Update endpoints and jobs
  7. Run full test suite

### 6. Phase 6: JSON-to-DB Startup Migrator
- **Objective:** Create a startup migration service that reads all existing JSON files, imports data into PostgreSQL, and deletes the JSON files after successful migration.
- **Files/Functions to Create:**
  - Create `Data/JsonMigrationService.cs` — `IHostedService` that runs once on startup before other services
    - Reads `admin.json` → inserts AdminRole rows → deletes file
    - Reads `tokens/{userId}/user.json` → inserts UserSettings rows → deletes file
    - Reads `tokens/{userId}/daikin.json` → inserts DaikinToken rows → deletes file
    - Reads `nordpool/{zone}/prices-*.json` → inserts PriceSnapshot rows → deletes files
    - Reads `schedule_history/{userId}/history.json` → inserts ScheduleHistoryEntry rows → deletes file
    - Logs progress and any failures per file
    - Only runs if old data directory contains JSON files; idempotent on re-run
- **Tests to Write:**
  - `JsonMigrationServiceTests` — Migrate each data type, verify DB contents match JSON, verify files deleted, verify idempotent re-run on empty dirs, verify partial failure handling (some files fail, others succeed)
- **Steps:**
  1. Write migration service tests using temp directories with fixture JSON
  2. Run tests — they fail
  3. Implement `JsonMigrationService`
  4. Run tests — they pass
  5. Register in Program.cs as a hosted service that runs before other services
  6. Run full test suite

### 7. Phase 7: Cleanup & Documentation
- **Objective:** Remove all dead file-based persistence code, update documentation, and verify the full app works end-to-end.
- **Files/Functions to Remove/Modify:**
  - Delete `StoragePaths.cs`
  - Delete `NordpoolPersistence.cs`
  - Delete `ScheduleHistoryPersistence.cs`
  - Delete or gut `UserSettingsService.cs` (keep `IsValidZone` if still used, move to a shared utility)
  - Delete or gut `AdminService.cs`
  - Update `README.md` — Document PostgreSQL requirement, docker-compose setup, connection string config, env var `PRISSTYRNING_ConnectionStrings__DefaultConnection`
  - Update `MIGRATION.md` — Document the JSON→DB migration behavior
  - Update `Prisstyrning.Tests/Fixtures/TempFileSystem.cs` — Replace with in-memory DB test helpers or remove if unused
  - Remove/update old persistence tests that tested file-based storage
  - Update `ROADMAP.md` — Mark persistence backend item as completed (PostgreSQL, not SQLite)
- **Tests to Write:**
  - No new tests, but verify all existing tests pass after cleanup
- **Steps:**
  1. Delete dead files and remove unused code paths
  2. Update documentation files
  3. Update test fixtures
  4. Run full test suite — all pass
  5. Verify docker-compose brings up app + postgres correctly
