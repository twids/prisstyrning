# Prisstyrning

Price based DHW (domestic hot water) schedule generation for Daikin ONECTA, now extended with a safety-gated thermal orchestrator for Home Assistant, P1P2MQTT and EMHASS.

The existing ONECTA/DHW scheduler remains the default writer after every install and migration. The new subsystem starts in `Legacy`, is disabled by configuration, and cannot send P1P2 or joint DHW commands until its guided readiness gates have been passed.

## Features
* Fetches hourly prices from Nordpool (background job every 6h + manual refresh)
* Generates DHW schedule (comfort / turn_off) with: max 4 actions per day, any turn_off block ≤ 2 hours
* **2-mode system**: Simplified from 3-mode (removed ECO) to eliminate unwanted heating on OFF→ECO transitions (see `MIGRATION.md`)
* **12-hour window scheduling**: Automatically updates schedules twice daily (00:05 and 12:05) to effectively allow up to 8 changes per day by splitting into two 12-hour windows
* **Comfort gap validation**: Configurable maximum hours between comfort periods (default 28h) to ensure regular hot water availability
* **PostgreSQL persistence**: All data (user settings, prices, schedule history, OAuth tokens) stored in PostgreSQL via EF Core
* Manual upload (PUT) of schedule to Daikin gateway (no auto-apply unless explicitly enabled)
* Configuration via `appsettings*.json` and/or environment variables (env has highest precedence; optional `PRISSTYRNING_` prefix)
* **Modern frontend**: React 18 + TypeScript + Material UI with dark theme, real-time updates, and responsive design
* Multi-arch container build (linux/amd64 & linux/arm64) via GitHub Actions
* **Testing**: backend regression tests plus Vitest, React Testing Library, accessibility checks and Playwright critical-flow tests
* See `ROADMAP.md` for planned improvements / technical debt

## Intelligent heating orchestrator

The new orchestration path keeps execution and optimization deliberately separate:

```text
Home Assistant sensors ──> prisstyrning ──> PostgreSQL
                              │   │
                              │   ├──> ONECTA (DHW, only the active writer)
                              │   └──> HA/P1P2MQTT (allowlisted LWT deviation only)
                              │
                              └──> EMHASS (calculation only, private Docker network)
```

`prisstyrning` owns telemetry validation, HA history resampling, weather/COP/grey-box models, five-minute DHW cycle planning, safety rules, writer leases, decisions and command verification. EMHASS only solves the 15-minute/48-hour space-heating problem. It receives all forecasts at runtime, has no valid Home Assistant URL or token and publishes no HA entities. Its documented `opt_res_latest.csv` is read through a read-only shared data volume; `prisstyrning` rejects stale files, missing thermal columns and incomplete horizons before creating its own versioned plan.

The operating modes are intentionally sequential:

| Mode | LWT writer | DHW writer | Purpose |
|---|---|---|---|
| `Legacy` | none; deviation must be zero | existing scheduler | Post-install default and immediate rollback |
| `Shadow` | none | existing scheduler | Collect telemetry and compare plans without writes |
| `LwtActive` | allowlisted P1P2 command | existing scheduler | Limited LWT correction after weather-curve validation |
| `FullActive` | allowlisted P1P2 command | joint planner | Atomic handover after DHW shadow acceptance |

`DhwWriter` is always exactly `Legacy` or `Joint`. A separate expiring database lease surrounds every ONECTA write and prevents handover while a write is in progress. Rollback to `Legacy` returns DHW ownership, stops new control, and writes LWT deviation zero while retaining telemetry and models. The thermal installation is bound to the existing unique legacy auto-apply/ONECTA owner only after an admin-authorized request; active LWT control is limited to one installation.

The UI is Swedish and exposes Overview, Plan, Rooms, Hot water, Model, Events and Settings. A permanent status strip shows the active mode/writer, data quality, plan age, EMHASS state, LWT deviation, fallback and the next action. All thermal and Home Assistant API reads and writes require the application admin role. Browser identities use an encrypted and signed cookie backed by persistent ASP.NET Data Protection keys; raw installation user IDs are not serialized by the thermal API. The old 24-hour schedule view remains available under **Legacy-DHW**.

## Configuration
Precedence (highest first):
1. Environment variables (with or without `PRISSTYRNING_` prefix)
2. `appsettings.development.json`
3. `appsettings.json`

Double underscore `__` maps to nested sections (standard .NET config convention).

