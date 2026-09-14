# Local-only EMHASS and idle planning inputs

- EMHASS 0.18.0 treats a non-empty dummy HA token as credentials and attempts HA
  configuration retrieval. Use its supported `empty` sentinel and do not inject
  SUPERVISOR_TOKEN. No real HA credentials belong in this solver container.
- A verified zero electric-power sample with explicitly inactive DHW, defrost and
  backup heater can initialize planning while idle. If hydraulic heat is missing,
  only zero flow or a temperature difference within 0.5 C permits a derived value.
- Raw telemetry is unchanged. This does not generate a COP training point or approve
  an active model. Predicted future COP uses weather-curve/load estimates in idle,
  as it already does during DHW, with explicit `weatherCurveEstimateWhileIdle` evidence.
- Invalid/missing power, active phases, stale required signals and implausible
  inverse temperature differences retain their existing rejection paths.
- No mode/writer/settings migrations or ONECTA payload changes.
- In Shadow only, the existing explicitly labelled assumed power input may initialize
  idle planning too. It retains zero plan confidence and is rejected for active inputs.

- EMHASS `thermal_inertia` is pure transport dead time, not mass/coupling time constant.
  The adapter now passes zero delay, matching the no-delay grey-box input dynamics,
  instead of inventing a 24-hour period without heating response. This adapter remains
  a first-order projection, not a full two-state EMHASS implementation.
- Solver row zero is the observed initial temperature. It may lie outside the desired
  band only when matching that initial state; all future rows still enforce comfort.
- Each solver request selects `method_ts_round=first`, matching the orchestrator's
  floor-to-quarter horizon instead of the upstream nearest-quarter default.

Verification: full backend suite 1488 passed, 7 existing skips, including zero delay and observed initial state. Production solver
success and repeated automatic plans remain separate release acceptance checks.
