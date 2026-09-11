# Remote Task Server with local Agent Studio

Status: Phase A architecture delivered. Phase B slice B2 principal and scope
hardening was implemented by AGT-2730 on 2026-09-07. Phase B slice B4
(Windows fallback and switch tooling) was implemented by AGT-2735 on
2026-09-09; see the [Windows fallback runbook](setup/windows-fallback-runbook.md).
Deployment and migration remain gated on the other Phase B slices and the
full release-gate rehearsal.

## Purpose and scope

The Task Server and Orchestrator Engine move to Hetzner and run as independently
supervised services. Robert's Angular Agent Studio remains on Windows at
`[::1]:4011`. `agent-runner-01` reaches the Task Server without the current
reverse tunnel from the Windows machine. Closing, sleeping, or restarting the
Windows machine therefore does not interrupt remote execution.

This plan narrows the earlier "host the control plane" direction. It does not
host Angular, publish a public Studio origin, or make the Task Server an
internet service. It follows the component boundary in
[Distributed Agent Studio target architecture](../concepts/distributed-agent-studio-target-architecture.md)
and the packaged service and recovery contract in
[Task Server deployment and recovery](setup/task-server.md).

The historical Remote-ready kickoff was retired on 2026-07-19. Its binding
outcomes are retained in the current target architecture and ADR-0059 in the
[ADR archive](../system/architecture/decisions/adr-archive.md): one
authoritative Task Server, Git as the code channel, authenticated remote
transport, fenced Runner authority, and no direct remote filesystem writer.

## Proposed decisions

| Topic | Decision |
|---|---|
| Task Server host | Create a dedicated Hetzner VM named `task-server-01`. Do not co-host it with `agent-runner-01`. |
| Runtime | Docker Compose instead of systemd packages (2026-09-06). Run Task Server, Orchestrator Engine, backup, and the private TLS edge as Compose services from `deploy/compose/control-plane/`, installed and updated by `deploy/release/agent-orchestrator/{install,update,rollback}-docker.sh` or `agent-orchestrator-setup --mode control-plane --target docker`. Reason: the same published images and the same guided installer as the local stack, one artifact family instead of a second systemd-only packaging path. Do not deploy the all-in-one legacy `OrchestratorApi` as the target service. See [control-plane-docker.md](setup/control-plane-docker.md). |
| Studio location | Keep Angular and a small loopback-only Studio connector on Robert's Windows machine. Do not serve Angular from Hetzner. |
| Private transport | Use WireGuard between `task-server-01`, `agent-runner-01`, and Robert's Windows device. The Task Server edge listens only on the WireGuard address; Kestrel remains on VM loopback. |
| Application authentication | Use distinct, revocable bearer credentials for Studio, Engine, and each Runner. Enforce authorization on reads, mutations, management routes, and event streams. `X-Client-Id` remains audit attribution only. |
| Task data | Copy the complete `agent-taskboard-workspace`, including `.git`, to a frozen migration source on the VM. Import it into the standalone Task Server only after exact inventory and backup gates pass. |
| Authority | The remote Task Server is the sole writer after cutover. The copied workspace and Windows store remain frozen recovery material, never a second live Task Server. |
| Fallback | Keep an exact-version Windows Task Server and Studio connector profile pre-installed, with a recent verified backup locally available. Rehearse the full switch back in less than 15 minutes before production cutover. |

These decisions are a package. Co-hosting the control plane with the Runner or
using one shared bearer token would invalidate the threat model below.

## Why a dedicated VM

A compromised Runner is an explicit threat, not only an availability failure.
On a shared VM, a root-level Runner compromise can read Task Server data,
service credentials, backups, and systemd configuration regardless of normal
Unix user separation. It can also starve or kill the control plane.

A dedicated small VM provides:

- a host boundary between execution-plane credentials and control-plane data;
- independent reboot, update, resource, and backup lifecycles;
- a firewall policy with no Runner toolchain, repository deploy key, or coding
  agent credential on the Task Server host;
- a meaningful revocation path for a compromised `agent-runner-01`.

Start with 2 vCPU, 4 GiB RAM, and a separately monitored data volume sized from
the measured workspace plus three backup generations. CPU and storage are
capacity starting points, not a purchasing commitment. The Phase B
infrastructure task records measured disk use and adjusts the volume before
installation.

## Target topology

```text
Robert's Windows device
  Angular Studio [::1]:4011
          |
          | same-origin /api and /hubs
          v
  Studio connector [::1]:5031
  injects Studio credential, enforces Origin and CSRF
          |
          | HTTPS over WireGuard
          v
task-server-01, dedicated Hetzner VM
  wg0:443, private TLS edge only
          |
          | loopback HTTP
          v
  Task Server 127.0.0.1:5071  <->  Orchestrator Engine
  systemd + SQLite state + verified backups
          ^
          |
          | HTTPS over WireGuard, Runner credential
          |
agent-runner-01
  Agent Runner systemd service
  repository clones and worktrees
  no Task Server filesystem access
```