### Key settings
| Section | Key | Environment variable | Description |
|---------|-----|----------------------|-------------|
| ConnectionStrings | DefaultConnection | `PRISSTYRNING_ConnectionStrings__DefaultConnection` | PostgreSQL connection string (required) |
| Hangfire | DashboardPassword | `PRISSTYRNING_Hangfire__DashboardPassword` | Password for Hangfire dashboard (Basic Auth). If not set, dashboard is inaccessible. |
| Price:Nordpool | DefaultZone | `PRISSTYRNING_Price__Nordpool__DefaultZone` | Default zone (e.g. SE3) |
| Price:Nordpool | Currency | `PRISSTYRNING_Price__Nordpool__Currency` | Currency (e.g. SEK, EUR) |
| Price:Nordpool | RefreshHours | `PRISSTYRNING_Price__Nordpool__RefreshHours` | Interval hours for background fetch (default 6) |
| Daikin | ClientId | `PRISSTYRNING_Daikin__ClientId` | OAuth client id (required for full OAuth) |
| Daikin | ClientSecret | `PRISSTYRNING_Daikin__ClientSecret` | OAuth client secret (may be empty for public client) |
| Daikin | RedirectUri | `PRISSTYRNING_Daikin__RedirectUri` | Explicit redirect URI (else built from PublicBaseUrl + RedirectPath) |
| Daikin | RedirectPath | `PRISSTYRNING_Daikin__RedirectPath` | Path appended to PublicBaseUrl when RedirectUri not set |
| Daikin | Scope | `PRISSTYRNING_Daikin__Scope` | OAuth scope (default `openid onecta:basic.integration`) |
| Daikin | IncludeOfflineAccess | `PRISSTYRNING_Daikin__IncludeOfflineAccess` | true adds `offline_access` to scope |
| Daikin | AuthEndpoint | `PRISSTYRNING_Daikin__AuthEndpoint` | Override authorize endpoint (rare) |
| Daikin | TokenEndpoint | `PRISSTYRNING_Daikin__TokenEndpoint` | Override token endpoint |
| Daikin | RevokeEndpoint | `PRISSTYRNING_Daikin__RevokeEndpoint` | Override revoke endpoint |
| Daikin | IntrospectEndpoint | `PRISSTYRNING_Daikin__IntrospectEndpoint` | Override introspection endpoint |
| Daikin | TokenFile | `PRISSTYRNING_Daikin__TokenFile` | Persisted token file path (default `tokens/daikin.json`) |
| Daikin | AccessToken | `PRISSTYRNING_Daikin__AccessToken` | (Optional) inject access token (bypasses OAuth refresh) |
| Daikin | RefreshToken | `PRISSTYRNING_Daikin__RefreshToken` | (Optional) inject refresh token |
| Daikin | ApplySchedule | `PRISSTYRNING_Daikin__ApplySchedule` | true/false allow automatic apply (default false in compose) |
| Daikin | SiteId | `PRISSTYRNING_Daikin__SiteId` | Force site id for apply (auto-pick first if empty) |
| Daikin | DeviceId | `PRISSTYRNING_Daikin__DeviceId` | Force device id for apply |
| Daikin | ManagementPointEmbeddedId | `PRISSTYRNING_Daikin__ManagementPointEmbeddedId` | Force embedded id (e.g. 2 for DHW) |
| Daikin | ScheduleMode | `PRISSTYRNING_Daikin__ScheduleMode` | Mode when uploading schedules (heating/cooling/waterHeating etc.) |
| Daikin:Http | Log | `PRISSTYRNING_Daikin__Http__Log` | Log HTTP requests (true/false) |
| Daikin:Http | LogBody | `PRISSTYRNING_Daikin__Http__LogBody` | Include body snippets (true/false) |
| Daikin:Http | BodySnippetLength | `PRISSTYRNING_Daikin__Http__BodySnippetLength` | Max chars of logged body snippet |
| Schedule | ComfortHours | `PRISSTYRNING_Schedule__ComfortHours` | Sequential comfort hours target (default 3) |
| Schedule | TurnOffPercentile | `PRISSTYRNING_Schedule__TurnOffPercentile` | Percentile threshold (e.g. 0.9) for expensive hours |
| Schedule | MaxComfortGapHours | `PRISSTYRNING_Schedule__MaxComfortGapHours` | Max hours between comfort periods (default 28, range 1-72) |
| Schedule | TurnOffSpikeDeltaPct | `PRISSTYRNING_Schedule__TurnOffSpikeDeltaPct` | Min % above neighborhood avg to count as spike |
| Schedule | TurnOffNeighborWindow | `PRISSTYRNING_Schedule__TurnOffNeighborWindow` | Neighborhood half-window size for spike avg |
| Schedule | ComfortNextHourMaxIncreasePct | `PRISSTYRNING_Schedule__ComfortNextHourMaxIncreasePct` | Max % increase allowed for extending comfort block |
| Storage | Directory | `PRISSTYRNING_Storage__Directory` | Directory for persisted price/schedule snapshots |
| Security | DataProtectionKeysPath | `PRISSTYRNING_Security__DataProtectionKeysPath` | Persistent directory for session-signing keys; normally below `/data` |
| Security | AcceptLegacyUserCookie | `PRISSTYRNING_Security__AcceptLegacyUserCookie` | One-time migration of known old unsigned sessions; default `false` and disable immediately after migration |
| Security | SecureCookies | `PRISSTYRNING_Security__SecureCookies` | Forces Secure session cookies; defaults to `true` outside Development |
| Thermal | EnableDhwWriterCoordination | `PRISSTYRNING_Thermal__EnableDhwWriterCoordination` | Enables the new database lease around legacy and joint DHW writes; default `false` so the first Legacy canary uses the unchanged write path |
| Thermal | AllowLwtActive | `PRISSTYRNING_Thermal__AllowLwtActive` | Deployment-level kill switch for LWT writes; default `false` |
| Thermal | AllowFullActive | `PRISSTYRNING_Thermal__AllowFullActive` | Deployment-level kill switch for joint DHW writes; default `false` |
| HomeAssistant:Telemetry | Enabled | `PRISSTYRNING_HomeAssistant__Telemetry__Enabled` | Enables read-only REST/WebSocket telemetry; default `false` |
| HomeAssistant:Telemetry | BaseUrl | `PRISSTYRNING_HomeAssistant__Telemetry__BaseUrl` | Internal Home Assistant base URL |
| HomeAssistant:Telemetry | TokenFile | `PRISSTYRNING_HomeAssistant__Telemetry__TokenFile` | Path to the read-only Docker secret |
| HomeAssistant:Control | BaseUrl | `PRISSTYRNING_HomeAssistant__Control__BaseUrl` | HA URL used only by the active control client |
| HomeAssistant:Control | TokenFile | `PRISSTYRNING_HomeAssistant__Control__TokenFile` | Path to the separate control Docker secret |
| HomeAssistant:Control | HeatingDeviationEntityId | `PRISSTYRNING_HomeAssistant__Control__HeatingDeviationEntityId` | Exact allowlisted `number.*` P1P2 deviation entity |
| Emhass | Enabled | `PRISSTYRNING_Emhass__Enabled` | Enables shadow optimization; default `false` |
| Emhass | BaseUrl | `PRISSTYRNING_Emhass__BaseUrl` | Internal endpoint, normally `http://emhass:5000` |
| Emhass | ResultPath | `PRISSTYRNING_Emhass__ResultPath` | Read-only path to EMHASS `opt_res_latest.csv`, normally `/emhass-data/opt_res_latest.csv` |
| Emhass | SolverTimeoutSeconds | `PRISSTYRNING_Emhass__SolverTimeoutSeconds` | Hard timeout, capped at 45 seconds |
| Emhass | OptimizationTimeStepMinutes | `PRISSTYRNING_Emhass__OptimizationTimeStepMinutes` | Must be 15 for this integration |
| Emhass | HorizonHours | `PRISSTYRNING_Emhass__HorizonHours` | Planning horizon, normally 48 hours |
| Root | PublicBaseUrl | `PRISSTYRNING_PublicBaseUrl` | Base URL used to auto-build redirect (if RedirectUri missing) |
| Root | PORT | `PRISSTYRNING_PORT` | ASP.NET listening port (defaults 5000) |

