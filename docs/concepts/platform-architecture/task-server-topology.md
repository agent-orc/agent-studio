---
id: platform-architecture-task-server-topology
title: "Task Server topology: processes, planes, and the cutover state"
status: active
category: concept
updatedAt: 2026-08-17
last-updated: 2026-08-17
reason: "Consolidate the scattered task-server topology material into one architecture page while the AGT-2663 cutover is in progress"
taskKey: AGT-2671
tags: [task-server, topology, distributed, api-plane, git-sync, migration]
related-tasks: [AGT-2663]
related-adrs: []
related-docs:
  - "docs/concepts/platform-architecture/README.md"
  - "docs/concepts/distributed-agent-studio-target-architecture.md"
  - "docs/operations/remote-task-server-local-studio.md"
  - "docs/system/domains/tasks.md"
---

# Task Server topology

> **Source:** no single dossier. This page consolidates
> [distributed-agent-studio-target-architecture.md](../distributed-agent-studio-target-architecture.md),
> [verteilte-task-server-git-sync.md](../verteilte-task-server-git-sync.md),
> [remote-task-server-local-studio.md](../../operations/remote-task-server-local-studio.md)
> and the `task-server/` sources.
> Index: [Platform architecture](README.md).

The AGT-2663 cutover to the standalone Task Server is **in progress** (lane
`3-progress`), so this page separates what runs today from what the cutover
still has to move. Every path and route below was verified against the working
tree.

## Purpose

Agent Studio for Software is designed as three independently deployable
runtimes: a replaceable human surface, an always-on control plane and an
execution plane. The canonical target is
[distributed-agent-studio-target-architecture.md](../distributed-agent-studio-target-architecture.md).

The repository is part-way through that separation. A standalone `task-server/`
process exists, is feature-complete for its own `/api/v1` surface and is proven
by a sibling-process harness, but the default configuration in this repository
still runs the legacy monolith as the live task authority. This page states
which process owns what right now, how the switch between the two shapes works,
and what is left.

## Process topology

| Process | Source | Default listen | Role today |
|---|---|---|---|
| Studio frontend | `frontend/` | Angular dev server, same-origin `/api` and `/hubs` | Human surface. Consumes the broad legacy `/api/**` surface, not `/api/v1`. |
| Studio backend (OrchestratorApi, the monolith) | `backend/`, composition root `backend/Host/EndpointMapping.cs` | `http://localhost:5030` | Live task authority today: task folders, lanes, review decision, post-processing, SignalR hub `/hubs/jobs`. Also hosts the interim `/api/v1` review plane. |
| Standalone Task Server | `task-server/`, entry `task-server/Program.cs` | `http://127.0.0.1:5071`; `http://127.0.0.1:5031` under `TASK_SERVER_PROFILE=local-compatibility` | Durable control plane and system of record on SQLite. Not the live authority in the default repo configuration. |
| Studio BFF | `studio-bff/Program.cs` | `http://127.0.0.1:5072`, upstream `TaskServer:BaseUrl` `http://127.0.0.1:5071` | Stateless same-origin proxy. Forwards `/api/v1/{**path}` and serves `/healthz`. It does not forward `/api/**` or `/hubs`. |
| Orchestrator Engine | `orchestrator-engine/` | API client only | API-only flow executor. Claims server-owned orchestration runs. Owns no task files and no checkout. |
| Agent Runner (`agent-host`) | `runner/`, client `runner/TaskServerClient.cs` | outbound only | Execution plane. Registers as exactly one `coding` or `review` service identity, owns worktrees, CLI processes and its durable outbox. |

The Runner never accepts inbound task mutations and browsers never talk to a
Runner. All Runner traffic is outbound to whichever server it is pointed at.

## State ownership

