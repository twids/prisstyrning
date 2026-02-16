## Phase 1 Complete: Infrastructure & Entity Models

Added PostgreSQL + EF Core infrastructure with 5 entity models, DbContext, initial migration, docker-compose PostgreSQL service with healthcheck, and startup migration with retry logic.

**Files created:**
- Data/Entities/UserSettings.cs
- Data/Entities/AdminRole.cs
- Data/Entities/PriceSnapshot.cs
- Data/Entities/ScheduleHistoryEntry.cs
- Data/Entities/DaikinToken.cs
- Data/PrisstyrningDbContext.cs
- Data/Migrations/ (InitialCreate migration)
- Prisstyrning.Tests/Unit/Data/DbContextTests.cs
- Prisstyrning.Tests/Unit/Data/EntityTests.cs

**Files changed:**
- Prisstyrning.csproj (added Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.EntityFrameworkCore.Design)
- Prisstyrning.Tests/Prisstyrning.Tests.csproj (added Microsoft.EntityFrameworkCore.InMemory)
- Program.cs (DbContext DI registration, startup migration with retry)
- appsettings.json (ConnectionStrings section)
- docker-compose.example.yml (PostgreSQL service with healthcheck, depends_on condition)

**Functions created:**
- PrisstyrningDbContext.OnModelCreating — entity configurations, indexes, jsonb columns
- Startup migration retry loop (5 attempts with exponential backoff)

**Tests created:**
- DbContextTests — DbContext creation, CRUD operations, duplicate PK handling, auto-increment verification
- EntityTests — Default value assertions for all 5 entity types

**Review Status:** APPROVED with minor recommendations addressed (healthcheck + retry logic)

**Git Commit Message:**
```
feat: add PostgreSQL + EF Core infrastructure

- Add Npgsql.EntityFrameworkCore.PostgreSQL and EF Core Design packages
- Define 5 entity models: UserSettings, AdminRole, PriceSnapshot, ScheduleHistoryEntry, DaikinToken
- Create PrisstyrningDbContext with OnModelCreating configuration (indexes, jsonb, constraints)
- Generate InitialCreate migration for PostgreSQL
- Register DbContext in DI with startup migration and retry logic
- Add PostgreSQL service with healthcheck to docker-compose.example.yml
- Add DbContext and entity unit tests
```