## Run locally (Docker)
Build image:
```bash
docker build -t prisstyrning:local .
```

Run container (requires a running PostgreSQL instance):
```bash
docker run --rm -p 5000:5000 \
  -e PRISSTYRNING_ConnectionStrings__DefaultConnection="Host=localhost;Database=prisstyrning;Username=prisstyrning;Password=prisstyrning" \
  -e PRISSTYRNING_Price__Nordpool__DefaultZone=SE3 \
  -e PRISSTYRNING_Price__Nordpool__Currency=SEK \
  prisstyrning:local
```

## docker-compose example
Full example (see `docker-compose.example.yml` for latest & comments):
```yaml
version: '3.9'
services:
  postgres:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: prisstyrning
      POSTGRES_USER: prisstyrning
      POSTGRES_PASSWORD: prisstyrning
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U prisstyrning"]
      interval: 5s
      timeout: 5s
      retries: 5

  prisstyrning:
    image: ghcr.io/twids/prisstyrning:latest
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      PRISSTYRNING_ConnectionStrings__DefaultConnection: "Host=postgres;Database=prisstyrning;Username=prisstyrning;Password=prisstyrning"
      PRISSTYRNING_Hangfire__DashboardPassword: CHANGE_ME
      PRISSTYRNING_Price__Nordpool__DefaultZone: SE3
      PRISSTYRNING_Price__Nordpool__Currency: SEK
      PRISSTYRNING_Daikin__ClientId: CHANGE_ME
      PRISSTYRNING_Daikin__ClientSecret: CHANGE_ME
      PRISSTYRNING_Daikin__RedirectUri: https://example.com/auth/daikin/callback
      PRISSTYRNING_Daikin__ApplySchedule: "false"
      PRISSTYRNING_Schedule__ComfortHours: "3"
      PRISSTYRNING_Schedule__TurnOffPercentile: "0.9"
      PRISSTYRNING_PORT: "5000"
    ports:
      - "5000:5000"

volumes:
  pgdata:
```
Start:
```bash
docker compose -f docker-compose.example.yml up -d
```