The local connector preserves the existing Angular same-origin shape and lets
the stable frontend keep using its loopback backend target. It is a stateless
credential and transport boundary, not a task authority. In remote mode it
forwards to the WireGuard origin. In fallback mode it forwards to the Windows
Task Server on a different loopback port.

The current `studio-bff` is a useful starting point, but it only forwards
`/api/v1` and does not yet cover the complete Studio API, authentication
surface, or `/hubs` WebSocket path. Extending and testing that boundary is a
cutover prerequisite.

## Network design

### WireGuard instead of a persistent SSH forward

WireGuard is the recommended client path for both Studio and Runner:

- it reconnects after Windows sleep and restart without a long-running SSH
  forwarding process;
- one private address and DNS name serve Studio, Runner, and operations;
- each device has a separately revocable network key;
- `agent-runner-01` reaches the control plane directly and no longer depends on
  a tunnel initiated by Robert's machine;
- the API still has no listener on the VM's public interface.

A persistent local SSH forward remains a viable emergency administration path,
but is not the normal data path. It should not become a second, unmonitored
production route.

WireGuard is network admission, not application authentication. A stolen
WireGuard key must not be enough to read or mutate tasks.

### Listener and firewall contract

- Kestrel listens on `127.0.0.1:5071` only.
- The TLS edge listens on TCP 443 bound to the `wg0` address only.
- The public interface has no TCP listener for the API, Studio, health, or
  management endpoints.
- Hetzner Firewall and `ufw` both default-deny inbound traffic.
- `ufw` permits TCP 443 only on `wg0`.
- Public UDP permits only the WireGuard handshake port. Public SSH remains an
  administration surface restricted to Robert's known source range or an
  existing bastion. No public rule permits 80, 443, 5030, 5031, or 5071.
- IP forwarding is off. WireGuard peers receive only the single Task Server
  route, not general VM or home-network access.
- Private DNS maps a non-public name to the WireGuard address. TLS uses a
  private CA trusted by the two clients, or an explicitly pinned certificate.
  Certificate expiry and rotation are monitored and rehearsed.

Phase B must prove the binding with `ss`, `ufw status`, Hetzner Firewall
inspection, a public-interface connection test, and a WireGuard-interface
connection test. A wildcard bind such as `0.0.0.0` or `[::]` fails the gate.

## Authentication and authorization design

Bearer authentication is selected over mTLS for the first deployment. WireGuard
already supplies device-level encrypted transport, while bearer principals fit
the existing Task Server and Runner seams and are simpler to rotate. TLS remains
mandatory inside WireGuard so credentials are not sent over cleartext HTTP.

Task Server now maps `AUTH=bearer` onto distinct persisted principals with
server-side hash-only credential storage and route scopes. Packaged setup
creates separate Studio and Engine bootstrap files, while host setup exchanges
the protected join credential for one bound Runner credential:

| Principal | Credential location | Allowed authority |
|---|---|---|
| `studio-robert-windows` | Windows Credential Manager, read only by the loopback Studio connector | Read allowed resources and perform normal Studio task and orchestration mutations. No Runner lease, credential administration, restore, or server lifecycle authority. |
| `agent-runner-01` | Root-owned Runner credential file with service-group read access | `runner.claim`, `runner.lease`, `runner.logs`, `runner.events`, `runner.artifacts`, and `runner.completion` for its own Runner identity. |
| `orchestrator-engine` | Root-owned Task Server host credential file | Only orchestration claim, renewal, stage completion, and required resource reads. |
| `break-glass-owner` | Offline operator secret, not loaded by Studio or Runner | Identity rotation, restore, maintenance, and emergency revocation. |

Requirements:

1. Every API read and mutation requires an authenticated principal, except the
   minimal non-secret liveness response and protocol negotiation.
2. `/hubs` negotiation, connection, subscription, and reconnect use the same
   authenticated Studio principal.
3. Unsafe Studio methods require connector-issued CSRF proof. The connector
   accepts only the exact local Studio Origin and rejects unexpected Host and
   WebSocket Origin values.
4. The Studio bearer never reaches Angular, browser storage, task content, or
   logs. The connector injects it after the local browser boundary.
5. Tokens are at least 256 bits, one-time reveal, hash-only at rest on the
   server, scoped, expirable, independently revocable, and rotated with an
   overlap-and-prove procedure.
6. The authenticated principal is the authorization input. `X-Client-Id` is
   stored separately as attribution and never changes the decision.
7. `X-Client-Id` without a valid bearer returns 401. A valid Runner bearer on a
   Studio, management, or other Runner identity's mutation returns 403.

The old `TaskServer:RequireAuthentication` plus `StudioBearerToken` and
`RunnerBearerToken` profile maps into the principal model only as a deprecated
migration bridge. It has an explicit post-Phase-B removal note. Production uses
a distinct revocable credential for each Runner, never a Runner-family shared
secret.

## Threat model

