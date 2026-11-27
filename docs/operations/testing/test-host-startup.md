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
Startup no longer inspects unmatched instances. Restore troubleshooting uses
the explicit command described in [storage hygiene](../temp-and-cache-hygiene.md).
