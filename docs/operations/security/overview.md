# Security overview

> **Status note (2026-07-07).** ADR-0059 promotes **remote execution** (Linux
> runner hosts + a central task-server URL) from explicit non-goal to a major
> goal. The "local-only" situation below still describes the **current,
> deployed** state and stays accurate until the remote phases land — but the
> product-thesis framing is superseded, and this document must be rewritten
> with a real threat model (auth on the central URL, SSH-provisioned runner
> hosts, per-runner identities) **before** any port is exposed beyond SSH.
> Plan of record: [concepts/distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md)
> (the central-URL auth boundary and runner split gate the remote phases).
>
> **Target clarification (2026-07-13).** The required human login, Runner
> service identity, HTTPS boundary, audit, and management model is now defined
> in [Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md#8-security-baseline-for-an-internet-reachable-server).
> The local-only text below is current-state documentation, not the networked
> target.

Agent Software Studio has two explicit security profiles. They solve different
problems and must not be confused. This page is the networked-threat-model
rewrite required by ADR-0059 before any port is exposed beyond SSH; the target
contract lives in
[Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md#8-security-baseline-for-an-internet-reachable-server).

| Profile | Intended use | Network exposure | Authentication |
|---|---|---|---|
| `local` | One trusted operator on one machine | Loopback only | No network authentication; `X-Client-Id` is attribution only |
| `networked` | One small organization using a separately hosted Task Server | HTTPS through the reference reverse proxy | Human server sessions, scoped API tokens for tools, and distinct Runner service credentials |

`X-Client-Id` is attribution in both profiles. It is never proof of identity.
The networked middleware derives the authenticated principal from the secure
session cookie, API token, or Runner bearer credential and records any supplied
`X-Client-Id` separately.

The local management plane has one deliberate profile-specific trust rule:
requests to `/api/v1/management/*` are accepted without a human session only
when they arrive over loopback as the seeded `X-Client-Id: local-default`
operator. This preserves account-free single-seat operation without turning
`X-Client-Id` into a network credential. In the `networked` profile every
management request requires a human owner or operator session; anonymous
requests return `401` with the Studio login entry.

## Networked threat model

The central URL contains task prompts, code and review evidence, CLI output,
project configuration, live events, and controls that start agents. Assume the
internet is hostile, credentials can be phished, browsers can be induced to
send cross-site requests, and a Runner host may need to be removed quickly.

The profile protects against anonymous reads and writes, password database
disclosure, session theft through JavaScript, browser CSRF, credential replay
after revoke or expiry, over-scoped Runner credentials, anonymous SignalR
reconnects, accidental cleartext exposure, and secrets appearing in shipped
Runner logs. It does not claim to protect a project from an authorized agent
that is already allowed to edit that project's checkout.

## Identity and authorization

- The first successful HTTPS bootstrap creates the sole initial owner and then
  closes permanently.
- Human passwords are stored as versioned salted PBKDF2-SHA512 hashes with
  600,000 iterations. Login failures are limited per client (10 failures,
  then a 15-minute lockout) and per username across clients (20 failures per
  15 minutes, then a short per-client delay). Full tracking tables keep their
  active budgets and fall back to fixed-size overflow counters. The settings
  are under `Security:LoginThrottle`; the
  [Studio website guide](../setup/studio-website.md#login-protection-behind-cloudflare)
  describes them. Password resets create a temporary password, revoke existing
  sessions, and force a change at the next login.
- Human sessions use a random opaque token stored only as a SHA-256 hash on the
  server. The cookie is `Secure`, `HttpOnly`, `SameSite=Strict`, host-only, and
  has idle plus absolute expiry. Browser mutations require the matching CSRF
  header. Because SameSite does not separate sibling hosts of one site, every
  unsafe request that carries a session cookie, and every sign-in, bootstrap,
  or invitation acceptance, must also carry a matching `Origin` or
  `Sec-Fetch-Site: same-origin`. Event streams (`/hubs/*` and every WebSocket
  upgrade) that carry a session cookie are checked on every method: a present
  `Origin` must match, a present `Sec-Fetch-Site` must be `same-origin`, and
  `Origin: null` is refused. Requests without either header from non-browser
  clients stay allowed. The networked profile accepts only the `__Host-`
  session cookie and sends it with `Secure`, `Path=/`, and no `Domain` attribute.
  Studio exposes an explicit sign-out action; a logout or API 401 tears
  down browser polling and SignalR before returning to the login gate.
- Roles are intentionally small: owner manages identities and all projects,
  operator reads and mutates allowed projects, contributor (shown as Product)
  reads assigned projects, creates wishes and cards, edits Backlog and Ready
  cards, uses project and card chat, comments on previews, and accepts a
  Human Review delivery; viewer is read-only. A contributor cannot access
  credentials, hosts, settings, quotas, merges, promotions, or raw logs.
  Product cards carry no run controls: project defaults choose the CLI, model,
  and mode, and the card records the signed-in user as creator. Product
  sessions read only screenshot, HTML, and Markdown result files and never run
  conversations, session events, or agent work detail. The per-route task
  contract is in
  [Product session task access](../../system/domains/tasks.md#product-session-task-access-agt-3182).
  Production approval is a separate grant that an owner gives for one assigned
  project. Operator, contributor, and viewer are scoped roles. Their project membership list
  restricts project routes; an empty list grants no project, and only the
  explicit `*` entry grants every project in this single organization. The
  route or addressed task decides the project; a `?project=` query value may
  only narrow that decision and never replaces it. Workspace task lists,
  Runner status and feed data, registry data, and global search results are
  filtered to the same allowed-project set. Body-addressed task-set mutations
  (reorder, batch-move) carry their targets in the request body, so their
  handlers enforce membership on every affected task; a single-task route whose
  project cannot be resolved is denied rather than allowed, so an unresolved
  project never becomes an authorization bypass. SignalR connections and
  subscriptions honour the forced password change and join only the groups
  of allowed projects.
  Preview comments are appended to the card timeline through
  `POST /api/tasks/{jobId}/preview-comments`, with the authenticated user as
  actor and the preview ID as the reference.
- A mapped route can declare a minimum role (`StudioRoleRequirement`), which
  the networked profile enforces for sessions and API tokens. Every local
  first-run route (`/api/first-run/...`: Git setup and repository probe, read
  and write) is mapped only in the local profile and requires the owner,
  matching the Task Server runner Git setup routes; others get
  `403 owner-required`. A networked checklist uses the Task Server plane. When
  the monolith has no Task Server connection, the checklist reports that the
  plane is unavailable. `AccessSecurityRouteInventory` lists the owner-only
  route families, and its test fails for any mapped route there without the
  declaration. The local loopback profile is unchanged.
- In the networked profile, creating, updating, removing, or reordering a
  repository-identifying project URL requires the owner, including candidates
  that are not currently selected. The project, actor, selected URL before and
  after, candidate lists before and after, and timestamp are written with the
  project mutation in `<TaskRepository>/.metadata/projects.json`.
- In the networked profile, only the owner can create or change project URL
  start rules, start a saved rule, or run a temporary rule through the URL test
  endpoint. Rule changes are audited with before and after digests in the same
  project registry write. Repository checkout and root path changes also require
  the owner. Login health fails when a resolved visitor address is an internal
  peer, indicating a broken trusted proxy chain.
- Owners mint short-lived one-time Runner enrollment codes. Enrollment reveals
  one service credential once. The server stores only credential hashes.
  Credentials carry explicit scopes, optional expiry, last-use time, and
  independent revocation. Rotation creates an overlapping credential so the
  old one can remain valid until the new daemon is proven.
- Settings > Access lets the owner invite, update, reset, or disable users;
  manage role bindings, groups, and service accounts; create and revoke named
  API tokens; create, list, and revoke runner enrollment codes; and revoke
  enrolled runners. API tokens use the operator or viewer role and an
  explicit project list (`*` grants all projects). The creation response shows
  the secret once; subsequent lists expose metadata only: creator, expiry,
  last use, and revocation. The existing `.security/access-state.json` holds
  their hashes alongside the other access credentials. A disabled creator
  cannot authenticate with a token they created.
- The browser uses `/api/auth` for a networked monolith session. When that
  endpoint is absent or reports `local`, the existing standalone Task Server
  Studio sign-in remains the frontend's auth path.
- Scripts send an API token as `Authorization: Bearer` in the networked
  profile. The task-api reference scripts accept `TASKBOARD_URL` for the HTTPS
  origin and `AGENT_STUDIO_API_TOKEN` for the secret. `X-Client-Id` remains
  attribution only. API tokens cannot call access administration, versioned
  Task Server, Runner service, or SignalR routes. A viewer token cannot mutate.
  API tokens are service accounts and pass the same named-permission check.
  Routes without a project address, including workspace timelines,
  expensive-jobs lists, and screenshots, require the `*` project grant. A
  `?project=` query cannot turn a workspace route into a project route.
- A Runner never receives a human password or session cookie.

### Rights model: role bindings per scope (IP-8)

The networked profile authorizes every human and service-account request
through role bindings (Dossier integrierte-plattform §9.5, decision IP-D9 A).
A binding is a principal (user, group, or service account) times a role times
a scope (the workspace, one project, or one environment of one project). The
effective permission set is the union of a principal's bindings, direct and
through its groups. Code: `backend/Features/Security/Iam/`.

| Role | Bindable at | Holds |
|---|---|---|
| Owner | workspace | every permission; break-glass role, the only one with `access.read`, `access.manage`, and `workspace.administer` |
| Admin | workspace, project | settings, operation, production deploy and approval, `secrets.write` |
| Developer | workspace, project, environment | cards, runs, chats, documents, non-production deploys |
| Product (`contributor`) | project | cards and chat, narrowed further by the Product allowlist |
| Viewer | workspace, project, environment | reads, secret metadata included |
| Approver | workspace, project, environment | `environment.production.deploy.approve` and reads |

- Every route declares one named permission, such as `project.cards.write`,
  `environment.production.deploy.approve`, `secrets.write`, or
  `secrets.resolve`: explicitly with `RequirePermission(...)` (or
  `RequirePermissions(read, write)` for the Task Server proxy
  `/api/v1/{**path}`: `workspace.read` and `workspace.operate`) or through one
  rule of `RoutePermissionCatalog`. `RoutePermissionInventoryTests` fails for
  any mapped route without a declaration, in the local, networked, and
  standalone-Task-Server host profiles. The middleware refuses an undeclared
  route with `permission-undeclared`; a method mismatch keeps its 405.
- The route decides the addressed project as before, read from the matched
  route pattern: a literal segment after `/api/tasks/` names no task, so the
  board (`/api/tasks/grouped`, also `/api/v1/studio/board`),
  `code-review/defaults`, and `commit-owner/{sha}` are unaddressed reads and
  `DELETE /api/tasks/orphan-folder` is workspace-wide (`workspace.operate`).
  `/api/analysis/{project}`, `/api/drift/{project}`, `/api/supervisor/{project}`,
  `/api/bus/{project}`, and `/api/runtime/{project}` address that project. A
  supervisor `cancel-run` or `force-fail` refuses (404 `job-not-in-project`,
  no intervention record) a job id that is not a card of the route project,
  because the intervention stops the run its job id names anywhere. The
  workspace health overview `/api/environment/health` lists only environments
  of projects the caller's bindings grant the read on. A binding covers a
  project route when its scope is the workspace or that project; workspace-wide
  routes need a workspace binding. Unaddressed reads accept any binding because
  their handlers filter per project. Unaddressed writes need a workspace
  binding, except task creation, reorder, batch-move, and the title and prompt
  helpers, whose handlers check the route's permission for every project they
  touch (`ProjectAccessAuthorization.AllowsTasks(context, projects,
  permission)`). Decision receipt lists filter by `project.read`; recording an
  operator intent also requires `project.runs.operate` on the receipt's own
  project. An environment binding grants its environment-level
  permissions in that environment and read access to the project.
- Moving a card to another project checks `project.cards.write` on both the
  source and destination. A Product binding in the destination cannot perform
  the move. A workspace binding still admits an unresolved card address so
  the handler can return 404; a project-only binding does not gain access to
  an unresolved address. Task ids and keys are resolved from resident card
  facts without waiting for a workspace scan. A scoped request must have the
  required permission in every project whose card matches the supplied id or
  key, including when a query names one project. Workspace-only bindings can
  be decided without a task lookup. New ids and keys are reserved across the
  workspace, and a move that would rename a card to a conflicting identity is
  refused.
- Hub project groups and conversation subscriptions use the current role
  bindings for `project.read`. The unscoped live-event group requires a
  workspace binding. Reading one `/api/v1/reviews/attempts/{id}` result also
  checks `project.read` against the attempt's card project and returns 404
  outside that scope. Canonical and legacy task keys resolve the project from
  the registry; bare task ids use the published task index. A workspace
  binding can read the attempt even after its card folder is removed. The older `/api/attempts/tasks/{key}`,
  `/api/attempts/runs/{id}`, and `/api/attempts/reviews/{id}` reads require
  `workspace.operate`, because their handlers do not resolve a project.
- Filesystem-layer snapshots accept a host folder path and can refresh a
  snapshot in that folder. Their routes require the Owner's
  `workspace.administer` permission. Managing installation secret backends
  also requires `workspace.administer`; deploy-job artifact reads require a
  Runner service credential. Hosted-volume reads require `project.read` on
  their project; schedule, backup, and restore require
  `environment.production.deploy` on the declared environment.
- Installation-wide prompt overrides, client identities, watcher and crash
  recovery records, workspace summary and quota records, and git telemetry
  require `workspace.operate`. Project-scoped bindings alone do not admit
  these unfiltered collections. Parked cards, pickup holds, and workspace
  screenshots remain available with `workspace.read`; their handlers filter
  entries by the caller's project bindings before returning them.
- Project quota snapshots under `/api/workspaces/{workspace}/projects/{project}/quota`
  and the global auto-review status require `workspace.operate`, because their
  responses are not project-filtered. Host execution infrastructure, agent-rule
  paths, ad-hoc usage, admin configuration, acceptance-rail counts, and
  integration-bounce metrics are workspace operator reads.
  Wiki search and steering-doc reads skip links in every path segment so a
  repository link cannot disclose a file outside that repository. Streamed
  global search visits only repositories the caller can read.
- CLI usage, transcripts, working memory, and session state require
  `workspace.operate`. The static catalogues, model list, maintenance model,
  effective mode, and quota strip reads use `workspace.read`. Quota refresh
  writes still require `workspace.operate`. Scoped quota reads omit raw CLI
  samples, which may include an account address. The interactive CLI `_probe` is
  mapped only in the local profile. A pipeline catalogue request with `projectName` checks
  `project.read` for that project before reading its settings.
- When `TaskServer:BaseUrl` is configured, `/api/v1` reads pass through the
  standalone proxy under its workspace permission. The proxy does not filter
  Task Server responses by project. The networked Studio stack leaves that
  setting unset; deployments that enable it must treat the proxy as a
  workspace-wide surface.
- Wiki and security document paths reject symbolic links at every segment
  beneath the project root, including the document itself.
- Environment scopes name the environment kind of the project's declarations
  (`docs/deployments/environments/*.json`): `stage`, `production`, or
  `other`, never an environment id. Releases address `stage`, promotions and
  the card release flag address `production`, and a rollback resolves its
  `environmentId` to the kind of that declaration (an undeclared id resolves
  to no environment, so only workspace and project bindings grant it). The
  deployment-request route reads `environmentId` from the JSON body and
  requires `environment.deploy` on that declared environment in both the
  route permission check and the handler's scope check. A stage binding cannot
  request a deployment to another environment. The store refuses any other
  environment key with `invalid-scope`.
- Deployment status, release, deployment, log, and audit reads require
  `project.read`. Deployment actions require a deploy permission. Production
  approve and reject routes require `environment.production.deploy.approve`;
  a human Approver bound at the workspace, project, or production environment
  may decide in scope. Service accounts cannot approve. The deployment
  lifecycle still checks the approval when it processes the decision.
  Deployment intervention requires `environment.production.deploy` for the
  production environment. Batch gate queue-stall release and review
  observation writes require `project.runs.operate`; recording operator
  candidate verification additionally requires a human Admin or Owner and
  `workspace.operate` on the workspace.
- Runbook definitions, runs, and logs require `project.read`. Requesting a
  run requires `environment.deploy` for the declared environment named in the
  route. Approving or rejecting a protected run requires
  `environment.production.deploy.approve`; service accounts cannot approve.
- The deployment card matrix requires `workspace.read` and filters project
  rows. Run targets use Runner credentials with the deploy scope for desired
  and observed state, and the handler checks the enrolled host id. Stopping
  an environment or confirming its removal from a run target requires
  `environment.production.deploy` on the environment's declared key.
- Application telemetry queries and ingest-token lists require `project.read`.
  Issuing or revoking an ingest token requires `project.settings.write` for
  that project. OTLP intake authenticates the ingest token itself and grants
  no Studio session or service-account access without one.
- Workspace secret metadata requires `secrets.read` on the workspace.
  `/api/secrets` and `/api/secrets/audit` filter each entry by its actual
  workspace, project, or declared environment scope. A service account with
  `secrets.write` may manage secrets in scope; records and audit entries name
  it as `service-account:<id>`.
- Requests never authorize through the stored legacy fields. After the
  permission decision the middleware gives the handler a projected principal
  for this request: `role` is the strongest role among the workspace and
  project bindings that grant the route's permission on the addressed
  project(s) (Admin and Developer appear as `operator`, Approver as `viewer`),
  and `projects` are the projects where the route's permission holds. An
  environment binding ranks as its role only for a request in its own
  environment and as Viewer elsewhere. A user who is Product in one project
  and Developer in another project, or Developer of one of its environments,
  therefore meets the Product lane rules in the first.
- Operator authority belongs to Admin and Owner. An `operatorOverride`
  completion needs Admin or Owner on the card's project (else 403
  `operator-authority-required`), and a decision card for `role:operator`
  accepts Admin and Owner only. Developer acts as `operator` for the other
  handler checks but decides a card only as `role:developer`. The Task Server proxy forwards `owner` for
  the Owner, `operator` only with `workspace.operate` on the workspace, and
  `viewer` otherwise.
- **Tightening for legacy operators.** `workspace.settings.write` and
  `workspace.operate` are workspace-level permissions. A legacy operator with
  a project list becomes Admin of those projects and keeps every project
  permission there, but can no longer change workspace, model, quota, prompt,
  or tag settings, create projects, or operate the installation (Task Server
  management, hosts, sweeps, recovery). The same holds for unaddressed writes
  in general. Give such a user Admin on the workspace to restore it. Only
  the Owner reads or manages access, as before the rights model.
- Quota admission for the Task Server and Runner declares `runner.service`;
  its dedicated service credential or Runner claim check still applies.
  Reading quota audit or changing quotas requires `workspace.administer` and
  the Owner handler check. Service accounts do not satisfy that Owner check.
- Service accounts are non-human principals with their own bearer tokens
  (`sat.` prefix), stored as hashes with expiry (default 90 days, at most one
  year), last use, and revocation. They need no CSRF token, can hold Admin,
  Developer, or Viewer, never hold a production approval, and need a
  workspace binding on unaddressed routes. They never call access
  administration or the versioned `/api/v1` plane. Handlers, audit records,
  and card timelines all see them as the one non-human actor
  `service-account:<id>`, which is also the actor of rollbacks and
  moves they trigger. Because that actor is not `human:`, a lane move by a
  service account or API token no longer counts as an operator requeue out of
  a decision lane or as an operator lane verdict, as a human operator's move
  does; the automated review path treats it like any other non-human
  transition. Runners and the engine keep their enrollment credentials and
  Runner scopes.
- API tokens (AGT-3186) are service accounts with one `api.` token, created
  under `/api/auth/api-tokens` with Admin (`operator`) or Viewer bindings for
  the requested projects. They keep the API-token limits: no
  installation-wide writes even with `*`, and no use once the creating user is
  disabled.
- Secret routes (AGT-3222) declare `secrets.read` for metadata reads and
  `secrets.write` for put, rotate, revoke, and delete, at the workspace
  (`/api/secrets/workspace`, which needs a workspace binding), project
  (`/api/projects/{project}/secrets`), or environment scope
  (`/api/projects/{project}/environments/{environment}/secrets`, whose
  environment id resolves to its kind like a rollback). No human route returns
  a value (write without read, IP-D8 A): `POST /api/runner/secrets/resolve`
  accepts only a Runner credential bound to a live lease (`runner.service`).
  `secrets.resolve` is declared for workload identities and grants no route
  yet.
- Migration: loading an older access state migrates a copy and replaces the
  live state only after a backup and the save succeed. Each migration writes
  its own backup `.security/access-state.v<from>.<UTC timestamp>.json`; older
  backups stay untouched. From version 0, owner becomes Owner on the
  workspace, operator becomes Admin, viewer stays Viewer, the Product role
  becomes Product per assigned project plus Approver on the `production`
  environment of each production-approval project, and the `*` entry becomes
  the workspace scope. An operator or viewer with an empty project list held
  no project and receives no binding. API tokens become service accounts that
  keep their token id and secret hash, so existing `api.` secrets keep
  working. Sessions, runners, enrollments, and invitations are unchanged. A
  single-user installation migrates its owner and needs no setup; a fresh
  bootstrap binds the first owner directly.
- Each user record keeps `role`, `projects`, and `productionApprovalProjects`
  as a downgrade-safe summary: only Admin is summarized as `operator` (with
  the Admin projects), Product as `contributor`, everything else as `viewer`,
  and a user without bindings as a viewer of no project. **Downgrade:** an
  older binary reads only this summary and drops role bindings, groups, and
  service accounts (API tokens included) the first time it saves the file;
  restore the migration backup to return to the old state. A later upgrade
  migrates again from that summary, so bindings, groups, and service accounts
  made under the rights model would be lost. Before a downgrade, copy the
  current `.security/access-state.json` (version 2) aside; before upgrading
  again, restore that copy.
- The legacy user endpoint keeps working: `PUT /api/auth/users/{id}` renames
  or disables without touching bindings when `role`, `projects`, and
  production approvals equal the current summary. A real role change replaces
  the user's direct bindings and keeps group bindings; it returns 409
  `bindings-managed` when the direct bindings cannot be expressed as a legacy
  role (for example Developer or Approver). Invitations accept explicit
  `bindings`. Validation runs on a copy; a refused update changes nothing.
- Access administration lives under `/api/auth/iam` and in Studio under
  Settings → Access, which also lists API tokens, runner enrollments, and
  runners. The store refuses any change that would leave the installation
  without an active Owner or that removes the acting Owner's own Owner role.
  With a standalone Task Server the Studio's accounts are the Task Server's;
  this area then reports that and manages nothing.
- `.security/iam-audit.jsonl` records every binding, group, membership,
  service-account, token, and legacy role change with the acting user and the
  time, append-only.

The minimum Runner scopes are `runner.claim`, `runner.lease`, `runner.logs`,
`runner.events`, `runner.artifacts`, and `runner.completion`. Reading the prompt
for a claimed task is covered by `runner.claim`. Lease requests bind the wire
`runnerId` to the authenticated service identity.

## Transport and exposed surfaces

The networked profile refuses non-HTTPS application requests, except the
minimal `/healthz` liveness endpoint. It trusts at most two forwarded-header
hops from configured proxies. The reference Caddy deployment owns ACME renewal, redirects HTTP,
sets HSTS, caps request bodies, forwards WebSocket upgrades, and serves Angular
from the same origin as `/api` and `/hubs`.

Every networked Studio response carries a Content-Security-Policy with
`frame-ancestors 'self'`, `Cross-Origin-Opener-Policy: same-origin`, a minimal
`Permissions-Policy`, `X-Content-Type-Options: nosniff`, and HSTS with
`includeSubDomains`. Responses without a sandbox policy also carry
`X-Frame-Options: SAMEORIGIN`. The API
sets them in `StudioResponseHeaderPolicy`, registered directly after the
forwarded-headers middleware so short-circuits, rejections, unmatched routes
and exception-handler errors carry them too. API responses never get the shell
policy:

- Documents a browser can render (`text/html`, `application/xhtml+xml`,
  `image/svg+xml`, `text/xml`, `application/xml`), such as agent-written task
  files and wiki SVG assets, get
  `sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'self'`.
  Stored markup gets an opaque origin and runs no script.
- Every other API response gets
  `default-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'`.
- The URL preview proxy and task result HTML keep their own sandbox policy and
  get no `X-Frame-Options`, because Studio frames those opaque-origin documents
  itself. The `/recovery` console has its own policy that allows only its
  inline script and calls to the Studio API.

The networked response middleware forces `Cache-Control: no-store` on all
`/api/*` and `/hubs/*` responses and on responses with a session cookie.

The website stack's frontend Caddy sends the shell policy
(`FrontendShellContentSecurityPolicy`) for the static Angular shell only. It
keeps `'unsafe-inline'` in `script-src`: sandboxed `srcdoc` frames inherit the
shell policy and run the inline scripts of repository HTML on their opaque
origin. Serving that HTML from a separate origin would allow a stricter shell
script policy.

The first-owner bootstrap skips the installer code only for a direct loopback
connection. The decision uses the connection peer captured before forwarded
headers apply, so a forwarded `127.0.0.1` (for example a `CF-Connecting-IP`
value) never counts as loopback.

Anonymous API reads, mutations, client registration, Runner registration, hub
negotiation, and reconnects fail closed. Debug, internal probe, and filesystem
diagnostic endpoints are not mapped in the networked profile. Error details are
disabled. The health response is only `ok` and exposes no version, dependency,
workspace, or identity information.

## Audit and credential handling

Remote claim, event, and completion records are appended under
`<TaskRepository>/.security/run-audit.jsonl`. Run records contain both the
initiating task owner or automation identity and the authenticated executing
Runner identity plus credential id. Security state and audit files are written
with owner-only permissions on Unix.

Each authenticated API-token call appends its token id, creator, method, route,
and response status to `<TaskRepository>/.security/api-token-audit.jsonl`;
other service-account calls go to `service-account-audit.jsonl` with the same
fields and the actor `service-account:<id>`. Refused calls are recorded too. Token values
and query strings are omitted from that audit. IAM changes go to
`iam-audit.jsonl`.

Networked project repository candidate changes are recorded in the registry's
`RepositoryAudit` list in the same atomic write as the project URL records.

Known credential shapes and named authorization, cookie, and password fields
are redacted from Runner log ingestion. Production exceptions omit stack
traces. Plaintext session, enrollment, and Runner secrets are never persisted
and are returned only by their creation response.

## Repository hygiene (Quality Studio sensors)

Quality Studio's `dependencies` and `gitleaks` sensors evaluate this checkout.
AGT-2991 brought both to a clean posture; keep them there.

- **Dependencies.** Runtime packages (Angular, `dompurify`, `@tiptap/*`, and
  the backend's `HtmlSanitizer`/`AngleSharp`) stay on patched releases. Build
  tooling is refreshed with `npm audit fix` in `frontend/`. Check with
  `npm audit` there and `dotnet list agent-taskboard.sln package --vulnerable --include-transitive`.
  Generate the frontend lockfile with `corepack npm install` from `frontend/`.
  Its `packageManager` declaration pins npm 11.21.1; the repository's
  `scripts/npm-frontend.sh` uses that version for automated installs and
  checks. The host's npm 10 can fail with `Cannot read properties of null
  (reading 'edgesOut')` while resolving optional peers or reject a lockfile
  generated by npm 11.
- **Secret scan baseline.** Synthetic test credentials are accepted narrowly
  in [`.quality/security/gitleaks.baseline.json`](../../../.quality/security/gitleaks.baseline.json),
  one exact `file:rule:line` fingerprint plus a `Note` per entry. Keep the fixture values
  obviously synthetic. Prefer the baseline over inline `gitleaks:allow`
  markers when the fixture file also contains private-key or token material,
  because the platform commit gate
  ([`CommitCandidateGate`](../../../backend/Features/Git/CommitCandidateGate.cs))
  scans the whole file on any edit and ignores gitleaks markers. A line shift
  in a baselined file surfaces the finding again, which is intended: re-review
  it and update the fingerprint.
- **Bounded reads.** Code that reads a file the reader does not control (CLI
  logs, JSONL ledgers, sidecars, agent-written `results/` files, sweep
  matches) goes through
  [`BoundedFileRead`](../../../backend/Shared/BoundedFileRead.cs) instead of
  `File.ReadAllText`/`ReadAllLines`. Whole-content readers refuse an oversized
  file; line readers keep the newest complete lines. A writer that updates a
  ledger row must keep working on an oversized ledger: read the newest window
  with `ReadTailLineWindow`, change the row there, and rewrite the file only
  from the window's `Offset`, so older rows keep their bytes. Never rewrite
  the whole file from a truncated window, because that deletes the older rows.

## Trust considerations in both profiles

- **Task folders are external to the product checkout.** The app reads and
  writes task folders below the configured central `TaskRepository`. Legacy
  watch-path configuration can still point at another directory, so a hostile
  or mistaken configuration could expose unintended paths.
- **CLI quota credentials.** Agents read their own auth state from disk (Claude session files, Copilot tokens). The app never reads or stores those secrets, but bugs in CLI drivers could conceivably leak prompts or session IDs into logs.
- **Markdown rendering.** Frontend renders Markdown for status, prompts, and now these security/architecture docs. The renderer is hand-written and avoids `innerHTML` for user input outside the dedicated editor; review it whenever you touch the markdown utility.

Deployment and rotation procedures are in
[networked-task-server.md](../setup/networked-task-server.md), and are exercised
by [`deploy/networked/rehearse-runbooks.sh`](../../../deploy/networked/rehearse-runbooks.sh)
with captured evidence in
[runbook-rehearsal-evidence.md](runbook-rehearsal-evidence.md). The detailed
target contract and lifecycle matrix are in
[distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md).