| State | Owner today | Storage |
|---|---|---|
| Cards, lanes, task metadata | Studio backend | `<TaskRepository>/projects/<projectKey>/tasks/<lane>/<KEY>/`, `task.json` per card |
| Project and workspace registry | Studio backend | `<TaskRepository>/.metadata/projects.json`, `workspaces.json`, fail-closed on load errors |
| Attempt authority, leases, fences (local profile) | Studio backend `AttemptAuthorityService` | under `<TaskRepository>/.metadata/` |
| Tasks, runs, leases, fences, events, artifacts, audit, orchestration runs (standalone profile) | Task Server | SQLite under `STORE_PATH`, tables created in `task-server/TaskServerStore.cs` (`tasks`, `runs`, `leases`, `fence_counters`, `events`, `artifacts`, `result_handoffs`, `result_ref_gc`, `orchestration_runs`, and others) |
| Orchestrator chat contexts | Switched at composition time, see below | local JSONL, or Task Server `orchestrator_contexts` and `orchestrator_context_turns` |
| Repository content, worktrees, result commits | Agent Runner host | Git checkouts plus `$RUNNER_WORKDIR/outbox/<run-attempt-id>/` |
| Backups | Task Server | `BACKUP_PATH`, default `<STORE_PATH>/backups`, SHA-256 verified |

There is exactly one writer for any logical workspace. The migration runbook in
[remote-task-server-local-studio.md](../../operations/remote-task-server-local-studio.md)
states the invariant plainly: at no time may both the legacy writer and the
Task Server accept mutations for the same workspace.

## The plane switch

Three independent switches decide the shape of a deployment. None of them
defaults to the standalone topology.

### 1. Local v1 versus proxied plane

`backend/Host/EndpointMapping.cs` derives one boolean and uses it as an
exclusive ownership branch:

```csharp
internal static bool MapsLocalV1(IConfiguration configuration)
    => !TaskServerPlaneProxy.IsConfigured(configuration);
```

`TaskServerPlaneProxy.IsConfigured` (`backend/Host/TaskServerPlaneProxy.cs`) is
true only when `TaskServer:BaseUrl` parses as an absolute `http` or `https`
URI.

| Configuration | `/api/v1` owner | Local mounts |
|---|---|---|
| `TaskServer:BaseUrl` set | Transparent proxy `MapMethods("/api/v1/{**path}", ...)` to the standalone origin | `MapV1ReviewPlaneEndpoints` and `MapManagementEndpoints` are not mapped |
| `TaskServer:BaseUrl` unset (repo default) | Monolith | `MapV1ReviewPlaneEndpoints` plus local `/api/v1/management` |

`backend/appsettings.json` contains no `TaskServer` section, so the checked-in
default is the local-v1 profile. The proxy injects
`X-Task-Protocol-Version`, `X-Task-Client-Version` and a bearer read from
`TaskServer:AuthToken` or `TaskServer:AuthTokenFile`, and answers an
unreachable upstream with HTTP 502 and code `task-server-unavailable`.

The same flag steers three other seams:

- `backend/Features/Security/AccessSecurityMiddleware.cs` passes an
  `Authorization` header on `/api/v1/` straight through when the proxy is
  configured, so the upstream stays authoritative.
- `backend/Features/Clients/ClientIdentityMiddleware.cs` treats the whole
  `/api/v1/` prefix as externally owned when proxied, and otherwise only
  `/api/v1/protocol`, `/runners`, `/runs`, `/reviews`.
- `backend/Host/Program.cs` binds `IOrchestratorChatPersistence` to
  `TaskServerOrchestratorChatPersistence` when proxied, and to
  `LocalOrchestratorChatPersistence` otherwise.

### 2. Runner plane negotiation

The Runner does not read the topology from configuration.
`runner/TaskServerClient.cs` posts `/api/v1/protocol/compatibility` and reads
the advertised server capability list:

| Server | Advertised capabilities | Effect |
|---|---|---|
| Standalone Task Server (`task-server/TaskServerStore.cs`) | `coding-plane`, `review-plane`, `orchestration-plane`, `host-orchestrator`, `management-plane` | Coding and review runners both use `/api/v1` |
| Monolith compat mount (`backend/Features/Runner/V1ReviewPlaneEndpoints.cs`, server id `orchestrator-monolith`) | `review-plane`, `capability-advertisement` | Review runners use `/api/v1`, coding runners fall back to the legacy `/api/runner/*` plane |
| No `/api/v1/protocol/compatibility` at all | HTTP 404 | Full legacy fallback |

