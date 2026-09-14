# Thermally feasible DHW reservation selection

Production renewal still failed after modulating house heat was enabled. The chosen
DHW reservation could not maintain minimum temperature even starting at the upper
comfort bound. The two-level planner must not choose such a flexible window and then
expect the house optimizer to make it possible.

- Screen flexible candidates with the same cooling coefficient, effective outdoor
  forecast, and comfort bounds sent to EMHASS. This is a necessary feasibility check,
  not a substitute for full solver validation or a claim of measured house response.
- Retain cost ordering among feasible candidates. If none exists, explicitly fail
  planning rather than silently dropping DHW or loosening comfort/hygiene constraints.
- Locked and running DHW remain unchanged; their full-horizon solver checks still apply.
- Rasterize start with floor and end with ceiling independently. A ten-minute start
  must reserve every overlapping quarter, including the final partial quarter.
- Keep legacy, mode permissions, and collision validation unchanged.

Tests cover offset starts, colder/warmer windows, actual initial temperature, and
coordinator selection/rejection. Existing normal coordinator fixtures now use a warm
forecast that is physically capable of surviving DHW; cold rejection has explicit tests.
