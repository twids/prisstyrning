## Plan Complete: Fix OAuth User Deduplication (Issue #66 Follow-up)

PR #67 added `sub` extraction and `DeriveUserId()` logic to deduplicate users across browsers, but the bug persisted due to 3 implementation issues. This plan fixed all 3 bugs and added comprehensive tests proving the deduplication works correctly.

**Phases Completed:** 3 of 3
1. ✅ Phase 1: Fix MigrateUserDataAsync to always overwrite tokens
2. ✅ Phase 2: Relax issuer validation and improve subject extraction
3. ✅ Phase 3: End-to-end integration tests for 2-browser scenario

**All Files Created/Modified:**
- DaikinOAuthService.cs
- Data/Repositories/DaikinTokenRepository.cs
- Prisstyrning.Tests/Integration/DaikinOAuthServiceIntegrationTests.cs

**Key Functions/Classes Added:**
- `MigrateUserDataAsync` — fixed to always overwrite target tokens (was skipping when target existed)
- `ExtractSubjectFromIdToken` — relaxed issuer validation to accept non-Daikin issuers when audience matches
- `FindByDaikinSubjectAsync` — new repository method for subject-based user lookup
- `RedactIssuerHost` — helper for safe issuer logging

**Root Causes Fixed:**
1. `MigrateUserDataAsync` skipped migration when target userId already had tokens — fresh tokens from browser B were orphaned
2. `ExtractSubjectFromIdToken` required issuer to contain `daikineurope.com` — silently failed for alternate issuers
3. Migration didn't preserve `DaikinSubject` column when copying tokens

**Test Coverage:**
- Total tests written: 9 new tests
- All tests passing: ✅ (291 passed, 4 skipped, 0 failed)

**Recommendations for Next Steps:**
- Add a database index on `DaikinSubject` column for efficient lookup at scale
- Consider adding array-form `aud` claim test coverage
- Monitor production logs for `[DaikinOAuth] id_token issuer not Daikin` to confirm issuer format
