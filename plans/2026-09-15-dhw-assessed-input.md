# DHW uses assessed planning inputs

The joint coordinator assessed and normalized tank and brine readings, but then
passed the raw telemetry sample into DHW planning. A Shadow-only unchanged tank
reading could therefore pass input validation and subsequently be treated as absent.
With an open reservation this produced a misleading profile-provenance failure;
without one it could omit the DHW proposal.

Pass the immutable assessed planning telemetry to both flexible and existing-cycle
DHW planning. Do not mutate raw measurements or relax validation. Shadow assumptions
remain recorded in the plan with zero confidence. Active modes must continue to
reject these inputs. Existing-cycle start temperatures still take precedence.

Regression coverage includes initial and repeated planning with unchanged tank and
brine inputs, preserved DHW reservation, Shadow labels, unchanged raw values and
Legacy writer, and rejection in LwtActive and FullActive.

Deployment acceptance is separate: verify the official image, existing Dockhand
stack, health/authentication and successive automatic plans without heat/DHW overlap.
