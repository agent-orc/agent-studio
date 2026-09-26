# Operations backchannel

Status: accepted architecture, foundation stage delivered in AGT-2907, 2026-09-26.
[ADR-0075](../architecture/decisions/adr-archive.md#adr-0075---operations-server-brokers-host-execution-beside-task-server-authority-2026-09-25)
records D1, D2 and D4. The
[source dossier](../../operations/operations-server-backchannel/index.html)
records the selected D3 scope and original constraints. The operator's
2026-09-26 instruction places the expansion on AGT-2907 and accepts this
contract and outbound channel as its first delivery stage.

## Current implementation and remaining gate

`operations-server` and `operations-agent` are independently executable .NET
services. Shared wire records live in `contracts/Operations.Contracts`.
`host.inspect` version 1 is the only executable definition. It returns OS,
architecture and processor count without starting a child process or reading a
project filesystem. Several frontend service principals can submit commands,
but can only read or cancel their own attempts. An agent authenticates outbound;
it has no inbound listener and cannot use service routes or another agent's ID.

This is a service-boundary foundation, not full one-box product parity. Existing
Connector handlers and BFF routes have not migrated. File, Git, CLI, launcher,
dossier, render/capture, host-control, review-subject permits, artifact upload,
usage accounting, result acceptance into task history, automatic enrollment and
rotation through the product, and fleet reconciliation remain unimplemented.
Unknown operation IDs fail closed. A capability advertisement does not enable
an operation that has no registered definition and executor.

The selected standard remains **one-box Docker after full route and security
parity**. The private Compose rehearsal below does not replace the root product
Compose file or claim that parity. AGT-2736 delivers its bounded Option C
baseline without scope extension. D3's expansion belongs to AGT-2907 and its
follow-up stages, building on that compatible transitional baseline. Neither
the baseline card nor its delivery is blocked by the later parity work.

## Ownership

| Resource | Authority |
|---|---|
| Tasks, lanes, runs, reviews, accepted evidence, projects and workspace identity | Task Server |
| Orchestrator sessions, context, budgets, model policy, decisions and accepted responses | Task Server |
| Operation catalogue, command idempotency, bounded attempts and operational events | Operations Server |
| Host tools, roots, filesystem and isolated executable actions | Operations Agent |
| Human sessions, browser Origin and CSRF, server-side upstream credentials | Connector or separate LAN browser edge |

Existing coding and review Runner protocols continue to use Task Server. The
new channel does not replace their admission or outcome handling.

## Protocol and access

All Operations API requests require `Authorization: Bearer ...` and
`X-Operations-Protocol: 1`. `/healthz` is a non-sensitive liveness exception.
Requests with Origin, Cookie, Sec-Fetch-Site, Forwarded or X-Forwarded headers
are rejected before dispatch, even with a valid token. No CORS or cookie
middleware is installed. HTTPS is required outside loopback. The explicit
`Operations:AllowPrivateHttp` escape is only for an isolated Compose network;
it must not be enabled on a LAN listener.

`Operations:PrincipalsFile` names a protected JSON document containing
`principals`. Each entry has `id`, `audience: operations-server`, `tokenSha256`,
`kind` (`service` or `agent`), `scopes`, `agentIds`, `roots`, and `revoked`.
No wildcard scope or host matching exists. The document is reread on every
request; atomic replacement revokes or rotates credentials without restarting.
Tokens and principal IDs must be unique. Raw bearer secrets are never returned
by an API. Production provisioning and rotation UI remain a delivery gate.

A service needs the definition's `host.inspect` scope, an exact allowed target
agent, and `operations.read` to read or cancel attempts. Task-independent work
also needs `maintenance.execute`, an explicit audited host-maintenance grant.
An agent needs `agents.connect`, the operation scope and its enrolled agent ID.
Only roots present in its enrollment may be advertised. The current diagnostic
executor advertises no filesystem roots.

| Method and path under `/api/operations/v1` | Contract |
|---|---|
| `GET /catalogue` | Versioned, bounded operation definitions |
| `POST /commands` | `OperationCommand`; validates scope, input digest, deadline, permit and target before admission |
| `GET /attempts/{id}` | Requesting principal's bounded attempt and ordered events |
| `POST /attempts/{id}/cancel` | Cancels queued work or marks running work cancelling |
| `POST /agents/register` | Enrolled agent advertises boot generation and allowed capabilities |
| `POST /agents/{agentId}/poll` | Outbound claim; same boot gets the same outstanding assignment |
| `POST /attempts/{id}/renew` | Agent, boot and fence check, then live permit introspection |
| `POST /attempts/{id}/result` | Bounded typed result with matching subject digest and cleanup proof |

A command includes a principal-scoped idempotency key, delegated actor, exact
target, operation version, immutable JSON input digest, deadline and correlation
ID. The authenticated requesting principal is supplied by the server. Digests
are SHA-256 over UTF-8 produced by `OperationsProtocol.Json`. Reuse the immutable
serialized input and command when replaying; semantic JSON reordering is not a
new canonicalization rule. Same key plus changed command is a conflict.

## Task Server permits

Task Server schema 20 adds bounded opaque permits and the `operations` service
principal kind. That kind can receive only `operations:inspect`. It cannot
read arbitrary task data, issue permits, claim runs, or move lanes. Studio and
Engine kinds may receive `operations:issue`; existing persisted principals
must be provisioned with that scope before using the route. The migration
preserves existing credential hashes and foreign keys. Older binaries refuse
schema 20; rollback to schema 19 requires restoring a matching pre-upgrade
backup, not just changing the executable.

- `POST /api/v1/operations/permits` accepts `IssueOperationPermitRequest` under
  `operations:issue`. It checks the live running subject, task ID and fence,
  bounds the grant by the current lease and requested deadline, stores only a
  hash of a randomly generated token, and audits the bound identity and digest.
- `POST /api/v1/operations/permits/introspect` accepts only the Operations
  principal under `operations:inspect`. Every bound field and the current task
  lease are checked again. Public-demo execution locks protect both routes.
- `Operations:PermitIntrospectionUrl` must name that exact HTTPS endpoint.
  `Operations:PermitCredentialFile` holds the independent introspection bearer.
  Requests include the current Task Server protocol header. Redirects and
  cookies are disabled, with a three-second timeout and bounded response.

The caller must set its command deadline no later than the issued `validUntil`.
Admission, dispatch, renewal and result reporting all introspect. An absent
endpoint, stale fence, expired permit, revoked credential, Task Server outage,
read-only mode or maintenance mode fails closed. A Task Server restart
quarantines live run authority according to its existing contract; possession
of the old opaque permit does not bypass that quarantine. Only coding-run
subjects are implemented; review and orchestrator subjects fail closed.

## Persistence, failure and replay

Operations Server uses its own SQLite store, separate from Task Server. One
immediate transaction orders command admission, assignment, cancellation and
result acceptance. Fences increase durably. The initial bound is 1,024 attempts,
128 enrolled agents and 16 MiB of serialized state. At capacity, admission
stops rather than evicting idempotency records or unacknowledged evidence.
Operational retention and reclamation require a later explicit contract.

Each `host.inspect` lease lasts at most 30 seconds and cannot exceed its permit
or deadline. Agent health expires after 45 seconds. Queued expiry becomes
`timed-out`; an active expired lease becomes `unreachable`. There is no automatic
reassignment. A changed boot cannot adopt live work. This release supports only
read-only operations; mutating capabilities require additional containment and
positive no-overlap evidence before they may be registered.

Events have monotonically ordered attempt-local cursors and digest-only payloads.
The command carries the actor and correlation ID joining the attempt to Task
Server permit audit. Successful results require a matching input digest, valid
typed output, cleanup proof and ordered timestamps. Duplicate identical reports
return the existing receipt; changed reports conflict. Arbitrary artifact paths,
URLs and uploads are rejected by this initial capability.

The agent writes a result before reporting and replays its bounded local spool
before registering a new boot or polling new work. A still-live report from the
previous boot is accepted under its original boot and fence, then the new boot
can register. After expiry, a valid `host.inspect` report is stored as
`lateResult` with a `late-result-recorded` event. Its attempt remains
`unreachable`, and the late report is never accepted as a task outcome. This
evidence receipt does not require a live Task Server permit; it does not grant
execution or renew a lease. Invalid or conflicting evidence remains on disk
and stops that agent from taking more work until reconciled. This deliberately does not
claim disconnected mutation recovery. Agent process generations are unique
per start; raw tokens and permits are not written to the result spool. The spool
is limited to 128 files and 8 KiB per result. A crash during diagnostic execution
can repeat that read-only inspection, but cannot repeat a host mutation because
none is enabled.
On restart, a complete `.json.tmp` result is promoted and reported before new
work is polled. An incomplete temporary write is removed so the server can
replay the read-only assignment. An existing `.json` result takes precedence
over its temporary counterpart.

Studio shutdown does not stop either service. An Operations outage leaves Task
Server reads available. Agent/service credential files remain outside task
storage. Operations Server cannot mint substitute task authority during an
outage or accept a result as a task verdict.

## Private Compose rehearsal

From the repository root:

```sh
docker compose -f deploy/compose/operations/compose.yaml up --build -d
docker compose -f deploy/compose/operations/compose.yaml down
```

This source-built rehearsal has an internal network and no published ports,
Docker socket or host checkout mount. Bootstrap generates separate service and
agent credentials on first start with restricted file permissions and reuses
them later. Each consumer mounts only its own secret volume. The server mounts
principal hashes, not raw client tokens. State and spool volumes survive `down`.
The image SDK must satisfy the repository's `global.json`.

The full one-box profile must still integrate Task Server, Engine, an HTTPS
browser edge and the complete Operations Agent adapter, then prove fresh boot,
upgrade, drain, credential rotation, backup/restore, detached Studio, outage,
LAN-Origin denial, public-demo denial, cutover and rollback. Workstation preserves
the local Connector boundary; distributed deployment requires private TLS over
WireGuard and outbound-only agents. These profiles must not receive a production
parity label from this rehearsal.

## Dependent work

| Card | Required effect |
|---|---|
| AGT-2736 | Deliver bounded Option C unchanged; AGT-2907 and its follow-ups own D3 expansion above that compatible baseline |
| AGT-2737 | Preserve the current local-Connector Phase B rehearsal; add later host-operation extraction and rollback evidence |
| AGT-2738 | Installer selects a declared profile after contracts and full deployment proofs stabilize |
| AGT-2764 | Preserve the bounded interim link soak; later connectivity and host actions use typed Operations resources |
| AGT-2770 | Task Server keeps durable model policy; agents later supply typed live discovery and quota facts |

## Typed follow-up stages

The operator explicitly split these remaining stages into follow-up cards.
They are not acceptance blockers for the delivered contract and outbound channel.

| Key | Type | Scope and exit condition |
|---|---|---|
| OPS-CATALOGUE | feature | Add the full File, Git, CLI, launcher, dossier, render/capture and host-control catalogue with bounded artifacts, usage, accepted-result projections and mutation containment. Each executor needs scope, roots, cancellation, output-limit and no-overlap evidence. |
| OPS-EDGE-ADAPTERS | migration | Adapt Connector handlers and BFF routes to separate Task and Operations upstreams; add review-subject permits. Route inventory must name one owner and edge per route, with no fallback. Preserve exact loopback Host/Origin/CSRF and keep credentials server-side. Depends on the required catalogue definitions. |
| OPS-DEPLOYMENT-PARITY | deployment | Build on AGT-2736 Option C and prove full workstation, one-box Docker and distributed parity: HTTPS-only public edge, published-image install, scoped credentials/enrollment/rotation, upgrade/drain, backup/restore, detached Studio, outages, fencing, cutover and rollback. Align installer and dependent host/model-probe surfaces. Depends on catalogue and edge migration. |

## Verification

```sh
dotnet test operations-server.Tests/OperationsServer.Tests.csproj --filter 'Category!=MachineBound'
dotnet test task-server.Tests/TaskServer.Tests.csproj --filter 'Category!=MachineBound&(FullyQualifiedName~OperationPermitTests|FullyQualifiedName~PrincipalAuthenticationTests|FullyQualifiedName~ArchitectureBoundaryTests|FullyQualifiedName~PublicDemo)'
```

The disposable `bash scripts/operations-smoke.sh` rehearsal builds both images,
boots the private stack, checks browser denials and a real diagnostic result,
then restarts Operations Server and verifies durable replay. It removes only
its own temporary Compose project and volumes.

Tests use fake time and in-process HTTP, isolated SQLite stores, distinct
principals, replayed commands, revoked task authority, cancellation and expired
leases. They also execute the outbound diagnostic agent against the real service
handlers. The cross-service test issues a real Task Server permit over authenticated
HTTP, executes through the outbound agent, and proves that releasing the task
lease blocks subsequent dispatch. The Compose smoke exercises real processes
and restart replay separately. No frontend behavior changed, so no UI screenshot
is claimed.
