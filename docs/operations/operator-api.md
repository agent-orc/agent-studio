# Operator API reference

Routes the operator agent uses to operate Studio, described for the operator.
The operator agent is the coding-agent CLI session the Owner runs on their own
machine ([operator agent setup](setup/operator-agent.md)). It authenticates
with an API token or an operator session token as `Authorization: Bearer`.
Which token may call which route is in the
[security overview](security/overview.md). The concept and the list of planned
routes are in the
[operator-agent-level Dossier](../operator-agent-level/index.html) (AGT-3462).

## Capacity

`GET /api/operator/capacity` (OP-5) is a read-only view for the operator agent.
`quota[]` has one cached snapshot per CLI subscription, with `fresh`,
`freshUntil`, and `exhausted` alongside the quota windows. `activeFallbacks[]`
names the card, source CLI and model, target CLI and model, reason, and the
central model selection `settingKey` and `settingHref`. `waitingCards[]` names
the card, reset time, reason, and its effective wait policy (`enabled`,
`thresholdMinutes`, `scope`). `modelRoutingPins[]` lists explicit project
consumer and pipeline-step pins, including pin mode.

The route accepts an operator or viewer token with `project.read`. Card
fallbacks, waits, and pins are limited to projects it can read. A principal
without installation-wide `workspace.operate` receives cached quota only,
with raw CLI samples removed, exactly as on `GET /api/cli/quota`. An
installation-wide reader gets the same background refresh behavior as that
quota route. Reading capacity does not change routing.

## Operator log and card attention

`POST /api/operator/log` appends an entry with `project`, `kind` (`observed`,
`decided`, `did`, or `handoff`), `text`, `cardKeys[]`, and `auditIds[]`. Owner
sessions and operator credentials may append within their project scope.
Operator sessions need a standing grant for `operator-log.append` (T1).
`GET /api/operator/log?project=<project>&after=<cursor>` returns `entries[]`
and the next `cursor`. The log is append-only and redacts Studio-issued
token-shaped values before storage. A project of `*` is reserved for an Owner
installation handoff. In the standalone profile, the Task Server stores the
log beside its orchestrator transcripts. Project and task chats include project
entries in the usual context budget and cite `operator-log:<project>` in their
context receipt.

`POST /api/tasks/{id}/attention` grants or renews a 10-minute attention lease
and returns its `lease` and current `revision`. The active holder appears on
the card header. Operator sessions need a `card.attention` standing grant (T1).
An operator API token must hold the lease and send
`If-Match: <revision>` on card writes. No lease returns 409
`attention-lease-required`; an absent revision returns 428; a stale revision
returns 412 `stale-card-revision`. Read the card's `ETag` or acquire attention
to get the current revision. The Owner can act at any time; an Owner action
ends an operator lease and appends a `handoff` entry to the log. An operator
cannot replace another actor's active lease.
Operator API tokens use the single-card move route; the batch move route
cannot carry a separate revision for each card and refuses API tokens.

## Deploy and restart preflight

`GET /api/operator/preflight/restart` and `GET /api/operator/preflight/deploy`
check every configured project before a control-plane restart or deploy. An
installation-wide reader is required so a scoped token cannot miss a blocker
in another project. A clear preflight returns 200 with `allowed: true`; a
blocked one returns 409 with `allowed: false` and `blockers[]`. Each blocker
has a stable `code`, `project`, `subject`, and `detail`:

| Code | Existing fact that blocks the operation |
|---|---|
| `pending-push` | An integration push is still pending, regardless of its age. |
| `flight-publishing` | A batch flight is in its publishing phase. |
| `card-mid-commit` | A card pipeline has a running commit or merge step. |

Read preflight again immediately before acting. A clear response is an
observation, not a reservation. `POST /api/operator/deploy/plan` accepts
`targetVersion` and `windowMinutes` (5 to 120, default 30), rechecks deploy
preflight, and returns an ordered plan with `createdAtUtc`, `expiresAtUtc`,
the Task Server's reported current version and protocol (or the local release
in a single-process installation), target version, and observed host versions.
The steps are `drain`, `task-server`, `readiness`, `engine`, `hosts` in one
window. A plan expires at `expiresAtUtc`; no route here executes a host update.

