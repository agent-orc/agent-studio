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
`X-Client-Id` into a network credential. A Studio whose hosts reach it through
SSH tunnels should set an [operator port](../setup/github-app.md#keep-the-tunnels-out-of-the-operators-routes)
so those hosts cannot reach the routes that decide who may reach what. In the `networked` profile every
management request requires a human owner or operator session; anonymous
requests return `401` with the Studio login entry.

In the networked profile, checkout sync and runner-host provider authentication
and sign-in are Owner-only. URL start rules run with a minimal child environment
that excludes the Studio API's credentials and service configuration. Host Git
processes disable repository hooks and fsmonitor and require explicit bare
repository access. The selected mitigation for repository scripts is the
Owner-only checkout sync gate; the Owner remains responsible for code promoted
into the developer checkout before starting a URL preview. Clearing the child
environment does not isolate a start rule: it still runs as the Studio API user
and can read absolute paths accessible to that account. Networked build-profile
validation is refused until a remote validation protocol is available; profile
edits and URL target changes require the Owner.
In the networked and public-demo profiles, host Claude one-shots use a
temporary empty working directory, disabled tools, and an empty strict MCP
configuration. The spawn boundary disables Claude attachments, and the shared
stdin boundary replaces every `@` in delivered prompt text so punctuation and
zero-width characters cannot introduce file mentions. Security file and wiki
companion writes refuse delivered links; security file and metadata routes
require the Owner in the networked profile. This
boundary protects against CLI file expansion; it does not isolate the CLI
process from host files accessible to the Studio API user. The trusted local
profile retains host build validation with its operator environment and gives
URL start rules the local environment. Its canonical Claude one-shot runner
keeps the requested working directory and tools.

**Accepted risk for single-team installs (2026-10-25), narrowed by SM-11.**
On a host still running agents with the process executor, a Developer-authored
card can read that host's provider credentials and other projects'
checkouts, so everyone allowed to submit coding work to it is one trust group.
SM-11 (see [Per-attempt containment](#per-attempt-containment-sm-11)) makes
the per-attempt work container the Linux default once a host's canary verdict
is go, removes the host service user from the container-runtime group, and
binds attempt tokens to one finished-or-live attempt. Until a host has a go
verdict, and for detached Claude and Codex coding and review sessions until
provider attempt tokens exist, the accepted risk still holds for that host. Do
not admit a second team to a host before `agent-host work-container check`
reports `contained-default` for its roles and the containment probe passes.

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
- Owner and Admin sign-in requires an enrolled authenticator code, a one-use
  recovery code, or a WebAuthn passkey assertion. An account without an enrolled
  factor receives a limited session for factor setup. Every step-up requires a
  registered second factor. TOTP enrollment returns recovery codes once; store them away from
  the browser. Passkey ceremonies require user verification and bind the
  challenge to the current origin and relying party. Each user can list active
  sessions with device, address, creation, and last-use times, revoke one, or
  revoke all other sessions. A signed-in browser shows a notice when it sees a
  newer session. The Owner can do this for any user. Live hub
  connections recheck their session every 15 seconds so revocation closes them.
  Revoking sessions from a user's Access row requires a fresh
  authenticator code, recovery code, or passkey assertion. Verification grants
  one session administration action and expires after five minutes. Sign-in
  itself does not grant step-up authority.
- The standalone Task Server Studio session uses the same 10-failure client
  lockout, 20-failure username budget, 15-minute window, 8-hour idle expiry,
  and 7-day absolute expiry as the networked monolith defaults.
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
- In the networked profile, the readiness and diagnostic checks never request
  a loopback or link-local project URL (`localhost`, `127.0.0.0/8`, `::1`,
  `0.0.0.0`, `169.254.0.0/16`, `fe80::/10`), typically one carried over from a
  laptop setup. The URL reports `local-only` and the UI shows `local only`
  instead of offline. A process Studio itself runs for the URL on the server is
  still checked. `ProjectUrlProbePolicy.Decide` owns this rule. The host also
  skips CLI model discovery and answers from the registry catalogue
  (`networked-registry`).
- Owners mint short-lived one-time Runner enrollment codes. Enrollment reveals
  one service credential once. The server stores only credential hashes.
  Credentials carry explicit scopes, an expiry (30 days by default, see
  [Runner credential binding](#runner-credential-binding-sm-9)), last-use
  time, and independent revocation. Rotation creates an overlapping credential
  so the old one can remain valid until the new daemon is proven.
- Settings > Access lets the owner invite, update, reset, or disable users;
  manage role bindings, groups, and service accounts; create and revoke named
  API tokens; create, list, and revoke runner enrollment codes; and revoke
  enrolled runners. API tokens use the operator or viewer role and an
  explicit project list (`*` grants all projects). An operator token binds the
  narrow `operator-agent` role (OP-1, below); Settings > Access shows that role
  name on the token. Without `expiresAt` a token expires after 30 days; the
  maximum stays 365 days. The creation response shows
  the secret once; subsequent lists expose metadata only: creator, expiry,
  last use, and revocation. The existing `.security/access-state.json` holds
  their hashes alongside the other access credentials. A disabled creator
  cannot authenticate with a token they created.
- The browser uses `/api/auth` for a networked monolith session. When that
  endpoint is absent or reports `local`, the existing standalone Task Server
  Studio sign-in remains the frontend's auth path.
- Scripts send an API token as `Authorization: Bearer` in the networked
  profile. The task-api reference scripts accept `TASKBOARD_URL` for the HTTPS
  origin and read the secret only from `~/.config/agent-studio/operator-token`
  with owner-only permissions. They refuse a file that group or others can
  access (`token-file-permissions`), the retired `AGENT_STUDIO_API_TOKEN`
  variable (`token-environment-refused`, no fallback), and a token on the
  command line (`token-argument-refused`), and redact the token from their
  output. `X-Client-Id` remains attribution only. API tokens cannot call access administration, versioned
  Task Server, Runner service, or SignalR routes. A viewer token cannot mutate.
  API tokens are service accounts and pass the same named-permission check.
  Routes without a project address, including workspace timelines,
  expensive-jobs lists, and screenshots, require the `*` project grant. A
  `?project=` query cannot turn a workspace route into a project route.
- `GET /api/operator/batch-triage` (operator-agent-level OP-4) is a
  `project.read` route without a project address. Unlike the other such
  routes it does not need the `*` grant: the middleware defers the project
  check to the handler, as for `/api/tasks`. Operator and viewer tokens,
  operator sessions and signed-in users may read it. The handler lists only the
  projects the principal may read, so a project-scoped token sees its own
  batch gate flights, members, pending pushes and queue stalls and nothing of
  other projects. It reads only; the per-project batch gate writes keep their
  own `project.runs.operate` check. See the
  [operator API reference](../operator-api.md#batch-triage).

The [operator log and attention routes](../operator-api.md#operator-log-and-card-attention)
use `project.read` for log reads and `project.cards.write` for log and lease
writes, with project scope checked by the handler. Only an Owner may append an
installation-wide (`*`) entry. Operator API-token card writes
require an active 10-minute attention lease and `If-Match` card revision; a
stale revision is refused with 412. Owner card actions end the operator lease
and record a handoff. Token-shaped text is redacted before the Task Server
persists it. Operator sessions use T1 `operator-log.append` and
`card.attention` grants for the two write routes. Their card writes continue
through the SM-4 standing-grant and SM-5 Owner-approval policy.
In the standalone profile, Studio forwards log reads and appends to the Task
Server's `/api/v1/studio/operator-log` route with `tasks:read` and
`tasks:write` scopes respectively.

### Deploy and restart preflight (OP-6)

The [operator preflight routes](../operator-api.md#deploy-and-restart-preflight)
check all configured projects for pending integration pushes, publishing
flights, and cards with a running commit step. A blocker returns HTTP 409 with
a stable code. The deploy plan rechecks these facts and gives the Task Server,
readiness, Engine, and hosts an ordered, expiring update window after drain.
Only an installation-wide reader may see or use the result. An override is a
T2 operation: SM-5 opens an exact-request approval for an operator session,
the Owner decides it in Studio after step-up, and the session may redeem it
once. Read and plan routes do not accept an override flag. No route here
executes host updates over SSH.

The Task Server rejects an Engine or Agent Host whose protocol exceeds its
current protocol before registration or claim, and records
`protocol.startup-refused` in the operator feed with the client and server
versions. The Engine and Agent Host also inspect the returned server version
and refuse startup if an older Task Server incorrectly says it is compatible.
- A Runner never receives a human password or session cookie.

#### Operator chat readiness read

`GET /api/runner/{contextKey}/orchestrator-chat` and the legacy project form
return `executionReadiness` alongside the transcript. The read reports whether
the configured Runner is polling and advertises a fresh Codex CLI and login;
it returns a reason code without exposing credentials. It grants no execution
authority. The browser disables chat submission when the read says the Runner
cannot execute. The POST route retains its server-side placement refusal.

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
| Operator agent (`operator-agent`) | workspace, project; service accounts only | the operator surface: reads, cards, runs, batch gate, review step models (`project.pipeline.write`), installation operation reads, stage deploys |
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
  it as `service-account:<id>`. Project environment secret-binding reads and
  writes require `secrets.read` and `secrets.write` at that environment scope.
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
- A scoped task request checks every card that its id, key, or legacy 128-byte
  id prefix can name, including a card selected by `watchPath`. The detail
  handler checks the resolved card's project again. Create refuses an id that
  shadows a foreign task or uses another project's short-code key pattern;
  generated keys skip occupied identities. These checks use resident task
  facts and create reservations without waiting for a workspace scan on a
  warm request.
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
  under `/api/auth/api-tokens` with Operator agent (`operator`) or Viewer
  bindings for the requested projects. They keep the API-token limits: no
  installation-wide writes even with `*`, and no use once the creating user is
  disabled.
- The `operator-agent` role (Dossier operator-agent-level, OP-1, decision
  OA-D4 B) holds exactly the operator surface: `workspace.read`,
  `project.read`, `project.cards.write` (create, title, lane moves and
  acceptance), `project.runs.operate` (continue and steer rounds, batch gate
  eject and close-now), `project.pipeline.write` (`PUT
  /api/projects/{project}/pipeline-step`, review step models),
  `workspace.operate` for reads such as `/api/watcher/*`, and
  `environment.deploy` (stage releases of gate-verified commits). It holds no
  `secrets.read`, `secrets.write`, or `secrets.resolve`, no production deploy
  or approval, no access permission, no `workspace.settings.write` or
  `workspace.administer`, and no `project.settings.write`. Only service
  accounts take it. A principal whose strongest granting role is
  `operator-agent` cannot make an installation-wide (unaddressed) write, so
  `workspace.operate` stays read-only for it. It does not hold operator
  authority: an operator override completion and the operator decider of a
  decision card stay with Admin and Owner. `POST /api/runner/secrets/resolve`
  answers 403 `secret-resolve-denied` to any service-account bearer.
  `GET /api/quota/admission/{project}` stays a Task Server and Runner claim
  route that no session or token reads; operator tokens read quota through
  `/api/cli/quota` and `/api/cli/quota/model-routes`.
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
  bootstrap binds the first owner directly. Version 3 (OP-1) rebinds every
  Admin binding of an API-token service account to `operator-agent` in the
  same scope and writes one `api-token.role-migration` entry per token to
  `.security/iam-audit.jsonl` (token id, from and to role, bindings). Legacy
  operator tokens from version 0 or 1 bind `operator-agent` directly and get
  the same entry. Service accounts created in the Access area keep their
  bindings. A networked server loads the state at start, so the migration
  and its audit entries run before the first request; an unreadable state
  file is logged and does not stop the start, and every request that reads it
  answers 500 until the file is repaired. An older binary does not know the role and grants such a token
  nothing until it is upgraded again.
- Each user record keeps `role`, `projects`, and `productionApprovalProjects`
  as a downgrade-safe summary: only Admin is summarized as `operator` (with
  the Admin projects), Product as `contributor`, everything else as `viewer`,
  and a user without bindings as a viewer of no project. **Downgrade:** an
  older binary reads only this summary and drops role bindings, groups, and
  service accounts (API tokens included) the first time it saves the file;
  restore the migration backup to return to the old state. A later upgrade
  migrates again from that summary, so bindings, groups, and service accounts
  made under the rights model would be lost. Before a downgrade, copy the
  current `.security/access-state.json` (version 3) aside; before upgrading
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

### Operator agent identity (security model SM-1)

The operator agent, the interactive coding agent session the Owner runs to
operate Studio, has its own identity instead of acting through the Owner's
account ([security model Dossier](../../security-model/index.html), decisions
D1, D4, and D11).

- The principal kind `operator-agent` is built on the service-account model:
  a service account of origin `operator-agent` (id `opa_*`) with role bindings
  like any service account, but no `sat.*` token. Its credentials are child
  sessions. The Owner creates the agent and starts each session in Settings →
  Access → Operator agents (`/api/auth/iam/operator-agents`).
- Each session receives its own `oas.*` bearer, returned once and stored only
  as a hash. A session lasts 1 to 8 hours (default 8). Only an active Owner
  starts, renews (again at most 8 hours from the renewal), or revokes it;
  disabling the agent revokes every session. A session also stops working when
  the Owner who started it is no longer an active Owner.
- Every request made with a session token, every control plane audit entry it
  causes, and every card timeline event written during it carries the actor
  `operator-agent:<id>/session:<sid>` and `delegatedBy: human:<owner id>`. The
  middleware opens an ambient operator scope for the request, so a handler
  that passes another actor still writes the session as actor; a non-human
  actor the handler named is kept as `reportedActor`. Queued batch moves keep
  the scope. The Owner never appears as the actor of a session's action.
- A session token is refused (403) on every `/api/auth/*` route (access
  administration, role bindings, standing grants, and the audit view
  and verification), on the versioned Task Server plane `/api/v1/*`, and on any
  route that needs `access.*`, `workspace.administer`, or `secrets.resolve`,
  whatever its bindings say. The standalone Task Server has no operator
  principal; operator sessions act only through the Studio API.
- The operator agent runs on the Owner's machine under its own OS user,
  without the Owner's SSH agent, keys, or credential files (D11). See the
  [operator agent setup guide](../setup/operator-agent.md).

### Operator operation catalogue, tiers, and standing grants (SM-4)

Every operator session request passes the policy step in
`AccessSecurityMiddleware` (security model SM-4, decisions SM-D2 option A,
SM-D3 option A, and SM-D7 option A). A request outside the catalogue is
refused before any role binding is read.

- The operation catalogue (`OperatorOperationCatalog`) gives each operation an
  id, exactly one tier, and a scope kind (workspace, project, environment, or
  host). Every read (GET, HEAD, OPTIONS, and the read-only reference-status
  POST) is `observe` (T0). A write is catalogued only by a narrow route rule;
  any other write is refused with `operation-not-catalogued`. The typed host
  operations of the Operations Server are catalogued too: `host.inspect` is T0
  and `service.restart` is T1.
- T1 routine: `card.requeue` (a move into Backlog, Preparation, orchestrator
  prep, or Ready, and the failed-pickup restore), `card.steer` (continue),
  `service.restart`, `host.drain` and `host.resume`, `gate.rerun` (integration
  retry and a pipeline step run), `stage.deploy`, and `production.promote`.
  A stage deploy or promotion is T1 only when the commit has full gate
  evidence: a green post-merge run on exactly that commit in which every
  command passed (D2). Without it the same request is `gate.bypass`.
- T2 high risk: `secrets.change`, `exposure.change`, `data.delete` (every
  catalogued delete), `history.rewrite`, `gate.bypass` (merge, external
  completion, a move into Human review or Completed), `keys.rotate`,
  `host.enrollment`, `access.change`, `grant.change`, and `catalogue.change`.
  A T2 request runs only under the Owner's approval of exactly that request
  (SM-5, next section); without one it is refused with `approval-required`.
  A T2 request on access administration or the Task Server plane
  (`/api/auth/*`, `/api/v1/*`) is refused there and never becomes an approval
  request, because no operator session may call those routes. Changing the
  catalogue is a code change through a card, so it is T2 by construction.
- The operator agent has no operation that pushes a protected branch (D3). A
  change reaches `develop` or `main` as a card through the gate.
- A T1 request is admitted only under a live standing grant of the operator
  agent that names the operation, covers the request's scope (a workspace
  grant covers everything, a project grant its project and environments, an
  environment grant one project environment, a host grant one host), is inside
  its optional daily UTC window, and has hourly rate left. Otherwise it is
  refused with `grant-required`, `grant-outside-window`, or
  `grant-rate-exceeded`. A refusal answers 403 with `error`, `operation`, and
  `tier`. The role bindings still apply after the policy step.
- A standing grant (`/api/auth/iam/standing-grants`) records the principal,
  tier T1, operation set, scope, rate per hour, optional window, issuer, issue
  time, expiry (default 7 days, at most 30), renewal, revocation, and reason.
  Only an active Owner issues, renews, or revokes one in Settings → Access →
  Standing grants. Issuing and renewing consume a fresh step-up (five minutes,
  single use); revoking needs none. Disabling the operator agent revokes its
  grants. The policy step reads the grant records under the store lock on
  every request, so a grant stops admitting at its expiry and on the first
  request after its revocation, well inside 60 seconds. The rate counts
  admitted requests of the last hour and is persisted with the grant, so a
  restart does not reset it.
- The catalogue and grant routes sit under `/api/auth/iam`, so an API token,
  a service account token, or an operator session gets 403 on all of them.
  Request and response bodies are camelCase JSON; tiers are `"T0"`, `"T1"`,
  and `"T2"`.

  | Method and route | Permission | Body and answer |
  |---|---|---|
  | `GET /api/auth/iam/operator-catalogue` | `access.read` | Answers `operations` (`id`, `tier`, `scopeKind`, `title`), `defaultGrantDays` (7), `maxGrantDays` (30), `defaultRatePerHour` (10), and `maxRatePerHour` (120). |
  | `GET /api/auth/iam/standing-grants?principalId=<opa id>` | `access.read` | Answers every grant, newest first; `principalId` is optional and narrows the list to one operator agent. |
  | `POST /api/auth/iam/standing-grants` | `access.manage`, active Owner, fresh step-up | Takes `principalId`, `operations` (catalogued T1 ids), `scope` (`kind` of `workspace`, `project`, `environment`, or `host`, with `project`, `environment` (`stage` or `production`), or `host` as the kind needs), `ratePerHour` (1 to 120, default 10), optional `window` (`startUtc`, `endUtc` as `HH:mm`), `expiresInDays` (1 to 30, default 7), and `reason` (at most 500 characters). Answers the grant. |
  | `POST /api/auth/iam/standing-grants/{id}/renew` | `access.manage`, active Owner, fresh step-up | Takes optional `expiresInDays` and sets the expiry that many days from now. A revoked or expired grant answers 409 `standing-grant-ended`. |
  | `POST /api/auth/iam/standing-grants/{id}/revoke` | `access.manage`, active Owner | Takes optional `reason`. Revoking a revoked grant answers it unchanged. |

  A grant answer carries `id` (`sgr_*`), `principalId`, `principalName`,
  `tier`, `operations`, `scope`, `ratePerHour`, `window`, `issuedBy`,
  `issuedAt`, `expiresAt`, `renewedAt`, `renewedBy`, `revokedAt`,
  `revokedBy`, `reason`, `revokeReason`, `state` (`active`, `expired`, or
  `revoked`), and `usesLastHour`. The step-up comes from
  `POST /api/auth/step-up` within the five minutes before the change and is
  used up by it. Refusals answer `error` and a message: 400
  `grant-operation-required`, `grant-operation-invalid` (not catalogued or not
  T1), `invalid-scope`, `grant-scope-mismatch` (for example a host operation
  in a project scope), `invalid-rate`, `invalid-window`, `invalid-lifetime`,
  `reason-required`, or `invalid-reason`; 403 `owner-required` or
  `step-up-required`; 404 `operator-agent-not-found` (unknown or disabled
  agent) or `standing-grant-not-found`.
- Grant changes are written to the IAM audit (`standing-grant.create`,
  `.renew`, `.revoke`). Each operator request's audit entry names the
  operation, its tier, the admitting grant id, or the policy refusal.
  `OperatorOperationCatalogInventoryTests` fails for a route matched by two
  rules, a rule that matches no mapped route, or a typed operation without a
  catalogue entry.

### Operator kill switch (security model SM-2)

The Owner ends operator access in one step
([security model Dossier](../../security-model/index.html), decision D1
option O4 with O3 as the transition path, and decision D7 option A, both as
recommended). The kill acts on the operator's Studio identity and on its typed
Operations Server principals; host certificates (O3) join it with SM-6. A
session lasts at most 8 hours, and every refusal lands within 60 seconds. The
switch only removes authority, so it asks for no step-up; resuming means
creating a new operator agent and starting a session, which only the Owner can
do.

- **End all operator access** in Settings → Access → Kill switch
  (`POST /api/auth/iam/operator-kill-switch/end-all`, Owner only) disables
  every operator agent and revokes every session in one save of the access
  state. The next request of any session is refused (`401`), and no new
  session can be started for a disabled principal. The same step then revokes
  the operator's Operations Server principals and cancels their queued
  operations (running ones are asked to stop). The same save revokes every
  live standing grant (SM-4) of those agents (`RevokedBy` is the Owner, or
  `system:control-plane-host` for the out-of-band command, reason `operator
  kill switch`), each with a `standing-grant.revoke` IAM audit entry, and the
  kill record lists them in `standingGrantIds`. No future grant can be issued
  or renewed, because a grant needs an enabled operator agent. A per-session
  kill keeps the agent's grants. The same save also cancels every open
  (pending or approved) approval request (SM-5) of those agents (reason
  `operator kill switch`), each with an `operator-approval.cancel` IAM audit
  entry, and the kill record lists them in `approvalIds`; a cancelled approval
  never runs (`approval-cancelled`). Host certificates (SM-6) join this step
  when that slice exists.
- **End session** on one session row
  (`POST /api/auth/iam/operator-agents/{id}/sessions/{sid}/kill`) refuses that
  session at once while the agent and its other sessions keep working. It
  cancels the open approval requests of that session only.
- Every use writes one entry in the control plane audit chain with stream
  `kill-switch` and its own event type: `operator-kill.all`,
  `operator-kill.session`, or `operator-kill.drill`. The Operations Server
  revocation that follows a kill writes `operator-kill.operations-server`
  (`ok`, or `failed` for its first failure). Each use also posts an
  `operator_kill_switch` event to the operator feed for the Owner; ending all
  access is an acute event, a passed drill a quiet one.
- **Operations Server.** A principal in the Operations Server principal file
  belongs to an operator agent when it names it in `operatorAgentId`. Studio
  calls `POST /api/operations/v1/principals/revoke-operators` with its own
  control plane principal, which needs the scope `principals.revoke`; an
  operator principal can never call it. The Operations Server adds the
  principals and their operator agent ids to its revocation list
  (`Operations:RevocationsFile`, default `operator-revocations.json` in its
  data directory), replaced atomically and read on every request like the
  principal file, which stays read-only. A principal added later for a revoked
  operator agent is refused too. A failed call stays pending and is retried with backoff from 2 to
  30 seconds until it succeeds; the Settings view shows it as pending.

  | Setting | Meaning |
  | --- | --- |
  | `Security:OperatorKillSwitch:OperationsServer:Url` | Base URL of the Operations Server; HTTPS only, no redirects. |
  | `Security:OperatorKillSwitch:OperationsServer:CredentialFile` | File with the control plane principal's bearer; read per call. |
  | `Security:OperatorKillSwitch:DrillIntervalHours` | Scheduled drill interval (default 720, one drill per 30 days; `0` turns it off). |
  | `Security:OperatorKillSwitch:PollSeconds` | How often the API checks the out-of-band latch and retries (default 2). |

- **Out of band.** When the UI is unreachable, the command on the control
  plane host `dotnet OrchestratorApi.dll --end-operator-access [--reason TEXT]`
  first writes the latch `.security/operator-kill-switch.request.json`; while
  it exists every operator session is refused. With the API stopped (the
  command takes the API's exclusive `.security/control-plane.lock`) the
  command applies the kill itself through the same coordinator, so the audit
  gets the same `operator-kill.all` entry with trigger `out-of-band` and actor
  `system:control-plane-host`; the Owner notice and any pending Operations
  Server revocation follow when the API starts, and the API re-enforces the
  kill on the state it loaded before removing the latch. With the API running
  the API applies the latch within one poll interval and the command waits for
  that. When the Operations Server is not reachable, `operations-server
  revoke-operators <principals-file> <data-directory> [operator agent ids]` on
  its host does the same revocation and cancellation locally.
- **Drill.** A drill creates a test principal with one session (hidden from
  the operator list), checks that the session is admitted, runs the same kill
  for that principal alone, and measures the time until Studio refuses the
  session and the Operations Server revocation returns. It passes when both
  happen within 60 seconds and every real operator session is still live. The
  scheduled drill runs one interval after the schedule starts and then every
  interval; **Run drill now** runs one on request. Each result (duration,
  Studio and Operations Server times, pass or the named failure) is kept in
  `.security/operator-kill-switch.json` and in the `operator-kill.drill` audit
  entry; a failed drill is an acute operator feed event. A drill does not run
  while an out-of-band latch is pending (`operator-kill-pending`), and a drill
  that stops before its kill step still disables its test principal, so no
  drill session outlives a kill.

The minimum Runner scopes are `runner.claim`, `runner.lease`, `runner.logs`,
`runner.events`, `runner.artifacts`, and `runner.completion`. Reading the prompt
and the attachments of a claimed task is covered by `runner.claim` and needs
the task's live lease. Lease requests bind the wire `runnerId` to the
authenticated service identity. A Runner credential on a route this server
does not map, such as a standalone Task Server feature route called against
the monolith, gets `404` with code `route-not-mapped`; the host then pauses
that poll loop for an hour and logs it once. The device sign-in and Git setup
claim loops instead stop for the life of the host process. Anonymous callers
still get `401`.
`HostRouteClassificationTests` classifies every route the host client calls.

The monolith maps the device sign-in (`/api/v1/management/device-logins`,
`/api/v1/runners/{id}/device-login/*`) and Git setup
(`/api/v1/management/runner-git-setups`, `/api/v1/runners/{id}/git-setups/*`)
routes itself (AGT-3432). The management routes are Owner-only. A host claims
with its own credential: `runner.claim` for the claims and the device sign-in
reports, `runner.events` for the Git setup reports. An empty queue answers
`204`. While a claim poll fails, the host backs off from 2 seconds up to
1 minute. It logs the status and refusal code once for each distinct failure,
logs the repeat count when the failure changes or the poll recovers, and goes
back to 2 seconds after an answer.

### Owner approval with step-up (SM-5)

A T2 operator request becomes an approval request instead of an effect
(security model SM-5, decision SM-D8 option A: the Owner decides in Studio
with step-up; an optional push notification only links there).

- **When it opens.** The policy step defers a T2 request until the route
  floors and the session's role bindings admit it, so the Owner is asked only
  about requests that could run. Without an `X-Operator-Approval` header the
  store opens an approval request and the answer is 403
  `approval-required` with `approvalId`, `operation`, and `tier`. A retry of
  the same request from the same session while the request is pending
  answers the same `approvalId`. One session holds at most 20 open (pending
  or approved) requests; the next answers 429 `approval-queue-full`. A body
  too large to digest in full (over 16 MB) answers 403 `approval-unbindable`.
- **What the Owner sees.** The operation and its tier, the request line
  (method, path, query), the parameters (the JSON body indented, with the
  values of secret-like fields such as `value`, `token`, `password`, `key`,
  or `credential` masked, other bodies through the credential redactor), the
  parameter digest, the target (project and task), the session actor
  `operator-agent:<id>/session:<sid>`, the instruction source, and the
  agent's stated reason. The agent states the source in
  `X-Operator-Instruction-Source` (`owner-chat`, `card`, `repository-file`,
  `web-page`, or `log`; anything else is recorded as `unstated`) and the
  reason in `X-Operator-Reason` (percent-encoded UTF-8, at most 500
  characters). Every source except `owner-chat` is shown as untrusted
  content that can propose but never authorize. The masked view is for
  review only; the digest binds the exact bytes.
- **Deciding.** Only an active Owner approves or rejects, in Settings →
  Access → Approval requests. Each decision consumes a fresh step-up
  (`POST /api/auth/step-up` within the five minutes before, single use);
  without one the decision is refused with 403 `step-up-required`. The
  deciding actor is the user of the authenticated Studio session. No header
  (`X-Client-Id` included) and no body field names it; a bearer principal
  (service account, API token, or operator session) is refused on these
  routes even if it sends the Owner's id. The Owner has 15 minutes from the
  request to decide; afterwards the request is `expired` and a decision
  answers 409 `approval-expired`. A decision is final (409
  `approval-decided`).
- **Running it.** The agent repeats the exact request with
  `X-Operator-Approval: <id>`. It runs only when the approval belongs to the
  same operator agent and session, names the same operation, matches the
  parameter digest (method, path, query, and body), is approved, unused, and
  within 15 minutes of the decision. The store marks it used under its lock
  before the handler runs, so a replay or a concurrent copy finds it used.
  The role bindings still apply. Refusals answer 403 with
  `approval-not-found` (unknown, or another session's), `approval-pending`,
  `approval-rejected`, `approval-used`, `approval-expired`,
  `approval-cancelled`, or `approval-digest-mismatch` (a changed card, query,
  or body); the approval check answers before the target is resolved, so a
  replay after the target was deleted still answers `approval-used`. The
  response status of the admitted request is recorded against the approval
  (`resultStatus`).
- **Cancelling.** Revoking the session or disabling the operator agent
  cancels its open approvals.
- **Audit.** The IAM stream records `operator-approval.request` (session
  actor), `.approve` and `.reject` (`human:<owner id>`), `.execute` (with the
  answer status), and `.cancel`. Every operator request entry that concerned
  an approval carries `approvalId` next to `policyReason` and the parameter
  digest.
- **Notification.** A new request emits one operator-feed event
  (`operator_approval_requested`) with a link to
  `#/workspace/settings/access?approval=<id>`. When
  `OperatorApprovals:Push:Url` is set (HTTPS, or HTTP on loopback), the
  notifier also posts `approvalId`, `title`, `message`, and `url` as JSON to
  that endpoint, with the bearer from `OperatorApprovals:Push:TokenFile` if
  set; `OperatorApprovals:StudioUrl` gives the link its address. The
  notification carries no token, digest, or decision route, so receiving it
  approves nothing. Delivery runs off the request path from a bounded queue;
  a failed delivery is logged and the request stays in Studio.

  | Method and route | Permission | Body and answer |
  |---|---|---|
  | `GET /api/auth/iam/operator-approvals?principalId=<opa id>` | `access.read` | Answers every approval, newest first: `id` (`apr_*`), `principalId`, `principalName`, `sessionId`, `actor`, `delegatedBy`, `operation`, `operationTitle`, `tier`, `method`, `path`, `query`, `parameters`, `parameterDigest`, `target`, `instructionSource`, `reason`, `requestedAt`, `expiresAt`, `state` (`pending`, `approved`, `rejected`, `used`, `expired`, or `cancelled`), `decidedBy`, `decidedAt`, `decisionNote`, `approvalExpiresAt`, `usedAt`, `resultStatus`, `cancelledAt`, and `cancelReason`. |
  | `POST /api/auth/iam/operator-approvals/{id}/approve` | `access.manage`, active Owner, fresh step-up | Takes optional `note` (at most 500 characters). Answers the approval. Refusals: 403 `owner-required`, `step-up-required`, or `human-session-required`; 404 `approval-not-found`; 409 `approval-expired`, `approval-decided`, `approval-cancelled`, or `approval-used`; 400 `invalid-note`. |
  | `POST /api/auth/iam/operator-approvals/{id}/reject` | `access.manage`, active Owner, fresh step-up | As approve. |

  Settled approvals stay in the identity store for 30 days.
  `OperatorApprovalFlowTests` cover the end-to-end flow, replay, a changed
  parameter, another session, expiry, a forged actor header, and the push
  link; `OperatorApprovalTests` cover the lifecycle matrix, step-up,
  cancellation, the queue limit, and masking.

### Host operations and certificates (security model SM-6)

SM-D1 uses option O4 for routine work and option O3 for a bounded transition.
The supported networked Compose stack starts the Operations Server and an
outbound local Operations Agent on the private website network. The Operations
Server has separate service and agent principals, no browser credentials, and
a loopback-only host port for private tunnels to other enrolled hosts. An
existing installation needs the Operations image digests, a Studio host CA
outside the data directory, and a certificate receipt directory before the
new Compose stack starts; follow the
[website update procedure](../setup/studio-website.md#first-update-that-adds-operations-server).
The public Docker firewall covers their addresses in the full website subnet;
the website systemd unit verifies that the subnet rule is first in
`DOCKER-USER` before Compose starts the public edge. Studio's
service credential is readable only by the API's dedicated Operations group
inside its mounted secret volume; the agent credential has a separate volume.
Studio's `POST /api/hosts/{id}/{inspect|restart|drain|resume|logs|configuration}`
routes accept an operator session only after the SM-4 policy step. The route
selects a typed catalogue entry and a host standing grant where T1 applies;
the handler submits the command with the session actor to the Operations
Server. The local agent advertises only operations whose host-owned allowlist
is present. Requests supply names and digests, never executable paths or
shell text.

The operations are `host.inspect` (T0), `service.restart` (T1), `host.drain`
and `host.resume` (T1), `host.logs.collect` (T0), and
`host.configuration.apply` (T1). Drain and resume call the fixed
`agent-host host-agent` role commands with a configured host-agent file. Log
collection reads the last 8192 bytes of an allowlisted file. Configuration
apply copies at most 1 MiB from a host-owned reviewed source after its SHA-256
matches both the allowlist and the command, then atomically replaces one
allowlisted target. Studio appends a hash-chained control plane audit entry
before submitting each operator host command, including the actor, host,
operation, input digest and idempotency key. The server validates each input
and output shape. It
stores every attempt transition in a SQLite table with update and delete
triggers that refuse mutation. Before executing, the agent appends an
owner-only host journal line and sends it to the server. The server accepts
the receipt only when the dispatched central history matches the actor,
agent, operation, and digest; a mismatch emits
`host-journal-without-central-audit`. The result follows the existing durable
spool and replay path.

For a command outside this catalogue, an operator session with a covering
host grant may request a user certificate at
`POST /api/hosts/{id}/certificates`. Studio's CA private key is mounted from a
host secret outside the API data directory. It signs only `agent-operations`
certificates with a fixed forced command, no extensions, and a validity
interval ending 59 minutes after issue. The forced command accepts only the
exact commands listed in `deploy/agent-host/agent-operations-command`.
`POST /api/hosts/{id}/certificates/{certificateId}/revoke` builds an OpenSSH
KRL and publishes it to the Operations Server; agents that have a
`revokedKeysFile` configured download and atomically install the newest KRL
on each poll. Hosts configure `RevokedKeys` and `TrustedUserCAKeys`, disable
password and standing authorized-key login for this account, and keep the
operations account unprivileged. The [operator setup guide](../setup/operator-agent.md)
has the migration sequence.

### Operator guardrails on card rounds (AGT-3483)

Two guardrails of the
[operator-agent-level Dossier](../../operator-agent-level/index.html) are
enforced by the server, for human sessions, API tokens and operator-agent
sessions alike. No route was added; the existing routes changed as follows.

- `POST /api/tasks/{id}/continue` starts a queued round from the card's latest
  recorded result ref and returns it as `base`. The optional `baseRef` must be
  one of the card's own result refs: any other value is refused with
  `400 base-ref-not-on-card` before the card is written, so a caller cannot
  point a run at an arbitrary branch of the repository.
- Continue (every mode), preview feedback, the orchestrator-log steer override
  and an operator requeue (`move`, `state`, `batch-move` into a run lane) of a
  member of a batch under isolation answer `409 batch-isolation-active` with
  the flight id. Batch gate eject stays allowed. The refusal is the same for
  every principal: it protects the delivery under test, not an access level.

The operator-facing description is in the
[operator API reference](../../system/contracts/operator-api.md).

### Runner credential binding (SM-9)

Every runner credential is bound to its host, its role, and an explicit
project set (security-model Dossier SM-9, decision D7 option A). Scopes still
decide the route family; the binding decides which work inside it.

- **Projects.** In the monolith, the enrollment code names the projects
  (`POST /api/auth/runner-enrollments` with `projects`; `*` grants all, an
  empty list grants none). The Owner changes them with
  `PUT /api/auth/runners/{id}/projects` or in Settings > Access. On the Task
  Server the projects are the host's project policy
  (`PUT /api/v1/hosts/{hostId}/project-policy`). With authentication on, a host
  without a policy holds no project; an empty claim names
  `host-project-policy-missing`.
- **Host.** A Task Server runner credential is pinned to the host id of its
  first registration, or to the host its enrolment names. A registration from
  another host id is refused with `runner-host-mismatch` (a copied credential).
  A monolith runner identity is one enrolled host. The deprecated shared
  `RunnerBearerToken` names no runner and is therefore bound to nothing.
- **Role.** A Task Server enrolment code records `coding` or `review`
  (host join tokens set it from the host role). A coding credential cannot
  claim reviews and a review credential cannot claim coding work
  (`runner-role-mismatch`). Without a recorded role, a credential that can
  claim only one kind of work has that role. In the monolith the exclusive
  scopes (`orchestration.write`, `runner.deploy`, `runner.release`,
  `gate.execute`) remain the role.
- **Card reads.** A runner reads a card only while it holds that card's live
  coding lease or review attempt (`runner-card-not-leased`). On the Task Server
  a runner that ran an earlier attempt of a finished card may read its
  lifecycle (key, lane, archive state, timestamps) with title and body withheld,
  which salvage retention needs. The monolith refuses that read; salvage then
  keeps the entry.
- **Claims and reports.** Coding, review, review package and publication claims select only
  work of bound projects. Run, review-attempt, review-package-attempt and gate routes act only on the
  caller's own claim (`runner-not-claimant`), reads included; a reviewer may
  read the source run of the attempt it holds.
- **Other reads.** On the Task Server every route a runner credential reaches
  only through `tasks:read` is refused (`runner-route-denied`) unless
  `RunnerRoutePolicy` lists it: installation metadata such as
  `/api/v1/artifact-limits`, its own host's capacity, the bound projects and
  their repositories and source facts, and the claimed card, run, review or
  gate. Studio, board, workspace and management reads are not runner reads.
- **Repository tokens.** `POST /api/v1/runners/{runnerId}/repository-credentials`
  mints GitHub App tokens only for the repositories of bound projects; a
  credential without projects receives none.
- **Lifetime.** A new runner credential expires 30 days after issue
  (`TaskServer:RunnerCredentialLifetimeDays`, `Security:RunnerCredentialLifetimeDays`).
  Rotation keeps the existing overlap: the new credential starts its own 30
  days and the previous one stays valid until its overlap deadline (Task
  Server) or until the Owner revokes it (monolith). Every authenticated answer
  to an expiring runner credential carries `X-Principal-Credential-Expires-At`,
  and the host logs a warning once a day in its last 7 days. Settings > Access
  and the credential inventory mark a credential as expiring within 14 days. A
  configured bootstrap secret file carries no expiry; rotate it through the
  file.
- **Migration.** Existing credentials keep working: a runner enrolled before
  this binding keeps all projects and its credentials keep no expiry. The
  Owner sees a notice for each ("Before binding", "No expiry"); the Task Server
  writes one `principal.expiry-migration-notice` audit entry and turns every
  already registered host into an explicit all-projects policy
  (`host-project-policy.migrated`). Narrow the projects and rotate each such
  credential to adopt the default.

### Per-attempt containment (SM-11)

Security-model Dossier SM-11 implements decision SM-D6 option H2 (one host,
separate users, per-attempt containers) and SM-D10 option A (no workflow
permission on attempt tokens). The executor code is in
[`runner/WorkContainers/`](../../../runner/WorkContainers/); the host setup is in
the [work-container section of the Linux runner guide](../setup/linux-runner-host.md#per-attempt-containment-by-default-sm-11).

- **Default after the canary.** `RUNNER_WORK_CONTAINER_MODE` defaults to
  `contained` on Linux (`off` elsewhere). Contained mode runs an attempt in a
  container only after `agent-host work-container proof` recorded a go
  verdict on the host (`rollout-verdict.json`); before that the process
  executor keeps running. `off` and `shadow` remain the operator rollback.
- **Fail closed after the canary.** With a go verdict, the
  `attempt-containment` capability becomes a claim requirement for coding and
  review. It is `unavailable`, and the Task Server refuses claims, when the
  service user can reach the runtime directly (`runtime-group-member`), the
  launcher is missing, the role image is not digest pinned, the work-zone
  egress policy is not applied and verified, or the container network is not
  a work-zone network.
- **One container per attempt, its own UID, only its repository.** Each
  attempt runs as a UID from 200000 to 265535 that no live attempt on the
  host holds, with a read-only root, all capabilities dropped,
  `no-new-privileges`, `--restart no` and a private tmpfs for secrets. A
  one-shot step without network, with only `CHOWN` and `FOWNER`, hands the
  fresh attempt volume to that UID. npm, NuGet and repository-mirror caches
  are per repository (`agent-cache-<cache>-<repository>-v<n>`), so no attempt
  sees another project's mirror or package cache.
- **Narrow launcher.** The host service user is not in the `docker` (or
  `podman`) group and has no sudo rule for the runtime. The host agent sends
  each runtime call as one JSON request to `/run/agent-work-launcher.sock`
  (root-owned, the service group, mode 0660). Socket activation starts the
  root-owned `/usr/local/sbin/agent-work-launcher` wrapper per connection,
  which checks root ownership of itself, its configuration and the agent-host
  release, then runs `agent-host work-container launch --stdio`; no setuid
  step is involved, so the daemon keeps `NoNewPrivileges=true`.
  `WorkContainerLauncherPolicy`
  admits only the executor's own calls: `agent-work-*` names, digest-pinned
  images, an attempt UID, the fixed entry, prepare and secret-injection
  scripts, volume or read-only bind mounts that pass the socket and
  provider-home deny list, and a network of the applied, verified work zone.
  Anything else, including `--privileged`, `--cap-add`, `-v`, host networking
  and arbitrary `exec` or `run`, exits 126 before the runtime runs.
- **Egress on every agent host.** The launcher refuses every container while
  the host has no applied and verified work zone, and contained mode defaults
  the container network to the zone's `agent-work`.
- **Attempt tokens.** Task Server mints a one-repository GitHub App token with
  `contents:read` (never `workflows`) per attempt after a one-use capability
  exchange bound to host, role, subject, attempt, lease, fence, epoch and,
  since SM-11, a project the host still holds (`project-not-bound`). It is
  injected through runtime stdin into the container's tmpfs. A sweep every
  15 seconds revokes the token of every attempt whose lease was released,
  completed, fenced or revoked, deletes the root copy, and records
  `revoked-at-completion`; renewal and new exchanges for that attempt are
  refused. Process-unknown attempts keep their token until re-adoption or
  the one-hour backstop.
- **Outside the container.** The launch plan refuses runtime sockets, provider
  homes (`.claude`, `.codex`, `.config/gh`), SSH, Git, cloud and kube
  credentials, and any secret-shaped environment name. Host enrollment
  credentials stay with the host agent; the attempt file carries no token.
- **No self-approval.** Task Server records the principal that claimed a
  coding attempt (`runs.claim_principal_id`). A review verdict is refused
  (`review-principal-is-coder`) when the review executor coded the delivery or
  the reporting principal claimed its coding attempt, and an authenticated
  caller that is not a runner principal cannot report a verdict
  (`review-principal-not-reviewer`). Review claims skip such deliveries
  (`coding-principal-excluded`). The monolith review plane refuses a report
  whose executor coded the delivery.
- **Probe.** `agent-host work-container probe` runs one attempt through the
  executor whose command tries to read the named host credential, provider
  login and other-project paths, find a runtime socket or a secret variable,
  see another repository's mirror, and connect to a non-allowlisted address.
  It writes `containment-probe.json` and exits non-zero unless every check
  passed.

Not yet contained: detached Claude and Codex coding and review sessions still
run as host processes, because provider root logins may not enter a container
and per-attempt provider tokens need the host provider broker gated by the
dynamic placement S5 concurrency proof. The policy records
`contained-default` for those roles, but the detached worker reports
`worker=process` in `executor-shadow.json`. Gate attempts need no repository
token; they receive their source as a bundle.

## Transport and exposed surfaces

### Stage 0 website access and deployment baseline (SM-D5 C)

The public website uses an identity-aware access provider that requires the Owner's second factor before any site path reaches Studio. Both public Caddy edge modes call the provider's `/verify` endpoint on every request and forward only a 2xx authorization result. The provider validates its own signed access session; client identity headers do not authorize a request. Origin-pull mTLS and the Docker firewall restrict the peer reaching Caddy. Studio username and password sign-in remains behind the access layer. In-product WebAuthn and TOTP are the later SM-8 slice, before public login. The private Caddyfile is for isolated network or loopback recovery only. [The website setup guide](../setup/studio-website.md) gives the provider contract and checks.

The reference Compose stack pins the API, frontend, and edge images by digest; each runs as UID 10001 with a read-only root, explicit writable bind mounts, all Linux capabilities dropped, and `no-new-privileges`. The API's `/tmp` is the disk-backed `$STUDIO_DATA_DIR/api-tmp` bind mount, because agent CLIs and build gates write scratch files there and a tmpfs would count against the API memory limit; the frontend and edge use a `/tmp` tmpfs. The setup script prepares writable mounts for that UID. `check-edge-config.sh` names a missing setting and checks the public access policy attestation and digest values in `.env`. Host security updates need a scheduled reboot window and post-reboot edge verification.
The ownership handoff uses the pinned edge image before the service starts. The deployment smoke resolves a real Caddy registry digest for this step and then runs the edge from that digest. Both Caddy containers have explicit XDG state directories and writable temporary homes; the private Caddyfile skips the local CA's attempted installation into the read-only container trust store. The baseline checker verifies these startup settings.

Backups use an authenticated encrypted CMS archive. The decryption key is in separate owner custody and is temporarily presented only for a restore. A weekly scratch restore proves that the recovered state accepts a full Owner or Operator sign-in with password and TOTP second factor, as a restored installation requires since AGT-3379, and publishes the `website.restore.signin` pass or fail signal. The baseline monitor alerts on repeated access login failures, each new access session, origin client certificate expiry, and a failed or stale restore signal. See the [backup and restore procedure](../setup/studio-website.md#backup-and-restore).

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

### Operator decision stream and watcher lease (operator agent level OP-2)

The [operator agent level Dossier](../../operator-agent-level/index.html)
(decision OA-D2 option B) replaces board polling with a typed decision
stream that API tokens may read. SignalR stays closed to tokens.

- `GET /api/operator/decisions?after=<cursor>&limit=<n>` returns a snapshot
  and `GET /api/operator/decisions/stream` the same events as Server-Sent
  Events (`id` is the cursor, `event` the type). Both declare
  `project.read`, resolve as handler-filtered routes, and are open to human
  sessions, API tokens, and operator agent sessions. Each reader receives only
  events of the projects it may read. Installation-wide events (no project,
  for example a provider limit) need an Owner, an all-projects grant, or the
  local profile. `watcher.heartbeat` names no project data and every reader
  receives it, because the lease is per installation.
- `GET /api/operator/watcher/lease` reads the lease. `POST` takes it, `POST
  .../lease/{leaseId}/heartbeat` renews it, and `DELETE .../lease/{leaseId}`
  releases it. The writes declare `project.runs.operate` with project
  bindings sufficing, so a viewer token or a viewer session gets 403. While
  a lease is active, a second `POST` gets `409 watcher-active` naming the
  holder actor, its session label, and its last heartbeat. A take-over needs
  `takeOver: true` in the body and is written to the control plane audit
  (`operator` stream, operation `watcher.lease.take-over`, the previous
  holder as target, its lease id and last heartbeat, and the given reason)
  before the lease changes; when that audit write fails, the take-over does
  not happen. Grants and releases are audited the same way.
- The publisher reads facts other features already decided and moves no
  card: lanes, open decision cards, quota waits and fallbacks from the card
  records, open watcher stall cases, red and isolating batches, the batch
  gate's pending-push age alert, and runner provider limits. Events, the
  published fact keys, and the lease live in `<TaskRepository>/.operator/`
  (`decision-events.jsonl`, `decision-facts.json`, `watcher-lease.json`) and
  hold no secret. Event reasons repeat the reason text of the underlying
  record.

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

### Control plane audit chain (security model SM-1)

One writer records the control plane audit trail in
`<TaskRepository>/.security/control-plane-audit.jsonl` (D4 option A2). Each
line carries `sequence`, `at`, `stream` (`iam`, `service-account`,
`api-token`, `operator-agent`, `secret`, `run`, `kill-switch`, `operator`), `actor`, `session`,
`delegatedBy`, `operation` (route or operation), `target`, `parameterDigest`
(`sha256:` over method, path, query, and body for bearer requests; over the
change details for IAM changes), `outcome`, the producer's full legacy record
in `details`, `previousHash`, and `hash`. The hash is SHA-256 over the stored
line without its `hash` member, so any changed byte, deleted, inserted, or
reordered line breaks the chain. The writer never edits or removes a line.
The IAM, service-account, API-token, secret, and run audits are routed
through it; their per-stream files above stay as read projections for the
existing readers.
The `operator` stream holds the watcher lease records of the operator
decision stream (`watcher.lease.grant`, `watcher.lease.take-over`,
`watcher.lease.release`); it has no per-stream file.

Forwarding: `AuditSinkForwarder` copies every entry, unchanged and in order,
to the configured off-host append-only sink and records the acknowledged
position in `control-plane-audit.forwarded.json`. It wakes on each append, so
entries normally leave within a second. The local chain is the queue: the
position moves only after the sink acknowledged a batch, so an unreachable
sink loses nothing. When the oldest unforwarded entry is older than
`ControlPlaneAudit:Sink:AlertAfterSeconds` (default 60) an acute
`audit-sink-unreachable` alert goes to the log and the operator feed; it
clears when the sink has caught up.

| Setting | Meaning |
| --- | --- |
| `ControlPlaneAudit:Sink:Kind` | `none` (default) or `https`. |
| `ControlPlaneAudit:Sink:Url` | Base URL of the sink; HTTPS required, plain HTTP only on loopback for a local relay. |
| `ControlPlaneAudit:Sink:AppendTokenFile` | File with the append-only bearer; read per call, so rotation needs no restart. |
| `ControlPlaneAudit:Sink:ReadTokenFile` | Optional separate read bearer for verification. |
| `ControlPlaneAudit:Sink:PollSeconds` | Fallback poll interval (default 5). |
| `ControlPlaneAudit:VerifyIntervalMinutes` | Verification interval (default 15). |

The HTTPS adapter sends `POST {url}/entries` with an NDJSON body of stored
lines and reads `GET {url}/entries?from=<sequence>&limit=<n>` back. The
endpoint, or the object store with write-once retention behind it, must
accept a sequence once, ignore a repeated identical sequence, and refuse
every change or deletion. Neither Studio nor the operator agent holds a
credential that can delete there.

Verification: `ControlPlaneAuditVerifier` recomputes the chain, checks it
against the highest sequence this process wrote and the forwarded position,
and compares every forwarded entry with the sink. A broken chain, a sink
that lacks a forwarded entry, or an entry that differs raises the acute
alert `audit-chain-broken`, `audit-sink-gap`, or `audit-sink-mismatch`. A
chain rewritten with valid hashes on the Studio host still fails against the
sink. The Owner sees sessions, their entries, the verification state, and
forwarding progress in Settings → Access → Operator activity
(`GET /api/auth/audit/operator-activity`, `POST /api/auth/audit/verify`).

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
