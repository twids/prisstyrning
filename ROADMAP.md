# Roadmap / Technical Debt

Prioritized actionable items captured 2025-01-25, updated 2025-01-30 after backend testing + frontend rewrite.

Legend: P1 = High impact/near-term, P2 = Medium, P3 = Nice-to-have

## Recently Completed (2025-01-30)
- [x] **Comprehensive backend testing** - 125 passing tests covering persistence, jobs, API endpoints, OAuth
- [x] **ECO mode removal** (GitHub Issue #53) - Simplified to 2-mode system (comfort/turn_off)
- [x] **Frontend complete rewrite** - React 18 + TypeScript + Material UI + Vite
- [x] **HttpClient testability** - Injected HttpClient pattern for all HTTP-calling services
- [x] **Schedule history bug fixes** - Fixed 7 critical bugs (JSON cloning, path traversal, concurrency, culture parsing)
- [x] **Code splitting** - Vite manual chunks for optimized bundle sizes
- [x] **CI/CD improvements** - PR workflow now builds frontend and backend, Docker multi-stage with Node.js
- [x] **Migration documentation** - Created MIGRATION.md for ECO removal upgrade path

## P1 – Core Refactor & Reliability
- [ ] Extract schedule generation into discrete methods (classify, comfort window, expensive spike detection, block compression)
- [ ] Introduce strongly typed Options classes (e.g. ScheduleOptions, DaikinOptions) with validation on startup
- [x] ~~Add unit tests~~ **DONE** - 125 tests (ScheduleAlgorithm, Persistence, Jobs, API endpoints, OAuth integration)
- [x] ~~Golden scenario tests~~ **DONE** - 17 ScheduleAlgorithmTests covering edge cases, null handling, invalid data
- [ ] Full structured logging via ILogger (remove remaining Console.WriteLine) + log level from config
- [ ] Health endpoint (/health) returning basic status + price cache age
- [x] ~~Ensure PriceMemory access thread safety~~ **DONE** - Added SemaphoreSlim in ScheduleHistoryPersistence
- [ ] Remove unconditional `|| true` enabling Swagger

## P1 – Error Handling & Safety
- [ ] Explicit error object on /api/schedule/preview when HA fetch fails (keep HTTP 200 vs 4xx? decide)
- [ ] Validate generated schedule (guards: actions per day <=4, turn_off <=2h) with auto-fix logging
- [x] ~~Input validation~~ **PARTIALLY DONE** - Added SanitizeUserId, test coverage for invalid inputs
- [ ] Mask tokens / secrets in logs

## P2 – Observability & Metrics
- [ ] Basic metrics (generation duration, gateway cache hits, HA fetch latency)
- [ ] /metrics endpoint (Prometheus format) or simple JSON fallback
- [ ] Correlation id per request (middleware)

## P2 – Performance & Resource Use
- [x] ~~HttpClientFactory integration~~ **DONE** - HTTP client injection pattern for testability
- [ ] Configurable gateway-devices cache TTL (env) instead of fixed 10s
- [ ] Avoid reparsing price JSON in /prices/timeseries when memory has full day

## P2 – Feature Enhancements
- [ ] Diff endpoint: compare preview vs current schedule (changed time slots + summary)
- [ ] Frontend diff visualization + highlight turn_off intervals in chart
- [ ] Preview query parameters to simulate alternate ComfortHours / percentiles without restarting
- [ ] Add schedule version tag (schemaVersion) to snapshot files
- [ ] Device ID auto-fill from gateway API (frontend enhancement)
- [ ] Persist device IDs in localStorage for convenience

## P3 – Developer Experience / Cleanup
- [ ] Extract schedule parsing/extraction logic in Program.cs to ScheduleInspector class
- [ ] Global JsonSerializerOptions centralization
- [ ] Convert magic scheduleId "0" into named constant
- [ ] Add LICENSE file (decide on MIT/Apache/etc.)
- [ ] CONTRIBUTING.md with build/test instructions
- [x] ~~NuGet cache step in GitHub Actions~~ **DONE** - Added caching to pr-build-verification.yml
- [ ] Security scan (Trivy) job in CI

## P3 – Future Ideas
- [ ] Multi-day (48h) optimization when tomorrow prices known – adapt comfort placement across boundary
- [ ] Adaptive comfort block length based on variance or min nightly floor hold
- [ ] Pluggable pricing adapters (Nordpool direct, Tibber, etc.)
- [ ] Optional persistence backend (SQLite) replacing flat JSON snapshot rotation
- [ ] E2E testing with Playwright or Cypress for React frontend
- [ ] Storybook for component development and documentation

## Completed (Historical Context)
- [x] Multi-arch docker builds (linux/amd64, linux/arm64)
- [x] Atomic snapshot writes with temp + retry
- [x] Gateway devices caching layer
- [x] English README + full config table
- [x] DHW schedule extraction (schedule.value) fix
- [x] Backend testing foundation (125 passing tests)
- [x] ECO mode removal (GitHub Issue #53)
- [x] Frontend rewrite (React 18 + TypeScript + Material UI)
- [x] Code splitting (Vite manual chunks)
- [x] Docker multi-stage build with frontend compilation
- [x] CI/CD frontend integration (pr-build-verification.yml)

---
Last updated: 2025-01-30 | Consider converting remaining items to GitHub Issues for tracking.