## Safe thermal deployment

[`docker-compose.thermal.example.yml`](docker-compose.thermal.example.yml) is the reference topology for Unraid/Dockhand. It adds an internal-only EMHASS service to the application and PostgreSQL. EMHASS `v0.18.0` is locked to multi-arch digest `sha256:b9c88442c2623c83469cb6ae103991a349cc63fbd5c8fd100d5e071e6ff41204`; its port is not published to the LAN. `/share` is a persistent bind mount and `/data` is a persistent named volume. The same data volume is mounted read-only at `/emhass-data` in `prisstyrning`, solely to consume the documented optimization CSV.

Before importing the stack:

1. Copy every currently effective ONECTA, Nord Pool, schedule, OAuth, routing and database value from the running stack. In particular, do not accidentally change `Daikin__ApplySchedule`; the legacy DHW job must keep behaving exactly as it does today.
2. For the first Legacy canary, leave HA telemetry and EMHASS integration disabled and omit the HA secrets from the production stack. Before Telemetry Shadow, create a read-only HA identity. Create the separate control identity only before the later LWT stage; it is reserved for the exact P1P2 `Deviation_Heating` entity.
3. Set `HOME_ASSISTANT_URL` to an internally reachable URL and leave `P1P2_DEVIATION_ENTITY_ID` empty until the real `number.*` entity has been verified.
4. Validate the rendered Dockhand/Compose configuration and confirm that only `prisstyrning` is attached to both networks. EMHASS must have no published port and no HA secret.
5. Persist Data Protection keys below the existing `/data` mount. If the current browser identity must be retained, set `AcceptLegacyUserCookie=true` only for the migration window, load the application once, verify that a signed `v1.*` cookie was issued, and then set it back to `false`.
6. Deploy with the new settings but leave the database mode at `Legacy`, `EnableDhwWriterCoordination=false`, `AllowLwtActive=false` and `AllowFullActive=false`. This keeps the existing scheduled ONECTA write path unchanged during the first canary. Migrations create the new thermal schema and only extend their own new tables; no legacy table is renamed, removed or reinterpreted.

`GET /health/live` checks the web process. `GET /health/ready` additionally checks PostgreSQL connectivity and that no EF migration remains pending. HA and EMHASS are deliberately excluded from Legacy readiness.

For forecast-aware planning, map either a weather/template entity whose `attributes.forecast` contains at least `datetime` and `temperature`, or provide the separate current wind/solar entities. Optional forecast fields are `wind_speed`, `solar_irradiance` or `solar_radiation`; temperatures and wind are normalized to °C and m/s. The Settings page can import up to 90 days of HA history, resample it to five minutes and preserve every existing live snapshot.

Then use the guided UI stages rather than editing the mode directly:

