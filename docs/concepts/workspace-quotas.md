# Workspace and project quotas

Workspace owners set weekly model-token and compute core-hour limits and a
hosting-slot limit in Settings. A project can also have its own limits. Both
levels apply: a project cannot bypass the workspace limit. A blank field has no
limit; zero refuses new work. Weeks start Monday at 00:00 UTC. Usage resets by
the read window, while the stored limits remain in force.

The quota read API is `GET /api/workspaces/{id}/quota` or
`GET /api/workspaces/{id}/projects/{projectId}/quota`. The snapshot includes
usage, remaining quota, seven daily buckets, and the largest card and project
consumers. The owner's `PUT` routes at the same paths update limits. An owner
can `POST` a reason to either path plus `/override`; the override expires at
the next week boundary and its actor, reason, scope, and expiry are stored in
the quota audit alongside the limits. `GET /api/workspaces/{id}/quota/audit`
returns that history.

Token counts come from the merged project token ledger. Ledger rows are
normalized, so input, cached input, cache writes, and output each count once
for every provider, including legacy OpenAI rows that recorded cached input
inside input. The price catalogue supplies a separate USD estimate. Compute
time comes from pipeline step start and end times, including earlier runs and
archived attempts. When a step records granted cores, its duration is
multiplied by them; legacy records without a core allocation use one core.
Claimed environment deploy jobs count one core from claim to report; a job
without a report counts until its five-minute lease ends. Archived projects
and their current-week pipeline steps remain in the workspace total. If any
token source of a limited scope is only partially readable, new work waits for
the meter rather than treating missing records as zero usage; the refusal names
the project and the unreadable source.

Usage is computed once per workspace for each task index generation and kept
for 30 seconds; concurrent readers share one computation. Admission does no
metering at all while no limit applies to the scope. Token and core-hour
admission is a check, not a reservation: work admitted before the limit is
reached runs to completion, so the overshoot is bounded by the work that was in
flight. Hosting slots are reserved atomically with their admission.

An unreadable quota settings file keeps the last good settings in memory, logs
a warning, and marks admissions as meter unavailable.

Hosting counts running project environments. Website dispatch reserves its slot
before sending the workflow and releases it when dispatch fails. A failed
workflow releases the slot only when that dispatch created the reservation, so
a failed redeploy keeps the slot of the live deployment. An inactive GitHub
Pages deployment is also found by the minute reconciliation service, without a
publish panel refresh. Refresh and dispatch use the same lock so an older run
cannot release a newer reservation.

Declared environments (`docs/deployments/environments/*.json`) take a slot on
their first deploy. A release, an approved promotion, and a rollback are
admitted with a hosting slot when the target environment is not running yet;
a refusal answers `409` with the reason. A deploy job claim reserves the slot
atomically; a refused job stays queued for the next claim. A healthy report
marks the environment running, a failed first deploy returns the slot, and
`POST /api/projects/{project}/environments/{environmentId}/stop` records an
operator-confirmed stop and frees the slot. Removing a declaration frees its
slot on the next environment read. A timed promotion that quota refuses stays
pending.

Status is derived from tokens and core-hours: `warning` at 80% and `blocked`
at 100%. Full hosting slots are a separate `hosting-full` state that refuses
only new deploys ("All N hosting slots are in use; new deploys wait until an
environment stops.").

New coding runs, review and gate claims, publish and deploy actions check
quota at admission. The monolith gate store reads the queued subjects'
projects from the task index once per claim and asks at most once per project,
outside its claim lock. The standalone Task Server calls the monolith's
`GET /api/quota/admission/{project}` with the project name. An unregistered
project is admitted with `meterUnavailable` so a name mismatch surfaces as a
meter event instead of stopping its claims.
`TaskServer:QuotaAdmissionBaseUrl` is the explicitly configured backend origin;
there is no implicit development port. Without it, quota admission is off in
the Task Server: it logs that once at startup and reports `quotaAdmission`
`off` in `GET /api/v1/management/status` (`on` while the meter answers,
`unreachable` while a project's meter is unavailable), with no per-claim event. The Task Server bearer must carry the Runner
claim scope in the backend, or `TaskServer:QuotaAdmissionBearerToken` must match
the backend's service credential setting. The admission route accepts that
credential without a human session and grants no access to other quota routes;
an API token cannot use it. The Task Server keeps a per-project verdict for
about 30 seconds, refreshes stale verdicts before a claim outside its write
gate and SQLite transaction, and reads only that cache inside the transaction.
If a configured meter becomes unreachable, the Task Server publishes one
`quota.meter-unavailable` operational event per project when it becomes
unreachable, adds one card event to the first card it claims for that project
during the outage, and publishes one `quota.meter-recovered` event when it
recovers; claims proceed meanwhile. A project without a cached verdict yet is
admitted without an event. At 80% usage, Studio displays a warning. At 100%, admission returns
a reason without interrupting active work. A weekly owner override allows new
admissions in its scope; an override on a project does not exempt it from a
blocked workspace limit. An override by an API token is audited as
`api:<tokenId>`.

The board header and eligible cards show the current stop reason: the Studio
shell's header cockpit (`StudioHeaderCockpit`) reads
`GET /api/workspaces/{id}/quota/admission` for the selected workspace, or the
default workspace in the all-projects view, once on change and then every
minute into `WorkspaceQuotaBoardState`. That one response carries the
workspace scope and every project scope. `WorkspaceQuotaBannerComponent`
renders the warning, stop, or hosting-full state as a header row, and
`TaskCardQuotaNoticeComponent` renders the refusal on cards that are neither
running nor finished.
