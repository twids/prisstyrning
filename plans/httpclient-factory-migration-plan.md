## Plan: Migrate to IHttpClientFactory

Refactor entire codebase from manual HttpClient instantiation to IHttpClientFactory to prevent socket exhaustion, enable resilience policies, and follow .NET best practices.

**Phases: 7**

---

## Phase 1: Register IHttpClientFactory and Configure Named Clients

**Objective:** Add IHttpClientFactory to DI container with pre-configured named clients for each HTTP service endpoint.

**Files/Functions to Modify/Create:**
- [Program.cs](Program.cs) - `builder.Services.AddHttpClient()` registrations (after line 40)

**Tests to Write:**
- None for this phase (configuration-only)

**Steps:**
1. Add `builder.Services.AddHttpClient("Nordpool")` with default headers (UserAgent, Accept, AcceptLanguage)
2. Configure `HttpClientHandler` for Nordpool client with `AutomaticDecompression`
3. Add `builder.Services.AddHttpClient("Daikin")` with UserAgent header
4. Add `builder.Services.AddHttpClient("HomeAssistant")` for Home Assistant integration
5. Add `builder.Services.AddHttpClient("Entsoe")` for ENTSO-E API client
6. Build and verify no compilation errors

---

## Phase 2: Refactor Client Constructors to Require HttpClient

**Objective:** Remove `new HttpClient()` fallback logic and require HttpClient injection, while maintaining test compatibility.

**Files/Functions to Modify/Create:**
- [NordpoolClient.cs](NordpoolClient.cs) - Constructor signature (line 12-25)
- [DaikinApiClient.cs](DaikinApiClient.cs) - Constructor signature (line 18-29)
- [HomeAssistantClient.cs](HomeAssistantClient.cs) - Constructor signature (line 13-22)
- [EntsoeClient.cs](EntsoeClient.cs) - Constructor signature (line 12-17)

**Tests to Write:**
- Update all existing integration tests to continue passing `new HttpClient(mockHandler)`

**Steps:**
1. **NordpoolClient**: Change constructor to require `HttpClient httpClient` (non-nullable), remove `new HttpClient()` fallback, remove header configuration (will be done in DI)
2. **DaikinApiClient**: Change constructor to require `HttpClient httpClient`, remove `new HttpClient()` fallback
3. **HomeAssistantClient**: Change constructor to require `HttpClient httpClient`, remove `new HttpClient()` fallback
4. **EntsoeClient**: Change constructor to require `HttpClient httpClient`, remove `new HttpClient()` fallback
5. Run tests to verify breaking changes are isolated to production code (tests still pass with explicit injection)

---

## Phase 3: Convert BatchRunner from Static to Instance-Based Service

**Objective:** Refactor `BatchRunner` to accept IHttpClientFactory via constructor to enable DI.

**Files/Functions to Modify/Create:**
- [BatchRunner.cs](BatchRunner.cs) - Convert from static class to instance class with constructor
- Add private field `_httpClientFactory`
- Update all static methods to instance methods

**Tests to Write:**
- Update [BatchRunnerIntegrationTests.cs] to instantiate `BatchRunner` with mock factory
- Verify existing 8 BatchRunner tests still pass

**Steps:**
1. Change `internal static class BatchRunner` to `internal class BatchRunner`
2. Add constructor: `public BatchRunner(IHttpClientFactory httpClientFactory)`
3. Store `_httpClientFactory` in private readonly field
4. Convert all static methods to instance methods
5. Update method bodies to use `_httpClientFactory.CreateClient("Nordpool")` and `_httpClientFactory.CreateClient("Daikin")`
6. Run tests with mock IHttpClientFactory that returns test-configured HttpClients

---

## Phase 4: Convert DaikinOAuthService from Static to Instance-Based Service

**Objective:** Refactor `DaikinOAuthService` to accept IHttpClientFactory via constructor.

**Files/Functions to Modify/Create:**
- [DaikinOAuthService.cs](DaikinOAuthService.cs) - Convert from static class to instance class
- Update methods: `HandleCallbackAsync`, `RefreshIfNeededAsync`, `RevokeAsync`, `IntrospectAsync`

**Tests to Write:**
- Update [DaikinOAuthServiceIntegrationTests.cs] (5 test methods) to use instance-based service
- Verify all OAuth flow tests still pass

