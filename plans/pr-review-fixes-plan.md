## Plan: Address PR #61 Review Comments

Fix all 10 Copilot code review comments on the admin API and user management PR. Covers race conditions, API consistency, input validation, accessibility, error handling, and test coverage.

**Phases: 3**

1. **Phase 1: Backend Fixes (AdminService.cs + Program.cs)**
    - **Objective:** Fix race conditions, API response consistency, userId validation, and Hangfire auth cookie validation
    - **Files/Functions to Modify:**
        - `AdminService.cs` — Add `SemaphoreSlim` lock around read-modify-write in `GrantAdmin`, `RevokeAdmin`, `GrantHangfireAccess`, `RevokeHangfireAccess`
        - `Program.cs` — Change `Results.Ok()` to `Results.Json()` in admin login/grant/revoke endpoints; add userId validation to grant/revoke endpoints; validate cookie in `HangfirePasswordAuthorizationFilter`
    - **Tests to Write:**
        - `AdminService_ConcurrentGrants_NoDataLoss` — verify concurrent operations don't lose data
    - **Steps:**
        1. Add a static `SemaphoreSlim` to `AdminService` and wrap all mutating methods with `await _lock.WaitAsync()` / `finally { _lock.Release() }`
        2. Write test `AdminService_ConcurrentGrants_NoDataLoss` that does parallel grants and verifies all are persisted
        3. Replace all `Results.Ok(new { ... })` with `Results.Json(new { ... })` in admin endpoints
        4. Extract userId validation into a helper and apply it to all `/users/{userId}/grant` and `/users/{userId}/hangfire` endpoints
        5. In `HangfirePasswordAuthorizationFilter.Authorize`, validate the cookie value (length, allowed chars) before using it

2. **Phase 2: Frontend Fixes (client.ts, AdminPage.tsx, Layout.tsx)**
    - **Objective:** Fix return types, error message parsing, accessibility, admin nav visibility, and type safety
    - **Files/Functions to Modify:**
        - `frontend/src/api/client.ts` — Return response from grant/revoke methods; parse JSON error messages in `post()`, `del()`, `adminLogin()`
        - `frontend/src/pages/AdminPage.tsx` — Add `aria-label` to delete IconButton; simplify `onSettled` callback signatures
        - `frontend/src/components/Layout.tsx` — Conditionally render Admin button based on admin status query
    - **Tests to Write:** None (frontend component tests not in scope)
    - **Steps:**
        1. Update `grantAdmin`, `revokeAdmin`, `grantHangfire`, `revokeHangfire` to return their response values with proper types
        2. In `post()`, `del()`, and `adminLogin()` error handling, try to parse error text as JSON and extract `.error` field for user-friendly messages
        3. Add dynamic `aria-label` to the delete `IconButton` that changes based on `user.isCurrentUser`
        4. Simplify `onSettled` callback type annotations in both mutation definitions
        5. In `Layout.tsx`, add admin status query and conditionally render the Admin nav button only when `isAdmin` is true

3. **Phase 3: Test Stubs (AdminEndpointTests.cs)**
    - **Objective:** Add placeholder integration tests for HTTP endpoint behavior
    - **Files/Functions to Modify:**
        - `Prisstyrning.Tests/Api/AdminEndpointTests.cs` — Add Skip'd test stubs
    - **Tests to Write:**
        - `Admin_NoAuth_Returns401` (Skip'd)
        - `Admin_ListUsers_ReturnsAllUsers` (Skip'd)
        - `Admin_NoPasswordConfigured_Returns403` (Skip'd)
    - **Steps:**
        1. Add three `[Fact(Skip = "...")]` test methods with descriptive comments about intended behavior
        2. Run `dotnet test` to verify all tests pass

**Open Questions:** None — all resolved.
