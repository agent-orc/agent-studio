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
Host Claude one-shots use a temporary empty working directory, disabled tools,
and an empty strict MCP configuration. Prompt enhancement and title generation
escape absolute file mentions before sending text to the CLI.

**Accepted risk for single-team installs (2026-10-25).** Coding agents on
`agent-runner-01` currently run with skip-all permissions in a shared host
environment. A Developer-authored card executed there can read that host's
provider credentials and other projects' checkouts. Treat all users allowed to
submit coding work to that runner as one trust group. Before admitting separate
teams, implement the Stage 2 per-job isolation proposed by the
[dynamic placement Dossier](../../dynamic-placement/index.html) (AGT-3171):
place each job in its own container, mount only that job's repository, and give
it a short-lived credential scoped to that job. Do not put persistent provider
credentials or another project's checkout in the job container.

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
  create and revoke named API tokens; create runner enrollment codes; and
  revoke enrolled runners. API tokens use the operator or viewer role and an
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
  Each mapped API route declares its token project scope. Routes without a
  project address, including workspace timelines, expensive-jobs lists, and
  screenshots, require the `*` project grant. A `?project=` query cannot turn
  a workspace route into a project route.
- A Runner never receives a human password or session cookie.

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
and response status to `<TaskRepository>/.security/api-token-audit.jsonl`.
Token values and query strings are omitted from that audit.

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
  If `npm audit fix` fails with `Cannot read properties of null (reading 'edgesOut')`,
  that is an npm 10 arborist defect on vitest's optional peer set; run
  `npx npm@11 update vitest` for that package, then `npm ci` so install
  scripts run.
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