`POST /api/operator/preflight/{restart|deploy}/override` is a T2 operation.
An operator session with installation scope first receives `approval-required`;
the Owner approves the exact request in Studio after step-up. Repeating it with
`X-Operator-Approval` returns `allowed: true`, the current blockers and
`overrideApprovalId`. The approval is single use and its result is audited.
An API token or direct human call cannot redeem it. Passing
`overrideBlocker=true` to a read or plan route returns 403
`override-requires-t2-request`. The override response is evidence for the
manual runbook decision; the plan route continues to enforce preflight.

## Batch triage

`GET /api/operator/batch-triage` (OP-4) answers "what is the batch gate doing,
and what may I do about it" for every project in one call. It reads only. It
starts no Git and takes no lease.

- **Who may call it.** Any principal with `project.read`: operator and viewer
  API tokens, operator sessions, and signed-in users. The response lists only
  the projects the caller may read. A token for `alpha` sees `alpha` and
  nothing about other projects.
- **Where the values come from.** Each project entry is built from the same
  data as `GET /api/projects/{project}/batch-gate/state?history=100` and
  `GET /api/projects/{project}/batch-gate/queue-stalls`. Fields named `state`
  hold the matching state-route entry verbatim, so the triage view and the
  per-project routes cannot disagree.

Response:

| Field | Meaning |
|---|---|
| `generatedAtUtc` | When Studio built the answer. |
| `pendingPushAlertMinutes` | The configured `BatchGate:PendingPushAlertMinutes` (default 3). |
| `projects[]` | One entry per readable project, ordered by name. |
| `projects[].batchGateEnabled` | The project's batch gate setting. |
| `projects[].plan` | The planner's latest decision (`wait`, `close`, `per-task`, `idle`, `disabled`). |
| `projects[].flights[]` | Every open flight: `batchId`, `parentBatchId`, `phase`, `phaseReason`, `isolating`, `memberKeys`, `state`. |
| `projects[].members[]` | Every open member: `taskKey`, `status`, `batchId`, `red`, `isolating`, `nextOperation`, `state`. |
| `projects[].redMembers` | Task keys proven red by isolation or red in pre-check. |
| `projects[].isolatingMembers` | Task keys in a flight that isolates a red batch. |
| `projects[].pendingPushes[]` | `jobId`, `approvedSha`, `sinceUtc`, `ageMinutes`, `alertAfterMinutes`, `overdue`, `reason`. |
| `projects[].queueStalls` | Releasable cards, the open stall episode and recent stall events. |

A flight is open until it is published or abandoned. A red isolation probe is
finished, and so is a red batch whose isolation already published a child. A
red batch and its open isolation children are `isolating`.

A pending push is `overdue` once it has waited `BatchGate:PendingPushAlertMinutes`.
An overdue push is the "lost pending push" runbook case. A restart or deploy
must wait for it.

### Next safe operation

`nextOperation` names one of three operations, a stable `reason` code and a
`detail` sentence. The operations map onto the existing per-project routes:

| `operation` | Route |
|---|---|
| `eject` | `POST /api/projects/{project}/batch-gate/members/{taskKey}/eject` |
| `close-now` | `POST /api/projects/{project}/batch-gate/close-now` |
| `wait` | None. Read the triage view again later. |

The rules, in order:

| Condition | `operation` | `reason` |
|---|---|---|
| The member's flight isolates | `eject` | `isolation-active` |
| The member is proven red or red in pre-check | `wait` | `red-returns-to-agent` |
| In a paused flight, integration branch red | `wait` | `integration-branch-red` |
| In another paused flight | `wait` | `flight-paused` |
| In a green or publishing flight | `wait` | `flight-publishing` |
| In any other open flight | `wait` | `flight-running` |
| Left its batch for the per-task gate | `wait` | `per-task-gate` |
| Pending, batch gate off | `wait` | `gate-disabled` |
| Pending, a push is pending in the project | `wait` | `pending-push` |
| Pending, a flight of the project is open | `wait` | `flight-open` |
| Pending, the planner waits for size or deadline | `close-now` | `batch-forming` |
| Pending, any other planner decision | `wait` | `planner` |

During isolation, eject is the only member action. Do not send continue,
steer or requeue rounds to those members. The Dossier records this as the
guardrail "No rounds to members of a batch under isolation".
