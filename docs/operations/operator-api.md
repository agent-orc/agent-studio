# Operator API reference

Routes the operator agent uses to operate Studio, described for the operator.
The operator agent is the coding-agent CLI session the Owner runs on their own
machine ([operator agent setup](setup/operator-agent.md)). It authenticates
with an API token or an operator session token as `Authorization: Bearer`.
Which token may call which route is in the
[security overview](security/overview.md). The concept and the list of planned
routes are in the
[operator-agent-level Dossier](../operator-agent-level/index.html) (AGT-3462).

## studio-op

The operator agent and the Owner call these routes through the official CLI
`studio-op` (OP-8, decision OA-D1 option B) instead of hand-written scripts.
It reads the token only from `~/.config/agent-studio/operator-token` (or
`operator-session`), mode 600, and has no token option or variable. It
redacts token-shaped values in every output, prints JSON with `--json`, and
exits with stable codes. Installation, token setup, the command-to-route table
and the exit codes are in [studio-op setup](setup/studio-op.md); the runbooks
are in the [operator skill](../../.agents/skills/operator/SKILL.md).

| Need | Command |
|---|---|
| Wake when a decision is needed; one watcher at a time | `studio-op watch` (watcher lease plus a local lock, decision stream with a saved cursor, exit code per event class) |
| Batch gate state, pending pushes, eject | `studio-op triage`, `studio-op eject <card>` |
| Continue round from the card's result ref | `studio-op continue <card> --prompt-file <file>` |
| Lane moves and acceptance | `studio-op move <card> <lane>`, `studio-op accept <card>` (green evidence and no open decision, OA-D5) |
| Create cards in Ready, repair titles | `studio-op create --ready`, `studio-op title` |
| Review step models of all projects | `studio-op routing set-step ... --all` |
| Quota and fallback | `studio-op capacity`, `studio-op routing` |
| Check before restart or deploy, server-first plan | `studio-op preflight restart\|deploy`, `studio-op deploy plan` |
| Write down why | `studio-op log append`, `studio-op log` |
| Know what it may do | `studio-op auth status` |

Card writes take the attention lease and send `If-Match` as described below.
`studio-op move` refuses the accepted lanes; acceptance goes through
`studio-op accept`, which reads the card and
`GET /api/tasks/{id}/review-projection` first and refuses red, missing or
disputed evidence, decision cards and open decisions with exit 7.

## Capacity

`GET /api/operator/capacity` (OP-5) is a read-only view for the operator agent.
`quota[]` has one cached snapshot per CLI subscription, with `fresh`,
`freshUntil`, and `exhausted` alongside the quota windows. `activeFallbacks[]`
names the card, source CLI and model, target CLI and model, reason, and the
central model selection `settingKey` and `settingHref`. `waitingCards[]` names
the card, reset time, reason, and its effective wait policy (`enabled`,
`thresholdMinutes`, `scope`). `modelRoutingPins[]` lists explicit project
consumer and pipeline-step pins, including pin mode. `queuedCards[]` lists
each Ready card whose next local pickup would not run its configured route,
as the model selection service answers that question now: `outcome`
(`fallback`, `wait`, or `throttle`), the configured and target CLI, model and
thinking level, the `reason`, and `nextResetAt` (when a wait asks again or a
fallback may return). The forecast is a selection preview and writes no
selection ledger entry. The usage detail's Capacity section renders this list.

The route accepts an operator or viewer token with `project.read`. Card
fallbacks, waits, and pins are limited to projects it can read. Installation
quota and host evidence require workspace-wide `workspace.operate`. A
project-only response has empty quota and host arrays, with generic card
outcomes and null quota deadlines. Workspace readers get the same background
quota refresh behavior as `GET /api/cli/quota`.

`pressure.at` is the UTC time of the read. `pressure.hosts[]` groups evidence
by host, executor and role. It timestamps the resource sample and the slot
reading independently. It reports effective and occupied slots, reported CPU
cores, one-minute load per core, RAM, swap use and traffic, CPU I/O wait,
filesystem free and total bytes and free percent, and observed claim holds.
Disk admission uses the existing pressure thresholds and hysteresis. Resource
samples older than 15 minutes are stale. Missing counters are null. A missing
or invalid disk reading yields `diskLevel: "unknown"`. The workspace inventory
is `partial` when the configured Task Server host read is unavailable.

`pressure.readyWork[]` and each host's `readyWork[]` name the queue source,
count and oldest known wait. Coding counts use the Ready lane or the part
assigned to that host. Review counts are from the shared pending-attempt
queue. Pull and SSH gate queues have separate sources. Claimed-gate queue
depth stays unknown where the Task Server exposes no aggregate queue reading.
A Ready count measures lane membership. Eligibility, claim holds and the
selection preview provide the context for interpreting that count.

`pressure.quota[]` retains one row per provider, subscription and observed
window. Failed, stale, unconfirmed and expired readings have null current
usage and UTC reset. A provider reset label can be present by itself. The
row gives the configured cap and pace, short-step reserve, effective cap for
regular work, admission pacing target and recent measured rate from that
subscription's history. `paceReleaseAt` estimates when this window alone
would admit work if usage stayed at the current reading. It requires fresh
usage and a known reset. `lastAdmission` and
`acceptedFallback` refer to selection ledger receipts attributable to that
window and account. A receipt without sufficient attribution yields null.

