# Prisstyrning

Price based DHW (domestic hot water) schedule generation for Daikin ONECTA using Nordpool day-ahead prices (per-user zone selectable).

## Features
* Fetches hourly prices from Nordpool (background job every 6h + manual refresh)
* Generates DHW schedule (comfort / eco / turn_off) with: max 4 actions per day, any turn_off block ≤ 2 hours
* **12-hour window scheduling**: Automatically updates schedules twice daily (00:05 and 12:05) to effectively allow up to 8 changes per day by splitting into two 12-hour windows
* **Comfort gap validation**: Configurable maximum hours between comfort periods (default 28h) to ensure regular hot water availability
* Manual upload (PUT) of schedule to Daikin gateway (no auto-apply unless explicitly enabled)
* Configuration via `appsettings*.json` and/or environment variables (env has highest precedence; optional `PRISSTYRNING_` prefix)
* Frontend: price chart + schedule grid + current DHW schedule visualization
* Multi-arch container build (linux/amd64 & linux/arm64) via GitHub Actions
* See `ROADMAP.md` for planned improvements / technical debt

## Configuration
Precedence (highest first):
1. Environment variables (with or without `PRISSTYRNING_` prefix)
2. `appsettings.development.json`
3. `appsettings.json`

Double underscore `__` maps to nested sections (standard .NET config convention).

### Key settings
| Section | Key | Environment variable | Description |
|---------|-----|----------------------|-------------|
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
| Schedule | TurnOffMaxConsecutive | `PRISSTYRNING_Schedule__TurnOffMaxConsecutive` | Max consecutive expensive hours pre-trim (<=6) |
| Schedule | MaxComfortGapHours | `PRISSTYRNING_Schedule__MaxComfortGapHours` | Max hours between comfort periods (default 28, range 1-72) |
| Schedule | TurnOffSpikeDeltaPct | `PRISSTYRNING_Schedule__TurnOffSpikeDeltaPct` | Min % above neighborhood avg to count as spike |
| Schedule | TurnOffNeighborWindow | `PRISSTYRNING_Schedule__TurnOffNeighborWindow` | Neighborhood half-window size for spike avg |
| Schedule | ComfortNextHourMaxIncreasePct | `PRISSTYRNING_Schedule__ComfortNextHourMaxIncreasePct` | Max % increase allowed for extending comfort block |
| Storage | Directory | `PRISSTYRNING_Storage__Directory` | Directory for persisted price/schedule snapshots |
| Root | PublicBaseUrl | `PRISSTYRNING_PublicBaseUrl` | Base URL used to auto-build redirect (if RedirectUri missing) |
| Root | PORT | `PRISSTYRNING_PORT` | ASP.NET listening port (defaults 5000) |

## Run locally (Docker)
Build image:
```bash
docker build -t prisstyrning:local .
```

Run container:
```bash
docker run --rm -p 5000:5000 \
  -e PRISSTYRNING_Price__Nordpool__DefaultZone=SE3 \
  -e PRISSTYRNING_Price__Nordpool__Currency=SEK \
  -e PRISSTYRNING_Storage__Directory=/data \
  -v $(pwd)/data:/data \
  prisstyrning:local
```

## docker-compose example
Full example (see `docker-compose.example.yml` for latest & comments):
```yaml
version: '3.9'
services:
  prisstyrning:
    image: ghcr.io/twids/prisstyrning:latest
    restart: unless-stopped
    environment:
      PRISSTYRNING_Hangfire__DashboardPassword: CHANGE_ME
      PRISSTYRNING_Price__Nordpool__DefaultZone: SE3
      PRISSTYRNING_Price__Nordpool__Currency: SEK
      PRISSTYRNING_Daikin__ClientId: CHANGE_ME
      PRISSTYRNING_Daikin__ClientSecret: CHANGE_ME
      PRISSTYRNING_Daikin__RedirectUri: https://example.com/auth/daikin/callback
      PRISSTYRNING_Daikin__ApplySchedule: "false" # keep false for transparency
      PRISSTYRNING_Schedule__ComfortHours: "3"
      PRISSTYRNING_Schedule__TurnOffPercentile: "0.9"
      PRISSTYRNING_Schedule__TurnOffMaxConsecutive: "2"
      PRISSTYRNING_Storage__Directory: /data
      PRISSTYRNING_PORT: "5000"
    volumes:
      - ./data:/data
    ports:
      - "5000:5000"
```
Start:
```bash
docker compose -f docker-compose.example.yml up -d
```

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
* **Restore**: Ensures NuGet packages can be restored
* **Build**: Compiles the project in Release configuration
* **Artifact Check**: Verifies the build produces expected output (`Prisstyrning.dll`)

Pull requests cannot be merged until the build verification passes. This ensures code quality and prevents broken builds from entering the main branch.

## OAuth tokens
After completing OAuth, tokens are persisted to `tokens/daikin.json` (if the volume is mounted). You may also inject `Daikin:AccessToken` / `Daikin:RefreshToken` directly for testing.

## Development hints
* **Local build verification**: Run `dotnet restore && dotnet build --configuration Release` to verify your changes locally before submitting a PR
* Local Nordpool snapshots stored as `data/nordpool/<ZONE>/prices-*.json`
* Frontend served from `wwwroot` (static + minimal JS)
* Schedule preview endpoint: `/api/schedule/preview`
* Current DHW schedule endpoint: `/api/daikin/gateway/schedule?embeddedId=2`

## License
No license specified yet (all rights reserved by default). Add a LICENSE file before broader distribution.
