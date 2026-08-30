# Intelligent heating orchestrator – implementation record

Status: the first .NET 10 release is deployed in the existing Dockhand `daikin` stack as revision `233afa4` (2026-08-30). Production remains `ControlMode=Legacy` / `DhwWriter=Legacy`; LWT/FullActive are disabled. Health, anonymous access protection and a real accepted legacy ONECTA write are verified in [the production record](2026-08-30-production-verification.md). Intelligent control is not operationally approved.

Post-deployment source-only work is recorded in [session recovery and proxy regressions](2026-08-30-session-recovery-regressions.md). Those changes have not been deployed.

## Delivered

- Additive EF Core storage for site/room/entity configuration, five-minute telemetry, hourly aggregates, model versions, plans/steps, DHW cycles, control state, writer leases, command audit and fallback events.
- Account-owned Home Assistant connections with separate telemetry and control identities. REST/history/WebSocket telemetry maintains an account-scoped timestamped cache; the control client exposes only `number.set_value` for the account's exact `number.*` entity.
- HA URL, enablement, staleness limit, allowlisted entity and AES-256-GCM-encrypted tokens are stored per server-verified Daikin account. No global HA environment-variable or Docker-secret fallback remains, and token values, fragments and lengths are never returned or logged.
- Daikin writes resolve only the encrypted token belonging to the server-verified account and fail closed when it is missing. The global Daikin access-token setting remains isolated to accountless legacy/test compatibility and cannot be selected for an authenticated account.
- Five-minute HA history import/resampling that never overwrites live snapshots, plus WebSocket reconnection/backoff and a fresh REST snapshot after reconnect.
- Sensor unit normalization, bounds/rate/staleness checks, three-invalid exclusion, three-valid recovery, critical-room fallback and deduplicated room/hydraulic diagnostics.
- Forecast-aware 2R2C grey-box and COP training with held-out validation, heat-output/COP guards, nightly versioning, model-drift warnings, room-specific offset/inertia/disturbance profiles and 400-day raw retention. Synthetic critical-room fallback values protect control but are excluded from readiness and model training.
- Five-minute, duration-based DHW planner with ten-minute ONECTA starts, whole-cycle cost, empirical Eco/Comfort profiles, 90th-percentile reservation, 20-minute lock, lifecycle tracking, actual cost and two-sample 60 °C verification. Incomplete price coverage selects the earliest safe start with a conservative price instead of risking availability, and the cost includes the estimated space-heating comfort margin consumed by the cycle.
- EMHASS 15-minute/48-hour space-heating coordination with locked DHW capacity and a 45-second hard timeout. The official container's documented `opt_res_latest.csv` is shared read-only, freshness/columns/horizon are validated, and mutual-exclusion groups prevent simultaneous space heating and locked DHW use of compressor capacity. EMHASS receives runtime data only and cannot access HA.
- PI-based LWT deviation controller with anti-windup, rate/change limits, DHW/defrost freeze, latched write failures and audited fail-to-zero attempts. A P1P2 write is accepted only after a fresh matching Home Assistant state confirms the requested value.
- Sequential `Legacy` → `Shadow` → `LwtActive` → `FullActive` mode service, measured readiness gates, installation ownership, expiring database-backed DHW write exclusion, atomic writer handover and visible rollback.
- A signed, database-backed session reuses the server-verified Daikin identity for legacy and thermal functionality. All account APIs require that session, all authenticated mutations require antiforgery validation, and administrative operations still require the admin role.
- The login gate offers an accessible, read-only retry after a failed session check. It hides cached installation views while verification is failing and never renders raw proxy/identity-provider error responses. This follow-up is locally verified, not yet deployed.
- Swedish responsive UI for Overview, Plan, Rooms, Hot water, Model, Events and Settings while keeping the legacy grid/admin pages.
- Vitest/React Testing Library/accessibility tests and Playwright critical-flow coverage in the PR gate.
- Digest-locked EMHASS reference topology in `docker-compose.thermal.example.yml` with a private internal network and persistent `/share` and `/data`.

## Verified locally

- Existing baseline before implementation: 425 backend tests passed, 9 skipped.
- Latest follow-up on 2026-08-30: Release build succeeded; the full .NET 10 backend suite passed 532 tests, with 9 existing skips and 0 failures. The deployed PR #120 previously passed 521 tests; the additional 11 tests are source-only proxy/antiforgery integration coverage.
- Production forwarding and antiforgery registrations are tested together using the real middleware and ephemeral test keys: trusted IPv4/mapped IPv6, untrusted headers, single-hop processing, ignored host spoofing, Secure/HttpOnly cookies, and rejection of missing/invalid/cross-account CSRF tokens. These tests do not start the full application or replace authenticated production acceptance.
- Focused thermal unit/integration-contract suite: 75 passed and 0 failed, including 92/96/100-quarter DST days, every allowed ten-minute DHW start boundary, incomplete-price fallback, empirical backup-heater phases, delayed/missed DHW runs, EMHASS CSV validation/mutual exclusion/hard timeout/tariff behavior, confirmed P1P2 writes and every LWT safe-zero trigger.
- Frontend: 14 Vitest component, interaction and accessibility tests passed in the latest follow-up.
- Playwright: 8 applicable desktop/mobile flows passed locally (6 existing duplicate-project exclusions), including the unauthenticated login boundary, sessions-error retry without control calls, readiness blocking, dirty-state preview, history import and mobile overflow/status navigation. The retry view's desktop/mobile screenshots were also visually inspected.
- TypeScript/Vite production build passed in the latest follow-up.

Earlier verification, retained for traceability (not all checks were rerun in the follow-up):

- `npm audit` reported zero known vulnerabilities after upgrading React Router and Vite to compatible fixed releases.
- Release `dotnet publish` produced a `net10.0` artifact successfully. The solution, Dockerfile and CI are all on .NET 10; the Release build completed with zero warnings and errors.
- NuGet's current advisory scan reported no vulnerable direct or transitive packages.
- All five generated thermal/account migrations are additive: the first creates the isolated thermal tables/indexes and the later migrations only extend thermal or credential/account storage. `dotnet ef migrations has-pending-model-changes --no-build` and the model-drift regression test report no pending model changes.
- Docker Compose configuration validation passed.
- EMHASS `v0.18.0` tag and multi-arch digest were verified against GHCR. Local Docker Desktop was unavailable at that stage. The existing production stack now includes the running, port-private EMHASS container, but `Emhass:Enabled=false`; live optimizer/house-model acceptance remains pending.

## Operational acceptance still required

Code verification does not satisfy the real-house gates. The initial deployment is verified in `Legacy` with existing ONECTA settings preserved. The user still needs to complete the authenticated account UI check and configure the account-owned HA connection before collection/model validation can begin. Continue read-only observation of ordinary legacy runs, then collect/validate the required shadow period, weather-curve days, model errors, DHW cycles and hygiene cycle through the UI. `FullActive` remains blocked until readiness is green.

Actual container/network/rollback facts are recorded in the production record. Windows Controlled Folder Access blocks changes to the shared infrastructure project's documents. A prepared patch is saved as `plans/2026-08-30-shared-infrastructure-update.patch`; the shared files remain unchanged and the protection must not be disabled or bypassed.
