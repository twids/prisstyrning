# Explicit write-free Shadow cold start

The joint planner previously required active, fully validated 2R2C and COP models
even in Shadow. This prevented early simulated plans while learning data accumulated.

- Shadow can use the existing grey-box fitting priors and conservative COP prior
  when a validated model pair is missing. These are not learned house parameters.
- Model evidence explicitly marks the prior; active plan consumption rejects it.
- Plan confidence is unverified, not an invented probability. UI labels temperature,
  cost and LWT proposals as uncertain simulations, not verified savings.
- Explicit live-collector `AssumedUnchanged` numerical state can initialize Shadow.
  Source age, receipt time and stale quality are retained; no historical training
  rows are upgraded or rewritten. Invalid units/ranges, disconnected HA, unavailable
  values and failed explicit liveness checks remain excluded.
- Price, operating phase and realtime COP are not extrapolated by this policy.
- Validated model pairs automatically replace priors on subsequent planning runs;
  readiness and active-control requirements remain unchanged.
- No migrations, credential changes, control-mode changes, or legacy payload changes.

Release acceptance: backend/frontend regression checks, CI, same-stack reversible
deployment, and two successive automatically generated Shadow plans. A successful
connection check or a simulated test dispatcher is not production solver evidence.
