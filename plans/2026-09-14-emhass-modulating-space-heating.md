# Modulating house heat with reserved DHW

The first production Shadow plan succeeded, but its automatic renewal was rejected
because EMHASS's continuous fallback removed binary mutual exclusion and allocated
space heating during reserved DHW. The orchestrator correctly rejected that result.

The adapter had incorrectly marked space heating as semi-continuous, which in the
installed EMHASS thermal model means exactly zero or nominal power per quarter.
That can make a narrow temperature band infeasible despite sufficient modulating heat.

Send space heating as continuous/modulating, while retaining DHW's fixed-power,
single-constant job and mutual-exclusion group. Installed EMHASS 0.18.0 links
non-semi-continuous group members to their own binary activity indicators, so the
original mixed-integer problem still prevents simultaneous operation. If a future
fallback violates that rule, the unchanged result validator must reject it.

No comfort bands, quality gates, legacy scheduling, or active-mode permissions change.
Actual automatic renewal remains a deployment acceptance check.
