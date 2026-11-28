# Backend test host startup

`WebApplicationFactory<Program>` runs the backend entry point inside xUnit. Its
default host starts no product `IHostedService` registrations and skips the
one-time repair, backfill, and report sweeps. Endpoint tests still run the
essential startup work: state folders, registry bootstrap, task access boot,
crash recovery, stale-progress recovery, and bus projection warmup.

Tests that cover a worker add that worker in `ConfigureTestServices`:

```csharp
builder.ConfigureTestServices(services =>
    services.AddHostedService<OrchestrationEngineReadinessService>());
```

A worker that a request path depends on is still a hosted service: when an
endpoint only queues work or reads what a worker publishes, the test of that
endpoint opts into the worker. Examples in the suite:

| Worker | Why the endpoint test needs it |
|---|---|
| `TaskRunnerService` | Builds the project runners that queue follow-ups (send back, preview feedback). |
| `OperatorApprovalNotifier` | Reads the approval notice queue and sends the push. |
| `OperatorKillSwitchService` | Applies the out-of-band latch, retries the host revocation, and schedules drills. |
| `BatchMoveJobCoordinator` | Runs queued batch moves. |
| `GitStateIndexService` | Publishes the Git facts that card reads join. |

Register a worker that the product resolves from a singleton the same way,
for example `services.AddHostedService(provider => provider.GetRequiredService<TaskRunnerService>())`,
so the request path and the worker share one instance.

Tests of the complete production startup path set both
`Startup:HostedServices=true` and `Startup:RepairSweeps=true` on the test host.
Outside xUnit, product hosted services and repair sweeps retain their normal
startup behavior. The repair-sweep switch is honored only for test hosts and
hosts whose environment is `Test` or `Testing`.

`AGENT_STUDIO_HOST_TIMELINE=<directory>` records one JSON line per disposed
host with each hosted-service start, named sweep, and startup phase. Use an
isolated directory per test process when measuring startup.

## Local state isolation and startup cost

The module initializer calls `TestTempRoot.Redirect()` before resolving any
host state. In addition to temporary paths, this sets
`AGENT_STUDIO_LOCAL_STATE_ROOT` to `<suite-root>/state`. `LocalStateRoot` honors
this absolute override before resolving the user's local application data
folder. Child API processes inherit it. The process-exit cleanup and six-hour
stale-suite cleanup therefore own instance state as well as fixture files.
A child-process test deliberately testing platform state resolution must clear
this override in that child's environment. Do not change it while hosts in the
same test process are running: state resolution is cached for the process.

`LocalStateRootGuardTests` checks that resolved instance state stays inside the
suite directory. `BatchGateStartupCostTests` exercises 50,000 and 200,000
synthetic siblings in suite-owned storage, with a 250 ms preparation tolerance.
`BatchGateHostStartupCostTests` starts the actual API with the same sibling
counts and a 1 s tolerance, comparing medians of three starts after warmup.
Both timing probes are `MachineBound`; the isolation guard runs routinely.
The named `backend-instance-startup-cost` machine-bound suite in
`.agent-studio/project.yml` runs these probes on a quiet, dedicated measurement
host. Routine, base, review, and coverage suites exclude them with
`Category!=MachineBound`. Run the measurement suite separately after building:

```sh
dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build \
  --filter 'Category=MachineBound&(FullyQualifiedName~BatchGateHostStartupCostTests|FullyQualifiedName~BatchGateStartupCostTests)'
```

The 1 s host-start tolerance measures sibling cost only when host load is
controlled. On 27 November 2026 the 200,000-sibling probe exceeded that tolerance
by 8.857 ms at a load of 230 on 12 cores. This busy-host result cannot establish
a sibling-dependent startup regression. Keep that result as load-labeled
evidence; do not widen the tolerance to accommodate loaded fleet hosts. Use the
quiet thresholds below for timing measurements. The routine isolation guard and
the explicit inspection tests remain independent of that wall-clock threshold.

Startup no longer inspects unmatched instances. Restore troubleshooting uses
the explicit command described in [storage hygiene](../temp-and-cache-hygiene.md).

For a whole-suite measurement, `scripts/measure-instance-suite.py` waits until
the one-minute host load is at most 75% of available CPUs and ten-second I/O
pressure is below 5%. It sets `AGENT_STUDIO_TEST_INSTANCE_SIBLINGS` to zero or
200000. An opt-in test module initializer seeds that many empty siblings under
the suite's own state root before any hosts start. Normal test runs do not
seed state. For example, after building:

```sh
python3 scripts/measure-instance-suite.py --label after-200000 --siblings 200000 \
  --results "$JOB_RESULTS_DIR" --scratch /tmp/instance-measurement -- \
  dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build \
  --filter 'Category!=MachineBound'
```

The JSON report records command wall time, seed time separately, all test
failures, suite-root cleanup, and the shared user's
instance entry names before and after (counts, hashes and any additions).
Compressed console logs accompany the report. A controlled comparison can
restore the previous `Prepare` implementation in a separate build while
keeping suite isolation enabled in both versions; state must never be seeded
in the user's shared directory.

On Linux, `--audit-mkdirs` also records `mkdir`/`mkdirat` calls from the test
process tree using `strace --seccomp-bpf`. The report lists calls naming the
shared instance root, so changes made by neighboring jobs can be distinguished
from this test run. Use the same tracing option for both timing variants and
report its overhead as part of the command wall time.

The initializer also records its process id. After that process exits, the
measurement owner removes only the recorded suite root if process-exit cleanup
left it behind. Automatic cleanup and any extra cleanup time are reported
separately. `--focused` allows a command with a `FullyQualifiedName` class filter
on a busy host; whole-suite commands always wait for the quiet threshold.
