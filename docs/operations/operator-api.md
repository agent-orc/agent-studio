# Operator API reference

Routes the operator agent uses to operate Studio, described for the operator.
The operator agent is the coding-agent CLI session the Owner runs on their own
machine ([operator agent setup](setup/operator-agent.md)). It authenticates
with an API token or an operator session token as `Authorization: Bearer`.
Which token may call which route is in the
[security overview](security/overview.md). The concept and the list of planned
routes are in the
[operator-agent-level Dossier](../operator-agent-level/index.html) (AGT-3462).

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
