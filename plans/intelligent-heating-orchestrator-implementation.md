# Intelligent heating orchestrator – implementation record

Status: implemented in source, not deployed or activated. Default mode remains `Legacy`.

## Delivered

- Additive EF Core storage for site/room/entity configuration, five-minute telemetry, hourly aggregates, model versions, plans/steps, DHW cycles, control state, writer leases, command audit and fallback events.
- Separate Home Assistant telemetry and control identities. REST/history/WebSocket telemetry maintains a timestamped cache; the control client exposes only `number.set_value` for one exact `number.*` entity.
- Docker-secret file support for both HA identities. Token values, fragments and lengths are never logged.
- Five-minute HA history import/resampling that never overwrites live snapshots, plus WebSocket reconnection/backoff and a fresh REST snapshot after reconnect.
- Sensor unit normalization, bounds/rate/staleness checks, three-invalid exclusion, three-valid recovery, critical-room fallback and deduplicated room/hydraulic diagnostics.
- Forecast-aware 2R2C grey-box and COP training with held-out validation, heat-output/COP guards, nightly versioning, model-drift warnings, room-specific offset/inertia/disturbance profiles and 400-day raw retention. Synthetic critical-room fallback values protect control but are excluded from readiness and model training.
- Five-minute, duration-based DHW planner with ten-minute ONECTA starts, whole-cycle cost, empirical Eco/Comfort profiles, 90th-percentile reservation, 20-minute lock, lifecycle tracking, actual cost and two-sample 60 °C verification. Incomplete price coverage selects the earliest safe start with a conservative price instead of risking availability, and the cost includes the estimated space-heating comfort margin consumed by the cycle.
- EMHASS 15-minute/48-hour space-heating coordination with locked DHW capacity and a 45-second hard timeout. The official container's documented `opt_res_latest.csv` is shared read-only, freshness/columns/horizon are validated, and mutual-exclusion groups prevent simultaneous space heating and locked DHW use of compressor capacity. EMHASS receives runtime data only and cannot access HA.
- PI-based LWT deviation controller with anti-windup, rate/change limits, DHW/defrost freeze, latched write failures and audited fail-to-zero attempts. A P1P2 write is accepted only after a fresh matching Home Assistant state confirms the requested value.
- Sequential `Legacy` → `Shadow` → `LwtActive` → `FullActive` mode service, measured readiness gates, installation ownership, expiring database-backed DHW write exclusion, atomic writer handover and visible rollback.
- Existing admin authorization protects every thermal mutation, HA test and history import; read views remain available to the bound installation.
- Swedish responsive UI for Overview, Plan, Rooms, Hot water, Model, Events and Settings while keeping the legacy grid/admin pages.
- Vitest/React Testing Library/accessibility tests and Playwright critical-flow coverage in the PR gate.
- Digest-locked EMHASS reference topology in `docker-compose.thermal.example.yml` with a private internal network and persistent `/share` and `/data`.

## Verified locally

- Existing baseline before implementation: 425 backend tests passed, 9 skipped.
- Full current backend suite: 501 passed, 9 intentionally skipped and 0 failed.
- Focused thermal unit/integration-contract suite: 75 passed and 0 failed, including 92/96/100-quarter DST days, every allowed ten-minute DHW start boundary, incomplete-price fallback, empirical backup-heater phases, delayed/missed DHW runs, EMHASS CSV validation/mutual exclusion/hard timeout/tariff behavior, confirmed P1P2 writes and every LWT safe-zero trigger.
- Frontend: 5 component/accessibility tests passed.
- Playwright: 4 applicable flows passed before the bounded history-import flow was added; the PR gate now contains that fifth flow for both desktop/mobile project selection. The latest local visual browser run was not permitted by the app environment, so that new browser flow remains for CI verification.
- TypeScript/Vite production build passed after dependency security updates.
- `npm audit` reported zero known vulnerabilities after upgrading React Router and Vite to compatible fixed releases.
- Release `dotnet publish` passed.
- All four generated thermal migrations are additive: the first creates the isolated thermal tables/indexes and the later migrations only extend those new tables. `dotnet ef migrations has-pending-model-changes --no-build` reports no model drift.
- Docker Compose configuration validation passed.
- EMHASS `v0.18.0` tag and multi-arch digest were verified against GHCR. A live container smoke test was not run because the local Docker Desktop daemon was stopped; no container was created.

## Operational acceptance still required

Code completion does not satisfy the real-house gates. Deployment must begin in `Legacy`, preserve the current ONECTA settings, and then collect/validate the required shadow period, weather-curve days, model errors, DHW cycles and hygiene cycle through the UI. `FullActive` remains blocked until readiness is green. After a verified Dockhand deployment, update the infrastructure project with the actual container names, networks, non-secret entity mappings and rollback path.