| Attacker | Capability assumed | Controls | Residual risk and response |
|---|---|---|---|
| Internet attacker | Can scan and send arbitrary traffic to the public VM address | No public API listener; Hetzner Firewall plus `ufw`; WireGuard handshake only; SSH source restriction; application auth still required after VPN admission | A WireGuard or SSH implementation flaw remains possible. Patch both hosts and alert on handshake and login anomalies. |
| Compromised Runner | Has `agent-runner-01` WireGuard key, scoped Runner token, repository deploy keys, and coding-agent credentials | Dedicated Task Server VM; no shared filesystem; Runner route scopes; fenced leases; per-identity audit; no management or arbitrary task mutation; immediate revocation of Runner token, WireGuard peer, and repository keys | The attacker can act within an already authorized run and may exfiltrate code available to that Runner. Revoke, fence the host, quarantine active attempts, and require positive no-overlap proof before reissue. |
| Stolen Robert device | May obtain the WireGuard key and, if the device is unlocked or decrypted, the Studio credential | Full-disk encryption; Windows Credential Manager; auto-lock; Studio token has no break-glass authority; separate revocation of VPN peer and Studio token; short session and credential audit | An unlocked compromised device can perform Studio-authorized actions. Revoke both credentials, review audit, rotate affected secrets, and restore from verified evidence if unauthorized mutations occurred. |

The design does not claim to protect Task Server plaintext from root compromise
of `task-server-01`. That event requires host isolation, credential rotation,
and restore to a clean VM from an off-host verified backup.

## Data and backup layout

The immutable release stays under `/opt/agent-orchestrator/<version>`.
Configuration and credentials stay under `/etc/agent-orchestrator`. Active
Task Server state, migration evidence, and local backup staging stay under
`/var/lib/agent-orchestrator`. None of these paths is a product source checkout.

The Windows `agent-taskboard-workspace` is copied over SSH with `.git`, task
metadata, prompts, timelines, results, and hidden application metadata intact.
It is staged on the VM as a migration source and becomes read-only after the
final copy. The standalone Task Server imports durable authority into its
SQLite store. The staged Git workspace remains migration and rollback evidence;
it is not mounted by the Runner and is never a second live writer.

Backup policy before cutover:

- one frozen filesystem archive of the Windows workspace, including uncommitted
  files, with a SHA-256 manifest;
- one `git bundle` or equivalent full-ref export plus `git fsck --full`
  evidence;
- one Task Server pre-import backup created by the migration endpoint;
- one restore-verified off-host copy outside `task-server-01`;
- one restore-verified copy staged for the Windows fallback.

After cutover, the Task Server backup timer creates integrity-checked SQLite
backups and sends them to separate off-host storage. The maximum planned
recovery point is five minutes. The exact interval is accepted only after a
load and restore rehearsal shows it does not disrupt lease renewal or
completion. The Windows standby regularly receives and verifies the newest
backup but never opens it while the remote server is authoritative.

## Current cutover gates

These are the remaining Phase B gates and the delivered milestones whose
production evidence must be attached before cutover:

1. **Studio API coverage.** Angular still consumes a broad legacy `/api`
   surface, while the standalone Task Server and current `studio-bff` expose
   only the versioned subset. Phase B must classify every Studio route as
   Task Server, local dev-seat helper, or retired, and prove all task,
   orchestration, host, file, event, and management paths remotely.
2. **Current workspace migration acceptance, implementation delivered by B3.**
   The standalone CLI and management API now inventory and import canonical
   `task.json` data, use `job.json` only as a compatibility fallback, enforce
   exact post-import counts, retain orphaned history as reported degradation,
   and persist signed import reports. The remaining acceptance step is an
   operator-run inventory and import against a frozen copy of the Windows
   workspace, followed by attaching both reports and their per-project and
   per-state counts to the D7 cutover card.
3. **Windows fallback artifact, closed by B4.** The control-plane release now
   also publishes a `win-x64` package (`publish-windows` in
   `.github/workflows/release.yml`) with a version-matched Windows service
   installation for all three components and a cross-platform full-backup
   restore path; see the [Windows fallback runbook](setup/windows-fallback-runbook.md).
4. **Connector security.** The local connector forwards `/api/v1` and `/hubs`
   with its Studio credential and now has an atomic remote/local upstream
   switch (`deploy/windows/fallback/switch-upstream.ps1`), but still needs
   complete route coverage, Credential Manager integration, strict Origin
   checks, CSRF, and protocol negotiation.

No API listener may open on `wg0` until Studio route ownership is complete and
the Task Server, Agent Host, Studio BFF, and connector authentication paths pass
their negative tests.

## Post-processing without an attached Studio

### Concept decision

Moving task state to the remote Task Server and moving post-step execution are
two migrations. The former removes the Windows workspace as the task database.
It does not make Git, build, test, lint, or model work executable on the Task
Server. Repository work belongs on a fenced Agent Host. Pure verdict and lane
policy belongs in the remote control plane.

