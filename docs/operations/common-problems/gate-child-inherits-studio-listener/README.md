---
id: gate-child-inherits-studio-listener
title: "Gate tests fail only on the Studio machine because the child inherits the Studio's listener"
status: fixed
first-seen: 2026-09-15T12:03:00Z
last-seen: 2026-09-15T14:41:00Z
severity: major
category: runtime
tags: [gate, merge-gate, tests, environment, aspnetcore, connector, windows, port, stable]
affects:
  - backend/Features/Pipeline/BuildTestGateRunner.cs
  - contracts/TaskServer.Contracts/HostListenerEnvironment.cs
  - backend.Tests/ConnectorProfileTests.cs
related-tasks: [AGT-2840, AGT-2825, AGT-2826, AGT-2827]
related-adrs: []
---

# gate-child-inherits-studio-listener

**What.** The local merge gate on the Windows Studio failed three cards in a row
(AGT-2825 14:03, AGT-2827 15:5x, AGT-2826 16:41) with the same five tests:

- `ConnectorProfileTests.Core_attach_task_lifecycle_routes_forward_with_the_unscoped_project_token`
- `ConnectorProfileTests.Published_api_and_hub_surface_is_fully_classified_and_hosts_no_authority_worker`
- `ConnectorProfileTests.Host_origin_session_and_csrf_are_enforced`
- `ConnectorProfileTests.Health_separates_liveness_from_redacted_upstream_readiness`
- `ConnectorProfileTests.Proxy_strips_browser_identity_and_injects_the_studio_credential_and_protocol`

The same tests passed in the developer checkout on the same machine and in the
remote review on Linux. Every affected card had passed remote review, so the gate
failure sent it to Human Review and cost a full re-review.

**Why.** The Studio backend starts the gate, and the gate handed its own
environment to `dotnet test`. That environment carries a listener:
`api.sh start` runs `dotnet run --project backend/OrchestratorApi.csproj --urls
http://127.0.0.1:5031`, and `dotnet run` applies the `http` profile from
`backend/Properties/launchSettings.json`, which puts
`ASPNETCORE_URLS=http://localhost:5030` into the launched process's
*environment*. The `--urls` argument only outranks it inside the backend's own
configuration, so the backend serves on 5031 while its environment keeps
advertising 5030 to every child it spawns. `WebApplicationFactory<Program>` boots
the real entry point inside the xunit process, so
`ConnectorHost.ValidateConfiguredListener` read the Studio's URL as the
connector's own configuration. The connector owns exactly one endpoint and
correctly refuses any other, so the host failed to build with

```
System.InvalidOperationException : The connector may listen only on http://[::1]:5031.
```

and every test in the class that boots a host failed - the four that do not boot
one kept passing. A developer shell carries no such variable, which is why the
same command passed two checkouts away, and the Linux review host does not start
its runner through `dotnet run` under a serving Studio.

The leak is reproducible in three lines: a throwaway project with an
`applicationUrl` in its launch profile, started as `dotnet run --urls <other>`,
prints the launch profile's URL from `ASPNETCORE_URLS` and the `--urls` value
from its arguments.

The symptom pointed at load and ordering (the failures appear roughly 19 minutes
into the suite, the machine is busy, a second Studio listens on the port), but
the failure is deterministic: it depends only on which process started
`dotnet test`.

The preparation gate already hands the prepare script a curated environment
(`PreparationHostEnvironment`). Verify commands had no such boundary.

**Fix (AGT-2840).**

1. `HostListenerEnvironment` names the variables through which a running host
   leaks its listener, and `BuildTestGateRunner` drops them from every verify
   child process. No gate command needs an inherited listener: one that serves
   brings its own configuration, and every other needs none.
2. `ConnectorProfileTests` boots its host with those variables set aside, so the
   suite no longer depends on which process launched it. A theory in that class
   reproduces the gate condition (`ASPNETCORE_URLS`, `URLS`,
   `Kestrel__Endpoints__Http__Url`) and fails without the fix.

**Symptom to recognise.** A test passes in a shell and fails under the gate on
the machine that runs the Studio, with an error about a port, a URL, a listener
or a configured endpoint. Compare the two environments before looking at timing.
