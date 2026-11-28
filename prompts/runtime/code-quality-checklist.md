## Code quality checklist

Check the full diff against the intended merge base, using the changed-file context:

- Normal path: trace the entry point to the changed behavior. Check that callers and readers accept the types, values, formats and defaults produced.
- Durable transitions: check failures before and during each changed write, state change, lease or result delivery. Trace retry or restart through to a consistent outcome with confirmation only after success.
- Compatibility: where applicable, check that existing records still load and replay preserves the supported outcome.
- Verification: check that changed tests exercise the real path and catch the defect, and that changed executable instructions work as written.

Block regressions, broken types, dropped error paths and other concrete defects that prevent safe delivery. This includes unused parallel implementations that leave the old behavior live, stubs, dead required branches and incomplete wiring. Treat shippable duplication, naming, unused code, stale comments and scope concerns as concerns. Pass when there is no concrete gap. Leave pure requirement-fit, documentation coverage and missing-test findings to their owning checks. Evaluate broken executable instructions and acceptance harnesses by their concrete consequence.

Cite the exact file, line or diff section checked. For a finding, name the failed branch or contract and the consequence. A block requires both a citation and a concrete gap.
