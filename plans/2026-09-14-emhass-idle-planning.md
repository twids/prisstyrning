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

Verification: full backend suite 1480 passed, 7 existing skips. Production solver
success and repeated automatic plans remain separate release acceptance checks.
