# Environment health checks

IP-14 of the [integrated platform Dossier](integrierte-plattform/index.html)
(§8, operations). The control plane probes every declared project environment
from outside the host that runs the app, so a host that is down cannot report
itself healthy. The results feed uptime, latency and incidents in the project's
Deploy area (Operations tab) and the workspace Environment view. Incidents
become Activity entries and cards.

Implemented decision: IP-D14 option A (built-in store, export later through
IP-15). Samples and state live in the installation; no extra service is needed.

## Declaring checks

Checks belong to the environment declaration in
`docs/deployments/environments/<id>.json`
([schema](../app/schemas/project-environment.schema.json)). Without a
`healthChecks` array the control plane derives the checks:

| Declaration | Derived check |
|---|---|
| `healthUrl` set | `http` against the health URL, expects 200 |
| no `healthUrl`, `address` is an HTTP(S) URL | `http` against the address, expects 200 |
| `address` uses HTTPS | `tls` against the address host and port |
| `address` is `host:port` or `tcp://host:port` | `tcp` connect |

An explicit `healthChecks` array replaces the derived list; an empty array turns
checks off. Each entry:

```json
{ "id": "api", "kind": "http", "url": "https://shop.example/api/health",
  "expectedStatus": 200, "bodyContains": "ok",
  "intervalSeconds": 60, "timeoutSeconds": 10,
  "failureThreshold": 3, "recoveryThreshold": 2 }
```

`kind` is `http` (URL, expected status, optional body text within the first
64 KiB), `tcp` (connect to `host` and `port`) or `tls` (handshake, certificate
validity and expiry; `port` defaults to 443). `tcp` and `tls` may take host and
port from `url`. The interval is 60 to 3600 seconds, the timeout 1 to 60 seconds
and below the interval. Redirects are not followed, so a 301 is a wrong status
unless it is the expected one. Defaults: interval 60 s, timeout 10 s, failure
threshold 3, recovery threshold 2. Numbers must be integers.

A declaration file that does not parse (a missing or mistyped property, a
non-integer number) is reported on its own environment with status `invalid`
and the parser's message. The project's other environments and every other
project keep being probed.

## Address policy

Targets come from repository files, so the control plane only connects to
addresses it accepts. `HealthCheckConnector` resolves the target on every
connection (HTTP through `SocketsHttpHandler.ConnectCallback`, TCP and TLS
directly), keeps the allowed addresses and connects to exactly those, so a DNS
answer that changes between checks (rebinding) is checked again. HTTP probes
use no proxy and close the connection after each request.

| Addresses | Policy |
|---|---|
| Loopback (`127.0.0.0/8`, `::1`), link-local (`169.254.0.0/16`, `fe80::/10`), unspecified (`0.0.0.0/8`, `::`), multicast and reserved (`224.0.0.0/3`, `ff00::/8`), cloud metadata (`169.254.169.254`, `100.100.100.200`, `168.63.129.16`, `fd00:ec2::254`), and IPv4-mapped forms of these | Always refused |
| RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), shared address space (`100.64.0.0/10`), IPv6 unique-local (`fc00::/7`) | Only with `"healthChecksAllowPrivateNetwork": true` on the environment |
| Everything else | Allowed |

IPv4 addresses embedded in `64:ff9b::/96` and `2002::/16` follow the same
blocked, private, or public classification as the embedded IPv4 address.

A refused target fails with `address-blocked` before a byte is sent. That is a
declaration problem, not an outage: the check shows `invalid`, and the result
is neither stored in the series nor counted toward an incident.

## Runtime

`EnvironmentHealthWorker` ticks every 15 seconds (`HealthChecks:TickSeconds`)
and runs the checks whose interval is due. At most
`HealthChecks:MaxConcurrency` probes (default 4) run at the same time.
`HealthChecks:Enabled=false` turns the worker off. Every failure is stored as a
bounded code: `timeout`, `connection-failed`, `dns-failed`, `unexpected-status`,
`body-mismatch`, `tls-failed`, `certificate-invalid` or `certificate-expired`.
Response bodies are never stored. Two codes are shown on the check but not
counted: `address-blocked` (see above) and `probe-failed`, a fault of the probe
itself, which is logged.

Each project, and within the reports each environment, fails on its own. A
broken declaration, an unreadable state file or a failing card write is logged
and retried on the next tick; probing, reporting and retention continue for the
rest.

Store layout under `<TaskRepository>/health-checks/<project>/`:

| Path | Contents | Retention |
|---|---|---|
| `state.json` | Counters, open and last incidents of checks and environments, card ids, certificate warnings | current |
| `<environment>/<check>/raw/<yyyy-MM-dd>.jsonl` | One sample per probe (at most one per minute) | 30 days |
| `<environment>/<check>/hourly.jsonl` | Count, failures, p50, p95, max latency per hour of every completed day | 400 days |

Once an hour every completed raw day is folded into hourly aggregates, and raw
days that have left the 30-day window are deleted. A day that has an aggregated
hour is never folded twice. Parsed files are cached in memory, at most
`HealthCheckStore.CacheCapacity` (256) of them, least recently used first out.

## Uptime, latency and incidents

Uptime is the share of successful probes in the last 24 hours, 7 days and
30 days. The 24-hour value comes from raw samples; 7 and 30 days come from the
hourly aggregates plus the raw samples after the last aggregated hour, so the
oldest edge has hour granularity. An environment's uptime is its worst check's. Its latency
percentiles (p50, p95, p99 over 24 hours) come from its first HTTP check. The
status is `down` while an incident is open, `invalid` when the declaration is
broken or a target is refused, `degraded` after a failed probe below the
threshold, `up` when every check passed, and `unknown` before the first probe.

An incident opens after `failureThreshold` consecutive failures. Its start time
is the first failure of that streak. It closes after `recoveryThreshold`
consecutive successes. More failures while it is open continue the same
incident, so a flapping target never opens a second one.

Check incidents are grouped per environment: the environment incident is open
while any of its checks has an open incident, so a site whose HTTP and TLS
checks fail together raises one alert and one card. Reports
(`environment_health_updated` timeline events carry the `change`):

- Open: an `alert` Activity entry with topic `environment-incident-opened` and
  a Backlog bug card tagged `environment-health` with every failing check, its
  target and its last ten probes. If the card of this environment's previous
  incident is still open, the new incident comments on that card instead
  (`change: reopened`).
- More checks: a check that fails while the environment incident is open
  comments on its card (`check-failing`); a check that recovers while others
  still fail comments too (`check-recovered`).
- Recovery: when the last check recovers, an `observation` Activity entry
  `environment-incident-recovered` and a `recovered` comment with the duration.
  The comment and Activity entry are tracked separately so a failed Activity
  write does not repeat a successful card comment.
- Removed checks: a check (or environment) that no longer appears in the
  declarations closes its open incident on the next tick (`check-removed`); the
  recovery report says so. A broken declaration file does not count as a
  removal.
- Certificate: a `tls` check warns once when the certificate is within 14 days
  of expiry and once more within 3 days (`environment-certificate-expiring`).
  The first warning creates a chore card, and the second one comments on it.

Cards use a deterministic slug (`health-<environment>-<start-to-second>`,
`certificate-<environment>-<check>-<notAfter>`). A tick that stopped after
creating a card but before saving its id therefore adopts the existing card.
Targets in cards, Activity and the API appear without user info, query string
and fragment.

## API

- `GET /api/projects/{projectName}/environments/health`: one project's
  environments with checks, uptime, latency and incidents.
- `GET /api/environment/health`: every declared environment of the workspace,
  down first, then invalid. An environment that cannot be watched carries
  `error`.
