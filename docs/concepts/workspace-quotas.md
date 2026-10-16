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
If a configured scope cannot read its token meter, new work waits for the
meter rather than treating missing records as zero usage.
Hosting counts recorded running project environments. A successful website
dispatch records its environment slot. The quota service exposes an explicit
stop update for environment lifecycle integrations.

New coding runs, review claims, and publish or website dispatches check quota
at admission. At 80% usage, Studio displays a warning. At 100%, admission
returns a reason without interrupting active work. A weekly owner override
allows new admissions in its scope; an override on a project does not exempt it
from a blocked workspace limit. The board header and eligible cards show the
current stop reason.
