# Build/test gate: the one targeted re-run of a red test step

Operations note for the Windows integration gate (`pre-develop-build-gate` and
`pre-main-test-gate`, both driven by `BuildTestGateRunner`). It states when the
gate re-runs failed tests, what that costs, and where the resulting flake list
is readable.

## Why the gate re-runs at all

Between 20:00 and 01:35 on 16./17.09.2026 six pre-develop gate runs rolled back
green deliveries because one to three backend tests failed inside the full
`OrchestratorApi.Tests` suite (about 6.700 tests, 25 minutes) and passed when
the same class was run alone on the same merge candidate:
`ProviderAuthProvisioningTests`, `ProjectRunnerPickupAtomicityTests`,
`LaneMutexRegistryConcurrencyTests`, `AutoReviewPostProcessingWorkerTests`,
`UpdateServiceRestartIdentityDrillTests` (twice), `WikiContentCacheTests`,
`BoardConditionalReadTests`, `TaskIndexCacheTests`.

Each false red cost a full remote re-review (40-100 min) plus another 25-minute
gate, and the operator had to prove the flake by hand every time (AGT-2814,
2818, 2810, 2849, 2843, 2703).

## What the gate does now (AGT-2853)

When a verify **test** step goes red with a product failure, the gate spends at
most one targeted re-run before it blocks the merge:

1. Read the exact failed test names out of the red run's output (both .NET
   logger shapes: `Failed <name> [12 ms]` and `<name> [FAIL]`).
2. Re-run only those names, on the same build and in the same workspace:
   `dotnet test <project> --no-build --filter "FullyQualifiedName=A|FullyQualifiedName=B"`.
   Any selection filter the staged planner had added is replaced, not extended.
3. **Green re-run** - the gate passes. The names are recorded as flaky; nothing
   is absorbed silently.
4. **Second red** - the original red is the verdict. The merge is rolled back
   exactly as before.

The decision is `GateFlakyRerunPolicy.Decide`. It declines the re-run, leaving
the original red standing, when any of these hold:

| Reason | Meaning |
|---|---|
| `not-a-product-failure` | Timeout, lock, OOM, missing source, or a toolchain crash. Those have their own ladders. |
| `not-a-test-command` | Not a single filterable `dotnet test` invocation (an `npm` script, a build step, a chained or piped command line). |
| `no-parsed-test-names` | No test name was readable, so there is nothing to target. |
| `too-many-failures` | More than 10 distinct failures. A broad red is a real red, not the one-to-three-test flake pattern. |
| `unfilterable-test-name` | A name carries characters a `FullyQualifiedName=` term cannot express verbatim (a theory case printed with its arguments). A filter that silently matches nothing would read as green. |
| `no-budget-left` | The gate-run budget is spent. |

## Budget

The re-run is charged to the **same** gate-run budget
(`GateRunBudgetPolicy`, `ProjectSettings.BuildTestGateTimeoutSeconds`, or the
`PostSteps:build-test-gate:TimeoutSeconds` override). It does not get its own
allowance and it does not extend the gate. With no budget left the gate does not
re-run at all and the original red stands, so a gate can never take longer
because of this feature than its budget already permitted.

In practice the re-run costs a test pass, not a build: `--no-build` reuses the
artifacts the red run already produced.

## Where the flake list is readable

Both surfaces use the classification `FlakyQuarantine`
(`ReviewFlakyQuarantine.Classification`), shared with the remote review
executor's baseline comparison, and the same two evidence fields the executor
writes (`RetryPerformed`, `FlakyQuarantinedFailures`).

- **Gate reason** - the one-line verdict reason names the tests:
  `verify gate passed (build-profile); FlakyQuarantine: <names> failed once and passed on the targeted re-run`.
- **Gate evidence log** - `<job>/post-steps/pre-develop-build-gate-<n>.log` and
  `…/pre-main-test-gate-<n>.log` carry a dedicated header line:
  `retryPerformed=true classification=FlakyQuarantine flakyQuarantined=<names>`.
  The log body also contains the re-run's own command line and output, stamped
  with the `flaky-rerun` phase.
- **Card timeline** - one `integration_gate_flaky_rerun` event per gate that
  quarantined something, with `gate`, `sha`, `classification`, and `tests` in
  its details (`GateFlakyRerunReceipts`).

Grep the flake list across cards with:

```sh
grep -rh "flakyQuarantined=" <workspace>/projects/*/*/*/post-steps/ | grep -v "=none"
```

## What to do with a quarantined test

A quarantined name is not a pass. It is a report that the test is
order-dependent, timing-dependent, or shares state with the rest of the suite.
Repair it the same way as any other red: reproduce by running the class alone
and then inside the full suite, fix the shared state, and keep the regression
test. The remote review executor additionally honours an explicit
`[Trait("Category", "ReviewFlaky")]` quarantine; the gate deliberately does not
require that trait, because the whole point is to see flakes that nobody has
marked yet.

## Related

- [Windows test baseline and platform gates](./windows-baseline-and-platform-gates.md)
- [Pipeline domain](../../system/domains/pipeline.md)
- [Review domain](../../system/domains/review.md)
