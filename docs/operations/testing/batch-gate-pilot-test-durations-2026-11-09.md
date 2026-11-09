# Batch gate pilot test durations, 9 November 2026

AGT-3507 measured `AgentStudio.Tests.BatchGatePilotServiceTests` as the
slowest backend test class: 797 s before and 701 s after AGT-3507, 214 tests,
more than three times the next class
([backend-test-class-durations-2026-11-05.md](backend-test-class-durations-2026-11-05.md)).
The gate shards the backend suite by class, so this class set the length of
its shard. AGT-3566 measured where the time went, removed the largest wait,
cut Git processes from the seeding helper, and split the class into topic
classes so that the shard planner can balance them.

## Result

| Measure | Before (3 runs) | After (3 runs) |
|---|---|---|
| Largest class, gate settings (summed TRX seconds) | 541.8, 610.3, 587.9 | 116.9, 171.3, 161.7 |
| Classes holding the 234 tests | 1 | 21 (22 declared; `BatchGateWaitingBatchTests` holds only MachineBound tests) |
| Test process wall time, gate settings | 546.4, 618.1, 593.2 s | 141.2, 236.4, 231.5 s |
| Summed seconds of the 234 tests, one class at a time | 541.8, 610.3, 587.9 | 227.7, 217.8, 286.5 |
| Passed / failed | 234 / 0 in each run | 234 / 0 in each run |

Every class stayed under 200 s in all three after runs with the gate's
settings. The summed time of all 234 tests, run one class at a time like the
single class before, fell by 51 to 63 percent; it is not under 200 s. A class
sum under the gate's settings includes the contention of the classes that run
beside it in the same process (xUnit runs up to six classes at once with
`DOTNET_PROCESSOR_COUNT=6`), so the same tests sum to more in a parallel run
than in a serial one.

## Method

All runs used the runner host `agent-studio-runner` (12 CPUs, shared with
other task runs), `DOTNET_PROCESSOR_COUNT=6`, `--no-build`, and
`--filter Category!=MachineBound` combined with a class filter. One test
process ran at a time. Before is `ea372702d` (current `develop` at the start
of the card), run from a copy of its build output. After is this change.

- **Before:** `FullyQualifiedName~AgentStudio.Tests.BatchGatePilotServiceTests`.
- **After, gate settings:** a filter naming the 22 new classes
  (`FullyQualifiedName~AgentStudio.Tests.<Class>.` for each), xUnit defaults.
- **After, serial:** the same filter with
  `-- xUnit.ParallelizeTestCollections=false`, so that classes run one at a
  time as the single class did before. This is the comparison of test cost.

Class time is the sum of the test durations in the TRX file. The host load
was sampled every 15 seconds during each run.

| Run | Start (CET) | 1-min load at start / end | Mean load (min-max) | Wall s | Summed s | Largest class s |
|---|---|---|---|---:|---:|---:|
| before-1 | 10:05 | 13.3 / 27.4 | 25.3 (6.6-62.0) | 546.4 | 541.8 | 541.8 |
| before-2 | 13:04 | 27.3 / 46.5 | 27.4 (10.6-58.1) | 618.1 | 610.3 | 610.3 |
| before-3 | 13:23 | 20.4 / 33.2 | 22.2 (9.0-35.7) | 593.2 | 587.9 | 587.9 |
| after-1 | 12:50 | 32.9 / 37.0 | 41.8 (32.9-49.6) | 141.2 | 743.4 | 116.9 |
| after-2 | 12:56 | 21.9 / 24.7 | 42.2 (21.9-55.9) | 236.4 | 1,199.9 | 171.3 |
| after-3 | 13:14 | 46.5 / 54.2 | 49.4 (40.4-56.6) | 231.5 | 1,175.3 | 161.7 |
| after-serial-1 | 12:52 | 37.0 / 21.9 | 21.2 (11.2-37.0) | 234.5 | 227.7 | 35.0 |
| after-serial-2 | 13:00 | 24.7 / 27.3 | 17.8 (11.0-26.8) | 226.8 | 217.8 | 38.4 |
| after-serial-3 | 13:18 | 54.2 / 20.4 | 40.9 (24.6-54.2) | 296.3 | 286.5 | 49.7 |

