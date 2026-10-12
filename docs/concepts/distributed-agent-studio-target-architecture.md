# Distributed Agent Studio target architecture

Status: canonical target picture, amended by ADR-0078 on 2026-10-11. Start with the
[North star](../operations/nordstern/index.html) for the complete target and delivery map.
This page defines the intended separation of Studio, Task Server, Orchestrator Engine,
Runner hosts, and Operations Server. It is a product
and architecture target, not a claim that every boundary already ships.

This page is the coordinating source for the implementation tasks listed in
[Delivery map](#12-delivery-map). Detailed concepts remain authoritative inside
their narrower scopes, but they must not contradict the lifecycle, authority,
security, or workspace-organization decisions here.

## 1. Decision summary

Agent Studio for Software is one product made from five runtime roles:

1. **Agent Studio** is the replaceable human surface. It is primarily the
   Angular application and, where needed, a thin surface-specific BFF. Closing
   it must not stop work.
2. **Task Server** is the always-on authority and durable system of record.
   It owns users, workspaces and project identity, tasks, orchestration
   state, leases, events, artifacts, audit, and management APIs. It validates
   versioned engine intents and grants fenced claims; it does not choose the next step.
3. **Orchestrator Engine** is the only decider for transitions between steps,
   planners and advisors. It reads bounded facts, chooses typed actions, and
   submits versioned intents and decision receipts to the Task Server. It does
   not hold task truth or command a runner directly.
4. **Agent Runner** is the execution plane. It owns host capabilities, code
   checkouts, coding-agent processes, tool processes, containment, and bounded
   delivery of typed events and artifacts to the Task Server.
5. **Operations Server** brokers bounded executable host and developer-seat
   operations through outbound Operations Agents. It accepts service principals,
   not browser sessions. Task Server remains the authority for task-linked permits
   and all orchestration sessions. Studio edges hold independent upstream credentials.
   Documentation and UI call this role the **Operations Broker**: it brokers
   host operations and is not a message broker. Code identifiers, images,
   routes, and contracts retain **Operations Server**.

**Glossary: Operations Broker.** The documentation and UI name for the
Operations Server role. It brokers host operations and is not a message broker;
code identifiers, images, routes and contracts keep **Operations Server**.

Shared contracts and orchestration libraries remain modules, not runtime roles.
[ADR-0078](../system/architecture/decisions/adr-archive.md#adr-0078---engine-placement-and-execution-rule-2026-10-11)
amends the earlier role and decision placement. The selected standard is one-box
Docker after full route and security parity. The initial service and read-only
agent do not yet establish that parity. See the
[Operations contract and remaining gates](../system/contracts/operations-backchannel.md).

The task/run topology below shows the authority and claim path. The separate operations path is
`frontend backend -> Operations Server <- outbound Operations Agent`, with
Task Server permit introspection on admission, dispatch, renewal and results.

The release-defining scenario is:

> Start Task Server and Agent Runner as independently supervised services.
> Start a task from Agent Studio, close Agent Studio completely, and prove that
> the task, its post-processing, and its durable state continue. Reopen Agent
> Studio, or open another authorized client, and observe the same task history.

The current requirement is **not** autonomous operation after the Task Server
has disappeared. A short transport interruption may be buffered. The Runner
must not claim new work, mint authority, or become a second task truth while the
Task Server is unavailable. The isomorphic multi-writer/offline-store direction
explored by AGT-2122 is therefore deferred and is not part of this target.

Claimable execution moves to runner hosts, decisions to the Orchestrator Engine,
and durable authority to the Task Server. The monolith retains none of these
decisions in the finished target.

## 2. One word, four different failure cases

"Offline" is too ambiguous to be an architecture requirement. The product uses
these explicit states instead:

| State | Required behavior |
|---|---|
| **Studio detached** | Fully supported. Task Server and Runner continue. Another Studio or future phone surface can attach. |
| **Runner transport interrupted** | No new claims. Buffer bounded events and artifacts, retry, and continue only within the still-valid server-issued execution authority. Stop safely before authority can no longer be renewed. |
| **Task Server unavailable** | No new task mutations, claims, reissues, or global decisions. Runners fail closed after the bounded renewal window and retain replayable evidence. |
| **Runner unavailable** | Task Server remains readable and manageable. The active attempt is `process-unknown` until fencing and positive no-overlap evidence make reassignment safe. |

"Client off, work continues" is a committed requirement. "Task Server off,
autonomous project continues" is a separate future research question.

## 3. Target topology

```text
 Studio and browser edges ---> Task Server <--- Orchestrator Engine
      human interaction          authority       only decider
      stateless BFF              intents          planners and advisors
                                 leases
                                 fences
                                 permits
                                    ^
                                    | claims, renewals, evidence
                          +---------+---------+
                          |                   |
                     Runner host A       Runner host B
                     coding/review       gate/batch/chat
                          |
                          +--- Git remotes (code, not task truth)

 Studio/BFF ---> Operations Server <--- outbound Operations Agents
                 non-claimable host operations only
```

Browsers never connect directly to a Runner for task mutations. Runners initiate
outbound connections to the Task Server. Git carries code; the Task Server API
carries task authority, events, evidence metadata, and commands.

## 4. Component boundaries

| Component | Owns | Must not own |
|---|---|---|
| **Agent Studio** | Human interaction, board and project views, task authoring, orchestration observation, admin UI, explicit operator commands, local UI preferences | Durable task state, lease authority, CLI process handles, hidden filesystem writes, long-running service liveness |
| **Task Server** | Canonical resources and IDs, Task API, state and accepted transitions, scheduling/admission, users and authorization, service identities, durable leases/fences, permits, event and artifact ingestion, audit, backup/restore metadata, management API | Choosing the next step, starting processes, host toolchains, repository worktrees, Angular component state |
| **Orchestrator Engine** | All transition decisions, planners and advisors, bounded model-backed decisions, versioned intents and decision receipts | Task truth, direct runner commands, host files, self-issued authority |
| **Agent Runner** | Host registration, capability probes, project workspaces, CodingAgentRunner integration, process containment, execution of admitted claimable work, typed output, artifact upload, bounded local spool | Global task namespace, user passwords, arbitrary lane changes, autonomous claims, policy changes, release authority |
| **Operations Server** | Versioned catalogue, scoped commands, bounded attempts, agent capabilities and operational evidence for non-claimable host operations | Pipeline step execution, task mutation, workspace identity, orchestration sessions, permit minting, browser cookies, native execution |
| **Shared contracts** | Versioned resource DTOs, event envelopes, command/result schemas, compatibility rules, deterministic policy primitives | Network hosting, persistence technology, UI projections, OS process ownership |

**Implementation status (2026-09-24).** Local Studio runs and detached Agent
Runner workers now construct and execute coding-agent requests through
CodingAgentRunner. Host adapters retain durability, fencing, output projection,
and process containment, but no longer own a second CLI argv or raw-spawn path.

### Where orchestration runs

The Orchestrator Engine is the only decider for transitions and planners,
including the batch advisor, capacity checks and improvement scouting. A strong
model chooses a typed action from bounded, versioned grounds. Deterministic code
validates authority and safety, enforces finite execution, dispatches the
action through the Task Server and records a receipt. The Task Server owns the
durable orchestration state machine and applies only valid, fenced intents.
The Runner cannot extend budgets or choose another global task. Orchestration
libraries may be shared, but decision ownership stays in the Engine.

A unit admitted by the Task Server as claimable work runs on the runner host
holding its lease. This includes coding, review, gate and batch attempts and
orchestrator chat turns, with their model calls, preparation, builds, tests,
commits and pushes. The Operations Server runs only non-claimable host operations:
developer-seat files and Git actions, lifecycle control, diagnostics and
person- or frontend-requested rendering and capture. Its result never changes
task state by itself. No pipeline step executes through the Operations Server.

Fix rounds continue on the reviewed delivery. One per-card budget counts every
automatic round. At budget end, non-test blocks become concerns with a linked
follow-up card; `build-tests` and `tests-and-evidence` blocks escalate. Review
prefers an eligible executor on another host but never waits for that preference.
Engine instances are interchangeable under per-occurrence leases, with one in
workstation and one-box profiles and two in the distributed profile. There is
no leader election. A project may require review on a different host; without
that opt-in, the review host preference never delays review (AGT-W65 D19 A,
implementation AGT-3069).

### Actors and decision steps

Human Operator and AI Operator are actors with the same Task Server intent
interface and different autonomy classes (AGT-W73 O1 A). The human sets aims
and grants approvals reserved for a person. The AI Operator works within those
bounds. Decision steps answer one bounded question per card or plan, choose a
typed action and leave a receipt. Execution steps do the work on runner hosts.
The Orchestrator Engine runs the AI Operator and decision steps. Escalation runs
from a decision step to the AI Operator to the human.

If the strong model is unavailable or returns an invalid or inadmissible action,
the decision step records `ask(reason=decision-unavailable)` and holds the
transition for human review. One bounded transport retry may use an alternate
strong model within the same capability and price ceiling. Already authorized
safe work may finish; mechanical cleanup and emergency stop remain available.
The configurable emergency run limit defaults to 24 hours and preserves
salvage evidence. Decision calls are capped at 24 per card, 8,000 input and
1,000 output tokens per call, and $5 in priced spend per card. Host capacity
checks have 48 scheduled and 12 event calls per host-day, 180,000 input and
90,000 output tokens, and a $10 priced spend cap. Rules have a versioned
workspace definition, bounded project overlay, dry run and explicit activation.
Grounds packs and authorized drill-downs remain bounded. AGT-3063 to AGT-3068
implement these AGT-W73 decisions and ADR-0078 amends ADR-0032 accordingly.

This split lets post-processing and reissue continue when Agent Studio is
closed without turning every Runner into an independent Task Server.

## 5. Independent lifecycles

### Process forms by deployment profile

The Engine and Operations Server are separate processes in every profile. They
may share a deployment unit but do not share decision or execution authority.

| Profile | Process placement |
|---|---|
| Workstation | All five roles on one developer machine, with a Connector browser edge, one Engine instance and the workstation as an ordinary runner host; loopback transport and a native host manager. |
| One-box Docker | HTTPS edge and BFF, Task Server, one Engine instance, Operations Server and first runner host in one Compose installation; separate Engine and Operations Server containers, an enrolled agent for native host operations, no Docker socket, only the HTTPS edge published. This is the standard after route and security parity. |
| Distributed | Task Server, two interchangeable Engine instances and Operations Server in separate containers, optionally on one control-plane host; Linux, Windows and workstation runner hosts join over private HTTPS on WireGuard. |

When the Engine is unavailable, transitions wait visibly. Neither the Task
Server nor the Operations Server becomes a fallback decider.

### Agent Studio lifecycle

- The web client can open, close, reload, and upgrade without affecting a run.
- It discovers capabilities and server version after login.
- Live views resume from a durable event cursor rather than an in-memory UI
  subscription.
- A thin BFF, if retained, may aggregate view models but cannot become task
  authority or process owner.

### Task Server lifecycle

- Runs as its own service, container, or supervised process with a dedicated
  configuration, data directory, version, health surface, and release cadence.
- Starts and stops independently of Agent Studio and every Runner.
- Owns schema/store migrations, backup, restore, retention, audit, and version
  compatibility.
- Persists lease and fence authority so a service restart does not silently
  admit a duplicate attempt.
- Supports drain/read-only maintenance modes before shutdown or migration.

A server API cannot be the only mechanism that starts its own dead process.
The operating system service manager or container platform owns start, stop,
and restart. The management API owns readiness, drain, maintenance intent,
safe-shutdown preparation, version, migration, and diagnostics.

### Agent Runner lifecycle

- Runs as a system service with its own version and configuration.
- Registers an explicit service identity and reports capabilities and health.
- Survives Studio shutdown because Studio is neither its parent process nor its
  network peer.
- Reconnects to Task Server with bounded backoff and idempotent event replay.
- Drains before upgrade. Existing attempts may finish while new claims stop.
- Never treats a lost heartbeat as proof that another process is dead.

## 6. Disconnect and restart contract

| Scenario | New claims | Current process | Writes and evidence | Recovery proof |
|---|---:|---|---|---|
| Studio closes | Yes | Continues | Normal server writes | Reopen from durable cursor |
| Studio host powers off | Yes | Continues | Normal server writes | Another authorized client sees the same state |
| Runner loses Task Server briefly | No | May continue only inside current lease safety window | Bounded local spool; no unacknowledged global transition is treated as settled | Reconnect, renew, replay idempotently |
| Task Server stays unreachable past renewal boundary | No | Cancel and reap safely | Preserve unsent evidence locally; mark unresolved on reconnect | Fence validation and operator-visible reconciliation |
| Task Server restarts | Paused until authority restored | Does not gain new authority | Durable fence prevents duplicate acceptance | Restart tests restore leases or enter fail-closed quarantine |
| Runner service restarts | No for that runner until healthy | Previous generation is unknown until containment evidence exists | Task Server keeps prior events | Same-boot service proof, changed boot ID, or infrastructure fencing |
| Second Runner requests same task | Server decides | No overlap allowed | Higher-fence traffic cannot overwrite current authority | Durable lease plus positive no-overlap gate |

The Runner spool is an outage buffer, not a local Task API. It stores typed
events, artifact chunks, acknowledgements, and idempotency keys. It does not
store a freely mutable clone of the board.

## 7. Task Server API and management story

### Resource API

The public contract is resource-based and path-free:

```text
/api/v1/workspaces/{workspaceId}
/api/v1/projects/{projectId}
/api/v1/projects/{projectId}/tasks/{taskKey}
/api/v1/runners/{runnerId}
/api/v1/runs/{runId}/events?after={cursor}
```

Absolute client paths and `watchPath` are compatibility inputs only. Public
identity uses stable server, workspace, project, task, run, and runner
IDs. Server-private storage paths and Runner-private checkout paths never cross
the wire as identity.

Commands carry idempotency keys, actor identity, expected resource version,
and, where relevant, execution or control fences. Event streams support cursor
replay and bounded retention.

### Management API

Every Task Server exposes an authenticated management surface for:

- bootstrap state and first-owner creation;
- users, password reset, sessions, roles, and project membership;
- Runner service identities, enrollment, token rotation, revoke, drain, and
  retirement;
- workspace, project, and repository bindings;
- server health, version, protocol compatibility, storage usage, and active
  migrations;
- backup creation, restore verification, retention, archive sweeps, and orphan
  diagnostics;
- audit search and export;
- maintenance mode, read-only mode, and safe shutdown preparation.

The first live contract is rooted at `/api/v1/management`. `GET /status` and
`GET /diagnostics` provide the shared read model. `POST /commands` accepts a
command kind, dry-run flag, exact confirmation, and idempotency key. Applied
commands append durable evidence to the server audit ledger. Backup archives
live outside the active data directory and are hash-checked and opened before
the server reports success. Runner rows are projections of the existing Runner
service-identity registry; this API does not maintain another client list.

### Management consoles

Two surfaces use the same API:

1. **Agent Studio administration** is the rich daily management console.
2. **Task Server bootstrap/recovery console** is a small server-hosted surface
   for first-owner setup, health, credentials, backup/restore, and recovery when
   Agent Studio is unavailable.

Neither console bypasses authorization or writes server files directly.
The server-hosted console is available at `/recovery`. It can establish the
first owner or an authenticated owner session, then uses the same management
routes as Agent Studio. It reports the systemd, container, or service-manager
lifecycle boundary and never offers a fictional self-start operation.

## 8. Security baseline for an internet-reachable server

The product remains deliberately small and single-organization. That does not
make an internet-facing task system safe without real identity boundaries.

### Human identity

- Task Server owns users.
- Initial authentication is username plus password, not enterprise SSO.
- Passwords use a modern memory-hard password hash with unique salts.
- Browser clients use secure, HttpOnly, SameSite session cookies over HTTPS.
  The Angular application never stores a reusable password or long-lived bearer
  token in local storage.
- State-changing browser requests have CSRF protection.
- The first useful roles are `owner`, `operator`, and `viewer`, with project
  membership where needed. A future OIDC provider can replace login without
  changing resource authorization.

### Runner identity

- A Runner never receives a person's password.
- One-time enrollment creates a distinct, revocable service principal.
- The resulting credential is scoped, rotatable, and stored in the host's
  protected service configuration or secret store.
- A run records both the human or automation principal that requested the work
  and the Runner service principal that executed it. "Runs in a user's context"
  means auditable delegated authority, not shared credentials.
- Repository deploy keys and coding-agent CLI credentials remain Runner-host
  secrets and are never returned to Studio or Task Server logs.

### Transport and operations

- HTTPS is mandatory before the Task Server is reachable beyond loopback or an
  SSH-only development tunnel. WebSocket/event endpoints use the same origin
  and authentication.
- A reverse proxy or ingress owns certificates, modern TLS, request limits, and
  secure headers. Plain HTTP may exist only on a private loopback hop.
- Login and enrollment endpoints are rate-limited and audited.
- Secrets are redacted from task output, diagnostics, and audit records.
- Backup and restore procedures include identity, task state, event history,
  artifacts, audit, and fence continuity.

`X-Client-Id` remains useful attribution. It is not authentication.

### Principal and data flow

```text
Browser
  username/password once
  <- Secure HttpOnly session cookie
  -> same-origin /api and /hubs with CSRF on mutations

Owner
  -> one-time enrollment code
Runner
  enrollment code once
  <- one-time service bearer
  -> outbound HTTPS claim, lease, logs/events, artifacts, completion

Task Server
  password hashes, session hashes, Runner secret hashes
  task state, fenced leases, dual-principal run audit
```

### Lifecycle matrix

| Identity or credential | Created by | Plaintext visibility | Normal use | Expiry | Rotation or reset | Revocation |
|---|---|---|---|---|---|---|
| First owner | Anonymous one-time bootstrap over HTTPS, only while no users exist | Password exists only in the browser request | Login and owner administration | Account does not auto-expire | Owner changes password | Another owner disables account; final active owner is protected |
| Human password | Owner at user creation or reset; user on change | Creation/reset response only for temporary value | Login only | Policy-driven, no scheduled expiry in the small-org baseline | Change verifies current password; reset revokes sessions and forces change | Disable user and revoke sessions |
| Human session | Task Server after successful login | Cookie only; HttpOnly prevents Angular access | Same-origin Studio API and hubs | Sliding idle and absolute deadline | New login creates a new independent session | Logout, password reset, disable, or expiry |
| CSRF value | Task Server with session | Secure SameSite cookie readable by Angular | Header on browser mutations | Session lifetime | Reissued with a new session | Invalid when its session ends |
| Runner enrollment code | Owner | One creation response and operator handoff | One enrollment call | Default 15 minutes, maximum 24 hours | Create another code | Single use or expiry |
| Runner credential | Task Server during enrollment or rotation | One response only, then Runner secret store | Only endpoints allowed by explicit scopes | Optional credential deadline | Add overlapping credential, prove daemon, revoke old credential | Individual credential or whole Runner identity |
| Fenced run lease | Task Server after scoped claim/acquire | Authenticated Runner response | Heartbeat and completion for one task | Short TTL renewed by heartbeat | New holder increments fencing token | Release, expiry, or fenced takeover |

### Authorization matrix

| Capability | Owner | Operator | Viewer | Runner |
|---|---:|---:|---:|---:|
| Read allowed projects | Yes | Yes | Yes | Claimed task input only |
| Mutate allowed projects | Yes | Yes | No | Scoped run protocol only |
| Manage users and Runner identities | Yes | No | No | No |
| Connect Studio SignalR stream | Yes | Yes | Yes | No |
| Claim and execute work | No | No | No | Required per-route scope |

Detailed controls live in
[security requirements](../operations/security/requirements.md), and the
operator deployment is
[networked Task Server](../operations/setup/networked-task-server.md).

## 9. Organizing one large product

Recursive subprojects are not the recommended model. They mix navigation,
authorization, configuration inheritance, repository layout, and delivery
ownership into an unbounded tree.

Use three explicit planning levels instead:

| Level | Purpose | Example |
|---|---|---|
| **Workspace / solution** | Groups component projects that form one product, and owns their shared defaults and access policy | `Agent Studio for Software` |
| **Component project** | Own backlog, task key, lifecycle, release/deployment target, Runner assignment, and health | `Agent Studio`, `Task Server`, `Agent Runner` |
| **Task** | One owned change in exactly one component project | `TS-24 Extract durable lease store` |

A cross-component **initiative** or epic groups tasks from several component
projects. It does not own execution settings and does not pretend that all work
belongs to one board project. A future portfolio view may aggregate several
workspaces, but it is not another configuration-inheritance level.

### Board behavior

- A component board shows only its own tasks by default.
- A workspace board is an aggregate solution view over its component projects.
- Solution-wide initiatives, dependencies, and milestones are visible without
  copying cards.
- The selected scope is explicit: workspace, initiative, or component
  project.
- Aggregate counts equal the visible child scopes.

### Project is not repository

A component project is a planning and deployment boundary, not a synonym for a
Git repository. Repository binding is separate:

```text
ComponentProject
  -> one or more RepositoryBinding records
     { repositoryId, optional subpath, branch model, execution role }
```

During extraction, Agent Studio, Task Server, and Agent Runner may share one
monorepo while already having separate projects and release lifecycles. Later
they may move to separate repositories without changing task identity or the
workspace/component hierarchy.

For execution safety, one task initially selects exactly one primary repository
binding. Multi-repository tasks require an explicit run-plan contract and are
not inferred from workspace membership.

This supersedes the strict `Project 1 -> 1 Git repository` assumption in the
earlier Project Relationship Model / Branch-Aware Wiki concept (since retired;
this page absorbed it). The branch and provenance rules remain valuable; only
repository cardinality
and project identity need revision.

## 10. Code and release organization

The runtime split should be visible in source before repositories are split:

```text
Agent Studio solution
  studio-web/            Angular surface and generated API clients
  studio-bff/            optional surface aggregation only
  task-server/           Task API, control plane, persistence, auth, management
  agent-runner/          daemon, host integration, execution adapter
  contracts/             versioned wire contracts and compatibility fixtures
  orchestration-core/    deterministic state and policy primitives
```

CodingAgentRunner remains the process and CLI-protocol library used by
`agent-runner`. The standalone Runner must not recreate a second unstructured
Codex invocation path.

That boundary is now implemented in both deployables with the exact NuGet pin
`CodingAgentRunner [0.7.0]`. The backend and Runner project files are the two
consumers of that one package version; an architecture test rejects drift. See
[ADR-0077](../system/architecture/decisions/adr-archive.md#adr-0077---studio-uses-codingagentrunner-as-its-only-card-run-cli-execution-layer-2026-09-24).

Each deployable publishes its own version and compatibility range. Contract
tests pin supported combinations. Release order is additive first: Task Server
accepts both old and new clients, then Runners and Studio upgrade, then obsolete
protocols are removed after telemetry proves they are unused.

Repository extraction is optional and follows operational need. Independent
service lifecycle, board ownership, versioning, tests, and deployment do not
wait for a repository split.

## 11. Acceptance and failure tests

The target needs a topology harness, not only unit tests. It starts each runtime
as a separate process with separate configuration and data directories.

The required test program is distributed across the delivery tasks. Owner tags
below identify the task that must supply the scenario and evidence. AGT-2196
owns the cross-process harness and composes the runtime scenarios; it does not
absorb the management-console or project-model implementations.

Required scenarios:

1. **Client-off golden path (AGT-2196):** start a real task, stop Agent Studio
   and its BFF, observe Runner and Task Server complete the lifecycle, then
   reconnect and replay the full history.
2. **Task Server process independence (AGT-2192, AGT-2196):** restart Studio
   repeatedly without a Task Server restart. Restart Task Server without
   accidentally parenting or killing Runner processes.
3. **Runner disconnect (AGT-2183, AGT-2196):** interrupt transport, prove no new
   claim, bounded spool, renewal safety stop, reconnect, and idempotent replay.
4. **Durable fencing (AGT-2182, AGT-2196):** restart Task Server during an active
   attempt and prove no second Runner overlaps and stale completion cannot win.
5. **Runner crash (AGT-2182, AGT-2185, AGT-2196):** kill the Runner service and
   its host process group, verify honest `process-unknown`, containment proof,
   and safe recovery.
6. **Authentication (AGT-2193, AGT-2196):** unauthenticated reads and mutations
   fail; viewer cannot mutate; revoked Runner cannot renew; password/session
   and CSRF behavior pass.
7. **TLS deployment (AGT-2193, AGT-2196):** Studio and Runner connect through
   the real HTTPS origin, including event-stream upgrade and certificate
   renewal rehearsal.
8. **Management recovery (AGT-2194):** bootstrap, backup, restore verification,
   drain, maintenance mode, and credential rotation work without filesystem
   edits.
9. **Compatibility (AGT-2192, AGT-2170, AGT-2196):** supported mixed versions
   work; unsupported protocol versions fail before a task is claimed.
10. **Project scope (AGT-2195):** workspace aggregate and three component boards
    show the same cards without duplication; cross-project initiative and
    dependency links resolve.

### Release proof

AGT-2196 supplies the release-blocking cross-process proof in
[`task-server.Tests/TopologyTests.cs`](../../task-server.Tests/TopologyTests.cs).
The harness launches the built Task Server, Studio BFF, and Agent Runner
assemblies as sibling OS processes with isolated ports, data roots, Runner
workspaces, and configuration. It uses a real local Git remote and a bounded
fixture agent process. The fixture is a deterministic CLI participant, not a
second task store or scheduler.

The CI gate runs four real-process scenarios:

1. The client-off golden path starts a task through Studio, stops and restarts
   the BFF three times, completes while detached into the deployed backend's
   auto-review handoff, then reconnects from a fresh BFF and replays the
   canonical task history.
2. The brief transport path interrupts only Runner-to-server connectivity,
   keeps a second task unclaimed, retains bounded output, then reconnects and
   proves typed event replay is idempotent.
3. The outage path holds Task Server unavailable beyond the Runner renewal
   safety boundary, observes cancellation, restarts authority into
   `process-unknown`, rejects a contender, kills the old Runner, records
   positive no-overlap proof, and admits one higher-fence replacement.
4. The network path runs Task Server over real HTTPS with separate Studio and
   Runner bearer credentials, proves anonymous history and event reads fail,
   and proves authenticated Runner ingestion plus Studio cursor replay.

Canonical replay is
`GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/history?after={cursor}`.
It returns stable task and run identities, server-sequenced typed events,
artifact metadata, task/run audit rows, and the last returned cursor. The
Runner maps plain stdout to `agent.message`, structured tool or command frames
to `tool.trace`, and Runner-owned diagnostics to bounded trace event kinds.
Task Server adds typed completion and post-processing evidence while the
deployed backend remains the review and reissue authority. Runner
disconnect/reconnect, Task Server unavailable, `process-unknown`, Runner
unavailable, and no-overlap events keep the failure classes distinct in replay.
Idempotency keys remain unique per run and payload.

The executable operator command and release decision rule are in the
[stable release contract](../operations/stable-release-contract.md#three-component-topology-gate).
Protocol fixtures remain the supported mixed-version source of truth. The same
CI step runs them and rejects an unsupported protocol with HTTP 426 before
registration or claim.

## 12. Delivery map

This table is the human-readable synchronization point. Every new task created
from this target must link this page in its prompt. This page links the stable
task key back. Task status remains authoritative on the board.

Until the workspace/component model exists, all delivery slices stay in the
current Agent Studio project and AGT-2129 epic. AGT-2195 owns the migration and
key-alias decision. Do not duplicate or renumber these tasks in the meantime.

| Area | Existing or planned task | Relationship to target |
|---|---|---|
| Decoupled UI and session lifecycles | AGT-2091 | Supplies detach/reattach and process-ownership rules. |
| Remote daemon and persistent connectivity | AGT-2004, AGT-2005 | Existing Runner service and connectivity foundation. |
| Remote host registry and management | AGT-1921, AGT-2094 | Existing host management and onboarding slices. |
| Task Server management UI and URL onboarding | AGT-1924 | Existing management surface; must consume the separated server API. |
| Remote execution hardening | AGT-2129 | Execution-plane epic. Its former "everything on Runner" wording is narrowed by this target. |
| Autonomous/isomorphic-store exploration | AGT-2122 | Historical exploration. Superseded for the current requirement by the fail-closed Task Server authority here. |
| Typed Runner event integration | AGT-2170 | Prevents raw CLI transport from leaking into host-specific parsers. |
| Standalone Task Server extraction | AGT-2192 | New control-plane service and migration boundary. |
| Network security and identity baseline | AGT-2193 | Human login, Runner service identity, TLS, authorization, and audit. |
| Live management API and recovery console | AGT-2194 | Replaces AGT-1924 seed/simulation with an authenticated server contract. |
| Workspace/component project model | AGT-2195 | Solution workspace, component boards, initiatives, and repository bindings. |
| Client-off topology acceptance harness | AGT-2196 | Release-blocking proof of independent lifecycles. |

## 13. Delivery sequence

1. **Freeze the authority model.** Record Task Server control-plane ownership,
   fail-closed disconnect semantics, and the workspace/component hierarchy.
2. **Make public identity path-free.** Finish stable resource IDs and remove
   client paths from new contracts.
3. **Land security before exposure.** Replace localhost-only requirements with
   the human and service identity model, then verify HTTPS deployment.
4. **Extract Task Server.** Give it an independent process, persistence,
   migrations, health, backup/restore, and management API while retaining local
   compatibility.
5. **Connect Agent Studio only by API.** Remove in-process task-store and Runner
   ownership from the surface runtime.
6. **Align Agent Runner with shared contracts.** Use CodingAgentRunner structured
   events, fenced commands, typed outcomes, and bounded replay. **Implemented:**
   CAR owns every card-run CLI launch; the Runner retains detached-worker
   durability and reattaches to the worker process rather than to CAR internals.
7. **Pass the client-off golden path.** This is the first complete target
   milestone.
8. **Add workspace/component organization.** Split boards and lifecycles without
   requiring a repository split.
9. **Operate on the public network.** Complete certificate, credential,
   monitoring, rotation, backup, and recovery rehearsals before declaring the
   central URL stable.

## 14. Explicit non-goals

- Autonomous multi-task operation while Task Server is unavailable.
- A second mutable Task API or board store on each Runner.
- Recursive subprojects with inherited settings and permissions.
- Enterprise SSO, multi-tenant billing, or a general workflow engine.
- Browser-to-Runner task mutations.
- Using a user's password as a Runner credential.
- Treating lease expiry alone as proof that an old process is dead.
- Requiring a repository split before service and project lifecycles separate.
- Making Agent Studio or its BFF an always-on dependency of execution.

## 15. Open decisions with recommended defaults

| Decision | Recommended default |
|---|---|
| First Task Server host | A small independently backed-up VM or service host, not the operator workstation. |
| Human auth | Local username/password accounts with secure server sessions; OIDC is a later adapter. |
| Runner credential | One-time enrollment followed by a rotatable scoped service credential. |
| Studio BFF | Optional and stateless; never authority. Start co-hosted, preserve a removable boundary. |
| Solution hierarchy | One solution Workspace -> Component Project, with cross-project initiatives and no recursive project tree. |
| Repository layout | Keep the monorepo until independent release or ownership pressure justifies extraction. |
| Task Server outage | No new work; bounded spool and fail-closed authority stop. |
| Direct Runner UI attachment | Not required for the first target. Reconnect through the surviving Task Server. |
