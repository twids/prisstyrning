# Prisstyrning

Prisstyrning is a .NET 8 ASP.NET Core web application for price-based DHW (domestic hot water) schedule generation using Daikin ONECTA and Nordpool day-ahead electricity prices.

Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.

## Working Effectively

### Bootstrap, Build, and Test - NEVER CANCEL Commands
- `dotnet restore` - takes 15 seconds. NEVER CANCEL. Set timeout to 60+ seconds.
- `dotnet build --configuration Release --no-restore` - takes 10 seconds. NEVER CANCEL. Set timeout to 60+ seconds.
- `dotnet test --configuration Release --no-build --verbosity normal` - takes 9 seconds, runs 13 tests. NEVER CANCEL. Set timeout to 60+ seconds.

### Run the Application
- ALWAYS run the build steps first before running the application.
- Run: `dotnet run --configuration Release`
- Application starts in ~1 second and listens on port 5000 (http://localhost:5000)
- Logs show: `[NordpoolPriceJob] started`, `[DaikinRefreshService] Started`, `Now listening on: http://[::]:5000`

### Docker Build (Network Dependent)
- Build image: `docker build -t prisstyrning:local .` - FAILS in sandboxed environments due to NuGet connectivity restrictions
- Run container: `docker run --rm -p 5000:5000 -e PRISSTYRNING_Storage__Directory=/data -v $(pwd)/data:/data prisstyrning:local`
- Document Docker builds as network-dependent and may fail in restricted environments

## Validation Scenarios

ALWAYS manually validate changes by running through these complete end-to-end scenarios:

### Core Application Scenarios
1. **Application Startup Test**: Start the application and verify it's listening on port 5000 with no errors
2. **Web Interface Test**: Navigate to `http://localhost:5000/` and verify the Prisstyrning web interface loads correctly
3. **API Functionality Test**: Test key endpoints:
   - `GET /api/schedule/preview` - Should return JSON with schedule data
   - `GET /api/user/settings` - Should return user configuration (comfortHours, turnOffPercentile, etc.)
   - `GET /api/prices/timeseries` - Should return 48 price data points for today and tomorrow
   - `GET /api/status` - Should return status: "ok" with timestamp

### Testing with Real Data
- The application automatically fetches real Nordpool pricing data for SE3 zone on startup
- Price data is cached in `data/nordpool/SE3/prices-*.json` files
- Test schedule generation by clicking "Generate Schedule" in the web interface
- Verify the schedule grid displays comfort/eco/off periods for today and tomorrow

### Manual UI Validation
- ALWAYS test the web interface after making changes to ensure it loads and displays data correctly
- Verify the price chart renders with today/tomorrow electricity prices
- Test the settings page at `/settings.html` if UI changes are made
- Check that schedule generation produces a valid 24x2 hour grid with comfort/eco/off states

## Common Tasks

### Repository Structure
```
Prisstyrning/
├── README.md                   # Main documentation
├── ROADMAP.md                  # Technical debt and future plans
├── Program.cs                  # Main application entry point and API endpoints
├── Prisstyrning.csproj         # .NET 8 project file
├── Prisstyrning.sln            # Solution file
├── ScheduleAlgorithm.cs        # Core schedule generation logic
├── Dockerfile                  # Multi-stage container build
├── docker-compose.example.yml  # Example Docker Compose configuration
├── wwwroot/                    # Static web assets (HTML, CSS, JS)
├── Controllers/                # ASP.NET Core controllers
├── Prisstyrning.Tests/         # Unit test project
└── .github/workflows/          # CI/CD pipelines
```

### Key Configuration Files
- `appsettings.json` - Default application configuration
- `appsettings.development.json` - Development overrides (optional)
- Environment variables with `PRISSTYRNING_` prefix override all settings

### Main API Endpoints
- `/` - Main web interface
- `/api/schedule/preview` - Generate DHW schedule preview
- `/api/user/settings` - User configuration (comfort hours, percentiles)
- `/api/user/schedule-history` - Historical schedule data
- `/api/prices/timeseries` - Nordpool price data (today + tomorrow)
- `/api/status` - Application health check
- `/api/daikin/*` - Daikin device integration endpoints

### Build Pipeline Validation
- All PRs automatically run: restore → build → test → artifact verification
- ALWAYS run `dotnet restore && dotnet build --configuration Release` before submitting PRs
- The CI build produces `Prisstyrning.dll` in `bin/Release/net8.0/`

### Configuration Examples
Set environment variables for testing:
```bash
export PRISSTYRNING_Price__Nordpool__DefaultZone=SE3
export PRISSTYRNING_Price__Nordpool__Currency=SEK
export PRISSTYRNING_Storage__Directory=./data
export PRISSTYRNING_PORT=5000
```

## Key Source Files

### Core Business Logic
- `ScheduleAlgorithm.cs` - Main schedule generation algorithm with two logic types: PerDayOriginal and CrossDayCheapestLimited
- `BatchRunner.cs` - Batch price fetching and schedule generation
- `NordpoolClient.cs` - Nordpool API integration for electricity prices
- `DaikinApiClient.cs` - Daikin ONECTA API integration

### Important Implementation Notes
- Schedule generation uses comfort hours, turn-off percentile, and activation limits
- Price data is fetched every 6 hours and cached locally
- DHW schedules support comfort/eco/turn_off states with max 4 actions per day
- All JSON responses are well-structured for frontend consumption

### Testing Guidelines
- The test suite in `Prisstyrning.Tests/` covers ScheduleAlgorithm edge cases
- Run `dotnet test --filter "FullyQualifiedName~TestMethodName"` for specific tests
- Tests validate null handling, invalid data, and boundary conditions
- ALWAYS add tests for new schedule algorithm features

### Debugging and Development
- Console logging shows price fetching, schedule generation, and Daikin API calls
- Local price snapshots stored in `data/nordpool/<ZONE>/prices-*.json`
- Frontend served from `wwwroot` with minimal JavaScript
- Schedule preview available at `/api/schedule/preview` for debugging

## Docker and Deployment

### Container Registry
- Multi-arch images built for linux/amd64 and linux/arm64
- Published to `ghcr.io/twids/prisstyrning` on master pushes and version tags
- Uses multi-stage Dockerfile with .NET 8 SDK and ASP.NET runtime

### Environment Variables
Key settings (see README.md for complete list):
- `PRISSTYRNING_Daikin__ClientId` / `PRISSTYRNING_Daikin__ClientSecret` - OAuth credentials
- `PRISSTYRNING_Schedule__ComfortHours` - Number of comfort hours (default: 3)
- `PRISSTYRNING_Schedule__TurnOffPercentile` - Price threshold for turn-off (default: 0.9)
- `PRISSTYRNING_Storage__Directory` - Data persistence directory

### Volume Mounts
- Mount `./data:/data` to persist price data and OAuth tokens
- Tokens stored in `tokens/daikin.json` within the mounted volume

## Troubleshooting

### Common Issues
- **Build failures**: Ensure .NET 8 SDK is installed
- **Test failures**: Check ScheduleAlgorithmTests for logic regressions
- **Docker build failures**: Network restrictions prevent NuGet package downloads
- **API errors**: Verify application is running and listening on port 5000
- **Price data issues**: Check `data/nordpool/` directory for cached files

### Performance Expectations
- Application startup: ~1 second
- Price data fetch: ~2-5 seconds for full day
- Schedule generation: <100ms for typical scenarios
- Web interface load: <1 second with cached price data

Always prioritize testing the actual user workflows rather than just checking that commands exit successfully.