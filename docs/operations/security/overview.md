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
| `networked` | One small organization using a separately hosted Task Server | HTTPS through the reference reverse proxy | Human server sessions and distinct Runner service credentials |

`X-Client-Id` is attribution in both profiles. It is never proof of identity.
The networked middleware derives the authenticated principal from the secure
session cookie or Runner bearer credential and records any supplied
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
  600,000 iterations. Login is throttled. Password resets create a temporary
  password, revoke existing sessions, and force a change at the next login.
- Human sessions use a random opaque token stored only as a SHA-256 hash on the
  server. The cookie is `Secure`, `HttpOnly`, `SameSite=Strict`, host-only, and
  has idle plus absolute expiry. Browser mutations require the matching CSRF
  header. Studio exposes an explicit sign-out action; a logout or API 401 tears
  down browser polling and SignalR before returning to the login gate.
- Roles are intentionally small: owner manages identities and all projects,
  operator reads and mutates allowed projects, and viewer is read-only.
  A non-empty project membership list restricts project routes. An empty list
  means all projects in this single organization. Workspace task lists,
  Runner status and feed data, registry data, and global search results are
  filtered to the same allowed-project set. Body-addressed task-set mutations
  (reorder, batch-move) carry their targets in the request body, so their
  handlers enforce membership on every affected task; a single-task route whose
  project cannot be resolved is denied rather than allowed, so an unresolved
  project never becomes an authorization bypass.
- Owners mint short-lived one-time Runner enrollment codes. Enrollment reveals
  one service credential once. The server stores only credential hashes.
  Credentials carry explicit scopes, optional expiry, last-use time, and
  independent revocation. Rotation creates an overlapping credential so the
  old one can remain valid until the new daemon is proven.
- A Runner never receives a human password or session cookie.

The minimum Runner scopes are `runner.claim`, `runner.lease`, `runner.logs`,
`runner.events`, `runner.artifacts`, and `runner.completion`. Reading the prompt
for a claimed task is covered by `runner.claim`. Lease requests bind the wire
`runnerId` to the authenticated service identity.

## Transport and exposed surfaces

The networked profile refuses non-HTTPS application requests, except the
minimal `/healthz` liveness endpoint. It trusts one forwarded-header hop from a
known proxy. The reference Caddy deployment owns ACME renewal, redirects HTTP,
sets HSTS, caps request bodies, forwards WebSocket upgrades, and serves Angular
from the same origin as `/api` and `/hubs`.

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
