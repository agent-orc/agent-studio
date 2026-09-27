# Workstation runner host

Status: Windows runner-host profile for D12 of the [Task Server bus
Dossier](../../task-server-bus/index.html#d12-workstation). I7 host placement is
promoted as AGT-2939. This page describes the runner-side contract; the Task
Server placement API and its release must be verified before enabling fleet
routing in a deployment.

## Topology and authority

Publish the same `runner/AgentRunner.csproj` used by a Linux Agent Host. Set
`RUNNER_WORKSTATION=1` for a workstation instance. The process still registers
one scoped runner principal, advertises capabilities, polls the ordinary claim
endpoint, renews its fenced lease, and reports through the ordinary result and
artifact endpoints. No workstation task queue, daemon listener, or workstation
RPC is introduced. Coding and review remain separate role registrations and
slot budgets when both are enabled on one machine.

The browser talks to the loopback Connector. The Connector retains its browser
session, exact Host and Origin checks, CSRF protection, and upstream secret
handling. A native host manager retains installation, updates, service restart,
and executor-profile management. Neither owns task claims. Use a dedicated
runner service identity with a protected `RUNNER_AUTH_TOKEN_FILE`; do not put
Connector or browser credentials in its environment. Detached coding and review
workers remove inherited Task Server and browser-edge variables before launch.

The workstation's `RUNNER_WORKDIR` and `RUNNER_STATE_DIR` must be writable only
by its runner identity. Repository preparation still creates a project cache and
an isolated linked worktree for each claim. The project-owned preparation script
sets versions and dependencies; the workstation profile does not run a task in
the Studio checkout or inherit the browser's process environment.

## Package and configure

Publish a self-contained Windows artifact from the same source and version as
the Linux host:

```sh
dotnet publish runner/AgentRunner.csproj -p:PublishProfile=win-x64 -o out/workstation-runner
```

The output contains `agent-host.exe`. Run it under the native host manager as a
long-lived `--poll` service. Supply the ordinary `RUNNER_SERVER_URL`, stable
`RUNNER_ID`, enrolled `RUNNER_CLIENT_ID`, `RUNNER_NAME`, `RUNNER_AUTH_TOKEN_FILE`,
`RUNNER_WORKDIR`, and `RUNNER_STATE_DIR` described in the
[Linux runner-host runbook](linux-runner-host.md) and
[networked Task Server setup](networked-task-server.md). Use a unique state and
work directory per role. The Windows host manager must restart the daemon after
failure or sleep; the Task Server still decides whether its old fence can be
re-adopted.

Additional workstation settings:

| Setting | Meaning |
| --- | --- |
| `RUNNER_WORKSTATION=1` | Advertise `host-class:workstation` and apply local admission. |
| `RUNNER_WORKSTATION_ROOTS=studio=C:\Projects\Studio;tools=D:\Source\Tools` | Up to 16 named, absolute local repository roots. Each existing root advertises `local-repo:<name>` as ready. Missing or reparse-point roots advertise unavailable. |
| `RUNNER_WORKSTATION_TOOLS=msbuild,powershell` | Executable names that must be present at advertisement and again before every worker start. Each advertises `toolchain:<name>`. |
| `RUNNER_PREVIEW_ORIGIN=https://preview.example.test` | Optional URL origin for preview evidence; HTTPS is required outside loopback. |
| `RUNNER_PREVIEW_REACHABILITY=operator-browser` | Declared audience: `operator-browser`, `host-only`, or `public`. |
| `RUNNER_PREVIEW_LIFETIME_SECONDS=3600` | Maximum lifetime from creation, bounded to one day. |

Windows hosts also advertise `platform:windows`, the architecture-specific
platform capability, and detected `toolchain:msbuild`, `toolchain:vswhere`,
`toolchain:pwsh`, and `toolchain:powershell`. The existing dotnet, Node, and
Playwright probes remain in use. Git is mandatory and is checked again before
worker creation. Missing tools do not appear ready. A source
using a local path or `file:` URL must resolve inside a configured root; relative
paths, network shares, reparse points, and paths outside the roots are refused
before Git clone or worker creation. Network Git sources follow the ordinary
host path. On the versioned Task Server claim, the admitted capability set is
returned with the lease; the workstation checks its local-root and toolchain
entries again just before it creates the worker.

## Placement

For an ordinary project, use I7's project placement with required capabilities
such as `platform:windows` and `toolchain:msbuild`, leaving the runner pin empty.
Another enrolled Windows host with matching fresh capabilities and available
capacity can then claim the next task without rewriting the task. A project
whose source truly exists only under a workstation root may require
`local-repo:studio` and an explicit runner pin. A pin documents that the work
waits while that workstation is asleep or absent. The runner's root check is a
second local guard even when placement was admitted from a fresh snapshot.

The Task Server owns project access grants, capacity, drain, selection, and
fencing. A workstation sleep cannot extend its lease. On wake, replay is denied
after the persisted stop-before deadline; the heartbeat stops the worker after
a missed renewal, and a restarted daemon kills a proven expired Windows worker
generation before admitting replacement work. An exact-current re-adoption
still requires Task Server confirmation. No live lease is transferred between
hosts.

## Preview artifact

Preview evidence is an artifact, not a shared filesystem path. A worker may
write `results/preview-url.json` with this shape:

```json
{
  "url": "https://preview.example.test/run/42",
  "reachableFrom": "operator-browser",
  "createdAtUtc": "2026-09-27T08:00:00Z",
  "expiresAtUtc": "2026-09-27T08:30:00Z"
}
```

Before including it in the result manifest, the runner checks the declared
origin and audience, rejects URL credentials, query strings, fragments, local
file URLs, expired links, and lifetimes beyond its configured maximum. A
rejected file is recorded as a partial artifact in `results/deliverables.md`.
The retained artifact records when the URL was expected to work; retaining the
JSON does not keep the preview service alive. The host manager or preview
service must make the declared URL reachable for that audience until expiry.
This reachability is a deployment property, not a Task Server file mount.

## Verification

Portable contract checks:

```sh
dotnet test runner.Tests/AgentRunner.Tests.csproj --filter 'FullyQualifiedName~WorkstationProfileTests|FullyQualifiedName~RemoteTaskRunnerClaimGuardTests|FullyQualifiedName~DurableLeaseAuthorityTests|FullyQualifiedName~WorkerBuildServerHygieneTests'
```

On an enrolled Windows workstation, publish the `win-x64` profile, run
`agent-host.exe --version` and `agent-host.exe --health-check`, then run the
machine-bound workstation test with
`dotnet test runner.Tests/AgentRunner.Tests.csproj --filter 'Category=MachineBound&FullyQualifiedName~WorkstationProfileTests'`.
This includes a real Windows process-tree termination check keyed by PID and
start time.
For deployment acceptance, submit one ordinary task with an unpinned placement,
observe its claim and fenced result on this workstation, drain it, and confirm
the next eligible task is claimed by a second capable host. Suspend the
workstation past its stop-before deadline during an isolated test attempt and
confirm that no stale report settles. These live host and routing checks are
required on the target installation; a Linux build cannot prove them.