Protocol constants live in `contracts/TaskServer.Contracts/ProtocolContracts.cs`:
current 2, supported range 1 to 2, headers `X-Task-Protocol-Version` and
`X-Task-Client-Version`, client kinds `studio`, `runner`, `review-runner`,
`engine`, `management`. An unsupported version returns HTTP 426 before any
mutation.

### 3. Orchestration execution mode

`backend/Features/Runner/OrchestrationExecutionMode.cs` accepts exactly
`Monolith` or `Engine` from `Orchestration:ExecutionMode` and defaults to
`Monolith`. `Engine` omits the legacy review and post-processing hosted
services from the monolith so the external `orchestrator-engine` owns those
loops instead.

## API planes and routes

### Standalone Task Server, `task-server/TaskServerEndpoints.cs`

| Group | Routes |
|---|---|
| Liveness | `GET /healthz`, `GET /readyz` |
| Protocol | `GET /api/v1/protocol`, `POST /api/v1/protocol/compatibility` |
| Resources | `GET|POST /api/v1/workspaces`, `GET|POST /api/v1/projects`, `GET|POST /api/v1/projects/{projectId}/tasks`, `GET|PUT /api/v1/projects/{projectId}/tasks/{taskIdentity}`, `GET .../attempts`, `GET .../history?after=` |
| Orchestrator contexts | `GET|PUT|POST /api/v1/orchestrator-contexts/projects/{projectIdentity}[/tasks/{taskIdentity}][/turns]`, `POST .../legacy-import` |
| Hosts | `GET|PUT /api/v1/hosts/{hostId}/runtime-capacity`, `GET|PUT /api/v1/hosts/{hostId}/project-policy` |
| Runners | `GET /api/v1/runners`, `PUT /api/v1/runners/{runnerId}`, `PUT .../capabilities`, `POST .../capability-failures`, `POST .../reports`, `POST .../claims`, `PUT .../outbox-status`, `POST .../review-claims` |
| Permits | `POST /api/v1/work-permits/{permitId}/accept` |
| Runs | `POST /api/v1/runs/{runId}/reconcile`, `POST .../post-steps/{stepExecutionId}/claim`, `.../complete`, `POST .../lease/renew`, `.../lease/release`, `POST .../result-finalization`, `POST .../completion`, `GET|PUT .../result-handoff`, `GET|POST .../events`, `GET|POST .../artifacts`, `GET .../artifacts/{artifactId}/content` |
| Reviews | `POST /api/v1/reviews/subjects`, `GET /api/v1/reviews/subjects/{subjectId}`, `GET /api/v1/reviews/attempts/{attemptId}`, `POST .../lease/renew`, `.../report`, `.../cleanup` |
| Orchestration | `GET|PUT /api/v1/orchestration/projects/{projectId}/flow-definition`, `GET|POST /api/v1/orchestration/runs`, `GET /api/v1/orchestration/runs/{runId}`, `POST /api/v1/orchestration/claims`, `POST .../lease/renew`, `.../lease/release`, `.../stages/complete` |
| Management | `GET /api/v1/management/status`, `/outboxes`, `/hosts`, `/audit`, `/invariants`, `/remote-hosts`, `PUT /mode`, `POST /prepare-shutdown`, `/backups`, `/restore`, `/attempts/{runId}/resolve-unknown`, `/remote-hosts/{hostId}/operator-drain`, `/remote-hosts/{hostId}/automatic-drain/clear`, `/migrations/legacy/inventory`, `/migrations/legacy/import` |

Modes are `Normal`, `Draining`, `ReadOnly`, `Maintenance`
(`contracts/TaskServer.Contracts/ManagementContracts.cs`).

### Monolith interim v1 mount, `backend/Features/Runner/V1ReviewPlaneEndpoints.cs`

