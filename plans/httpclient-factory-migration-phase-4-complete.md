## Phase 4 Complete: Convert DaikinOAuthService to Instance Class

Converted DaikinOAuthService from static class to instance-based service with IHttpClientFactory constructor injection. Four HTTP methods converted to instance methods without HttpClient parameters. All call sites updated to inject DaikinOAuthService. Compilation errors remain at 20 (all related to DaikinApiClient/NordpoolClient instantiation in Phase 6).

**Files created/changed:**
- DaikinOAuthService.cs
- Program.cs
- BatchRunner.cs
- Jobs/DaikinTokenRefreshHangfireJob.cs

**Functions created/changed:**
- DaikinOAuthService constructor (added IHttpClientFactory parameter)
- DaikinOAuthService.HandleCallbackAsync (converted to instance, removed httpClient parameter)
- DaikinOAuthService.RefreshIfNeededAsync (converted to instance, removed httpClient parameter)
- DaikinOAuthService.RevokeAsync (converted to instance, removed httpClient parameter)
- DaikinOAuthService.IntrospectAsync (converted to instance, removed httpClient parameter)
- BatchRunner constructor (added DaikinOAuthService parameter)
- DaikinTokenRefreshHangfireJob constructor (added DaikinOAuthService parameter)
- Program.cs: 9 /auth/daikin endpoints updated to inject DaikinOAuthService
- Program.cs: 4 /api/daikin endpoints updated to inject DaikinOAuthService

**Tests created/changed:**
- N/A (test updates deferred to Phase 7)

**Review Status:** APPROVED

**Git Commit Message:**
```
refactor: Convert DaikinOAuthService to instance class with IHttpClientFactory

- Changed DaikinOAuthService from static class to public instance class
- Added IHttpClientFactory constructor injection
- Converted 4 HTTP methods to instance methods without httpClient parameters
- Updated all 4 methods to use factory.CreateClient("Daikin")
- Removed httpClient fallback logic and Dispose calls (factory manages lifecycle)
- Registered DaikinOAuthService as singleton service in Program.cs
- Updated BatchRunner to inject DaikinOAuthService
- Updated DaikinTokenRefreshHangfireJob to inject DaikinOAuthService
- Updated 13 Program.cs endpoints to inject DaikinOAuthService
- Maintained 20 compilation errors (will be resolved in Phase 6)
```
