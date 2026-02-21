## Phase 1 Complete: Fix MigrateUserDataAsync and token save flow

Fixed the OAuth user deduplication migration logic to always overwrite target tokens instead of skipping when target already exists. This was the primary reason the bug persisted after PR #67.

**Files created/changed:**
- DaikinOAuthService.cs
- Data/Repositories/DaikinTokenRepository.cs
- Prisstyrning.Tests/Integration/DaikinOAuthServiceIntegrationTests.cs

**Functions created/changed:**
- `MigrateUserDataAsync` — removed `if (existingTarget == null)` guard; always overwrites target with source tokens
- `FindByDaikinSubjectAsync` — new repo method to query by DaikinSubject column
- 5 new test methods + 1 updated test

**Tests created/changed:**
- MigrateUserDataAsync_WhenTargetExists_OverwritesWithSourceTokens
- MigrateUserDataAsync_WhenTargetNotExists_CopiesToTarget
- MigrateUserDataAsync_PreservesDaikinSubject
- FindByDaikinSubjectAsync_ReturnsExistingToken
- FindByDaikinSubjectAsync_ReturnsNullWhenNotFound

**Review Status:** APPROVED

**Git Commit Message:**
fix: always overwrite tokens on user migration for OAuth dedup