| Owner | Responsibilities |
|---|---|
| Task Server | Own task and RunAttempt state, immutable ReviewSubjects, post-step plans, review-cycle epochs, retry budgets, authoritative lane transitions, idempotency, and integration commands. It never starts a project process or reads a host checkout. |
| Orchestrator Engine | Evaluate persisted, normalized facts through the public Task API and request one bounded action. It owns no task files, checkout, or attempt authority. |
| Agent Host | Materialize the exact fenced subject, execute checkout-bound Git, build, test, lint, and model work, and report typed facts plus content hashes. It cannot move a lane or create a replacement coding run directly. |
| Operator | Keep explicit acceptance, destructive override, unresolved conflict, credential, and break-glass decisions. Studio is one client for these decisions, not an execution dependency. |

The Angular process is already irrelevant to execution. The compatibility
dependency is the legacy `OrchestratorApi` process and its local
`TaskRepository`. `AutoReviewPostProcessingWorker` reads a card from that
filesystem, `ReviewDecisionOrchestrator` runs the chain, and the same process
writes decision and lane state into task folders. Canonical Remote attempts
must use Task Server records instead.

### Placement rule and current dependency map

A step is **host-capable** when every side effect can be scoped to an immutable
repository subject, a disposable checkout, declared credentials, and a typed
result envelope. A step is a **server decision** when it only combines persisted
facts and chooses a transition. A step remains **backend-bound** when it reads
the legacy workspace registry or mutates paths outside a fenced project
checkout without an API contract. A deferred step may have an operator or Task
Server trigger and still use a host executor.

| Concern and affected steps | Current paths, services, and serialization |
|---|---|
| Catalogue and ordering | `backend/Features/Pipeline/PipelineCatalogue.cs` defines standard, UI, concept, drift, and abort steps. `PipelineExecutionLog.cs` writes card-local `pipeline-execution.json`. |
| Coordinator and decision | `backend/Features/Runner/AutoReviewPostProcessingQueue.cs` owns the in-process worker and capacity semaphore. `ReviewDecisionOrchestrator.cs` owns `_tickGate`, `_postProcessingGitGate`, `GuardedMoveJob`, review-cycle files, and reads of `status.md`, `results/`, and `completion-acceptance.json`. `CompletionGate.cs` contains the completeness policy. |
| Aspects and grade | `AspectRunnerService.cs` uses a per-card `SemaphoreSlim`, prompt and CLI services, and task-folder evidence. `backend/Features/Review/CodeReviewStepService.cs` writes the grade report. |
| Build, lint, and radar | `BuildTestGateRunner.cs` owns static `ProcessGate`, machine `flock`, `RemoteGate`, and disposable review roots. `LintScssRunner.cs` starts stylelint below `<repo>/frontend`. `RegressionRadarService.cs` reads the repository SHA range. |
| Worktree, integration, and conflict | `ProjectRunner.cs` owns `_integrateLock`; `WorktreeTaskLifecycle.cs` records containment; `runner/GitWorkspace.cs` owns process-wide `GitMetadataGate`. Inputs include the active checkout, task ref, integration ref, and pipeline log. |
| Accepted integration | `TaskTransitionService.cs` calls `CommitAttributionService.cs` and `MergeIntoDevelopRunner.cs`. The latter owns `_mergeGate` and `_pushGate`; `IntegrationPushQueue.cs` and `IntegrationPushWorker.cs` own deferred retry. Evidence is under the card's `post-steps/`. |
| Wiki producers | `WikiMaintenancePostStepRunner.cs`, `WikiLearningsPostStepRunner.cs`, and `AgentsWikiSyncPostStepRunner.cs` mutate managed repository documentation. `ManagedProjectArtifactCommitService.cs` serializes publication per repository. |
| Task spawn | `TaskSpawnerPostStepRunner.cs` invokes a local one-shot model call and `TaskMutationService`; `SpawnedTaskLedger.cs` writes card metadata. |
| Drift | `backend/Features/Drift/DriftPostStepRunner.cs` reads `TaskRepository/projects/<project>`, repository content, lanes, and prior `logs/drift/*.md`; dimension services and `DriftReportStore` remain workspace-bound. |

File names without an explicit directory above are below
`backend/Features/Pipeline/` or `backend/Features/Runner/` as indicated by the
concern.

### Standard post-step classification