**Steps:**
1. Change `internal static class DaikinOAuthService` to `internal class DaikinOAuthService`
2. Add constructor: `public DaikinOAuthService(IHttpClientFactory httpClientFactory)`
3. Remove `HttpClient? httpClient = null` parameters from all methods
4. Update method bodies to use `_httpClientFactory.CreateClient("Daikin")`
5. Keep `BuildAuthorizeUrl` static (pure function, no HTTP needed)
6. Run tests with injected mock factory

---

## Phase 5: Register Services in DI and Update Hangfire Jobs

**Objective:** Register all HTTP client services in DI container and update Hangfire jobs to inject them.

**Files/Functions to Modify/Create:**
- [Program.cs](Program.cs) - Register services (after line 52)
  - `builder.Services.AddSingleton<BatchRunner>()`
  - `builder.Services.AddSingleton<DaikinOAuthService>()`
- [Jobs/NordpoolPriceHangfireJob.cs](Jobs/NordpoolPriceHangfireJob.cs) - Add constructor injection
- [Jobs/DaikinTokenRefreshHangfireJob.cs](Jobs/DaikinTokenRefreshHangfireJob.cs) - Add constructor injection
- [Jobs/ScheduleUpdateHangfireJob.cs](Jobs/ScheduleUpdateHangfireJob.cs) - Add constructor injection
- [Jobs/InitialBatchHangfireJob.cs](Jobs/InitialBatchHangfireJob.cs) - Add constructor injection

**Tests to Write:**
- Update [NordpoolPriceJobTests.cs], [ScheduleUpdateJobTests.cs] to inject services
- Verify all 7 Hangfire job tests still pass

**Steps:**
1. Register `BatchRunner` and `DaikinOAuthService` as singletons in Program.cs
2. Update `NordpoolPriceHangfireJob` constructor to accept `IHttpClientFactory` and `IConfiguration`
3. Update `DaikinTokenRefreshHangfireJob` constructor to accept `IHttpClientFactory` and `DaikinOAuthService`
4. Update `ScheduleUpdateHangfireJob` constructor to accept `BatchRunner` and `DaikinOAuthService`
5. Update `InitialBatchHangfireJob` constructor to accept `BatchRunner`
6. Update test setup to create services with mock factories
7. Run all Hangfire job tests

---

## Phase 6: Refactor Program.cs API Endpoints to Use DI

**Objective:** Remove manual `new NordpoolClient()` and `new DaikinApiClient()` calls in Minimal API endpoints, inject from DI.

**Files/Functions to Modify/Create:**
- [Program.cs](Program.cs) - Update all API route handlers (lines 260-750)
  - `/preview/prices/{zone}` endpoint
  - `/api/prices/timeseries` endpoint
  - `/api/daikin/devices` endpoint
  - `/api/daikin/apply-schedule` endpoint
  - `/api/user/daikin/devices` endpoint

**Tests to Write:**
- Update [EndpointIntegrationTests.cs] (4 endpoint tests) to work with DI-based approach
- Verify all API integration tests pass

**Steps:**
1. Add `IHttpClientFactory httpFactory` parameter to all route handlers that create clients
2. Replace `new NordpoolClient(...)` with `httpFactory.CreateClient("Nordpool")` + constructor call
3. Replace `new DaikinApiClient(...)` with `httpFactory.CreateClient("Daikin")` + constructor call
4. Update HomeAssistant endpoints to inject `httpFactory.CreateClient("HomeAssistant")`
5. Verify all API endpoints respond correctly via integration tests

---

## Phase 7: Final Cleanup and Validation

**Objective:** Remove obsolete code, run full test suite, verify production build.

**Files/Functions to Modify/Create:**
- Remove any unused `HttpClient? httpClient = null` parameters that are no longer needed
- Update README.md if needed to document new DI architecture

**Tests to Write:**
- None (full test suite run)

**Steps:**
1. Search codebase for any remaining `new HttpClient()` patterns and remove
2. Run `dotnet test --configuration Release` - verify all 125 tests pass
3. Run `dotnet build --configuration Release` - verify no warnings
4. Verify Docker build still works (if network-dependent NuGet issues allow)
5. Review ROADMAP.md and remove "TODO: Use IHttpClientFactory" if present

---

## Open Questions

1. **Should we use Typed Clients?** Instead of `httpFactory.CreateClient("Nordpool")` + manual constructor, we could use `builder.Services.AddHttpClient<NordpoolClient>()` and inject clients directly. This is cleaner but requires more DI container changes.

2. **Resilience policies?** Should we add Polly retry policies while refactoring (e.g., transient fault handling for Nordpool API)?

3. **Test strategy?** Current tests inject `new HttpClient(mockHandler)` - should we change to mock `IHttpClientFactory` instead for better isolation?