1. Configure entity mappings and verify live values/units while still in `Legacy`.
2. Enter `Shadow` and collect at least 21 days, including ten real heating days and the required DHW comparison cycles.
3. Perform the seven-heating-day weather-curve test with deviation zero and legacy DHW still active.
4. Enable `LwtActive` at ±1 °C; expand to ±3 °C only after another seven problem-free heating days.
5. Move to `FullActive` only when every readiness check is green. The handover changes `DhwWriter` atomically; it never deletes the legacy job.

At any point in an active mode, use the permanently visible **Rollback** action. It sets `DhwWriter=Legacy`, stops joint writes and requests zero LWT deviation. If the new services are unavailable while the database is still in `Legacy`, the existing DHW path continues independently.

The EMHASS image requires placeholder HA fields during initialization, but [`emhass/secrets_emhass.stateless.yaml`](emhass/secrets_emhass.stateless.yaml) intentionally contains no valid HA address or credential. Do not replace it with a real token. `prisstyrning` supplies prices, load, outdoor temperature, comfort bounds and DHW reservation in each request, then validates the freshly written `opt_res_latest.csv`. It never calls `publish-data`.

## GitHub Container Registry
Workflow (`.github/workflows/container.yml`) builds multi-arch and pushes manifest to:
```
ghcr.io/<owner>/prisstyrning
```
On `master` pushes and version tags (`v*.*.*`).

## Multi-arch notes
The GitHub Actions pipeline enables `linux/amd64` and `linux/arm64` with QEMU emulation. If you only need one architecture, drop the `platforms:` line for faster builds.

## Build Verification
All pull requests are automatically verified with GitHub Actions (`.github/workflows/pr-build-verification.yml`):
* **Backend**: Restores NuGet packages, builds in Release configuration and runs the full regression suite
* **Frontend**: Installs npm dependencies, builds React app with TypeScript and Vite
* **Artifact Check**: Verifies build produces expected output (`Prisstyrning.dll` and `wwwroot/` assets)

Pull requests cannot be merged until the build verification passes. This ensures code quality and prevents broken builds from entering the main branch.

## OAuth tokens
After completing OAuth, tokens are persisted to the PostgreSQL database. You may also inject `Daikin:AccessToken` / `Daikin:RefreshToken` via environment variables for testing.

## Development

### Build Instructions

**Backend:**
```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

**Frontend:**
```bash
cd frontend
npm install
npm run dev  # Development server on http://localhost:5173
npm run build  # Production build to ../wwwroot
```

**Run Application:**
```bash
# Terminal 1: Backend (serves API + static frontend from wwwroot)
dotnet run --configuration Release

# Terminal 2 (optional): Frontend dev server with HMR
cd frontend && npm run dev
```

### API Endpoints
* Schedule preview: `/api/schedule/preview`
* Current DHW schedule: `/api/daikin/gateway/schedule?embeddedId=2`
* User settings: `/api/user/settings`
* Schedule history: `/api/user/schedule-history`
* Price timeseries: `/api/prices/timeseries`
* Status: `/api/status`
* Thermal config/status/readiness/plan/history/events: `/api/thermal/*`
* Guided mode and manual override: `POST /api/thermal/mode`, `POST/DELETE /api/thermal/override`
* Home Assistant status/test/entity discovery: `/api/home-assistant/*`
* Bounded HA training-history import: `POST /api/home-assistant/import-history`

### Data Storage
All application data is stored in PostgreSQL:
* User settings (comfort hours, zone, percentiles)
* Nordpool price snapshots (per zone and date)
* Schedule history (per user)
* OAuth tokens (Daikin access/refresh tokens)
* Five-minute thermal telemetry (400 days) and indefinite hourly aggregates
* Versioned grey-box/COP model metadata, joint plans, DHW cycles and target verification
* Writer leases, control commands, fallback events and the decision audit trail

EF Core migrations are applied automatically on startup.

### Testing
* Backend: `dotnet test --verbosity normal`
* Frontend unit/accessibility: `cd frontend && npm test`
* Frontend production build: `cd frontend && npm run build`
* Critical browser flows: `cd frontend && npm run test:e2e`
* Dependency audit: `cd frontend && npm audit`
* See `Prisstyrning.Tests/README.md` for testing strategy

### Migration from v1.x
See `MIGRATION.md` for detailed upgrade instructions, including ECO mode removal and configuration changes.

## License
No license specified yet (all rights reserved by default). Add a LICENSE file before broader distribution.