| Step | Current local dependencies and locks | Target and required contract | Order |
|---|---|---|---:|
| `post-orchestrator-review` | Card body, status, CLI tail, results inventory, completion acceptance, review-cycle counter, `CompletionGate`, and `PipelineExecutionLog`. | **Server decision.** For canonical Remote Review, the Task Server now accepts only normalized fenced report facts and keeps the card in Auto Review through cleanup. Legacy card completeness still needs a structured fact migration. | S1, Remote delivered |
| `post-build-test-gate` | Watched repository path, exact SHA, `BuildProfile`, mode, timeouts, `ProcessGate`, machine `flock`, optional SSH bridge, and disposable review root. | **Host-capable.** Create an immutable GateSubject/GateAttempt with RunAttempt ID, repository source, Result-SHA, plan/policy digest, argv, working directory, environment allow-list, deadlines, resource class, and output bounds. Host reports tested HEAD/tree, exit/signal, classification, artifact hashes, and cleanup proof. | G1, first candidate |
| `aspect-requirement-fit` | Prompt, task/status evidence, result inventory, branch diff, template, model routing, token budget, CLI, and exact checkout. | **Host-capable, already represented by Remote Review.** Keep immutable ReviewSubject, resolved model plan, typed verdict, usage, and exact-SHA workspace proof; Task Server owns effects. | R1 |
| `aspect-code-quality` | Same checkout, evidence, prompt, model, CLI, and budget dependencies. | **Host-capable through Remote Review** with the same subject and report contract. | R1 |
| `aspect-documentation-impact` | Same dependencies plus repository documentation inventory. | **Host-capable through Remote Review** with declared documentation inputs in the subject digest. | R1 |
| `aspect-tests-and-evidence` | Same dependencies plus command and durable result evidence. | **Host-capable through Remote Review** with artifact references instead of card-folder reads. | R1 |
| `post-worktree-containment` | Runner `GitWorkspace`, task worktree, result ref, process-wide `GitMetadataGate`, RunAttempt, lease, fence, base/result SHA, and manifest digest. | **Host-capable, delivered.** Host permit, durable journal, immutable handoff, cleanup proof, and `post_step_executions` completion already gate coding completion. | H0 |
| `post-integrate-merge` | Mutable task worktree/branch, integration head, Git metadata, `_integrateLock`, containment result, and pipeline log. | **Host-capable, high authority.** Require fenced result ref, expected integration head, exclusive repository lease, and typed merged/already-merged/conflict/environment result. | I1 |
| `post-conflict-resolution` | Non-idempotent model-guided mutation, conflict set, model/CLI credentials, Git state, and pipeline log. | **Host-capable, high risk.** New fenced checkout generation, conflict digest, bounded model plan, resolved tree hash, tests to rerun, no-push boundary, and stale-head rejection. | I2 |
| `post-git-commit-attribution` | Run base/head and commit range; writes task-folder evidence before Auto Review. | **Host-capable analysis, server-owned record.** RunAttempt and Result-SHA identify the range; host reports ordered commits and Task Server stores them under the fence. | G0 |
| `post-merge-into-develop` | Deferred acceptance calls `MergeIntoDevelopRunner`, `GitService`, settings, `_mergeGate`, and card-local outcome logs. | **Operator or Task Server trigger, host execution.** Durable integration command with accepted ReviewSubject, expected target head, repository lease, and typed result. | I1 |
| `post-merge-into-develop-push` | `_pushGate`, `IntegrationPushQueue`, credentials, local and remote integration heads, environmental retry. | **Task Server trigger, host execution.** Follow one successful merge generation with scoped credentials, expected heads, idempotency key, and remote-head acknowledgement. | I1 |
| `post-lint-scss` | Watched frontend path, `npx stylelint`, project warn/fail mode, timeout, task-folder log, and local lane policy. | **Host-capable GateAttempt.** Exact subject, Angular applicability, argv, toolchain digest, mode, timeout, bounded output; Task Server applies warn/fail policy. | G2 |
| `post-regression-radar` | `GitService` diff over the run commit chain and card-local reporting row. | **Host-capable analysis.** Base/result SHA and repository source in; typed classification artifact and hash out; Task Server stores it. | G0 |
| `post-wiki-maintenance` | Mutates `docs/<theme>/common-problems`, occurrence files, and index using watched-root and task data; later commit/push publication. | **Backend-bound for now.** Needs API task facts, producer-owned path manifest, fenced repository-write subject, per-project Git lease, attribution, and publication policy. | B1 |
| `post-wiki-learnings` | Reads task outcome, status, aspect verdicts, diff, and results; writes `docs/operations/learnings/<task>.md` and index. | **Backend-bound for now.** Replace card reads with API facts, then use the fenced repository-write contract. | B1 |
| `post-agents-wiki-sync` | Reads/writes `AGENTS.md`, designated-topic registry, current-state pages, and index; derives matches from paths and tags. | **Backend-bound for now.** Needs API facts, producer-owned paths, repository lease, link validation, commit, and push acknowledgement. | B1 |
| `post-code-review-grade` | Local diff, task evidence, build result, prompt/model/CLI services, token ledger, and card-folder grade evidence. | **Host-capable through Remote Review.** Immutable subject and model plan in; typed grade, findings, usage, and artifact hashes out. Grade remains advisory to server policy. | R1 |
| `post-task-spawner` | Model relevance decision, target project settings, local dedupe ledger, and local task service. | **Split.** Host returns a typed spawn proposal; Task Server validates scope, owns limit/dedupe, and creates the related task idempotently. | B2 |
| `post-orchestrator-decision` | Combines gates, aspects, grade, lint, evidence, quality, and retry facts; writes journals and uses `GuardedMoveJob` for lane effects. | **Server decision.** Canonical Remote Review now queues a full-envelope Engine run after cleanup. Task Server applies shared reissue budget and one version-fenced lane transaction. Legacy filesystem decisions remain compatibility-only. | S2, Remote delivered |
| `post-drift-adr-code` | Task repository, lanes, repository ADR/code/schema, prior drift reports, and model service. | **Backend-bound until snapshot API exists.** Versioned repository/workspace snapshot, model plan, and server report store. | B3 |
| `post-drift-software-architecture` | Repository module/schema/test trees, architecture models, recent task folders, shared report store, and model. | **Backend-bound until snapshot API exists.** Same snapshot/report contract with explicit architecture inputs. | B3 |
| `post-drift-docs-marketing` | Canonical docs, mockups, recent lanes, completed task folders, report history, and model. | **Backend-bound until snapshot API exists.** Docs/mockup snapshot with no live lane scan. | B3 |
| `post-drift-spec-task-job` | Specifications and task folders across lanes, shared report store, and model. | **Backend-bound until task-history API exists.** Task Server history plus immutable repository snapshot and typed report. | B3 |
| `post-drift-code-pattern` | Exact checkout, `docs/system/contracts/code-patterns.md`, and workspace report store; analysis is deterministic. | **Host-capable analysis.** Exact subject and rules digest in; typed findings out; Task Server stores the report. | G0 |