`GET /api/v1/protocol`, `POST /api/v1/protocol/compatibility`,
`PUT /api/v1/runners/{runnerId}`,
`POST|PUT /api/v1/runners/{runnerId}/capabilities`,
`POST /api/v1/runners/{runnerId}/capability-failures`,
`PUT /api/v1/runners/{runnerId}/outbox-status`,
`POST /api/v1/runners/{runnerId}/review-claims`,
`GET /api/v1/reviews/attempts/{attemptId}`, `POST .../lease/renew`,
`.../report`, `.../cleanup`, `GET /api/v1/runs/{runId}/result-handoff`.

This mount is a compatibility adapter, not a second store. It translates the
published contracts onto the monolith's `AttemptAuthorityService` and task
folders, and its file header states the rule: the monolith remains the single
task and AttemptAuthority writer.

The monolith additionally maps `/recovery` and
`/api/v1/management/{status,diagnostics,remote-hosts,commands}` plus
`POST /api/v1/management/remote-hosts/provider-auth` in
`backend/Features/Management/ManagementEndpoints.cs`, again only in the
local-v1 branch.

### Legacy planes still in use

- Coding runner plane: `/api/runner/lease/acquire`, `/lease/renew`,
  `/lease/release`, `/claim`, `/logs`, `/events`, `/artifacts`, `/completion`,
  `/project-chat/{claim,renew,complete}`, `/epic-planning-prompt`,
  `/queue-starvation`; client registration at `/api/clients/register` and
  `/api/clients/{clientId}`.
- Studio plane: the whole `/api/tasks/**`, `/api/projects/**`,
  `/api/workspaces/**`, `/api/epics` and roughly fifty further groups composed
  in `backend/Host/EndpointMapping.cs`, plus the SignalR hub `/hubs/jobs`.

Neither of these has a `/api/v1` equivalent, and `studio-bff` forwards neither.

## Git model

Git is the code and evidence channel. It is not the task truth channel.

| Artifact | Shape | Producer |
|---|---|---|
| Fenced immutable result ref | `refs/heads/agent-studio/results/<runAttemptId>/fence-<n>/<resultSha>` | `contracts/TaskServer.Contracts/FencedGitRefs.cs` |
| Unfenced fallback result ref | `refs/heads/agent-studio/results/<runAttemptId>/<resultSha>` | `runner/GitWorkspace.cs` when no fencing token is held |
| Salvage branch | `runner/<runner-id>/<task-key>` | Runner worktree teardown |
| Collision ref | `runner/<runner-id>/<task-key>-collision-<localSha>-<remoteSha>` | Runner, on divergence, never force-pushed |
| Quarantine ref | `agent-studio/quarantine/...` | Runner, for lease-loss and unattributed crash debris |

Delivery is proven, not assumed: the Runner verifies the published ref through
`git ls-remote` against the registered repository URL before completion, and
the Task Server accepts a result handoff only when the ref name matches the
current run, fence and result SHA. Result refs are garbage collected by
`task-server/ResultRefGc.cs`, which spares the current attempt, non-accepted
cards, active or non-terminal reviews, and anything inside the retention window
(`ResultRetentionDays` 30, `ResultRefGcSweepMinutes` 360).

Separately, the **Transition-Committer** commits workspace evidence into the
`<TaskRepository>` Git repository after lane transitions. It is configured
under `WorkspaceEvidence` in `backend/appsettings.json` (`Enabled: true`,
`DebounceSeconds: 15`, `MaxDelaySeconds: 60`, `Push: false`) and implemented in
`backend/Features/Pipeline/WorkspaceEvidence{Batcher,Queue,Worker}.cs`. It
commits locally by default and does not push, so it currently delivers the
audit trail without any cross-host replication.

The evaluation in
[verteilte-task-server-git-sync.md](../verteilte-task-server-git-sync.md)
(concept, 2026-07-19) is explicit that this is deliberate. Git is accepted as
the evidence and replication layer and rejected as the primary control channel:
lane transitions are folder renames, which a three-way text merge cannot
resolve, and Git provides neither ordering, exclusivity nor a push signal.
Control stays on HTTP with leases and fences, as described in
[fencing, leases, and authority](fencing-leases-and-authority.md).

## Migration state

### Delivered

- The standalone `task-server` process exists with its own SQLite store, schema
  migrations, `/readyz` authority gate, backup and restore with SHA-256
  verification, the four operating modes, and the full `/api/v1` surface listed
  above.
