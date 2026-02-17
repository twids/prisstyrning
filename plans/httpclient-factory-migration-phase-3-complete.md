## Phase 3 Complete: Convert BatchRunner to Instance Class

Converted BatchRunner from static class to instance-based service with IHttpClientFactory constructor injection. All 4 static methods converted to instance methods, and both HttpClient creation points now use factory pattern. Reduced compilation errors from 24 to 20.

**Files created/changed:**
- BatchRunner.cs
- Program.cs
- Jobs/ScheduleUpdateHangfireJob.cs
- Jobs/InitialBatchHangfireJob.cs
- Jobs/DailyPriceHangfireJob.cs

**Functions created/changed:**
- BatchRunner constructor (added IHttpClientFactory parameter)
- BatchRunner.GenerateSchedulePreview (converted to instance method)
- BatchRunner.RunBatchAsync (converted to instance method)
- BatchRunner.RunBatchInternalAsync (converted to instance method)
- BatchRunner.SaveHistoryAsync (converted to instance method)
- ScheduleUpdateHangfireJob constructor (added BatchRunner parameter)
- InitialBatchHangfireJob constructor (added BatchRunner parameter)
- DailyPriceHangfireJob constructor (added BatchRunner parameter)
- Program.cs: Two route handlers updated to inject BatchRunner
- Program.cs: HandleApplyScheduleAsync signature updated

**Tests created/changed:**
- N/A (no test changes in Phase 3)

**Review Status:** APPROVED

**Git Commit Message:**
```
refactor: Convert BatchRunner to instance class with IHttpClientFactory

- Changed BatchRunner from static class to public instance class
- Added IHttpClientFactory constructor injection
- Converted all 4 static methods to instance methods
- Updated NordpoolClient instantiation to use factory.CreateClient("Nordpool")
- Updated DaikinApiClient instantiation to use factory.CreateClient("Daikin")
- Registered BatchRunner as singleton service in Program.cs
- Updated 2 Program.cs route handlers to inject BatchRunner
- Updated 3 Hangfire jobs to inject BatchRunner in constructors
- Reduced compilation errors from 24 to 20 (remaining in Phase 4-6)
```