### Triggered and specialised post-steps

| Step | Current dependency | Target and order |
|---|---|---|
| `post-abort-review` | Local task contract, CLI output, prompt registry, rerun budget, and card report. | Host-capable ReviewAttempt plus server-owned abort policy and budget after typed abort facts exist. R1/S2. |
| `post-ui-iteration-artifact` | Screenshots, Playwright evidence, task metadata, and pipeline log under the card. | Host uploads an evidence manifest; Task Server validates completeness and records the iteration. G0. |
| `post-ui-human-review-gate` | Local marker plus lane move through `GuardedMoveJob` and `_postProcessingGitGate`. | Task Server decision. It remains an operator gate; Studio is optional. S2. |
| `post-concept-workbench-placement` | Repository concept/Dossier paths and publication Git work. | Backend-bound until the repository-write contract exists. B1. |
| `post-concept-review` | Concept artifact, card evidence, prompt/model/CLI services, and local review record. | Host-capable immutable-subject review with server-owned typed verdict. R1. |
| `post-concept-sight-review` | Rendered concept evidence, local result inventory, and vision review. | Host-capable through the Remote Review vision plan; Task Server owns the gate. R1. |
| `post-concept-promotion` | Operator acceptance, repository paths, commit/push, destination policy, and local task creation. | Operator or Task Server trigger with host Git execution after repository-write and integration contracts exist. B1/I1. |

### Delivered decision slice

Canonical Remote Review now keeps a valid non-infrastructure report in
`4-auto-review` until cleanup proves the disposable workspace is gone. That
cleanup atomically creates one idempotent orchestration run. Its immutable
payload contains `RunAttemptId`, `ReviewSubjectId`, `ReviewAttemptId`,
`ResultSha`, review policy hash, review report hash, normalized outcome,
verdicts, and gate facts. This is not the legacy `review-subject.json` sidecar.

Every project receives the code-owned default five-stage flow. The external
Engine evaluates the payload through the public API. Task Server settlement
snapshots the task resource version, rejects stale authority, and applies one
of three unambiguous effects:

- `Pass` continues to the final Human Review handoff;
- `ProductFailure` consumes the task-wide budget and returns to `2-ready`, or
  reaches `5e-escalated` when the budget is exhausted;
- `ReviewInfra` never enters this flow in the normal path; it retries the same
  immutable ReviewSubject. An unexpected payload fails closed.

Lane mutation, audit, and lifecycle evidence are one Task Server transaction.
Engine restart is safe because leases and fences are persisted. Studio and the
legacy backend are absent from the path. Integration remains an explicit Human
Review command and is not inferred from a passing post-processing verdict.

### First execution migration candidate: `post-build-test-gate`

The first high-value checkout-bound move is `post-build-test-gate`. The current
target architecture uses a dedicated bounded GateSubject/GateAttempt contract,
not another host-side orchestrator. `HostReport` remains the source of executor
capability, capacity, and fault facts; the GateAttempt supplies work ownership.

The interface cut is:

1. Engine resolves a versioned plan from the pipeline definition and creates a
   GateSubject bound to task, RunAttempt, repository, Result-SHA, immutable ref
   or source bundle, pipeline definition version, policy hash, and plan digest.
2. Task Server creates a GateAttempt and admits only a host with fresh
   `executor:gate`, repository, Git, disk, and required toolchain capabilities.
3. Host claims with lease, fence, authority epoch, and idempotency key;
   materializes a fence-specific disposable checkout and proves
   `HEAD == ResultSha` before executing ordered argv with declared working
   directories, environment names, deadlines, resource class, and output caps.
4. Host reports per-command exit/signal/duration, tested tree, classified
   product or infrastructure failure, artifact hashes, toolchain digest, and
   cleanup proof. It never chooses a lane.
