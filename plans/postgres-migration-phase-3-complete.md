## Phase 3 Complete: Price Data Repository

Replaced file-based `NordpoolPersistence` price storage with EF Core-backed `PriceRepository`. All price reads/writes in active code paths now use the database. `PriceMemory` remains as an in-memory cache. `NordpoolPersistence.cs` left intact for Phase 7 cleanup.

**Files created:**
- Data/Repositories/PriceRepository.cs
- Prisstyrning.Tests/Unit/Data/PriceRepositoryTests.cs

**Files changed:**
- Program.cs (DI registration, startup preload from DB, `/api/prices/nordpool/latest` and `/api/prices/timeseries` endpoints)
- Jobs/NordpoolPriceHangfireJob.cs (replaced file I/O with PriceRepository)
- BatchRunner.cs (removed NordpoolPersistence.Save call, updated stale comment)

**Functions created:**
- PriceRepository.SaveSnapshotAsync (upsert by zone+date)
- PriceRepository.GetLatestAsync (newest by date for zone)
- PriceRepository.GetByDateAsync (specific zone+date)
- PriceRepository.GetByDateRangeAsync (inclusive range query)
- PriceRepository.DeleteOlderThanAsync (retention cleanup)

**Tests created:**
- SaveSnapshotAsync_NewEntry_CreatesRecord
- SaveSnapshotAsync_ExistingEntry_Updates
- SaveSnapshotAsync_NormalizesZone
- SaveSnapshotAsync_DifferentZones_IsolatesData
- GetLatestAsync_ReturnsNewestByDate
- GetLatestAsync_NoData_ReturnsNull
- GetByDateAsync_Found_ReturnsSnapshot
- GetByDateAsync_NotFound_ReturnsNull
- GetByDateAsync_WrongZone_ReturnsNull
- GetByDateRangeAsync_ReturnsOnlyInRange
- GetByDateRangeAsync_Empty_ReturnsEmptyList
- DeleteOlderThanAsync_RemovesOld_KeepsNew
- DeleteOlderThanAsync_NothingToDelete_ReturnsZero

**Review Status:** APPROVED

**Git Commit Message:**
feat: add PriceRepository replacing file-based price storage

- Create PriceRepository with upsert, query, and retention methods
- Replace NordpoolPersistence calls in Program.cs, NordpoolPriceHangfireJob
- Add DB-based startup preload for PriceMemory cache
- Add 13 PriceRepository unit tests
