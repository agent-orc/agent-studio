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
threshold 3, recovery threshold 2.

## Runtime

`EnvironmentHealthWorker` ticks every 15 seconds (`HealthChecks:TickSeconds`)
and runs the checks whose interval is due. At most
`HealthChecks:MaxConcurrency` probes (default 4) run at the same time.
`HealthChecks:Enabled=false` turns the worker off. Every failure is stored as a
bounded code: `timeout`, `connection-failed`, `dns-failed`, `unexpected-status`,
`body-mismatch`, `tls-failed`, `certificate-invalid` or `certificate-expired`.
Response bodies are never stored.

Store layout under `<TaskRepository>/health-checks/<project>/`:

| Path | Contents | Retention |
|---|---|---|
| `state.json` | Counters, open and last incident, card ids, certificate warnings | current |
| `<environment>/<check>/raw/<yyyy-MM-dd>.jsonl` | One sample per probe (at most one per minute) | 30 days |
| `<environment>/<check>/hourly.jsonl` | Count, failures, p50, p95, max latency per hour | 400 days |

Once an hour a raw day that has left the 30-day window is folded into hourly
aggregates and deleted. An hour that is already aggregated is never written
twice.

## Uptime, latency and incidents

Uptime is the share of successful probes in the last 24 hours, 7 days and
30 days. An environment's uptime is its worst check's. Its latency
percentiles (p50, p95, p99 over 24 hours) come from its first HTTP check. The
status is `down` while an incident is open, `degraded` after a failed probe
below the threshold, `up` when every check passed, and `unknown` before the
first probe.

An incident opens after `failureThreshold` consecutive failures. Its start time
is the first failure of that streak. It closes after `recoveryThreshold`
consecutive successes. More failures while it is open continue the same
incident, so a flapping target never opens a second one. Reports:

- Open: an `alert` Activity entry with topic `environment-incident-opened` and
  a Backlog bug card tagged `environment-health` with the check, the target and
  the last ten probes. If the card of this check's previous incident is still
  open, the new incident comments on that card instead
  (`environment_health_updated` timeline event, `change: reopened`).
- Recovery: an `observation` Activity entry `environment-incident-recovered`
  and a `recovered` comment with the duration on the card.
- Certificate: a `tls` check warns once when the certificate is within 14 days
  of expiry and once more within 3 days (`environment-certificate-expiring`).
  The first warning creates a chore card, and the second one comments on it.

Cards use a deterministic slug (`health-<environment>-<check>-<start>`,
`certificate-<environment>-<check>-<notAfter>`). A tick that stopped after
creating a card but before saving its id therefore adopts the existing card.

## API

- `GET /api/projects/{projectName}/environments/health`: one project's
  environments with checks, uptime, latency and incidents.
- `GET /api/environment/health`: every declared environment of the workspace,
  down first.