5. Task Server validates and persists the report. Engine consumes the terminal
   gate result; Task Server applies retry or reissue policy. The existing SSH
   bridge is removed only after parity and restart/loss canaries pass.

### Migration sequence and completion condition

1. **H0, delivered:** HostReport negotiation, permits, durable journal, replay,
   and `post-worktree-containment`.
2. **S1/S2 for canonical Remote Review, delivered here:** full-envelope review
   cleanup handoff, durable Engine decision, task-wide reissue budget, lane
   move, and lifecycle evidence.
3. **G0:** commit attribution, regression radar, UI evidence manifest, and
   code-pattern drift over immutable subjects.
4. **G1/G2:** claimable `post-build-test-gate`, then lint, with a separate gate
   pool and exact-subject evidence.
5. **R1:** converge remaining aspect, grade, abort, concept, and vision calls on
   immutable ReviewSubjects.
6. **I1/I2:** durable integration commands, merge/push execution, and conflict
   resolution under repository leases. Operator acceptance remains explicit.
7. **B1-B3:** replace workspace registry, wiki, task-spawn, and drift filesystem
   dependencies with history, snapshot, repository-write, and mutation APIs.

Detached-Studio acceptance covers a full wave: finish coding, preserve the
RunAttempt envelope, execute Remote Review, clean its workspace, run the Engine
decision, exercise one bounded reissue, and reach Human Review with the Windows
Studio connector and legacy backend stopped. The later integration slice adds
an accepted integration command and host execution. No card may remain in
`post-processing-running` solely because Studio is down.

## Migration runbook

### Entry gates

Before the production maintenance window:

- the exact release passes the topology, protocol, authentication, backup, and
  restore suites;
- the route inventory contains no unclassified Studio request;
- a dry run from a disposable copy preserves all expected task data;
- remote and Windows fallback packages report the same version and schema
  range;
- WireGuard, firewall, certificate rotation, token rotation, and public
  non-reachability have been rehearsed;
- the rollback drill has completed in less than 15 minutes using production
  scripts and representative data;
- there are no active or `process-unknown` attempts at the freeze point.

### Before and after evidence

Save one signed or checksum-protected migration report containing:

- workspace Git HEAD, `git status --porcelain`, `git fsck --full`, and full-ref
  bundle hash;
- total projects and tasks, including archive;
- task counts per project and state;
- event, result artifact, attachment, and evidence-Git counts;
- epic, reference, and project-registry counts where supported;
- source manifest hash, migration ID, import integrity SHA-256, Task Server
  version, and schema version;
- every migration warning, with an explicit accept or stop decision.

Project count, total task count, each per-project and per-state count, events,
and artifacts must match exactly before and after. A warning, missing archived
card, or unexplained count delta stops cutover.

### Production sequence

1. Announce the maintenance window. Drain the local Runner and stop automatic
   pickup. Resolve every active or unknown attempt.
2. Put the local authority in read-only mode. Stop all local Task Server,
   orchestrator, scheduled mutation, and workspace-watcher processes.
3. Capture the final inventory, filesystem archive, Git bundle, and checksums.
   Record the time at which the single-writer freeze began.
4. Copy the frozen workspace to the VM over SSH. Verify the file manifest,
   counts, Git refs, and `git fsck --full` on Linux. Check case-colliding paths
   before import.
5. Start the remote Task Server in `Maintenance` with authentication enabled.
   Run legacy inventory against the staged workspace and compare it to the
   saved Windows inventory.
6. Import with `freezeConfirmed:true` and the exact migration ID. Save the
   automatically created pre-import backup and returned integrity SHA-256.
7. Repeat the full before and after count comparison. Verify the backup without
   changing active data.
8. Start the Orchestrator Engine. Change the local Studio connector from its
   rehearsal target to the remote WireGuard origin. Keep admission closed.
9. Change `agent-runner-01` to the WireGuard Task Server URL and its scoped
   Runner credential. Disable, but do not delete, the old Windows reverse
   tunnel scheduled task.
10. Open normal admission. Prove authenticated Studio read, one reversible
    task mutation, SignalR reconnect, Runner claim and renewal, artifact
    upload, completion, and Studio detach/reconnect.
11. Turn the Windows Studio processes off for an acceptance interval. The
    Runner and Engine must continue and the task history must be present after
    Studio returns.
12. Mark the remote server authoritative only after all evidence is saved. Keep
    the frozen Windows source and the prior tunnel configuration through the
    agreed stabilization period.

At no time may both the legacy Windows writer and the remote Task Server accept
mutations for the same logical workspace.

## Rollback in less than 15 minutes

The rollback target is the version-matched Windows Task Server, not direct
editing of the frozen workspace. The local Studio connector changes upstream;
Angular stays at `[::1]:4011`.

Two data cases exist:

- **Before the first accepted remote mutation:** use the untouched frozen
  Windows authority and source backup.
- **After remote mutations:** restore the newest verified remote Task Server
  backup on Windows. Never restart the stale pre-cutover writer.

The rehearsed timeline is:

