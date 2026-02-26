## Plan: Security Hardening (Findings 2–10)

Fixes gitignore casing, constant-time password comparison, rate limiting on admin login, HTTP security headers, generic error messages, debug endpoint restrictions, Swagger dev-only gate, CORS policy, and OAuth state TTL.

**Phases (5)**

1. **Phase 1: Low-risk config fixes**
    - **Objective:** Fix `.gitignore` casing and remove Swagger `|| true`
    - **Files/Functions to Modify/Create:** `.gitignore`, `Program.cs` (Swagger conditional)
    - **Tests to Write:** Build verification only
    - **Steps:**
        1. Add `appsettings.development.json` (lowercase) to `.gitignore`
        2. Remove `|| true` from Swagger conditional so it's dev-only

2. **Phase 2: Constant-time password comparison**
    - **Objective:** Replace all `==`/`!=` string comparisons for passwords with `CryptographicOperations.FixedTimeEquals()`
    - **Files/Functions to Modify/Create:** `AdminService.cs` (CheckAdminAccess), `Program.cs` (admin login, HangfirePasswordAuthorizationFilter)
    - **Tests to Write:** AdminServiceTests for constant-time comparison
    - **Steps:**
        1. Add a `SecureCompare` helper to `AdminService`
        2. Replace password comparison in `CheckAdminAccess()`
        3. Replace password comparison in admin login endpoint
        4. Replace password comparison in Hangfire auth filter

3. **Phase 3: Security headers & CORS**
    - **Objective:** Add HTTP security headers middleware and restrictive same-origin CORS policy
    - **Files/Functions to Modify/Create:** `Program.cs` (middleware pipeline)
    - **Tests to Write:** Build verification
    - **Steps:**
        1. Add security headers middleware (X-Frame-Options, X-Content-Type-Options, Referrer-Policy, Content-Security-Policy)
        2. No separate CORS middleware needed since it's a single-origin app — headers alone suffice

4. **Phase 4: Generic error messages & debug endpoint restriction**
    - **Objective:** Stop returning `ex.Message` to clients; restrict debug endpoints behind admin auth
    - **Files/Functions to Modify/Create:** `Program.cs` (~8 catch blocks, debug endpoints, start-min endpoint)
    - **Tests to Write:** Build verification
    - **Steps:**
        1. Replace all `ex.Message` in error responses with generic messages, log exceptions server-side
        2. Add admin auth check to `/api/prices/_debug/*` and `/auth/daikin/debug`
        3. Remove `/auth/daikin/start-min` endpoint

5. **Phase 5: Rate limiting on admin login & OAuth state TTL**
    - **Objective:** Add rate limiting on admin login; add TTL-based eviction to OAuth state dictionary
    - **Files/Functions to Modify/Create:** `Program.cs`, `DaikinOAuthService.cs`, `Prisstyrning.csproj`
    - **Tests to Write:** Build verification
    - **Steps:**
        1. Add rate limiting middleware with fixed window (5 req/min) on `/api/admin/login`
        2. Replace `Dictionary<string,string>` in `DaikinOAuthService._stateToVerifier` with `ConcurrentDictionary` + timestamp eviction (10-min TTL)

**Open Questions**
1. None — all resolved.
