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

Tests of the complete production startup path set both
`Startup:HostedServices=true` and `Startup:RepairSweeps=true` on the test host.
Outside xUnit, product hosted services and repair sweeps retain their normal
startup behavior. The repair-sweep switch is honored only for test hosts and
hosts whose environment is `Test` or `Testing`.

`AGENT_STUDIO_HOST_TIMELINE=<directory>` records one JSON line per disposed
host with each hosted-service start, named sweep, and startup phase. Use an
isolated directory per test process when measuring startup.