| Elapsed | Action |
|---:|---|
| 0 to 2 min | Stop admission, drain if reachable, and fence the remote VM. If the host cannot be contacted, use the Hetzner control plane to power it off or detach its network before starting local authority. Loss of heartbeat alone is not fencing. |
| 2 to 5 min | Select and verify the newest local backup by ID and SHA-256. Record its recovery point. |
| 5 to 8 min | Restore it to the pre-installed Windows Task Server, start in maintenance, verify integrity and counts, then enter normal mode. |
| 8 to 10 min | Atomically switch the loopback Studio connector upstream to the Windows Task Server and verify board read plus one authenticated mutation. |
| 10 to 13 min | Re-enable the preserved reverse SSH tunnel, point `agent-runner-01` at `127.0.0.1:15031`, retain Runner authentication, and restart its service. |
| 13 to 15 min | Verify Runner health, lease renewal, Studio events, and sole-writer status. Publish the actual recovery point and elapsed time. |

The Windows fallback Task Server continues to require token authentication
while the reverse tunnel is enabled. A loopback listener does not make a
tunneled mutation anonymous or safe.

Failback to Hetzner is a new migration window with the same freeze, inventory,
backup, and sole-writer gates. It is never an automatic resynchronization.

## Phase B delivery slices and effort

Every slice is a separate task and starts only after Robert approves this
concept.

| Order | Slice | Acceptance result | Estimate |
|---:|---|---|---:|
| B1 | Studio route ownership and secure local connector | Full `/api` and `/hubs` matrix; remote task workflows pass; local-only dev-seat routes are explicit; connector keeps secrets out of Angular and enforces Origin and CSRF | 5 to 8 engineering days |
| B2 | Task Server principal and scope hardening | Implemented by AGT-2730: separate hash-only Studio, Engine, and per-Runner credentials; route scopes; hub auth boundary; rotation and revoke tests; `X-Client-Id` negative tests | Complete 2026-09-07 |
| B3 | Current-workspace migration and evidence | Implemented by AGT-2732: `task.json` with `job.json` fallback; canonical per-project and per-state inventory; archive, events, pointer-only artifacts, Git and authority evidence; counted orphan ledger; Maintenance-only idempotent import; signed reports; mismatch stops; and backup/restore inventory-hash continuity. The Windows operator still performs the frozen rehearsal and production cutover and attaches both reports to D7. | Complete 2026-09-11; operator cutover evidence pending |
| B4 | Windows fallback and switch tooling | Implemented by AGT-2735 on 2026-09-09: version-matched Windows service for all three components; cross-platform full-backup restore with a case-collision guard; warm standby pull; atomic connector profile switch; scripted reverse-tunnel drill in both directions with a measured sub-15-minute report; Windows CI coverage. The timed real-infrastructure rehearsal remains a B6 operator drill. | Complete 2026-09-09 |
| B5 | Private Hetzner foundation | Dedicated VM, WireGuard peers, private TLS, dual firewall, systemd packages, off-host backup, monitoring, and proof of no public API listener | 2 to 3 engineering days plus operator access |
| B6 | Rehearsal and production cutover | Representative dry run, signed evidence, maintenance-window cutover, detached-Studio proof, rollback drill, and operator handoff | 2 to 4 engineering days plus one operator window |

Expected total: 18 to 30 engineering days plus one to two operator days. The
largest uncertainty is B1 because current Angular functionality still spans
legacy backend routes. B1 produces a route-counted estimate before the
remaining schedule is committed.

## Release gates

Phase B is complete only when all of the following are true:

- public connection attempts to every API and health port fail;
- WireGuard clients can reach the private TLS origin and unregistered peers
  cannot;
- missing or invalid auth returns 401, insufficient scope returns 403, and
  `X-Client-Id` alone never authorizes;
- a compromised-Runner simulation cannot call Studio or management mutations;
- systemd restarts Task Server and Engine independently of Studio;
- a run continues through Windows sleep or shutdown;
- migration counts, Git evidence, and integrity hashes match exactly;
- off-host backup restore succeeds on Linux and Windows;
- the old reverse tunnel is disabled in remote mode but remains documented and
  tested for fallback;
- the sole-writer invariant is visible in both cutover and rollback evidence;
- the measured rollback completes in less than 15 minutes.

## Related documents

- [Distributed Agent Studio target architecture](../concepts/distributed-agent-studio-target-architecture.md)
- [Task Server deployment and recovery](setup/task-server.md)
- [Control plane on Docker (task-server-01)](setup/control-plane-docker.md)
- [Security overview](security/overview.md)
- [Security requirements](security/requirements.md)
- [Release, installation, update, and rollback](releases.md)
- [Remote runner persistent connection](setup/remote-runner-persistent-connection.md)
- [Token refresh without a tunnel](token-refresh-ohne-tunnel.md)
- [Distributed execution connection health](haertung-verteilte-ausfuehrung/target-architecture/connection-health.md)
- [Control plane as a distributable](haertung-verteilte-ausfuehrung/target-architecture/distributable.md)
