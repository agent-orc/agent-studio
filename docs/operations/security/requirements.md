# Security requirements

These constraints are release gates for their named profile.

## Local profile

### L1: loopback only

The backend and development frontend bind only to loopback. Local CORS allows
the documented development origins. A local instance is not safe to expose by
port-forwarding, tunneling, or changing the listener to `0.0.0.0`.

### L2: attribution is not authentication

`X-Client-Id` may select client defaults and provide audit attribution. It must
never grant network access or be described as a credential.

## Networked profile

The app never edits a watched project's source code directly. All writes that affect the target repo go through the agent (which the user supervises) or through the app-owned task lifecycle (`docs/system/contracts/agent-task.md`).

### N1: HTTPS before exposure

The Task Server is reachable only through a TLS-terminating reverse proxy.
HTTP redirects to HTTPS. HSTS is present. The application rejects requests
whose trusted forwarded scheme is not HTTPS. Angular, the API, and hubs share
one origin.

### N2: humans use server sessions

First-owner bootstrap is one-time. Passwords use modern salted adaptive
hashing. Login throttling, password change, owner reset, forced change, session
idle expiry, absolute expiry, logout, secure cookies, and CSRF protection are
mandatory. Angular stores no password, session token, or bearer credential.
Logout and an expired-session 401 must stop browser polling and event reconnects
before returning Studio to the login gate.

### N3: authorization stays small

The only human roles are owner, operator, and viewer. Owner identity operations
cannot remove the final active owner. Viewer mutations fail. Project membership
is enforced on project-addressed routes for scoped non-owner accounts, and on
body-addressed task-set mutations (reorder, batch-move) for every affected task.
A single-task route whose project cannot be resolved fails closed rather than
allowing the request; scope is never granted by an unresolved project. The
project comes from the route or addressed task first; `?project=` may narrow
but never replace it. An empty membership list grants a scoped account no
project; only the explicit `*` entry grants every project. There is
no tenant boundary, SSO, billing role, or policy language. Workspace-wide task,
Runner, registry, and search collections must filter out disallowed projects.

### N4: Runners use revocable services identities

Open self-registration is disabled. An owner creates a short-lived one-time
enrollment code. Runner credentials are random, one-time reveal, hash-only at
rest, independently scoped, expirable, last-use tracked, and revocable.
Overlapping rotation is supported. Runner ids on lease and completion requests
must match the authenticated identity.

### N5: least-privilege Runner scopes

Claim, lease and heartbeat, log upload, event upload, artifact upload, and
completion are separate scopes. A credential missing the route's scope receives
403. Runner service credentials cannot sign in to Studio or call human admin
surfaces.

### N6: reads, writes, and streams fail closed

All API reads and writes require a human session unless the route is an
explicitly scoped Runner route. SignalR negotiation, connection, subscription,
and automatic reconnect require a live human session that is not waiting on a
forced password change. A request under `/api/v1/` skips local authentication
only when routing selected the standalone Task Server proxy endpoint and it
carries exactly one well-formed `Bearer` credential; the upstream then
authenticates it. `/healthz` and the
minimum pre-auth endpoints are the only anonymous application routes.

### N7: dual-principal run audit

Every remote run records the initiating human or automation principal and the
authenticated executing Runner principal. Credential ids may be recorded;
credential plaintext may not.

### N8: production surface reduction

Debug and internal probe endpoints are absent. Exception details are off.
Health is minimal. Reverse proxy and Kestrel request limits are finite. Proxy
configuration preserves WebSocket upgrades and only one trusted forwarded
header hop.

### N9: credential redaction

Passwords, cookies, authorization headers, session tokens, enrollment codes,
and Runner bearer secrets must not appear in logs, task output, diagnostics, or
audit. Tests cover the product credential formats and named secret fields.

## Shared data rules

- Secret material never enters the repository.
- Watched-project writes follow the task lifecycle contract.
- User-authored Markdown remains inert and sanitized.
- Security actions leave durable, non-secret audit evidence.