`pressure.codingAdmissions[]` gives each visible Ready card's local selection
preview. `class` distinguishes `first-launch`, `recovery` and `unclassified`.
`hardPinned` comes from the effective routing request and applies independently
of work class. `retryAt` requires current quota evidence for the preview's
account. Reading capacity leaves the admission policy and claims unchanged.

`GET /api/operator/coding-capacity` (AGT-4053) reads the published board,
archive-inclusive dependency graph, fresh coding hosts and cached selection
previews. `fleet` and `projects[]` contain total, used and free coding slots,
Ready and claimable counts, `cards[]` with all applicable `dependency`,
`dispatch-rule`, `quota` and `parked` blocks, and `heads[]` ordered with escalated
and parked chain heads first, then by the number of Ready cards behind them.
A live dispatch admission supersedes an older recorded refusal. A remaining
refusal is explicitly labelled as the last observed claim refusal; capacity
is a read model, not a lease permit or a repository preflight.

Fleet cards are limited to the same watched projects represented in `projects[]`.
A card still present in a published snapshot after its project is unwatched does
not add Ready work to the fleet. Authorized cross-project dependency heads remain
visible as blockers through the archive-inclusive reference graph.
Duplicate card keys, including case-only variants, keep the first published
occurrence among the reader's watched projects. Each key contributes once to
project rows and fleet card counts, matching the reference index's
first-occurrence rule.

`dependencyWidth` is the maximum antichain of the unfinished Ready and Progress
graph, equivalently its minimum chain cover. It accounts for forks, joins,
cycles, cross-project edges and fulfilled delivery edges. `maxConcurrentCards`
bounds that width by available coding capacity. Project pools may share hosts;
the fleet counts each coding executor once rather than adding overlapping
project pools. Class-placed project capacity also respects its project limit.
Review and gate roles, stale or closed hosts, retired and read-only clients,
and fixture cards are excluded. A project-scoped reader sees only its project
cards and matching host pool; subscription rows require `workspace.operate`.

Each `fleet` and `projects[]` row also carries four admission fields:

- `admissibleSlots`: free slots the central host policy admits now. It never
  exceeds `freeSlots` or the claimable cards. A project row sums only hosts that
  pass that project's repository preflight; a rejected host adds no admissible
  slot and is named with its reason in the cause (`<host>: <reason>`).
- `runnableNow`: Ready cards whose only blocks are `admission` holds.
- `lastProgressAt`: the latest claim progress on the row's hosts, or null.
- `allocations[]`: per runner `runnerId`, `bootstrapSlots`, `configuredSlots`,
  `adoptedSlots` and `state` (`adopted` or `pending adoption`).

Used slots on a shared physical host include every coding role on that host,
draining peers included. A class-routed project row is bounded by both its
project limit and the free slots of the shared host, so a host full with other
projects' or classes' work is never reported as free. When the ramp or the
target-load brake admits fewer slots than are free and no admission hold
applies, the cause names that limit (for example `conservative ramp admits one
run every 60s and limits admission to 1 of 5 free slots`) instead of advising
more cards.

`subscriptions[]` reports used percent, elapsed percent and projected percent
left at reset for each subscription window. The rate is the average usage
since the observed or inferred window start. A missing `resetAt` says
`reset unknown`; unknown duration, an expired reset, stale or suspicious readings
and the first 5% of a window do not produce a leftover projection.

The decision stream and Needs you expose `capacity.underused` after coding
slots have been continuously observed free for more than 20 minutes, when no
card is claimable or fewer cards are claimable than free slots while Ready is
nonempty. Admission is also reported as stalled while Ready is nonempty and
fewer cards are claimable than runnable, `admissibleSlots` is below `freeSlots`,
or, with no projected subscription leftover, runnable cards fill the free slots
without being claimed; the item then names the admission cause. The fleet also reports projected subscription leftovers above 20%
even when enough cards are claimable. The item names the cause and next action.
The named thresholds live in `CodingCapacityProjection`, not settings. Full
capacity, loss of the published board, or loss of fresh host capacity resets
the observation; after a process restart the 20-minute observation starts anew.
The board header renders the selected project or fleet count as one quiet line.

## Attention

`GET /api/operator/attention` returns the current visible occurrences in
`items`, their total in `count`, and the subtotals in `decisionCount` and
`noticeCount`. Each item has an occurrence `id`, an `isDecision` flag and a
`canDismiss` flag. The Studio header also reads Dossier gates, backup state
and credential inventory.

Send `POST /api/operator/attention/dismiss` with a group of current IDs and
a written reason, for example `{"ids":["<occurrence-id>"],"reason":"Tracked in the handover"}`.
The handler checks run-operation permission for every affected project.
Installation notices require workspace-wide permission. An operator agent
session also needs the routine `attention.dismiss` grant. A successful write
returns 204 and records the actor, reason and occurrences in the control-plane
audit. A changed or invisible group returns 409. A new occurrence appears
again, including a recurring host condition after an observed recovery.

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

## Magazine

`PUT /api/magazine/{scope}/{slug}` publishes an article for the installation
(`installation`), a workspace (`workspace:<id>`) or a project
(`project:<id>`). An existing article needs `If-Match: "<revision>"`: an
absent header returns 428 `if-match-required`, and a stale revision returns
409 `revision-conflict`. Operator API tokens hold `magazine.write`. A
project-scoped token publishes only in its projects, and the installation and
workspace magazines need a token for all projects. Operator sessions read
the magazine but do not publish. `studio-op magazine publish` wraps the read
and the write. Routes, limits and error codes:
[magazine domain](../system/domains/magazine.md).

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
