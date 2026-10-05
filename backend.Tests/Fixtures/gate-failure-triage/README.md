# Gate failure triage fixtures (AGT-3009)

Captured merge-gate evidence logs (`post-steps/pre-develop-build-gate-N.log`)
used by `GateFailureTriagePolicyTests` and `GateFailureRouterTests`. Real logs
are copied unchanged from the frozen workspace snapshot of 2026-09-25.

| File | Source | Expected class |
|---|---|---|
| `environment-worker-crash.AGT-2724.log` | real, AGT-2724 | `environment` / `worker-crash` |
| `environment-run-budget.AGT-2839.log` | real, AGT-2839 | `environment` / `run-budget` |
| `product.AGT-2722.log` | real, AGT-2722 | `product` |
| `green-proven-flake.AGT-2867.log` | real, AGT-2867 | not a failure: green after a passing targeted re-run |
| `shared-cause.AGT-2677.log` | real, AGT-2677 | `product`, same fingerprint as AGT-2752 |
| `shared-cause.AGT-2752.log` | real, AGT-2752 | `product`, same fingerprint as AGT-2677 |
| `undecidable-conflict.AGT-2712.log` | real, AGT-2712 | `undecidable` (budget marker and a failing test) |
| `integration-branch.derived-from-AGT-2752.log` | derived | `integration-branch` |
| `environment-transport.derived.log` | derived | `environment` / `transport` |

Derived fixtures exist because no captured log has that evidence yet:

- `integration-branch.derived-from-AGT-2752.log` is the AGT-2752 log with the
  AGT-2916 diagnosis suffix (`diagnosis=... baseline=red ...`) appended to its
  `reason=` line. Every captured log predates the diagnosis contract.
- `environment-transport.derived.log` uses the current header format with a
  git transport failure as its output. No captured gate log failed on transport.
