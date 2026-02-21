## Plan: Fix OAuth User Deduplication (Issue #66 Follow-up)

PR #67 added `sub` extraction and `DeriveUserId()` logic but the bug persists due to 3 implementation issues in the migration/token save flow. This plan fixes all 3 bugs to make cross-browser user dedup actually work.

**Phases: 3**

1. **Phase 1: Fix MigrateUserDataAsync to always overwrite tokens**
    - **Objective:** When a user logs in from browser B and the deterministic userId already has tokens (from browser A), the fresh tokens from the new login must REPLACE the stale ones. Currently, migration is skipped entirely when the target exists.
    - **Files/Functions to Modify/Create:**
        - `DaikinOAuthService.cs` → `MigrateUserDataAsync` — remove the `if (existingTarget == null)` guard; always overwrite target tokens with source tokens (fresh ones)
        - `DaikinOAuthService.cs` → `HandleCallbackWithSubjectAsync` — pass `subject` when saving so the `DaikinSubject` column is always populated
        - `Data/Repositories/DaikinTokenRepository.cs` → `FindByDaikinSubjectAsync` — add a lookup method to find existing tokens by DaikinSubject (needed for dedup)
    - **Tests to Write:**
        - `MigrateUserDataAsync_WhenTargetExists_OverwritesWithSourceTokens`
        - `MigrateUserDataAsync_WhenTargetNotExists_CopiesToTarget`
        - `MigrateUserDataAsync_PreservesDaikinSubject`
    - **Steps:**
        1. Write failing tests verifying token overwrite behavior
        2. Fix `MigrateUserDataAsync` to always save source tokens to target (overwrite), delete source, and preserve `DaikinSubject`
        3. Run tests to confirm they pass

2. **Phase 2: Add DaikinSubject lookup and improve callback flow**
    - **Objective:** When a second browser logs in with the same Daikin account, look up the existing user by `DaikinSubject` to find the canonical userId, rather than relying solely on the two-step save-then-migrate pattern. Also relax the issuer validation to be more defensive.
    - **Files/Functions to Modify/Create:**
        - `Data/Repositories/DaikinTokenRepository.cs` → `FindByDaikinSubjectAsync(string subject)` — query by DaikinSubject column
        - `DaikinOAuthService.cs` → `ExtractSubjectFromIdToken` — relax `iss` validation to also accept common Daikin-related issuers, and log the actual issuer on rejection for debugging
        - `Program.cs` → `/auth/daikin/callback` handler — after getting the subject, look up existing token by subject to find the canonical userId
    - **Tests to Write:**
        - `FindByDaikinSubjectAsync_ReturnsExistingToken`
        - `FindByDaikinSubjectAsync_ReturnsNullWhenNotFound`
        - `ExtractSubjectFromIdToken_WithAlternateIssuer_ReturnsSubject`
    - **Steps:**
        1. Write failing tests for FindByDaikinSubjectAsync
        2. Implement FindByDaikinSubjectAsync
        3. Relax issuer validation
        4. Update callback to use subject lookup
        5. Run all tests

3. **Phase 3: End-to-end verification and cleanup**
    - **Objective:** Verify the complete flow works: Browser A login → token saved with subject under stable userId → Browser B login with same Daikin account → subject lookup finds existing user → cookie remapped → same user
    - **Files/Functions to Modify/Create:** Test files only
    - **Tests to Write:**
        - `CallbackFlow_SecondBrowser_SameDaikinAccount_RemapsToExistingUser`
        - `CallbackFlow_FirstLogin_CreatesStableUserId`
    - **Steps:**
        1. Write integration tests covering the full 2-browser scenario
        2. Run complete test suite
        3. Verify build passes

**Open Questions:** None — all three bugs are clear and the fix is straightforward.