After the measured runs, the seeding commit took its date from Git (as
`git commit` did) instead of the test process clock, which the test clock
guard requires. One more run with the gate's settings on that final build
(13:45, load 16.8 at start, 23.4 at end) passed all 234 tests in 127.1 s wall
time; its largest class was `BatchGatePilotRecoveryTests` at 109.4 s.

The after runs with the gate's settings saw a higher host load than the
before runs. In after-2 the first test of each of the six classes that started
together took 21 to 57 s (one to seven seconds in the other runs): six cold
hosts compiled and started at once on a host at load 40 to 56. That start is
part of the class sums of after-2.

### Per class, after

Summed TRX seconds.

| Class | Tests | after-1 | after-2 | after-3 | serial-1 | serial-2 | serial-3 |
|---|---:|---:|---:|---:|---:|---:|---:|
| `BatchGatePilotFormationTests` | 18 | 83.2 | 171.2 | 108.5 | 13.3 | 20.5 | 28.7 |
| `BatchGatePilotRecoveryTests` | 33 | 116.9 | 47.0 | 161.7 | 29.7 | 21.1 | 28.2 |
| `BatchGatePilotPublicationTests` | 18 | 43.0 | 111.4 | 68.5 | 35.0 | 12.3 | 48.3 |
| `BatchGateEngineApplierContractTests` | 42 | 77.4 | 44.7 | 102.5 | 14.1 | 16.7 | 49.7 |
| `BatchGatePilotFallbackStormTests` | 4 | 23.1 | 96.9 | 47.4 | 9.7 | 5.8 | 4.8 |
| `BatchGateQueueStallTests` | 14 | 57.8 | 65.0 | 94.3 | 8.2 | 9.8 | 30.7 |
| `BatchGatePilotBaseCheckTests` | 6 | 29.8 | 92.8 | 33.9 | 13.3 | 17.6 | 6.6 |
| `BatchGatePilotStackingTests` | 7 | 61.6 | 63.0 | 90.5 | 23.3 | 9.9 | 18.3 |
| `BatchGatePilotOrphanRecoveryTests` | 5 | 14.1 | 89.9 | 40.3 | 6.9 | 6.5 | 4.4 |
| `GateResultShadowObservationTests` | 13 | 44.1 | 82.3 | 83.3 | 19.7 | 8.8 | 14.7 |
| `BatchGatePilotMergedTreeFallbackTests` | 8 | 34.3 | 31.9 | 56.4 | 9.2 | 13.8 | 5.3 |
| `BatchGatePilotPlanningTests` | 20 | 38.7 | 55.6 | 45.9 | 8.3 | 38.4 | 7.5 |
| `BatchGateAdmissionRecoveryTests` | 5 | 8.2 | 53.4 | 15.7 | 1.3 | 1.4 | 8.7 |
| `BatchGateEngineAdmissionTests` | 14 | 26.3 | 22.3 | 51.0 | 6.7 | 6.2 | 5.5 |
| `GateResultApplierContractTests` | 8 | 17.2 | 20.3 | 41.2 | 5.3 | 6.1 | 4.3 |
| `BatchGatePilotFormationAdmissionTests` | 6 | 12.7 | 40.0 | 33.5 | 4.0 | 4.2 | 4.2 |
| `BatchGateIdleHostTests` | 2 | 6.7 | 32.1 | 18.3 | 11.2 | 7.5 | 2.2 |
| `BatchGateServiceRestartTests` | 1 | 4.3 | 28.4 | 8.5 | 0.5 | 0.5 | 1.1 |
| `BatchGatePilotBaseMoveTests` | 4 | 17.0 | 23.7 | 28.0 | 4.2 | 4.8 | 7.5 |
| `BatchGatePreCheckTests` | 4 | 20.7 | 15.4 | 24.6 | 2.4 | 4.1 | 4.0 |
| `BatchGateCancellationTests` | 2 | 6.2 | 12.4 | 21.5 | 1.3 | 1.8 | 1.8 |

## Where the time went

An EventPipe sample profile (`Microsoft-DotNETCore-SampleProfiler`) of 28
tests of the class, and a timestamped trace of every log line and fixture Git
call over a whole class run, showed:

