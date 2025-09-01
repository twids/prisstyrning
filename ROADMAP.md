# Roadmap / Technical Debt

Prioritized actionable items captured on 2025-09-01 so they are not lost.

Legend: P1 = High impact/near-term, P2 = Medium, P3 = Nice-to-have

## P1 – Core Refactor & Reliability
- [ ] Extract schedule generation into discrete methods (classify, comfort window, expensive spike detection, block compression)
- [ ] Introduce strongly typed Options classes (e.g. ScheduleOptions, DaikinOptions) with validation on startup
- [ ] Add unit tests for: comfort expansion, spike detection, max 2h turn_off, <=4 actions/day compression
- [ ] Golden scenario tests (monotonic up, monotonic down, spiky, flat) with expected segment outputs
- [ ] Structured logging via ILogger (remove Console.WriteLine) + log level from config
- [ ] Health endpoint (/health) returning basic status + price cache age
- [ ] Ensure PriceMemory access thread safety (immutable snapshot or lock)
- [ ] Remove unconditional `|| true` enabling Swagger

## P1 – Error Handling & Safety
- [ ] Explicit error object on /api/schedule/preview when HA fetch fails (keep HTTP 200 vs 4xx? decide)
- [ ] Validate generated schedule (guards: actions per day <=4, turn_off <=2h) with auto-fix logging
- [ ] Input validation for /gateway/schedule/put (payload size, scheduleId format, required fields)
- [ ] Mask tokens / secrets in logs

## P2 – Observability & Metrics
- [ ] Basic metrics (generation duration, gateway cache hits, HA fetch latency)
- [ ] /metrics endpoint (Prometheus format) or simple JSON fallback
- [ ] Correlation id per request (middleware)

## P2 – Performance & Resource Use
- [ ] HttpClientFactory integration for Daikin & HA clients
- [ ] Configurable gateway-devices cache TTL (env) instead of fixed 10s
- [ ] Avoid reparsing price JSON in /prices/timeseries when memory has full day

## P2 – Feature Enhancements
- [ ] Diff endpoint: compare preview vs current schedule (changed time slots + summary)
- [ ] Frontend diff visualization + highlight turn_off intervals in chart
- [ ] Preview query parameters to simulate alternate ComfortHours / percentiles without restarting
- [ ] Add schedule version tag (schemaVersion) to snapshot files

## P3 – Developer Experience / Cleanup
- [ ] Extract schedule parsing/extraction logic in Program.cs to ScheduleInspector class
- [ ] Global JsonSerializerOptions centralization
- [ ] Convert magic scheduleId "0" into named constant
- [ ] Add LICENSE file (decide on MIT/Apache/etc.)
- [ ] CONTRIBUTING.md with build/test instructions
- [ ] NuGet cache step in GitHub Actions
- [ ] Security scan (Trivy) job in CI

## P3 – Future Ideas
- [ ] Multi-day (48h) optimization when tomorrow prices known – adapt comfort placement across boundary
- [ ] Adaptive comfort block length based on variance or min nightly floor hold
- [ ] Pluggable pricing adapters (Nordpool direct, Tibber, etc.)
- [ ] Optional persistence backend (SQLite) replacing flat JSON snapshot rotation

## Completed (for historical context)
- [x] Multi-arch docker builds
- [x] Atomic snapshot writes with temp + retry
- [x] Gateway devices caching layer
- [x] English README + full config table
- [x] DHW schedule extraction (schedule.value) fix

---
Generated initially; update as tasks progress. Consider converting to GitHub Issues (one per bullet) for tracking.
