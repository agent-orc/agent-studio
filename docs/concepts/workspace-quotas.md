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

Token counts come from the merged project token ledger, with cached input
counted once. The price catalogue supplies a separate USD estimate. Compute
time comes from pipeline step start and end times, including earlier runs and
archived attempts. When a step records granted cores, its duration is
multiplied by them; legacy records without a core allocation use one core.
Archived projects and their current-week pipeline steps remain in the workspace
total. If any token source is only partially readable, new work waits for the
meter rather than treating missing records as zero usage.
Hosting counts recorded running project environments. Website dispatch reserves
its slot before sending the workflow and releases it when dispatch fails. A
failed workflow releases its slot when the publish run is refreshed. An inactive
GitHub Pages deployment is also found by the minute reconciliation service,
without a publish panel refresh. Refresh and dispatch use the same lock so an
older run cannot release a newer reservation. The quota service exposes an explicit stop
update for other environment lifecycle integrations.

New coding runs, review and gate claims, and publish or website dispatches check
quota at admission. The standalone Task Server calls the monolith's
`GET /api/quota/admission/{project}` with the project name before coding,
review, and gate claims. An unknown project is refused.
`TaskServer:QuotaAdmissionBaseUrl` is the explicitly configured backend origin;
there is no implicit development port. The Task Server bearer must carry the
Runner claim scope in the backend, or `TaskServer:QuotaAdmissionBearerToken`
must match the backend's service credential setting. The admission route accepts
that credential without a human session and grants no access to other quota
routes. If a configured meter is unreachable, claims proceed with a card event,
claim message, and operator event describing the outage. At 80% usage, Studio displays a warning. At 100%, admission
returns a reason without interrupting active work. A weekly owner override
allows new admissions in its scope; an override on a project does not exempt it
from a blocked workspace limit. The board header and eligible cards show the
current stop reason: the Studio shell's header cockpit (`StudioHeaderCockpit`)
reads the admission of the selected workspace, or of the default workspace in
the all-projects view, and of its projects once on change and then every
minute into `WorkspaceQuotaBoardState`. `WorkspaceQuotaBannerComponent` renders
the warning or stop as a header row, and `TaskCardQuotaNoticeComponent` renders
the refusal on cards that are neither running nor finished.