| Cost | Share of test-thread samples | What it was |
|---|---:|---|
| Index refresh wait | 36 % | `TaskIndexCache.EnsureFresh` waited for a background refresh that starts with `Thread.Sleep(RefreshCoalesceWindow)`, a fixed 200 ms. Every lane move or task write followed by a lookup paid it, and the tests are serial. |
| Host start | 31 % | One `WebApplicationFactory<Program>` per test: about 0.4 s of CPU per host for DI resolution, endpoint mapping (about 150 ms), startup sweeps (about 85 ms), middleware and hosted service start. No single setting removes it. After change 1 it was 51 % of the remaining time. |
| File writes and fsync | 15 % | Attempt authority, orchestration store and lane writes flush to disk. |
| Git child processes | 9 % | Fixture and production Git. The fixture alone started about 3,650 Git processes per class run, ten per seeded card; after change 1 its share was 10 %. |

Remaining outliers of 10 to 37 s in single runs did not repeat in isolation and
were not tied to one timeout: the trace showed gaps of several seconds spread
over Git worktree operations and file writes while the host load was 20 to
60. Tests that took 30 s in one run took 2 to 4 s in the next.

Two candidates were measured and not adopted:

- **DI dynamic engine off** (`Microsoft.Extensions.DependencyInjection.DisableDynamicEngine`):
  62 % of the sampled CPU time sat on the finalizer thread finalising
  `DynamicResolver` objects of disposed hosts, but the switch changed the
  subset time from 72.9 s to 71.7 s and from 59.9 s to 62.5 s (noise). The
  finalizer was not on the test path.
- **Temp root on tmpfs** (`AGENT_STUDIO_TEST_TEMP_ROOT=/dev/shm/...`): the
  subset went from 57 to 65 s down to 48 s. It moves the whole suite's temp
  root, depends on the host's `/dev/shm` size, and was left as a possible
  suite-wide follow-up.

## Changes

1. **Configurable index refresh window.** `TaskIndexCache` reads
   `TaskIndexCache:RefreshCoalesceMilliseconds` (default 200, unchanged in
   production) next to its existing TTL and reconciliation keys. The batch gate
   pilot fixture sets 0: each test drives one reader at a time, so there is no
   concurrent reader to coalesce. Refresh stays single-flight and readers
   still wait for the refresh their mutation needs. On the 28-test subset this
   alone took the time from 57 to 93 s down to 27.4 s, and the whole class
   from 541.8 s to 337.8 s.
2. **Seeding with one fast-import.** `SeedMember` writes the result commit and
   the remote-tracking ref a fetch would write with one `git fast-import`,
   then pushes the result branch, instead of checkout, add, commit, rev-parse,
   push, fetch and checkout back. The repository ends in the same state: the
   result commit under `refs/heads/agent-studio/results/<key>` on origin and
   `refs/remotes/origin/agent-studio/results/<key>` locally, no local result
   branch (still asserted), HEAD detached at origin's develop. A fix round with
   a develop merge keeps the working tree path. This change was not measured
   on its own; it is part of every after run.
3. **One class per topic.** `BatchGatePilotServiceTests` is now an abstract
   fixture with the shared state and helpers (`private protected`) and no
   tests. Its 207 test methods (234 cases without MachineBound) sit in 22
   sealed classes: one per existing topic file (for example
   `BatchGateQueueStallTests`, `BatchGatePilotStackingTests`), and the former
   main file is split into `BatchGatePilotPlanningTests`,
   `BatchGatePilotFormationTests`, `BatchGatePilotPublicationTests` and
   `BatchGatePilotRecoveryTests`. The shard planner reads top-level,
   non-abstract classes from the source and assigns them longest first, so the
   former single unit of about 600 s is now 22 units of at most 50 s serial.
   Classes without a recorded timing are estimated at the median until the
   next sharded run records them. No test, assertion or test body changed;
   `EngineApplierContractGuardTests` still reads
   `BatchGatePilotServiceTests.ApplierContractCases`.

Options considered for the split and not taken: nested test classes (the
shard planner does not see nested classes, so all of them would run in the
complement shard), and one shared host for several tests (it would break test
independence; every test seeds its own workspace and singletons).

## Evidence

The TRX files, console logs, load traces, per-class and per-test CSV files and
the measurement scripts of all runs are in the AGT-3566 run evidence
(`results/class-durations.csv`, `results/test-durations.csv`,
`results/runs/<run>/`).