- `studio-bff` exists as a stateless `/api/v1` proxy with optional TLS
  certificate pinning through `TaskServer:TlsServerCertificateSha256`.
- `orchestrator-engine` exists and drives flows exclusively through
  `/api/v1/orchestration/*`.
- `TaskServerPlaneProxy` and the exclusive `MapsLocalV1` ownership branch are
  in place, so a single configuration value moves `/api/v1` ownership without
  code changes.
- Remote Review is live on the versioned plane. Review runners already
  negotiate `review-plane` against the monolith compat mount and run the full
  claim, renew, report, cleanup lifecycle there.
- Protocol negotiation, HTTP 426 rejection, and the release-blocking
  sibling-process harness `task-server.Tests/TopologyTests.cs`.
- Systemd packaging and the backup timer under `deploy/systemd/`, plus the
  `task-server backup --name timer` command path.
- Local Transition-Committer evidence commits and result-ref garbage
  collection.

### Open: what AGT-2663 still has to move

- **The default is still local-v1.** `backend/appsettings.json` sets no
  `TaskServer:BaseUrl`, so in this repository the monolith owns `/api/v1`,
  holds attempt authority and writes task folders. The standalone server is not
  the live authority.
- **The coding plane has not moved.** The monolith advertises only
  `review-plane` and `capability-advertisement`, so coding runners still claim,
  lease and complete over `/api/runner/*`.
- **Orchestration still defaults to `Monolith`.**
  `Orchestration:ExecutionMode` must be set to `Engine` for the external Engine
  to own the review, council, post-processing, gate and completion loops.
- **The Studio surface is not on the versioned plane.** Angular consumes the
  broad legacy `/api/**` plus `/hubs/jobs`. `studio-bff` covers neither.
  Classifying every Studio route as Task Server, local dev-seat helper, or
  retired is slice B1 and is the largest open estimate.
- **The migrator reads the wrong file.**
  `task-server/LegacyMigrationService.cs` enumerates `job.json`, while the
  active backend writes `task.json` (`backend/Features/Tasks/TaskJsonFile.cs`).
  A production inventory would report a false zero. This is a hard stop for any
  real import.
- **Credentials are not yet scoped.** The packaged install defaults to one
  shared bearer through `AUTH=bearer`. The interim alternative
  (`TaskServer:RequireAuthentication` with separate `StudioBearerToken` and
  `RunnerBearerToken`) is a transition, not the target of distinct hash-only
  per-principal credentials with route scopes.
- **No Windows fallback artifact.** The documented control-plane release
  profile is `linux-x64`, so the move is not yet reversible in the sense the
  rollback drill requires.
- **The local connector is incomplete.** It needs full `/api` and `/hubs`
  forwarding, secret-file integration, strict Origin checks, CSRF and an atomic
  remote or local upstream switch before any listener is opened.

Phase B slices B1 to B6 in
[remote-task-server-local-studio.md](../../operations/remote-task-server-local-studio.md)
carry these items, estimated at 18 to 30 engineering days plus operator time,
and remain subject to operator approval. Deployment of the dedicated host,
WireGuard transport and the production cutover window are separate tasks after
that approval.

## Related documents

- [Distributed Agent Studio target architecture](../distributed-agent-studio-target-architecture.md)
- [Task Server deployment and recovery](../../operations/setup/task-server.md)
- [Networked Task Server](../../operations/setup/networked-task-server.md)
- [Remote Task Server with local Agent Studio](../../operations/remote-task-server-local-studio.md)
- [Distributed Task Servers with Git as sync transport](../verteilte-task-server-git-sync.md)
- [Tasks domain](../../system/domains/tasks.md), [Runner domain](../../system/domains/runner.md)

## Living knowledge log

- **2026-08-17 (AGT-2671):** Page created by the Dossier curation sweep, which
  found the task-server topology spread across four documents and the sources
  with no single entry point. Extraction surfaced one defect worth a card:
  `task-server/LegacyMigrationService.cs` enumerates `job.json` while the
  backend writes `task.json`, so the legacy inventory reports a false zero.
