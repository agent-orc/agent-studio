# Application telemetry intake (OTLP)

Agent Studio accepts OpenTelemetry metrics, logs, and traces from the applications it
deploys and keeps them in a built-in store, one store per project environment.
This implements Dossier
[integrierte-plattform](../integrierte-plattform/index.html) section 8.3 (IP-15)
with decision IP-D14 option A: a built-in store, with export hooks for external
systems. No exporter ships yet. Deploy, Operations reads its trends and log
search from this store, and Deploy, Telemetry manages the ingest tokens.

## Connect an application

### Through a deploy (recommended)

Enable telemetry for the environment in schema 2 `.agent-studio/project.yml`:

```yaml
environments:
  - name: staging
    telemetry: true
  - name: production
    protected: true
    telemetry: true
```

A legacy declaration under `docs/deployments/environments/<id>.json` accepts
`"telemetry": true` as well. Every container deploy of such an environment then
sets these variables on the workload:

| Variable | Value |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `Telemetry:PublicOtlpEndpoint`, or `<studio>/api/telemetry/otlp` under the URL the deploy host reached Studio at |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=Bearer <ingest token>` |
| `OTEL_RESOURCE_ATTRIBUTES` | `deployment.environment.name=<environment>,agent_studio.release.id=<release>` |

At the deploy claim Studio mints an ingest token for metrics and logs scoped to
the environment (name "Studio deploy", created by `studio:deploy`). It stores
the token through the secret provider as `studio-telemetry-ingest` in the
environment's scope and binds it there. The deploy host resolves it like any
other workload secret, so Studio never shows or logs it. Revoking that token
under Deploy, Telemetry stops the running workload at once; the next deploy
mints a new token and revokes earlier deploy tokens. SSH container steps pass
the variables into the compose service even when the compose file does not map
them. A project must not declare these variables itself for such an
environment. Static sites and run-zone (edge) environments get no telemetry
variables. Set `Telemetry:PublicOtlpEndpoint` when workloads reach Studio at
another address than the deploy host does, for example from a container network.

The `container-service` project template ships the OpenTelemetry Node SDK
configured for this.

### By hand

1. Declare the environment (schema 2 `project.yml`, or
   `docs/deployments/environments/<id>.json`). Tokens are issued only for
   declared environments.
2. Open Deploy, Telemetry, choose the environment and create a token (owner or
   operator). Studio shows the endpoint, protocol and headers once; copy them.
   The page lists every token with its signals, creation, last use (updated at
   most every five minutes) and expiry, and revokes a token after a confirming
   click. The same operations are available on the API:

   ```http
   POST /api/projects/{project}/environments/{environment}/telemetry/tokens
   {"name": "shop-api", "signals": ["metrics", "logs", "traces"], "expiresAt": null}
   ```

   The response carries `secret`, `otlpEndpoint`, and `otlpHeaders`.
3. Configure the OpenTelemetry SDK or collector of the application:

   ```sh
   OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf      # or http/json
   OTEL_EXPORTER_OTLP_ENDPOINT=https://<studio>/api/telemetry/otlp
   OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <secret>"
   ```

   The SDK appends `/v1/metrics`, `/v1/logs`, or `/v1/traces`.

List tokens with `GET .../telemetry/tokens` and revoke one with
`DELETE .../telemetry/tokens/{tokenId}`. A revoked or expired token gets
HTTP 401 at once. A token whose environment is no longer declared (the
declaration file was removed, or the project left Studio) gets HTTP 401 within
30 seconds, the time the intake trusts the declarations it last read. The token
is rejected, not revoked: if the declarations cannot be read, the intake answers
503 with `Retry-After` and the token keeps working once they can be read again.

## Operations view

Deploy, Operations reads the last 30 days of each environment from this store
in hourly steps (`IOperationsTelemetryReader` is `StoreOperationsTelemetryReader`).
The metric mapping, the store-disabled state and log search are described in
[Project Operations mapping](#project-operations-mapping) below. A workload that
Studio deploys carries its release as the `agent_studio.release.id` resource
attribute, which the release filter of log search matches.

## Intake behaviour

| Case | Response |
|---|---|
| Accepted | 200 with an OTLP `Export*ServiceResponse`; `partialSuccess` counts points or records that were not stored and gives the first reason |
| Missing, unknown, revoked, or expired token, or an environment that is no longer declared | 401 |
| Environment declarations cannot be read | 503 with `Retry-After: 2` |
| Token without the signal | 403 |
| Content type other than `application/x-protobuf` or `application/json` | 415 |
| Body over `Telemetry:MaxRequestBytes` (8 MiB, also after gzip expansion) | 413 |
| More than `Telemetry:MaxRecordsPerRequest` (20,000) points or records, more than `Telemetry:MaxScopesPerRequest` (4,096) resources and scopes together, more than `Telemetry:MaxDecodedBytesPerRequest` (32 MiB) of estimated memory, or more than `Telemetry:MaxStoredBytesPerRequest` (64 MiB) to store | 413 |
| Malformed protobuf or JSON, including a JSON string with a lone surrogate escape (`"\uD800"`) or a number outside the double range (`1e400`) | 400 |
| Body not received and decoded within `Telemetry:RequestDeadlineSeconds` (30) | 408 |
| Durable staging is unavailable | 503 with `Retry-After: 2` |
| A metric request would exceed the environment's series count or byte limit, including metric batches still waiting to be written | 429 with `Retry-After: 2`; no points from that request are acknowledged |
| Queue full, this environment's share of the queue full, `Telemetry:MaxConcurrentRequests` (4) requests already in progress, or `Telemetry:MaxConcurrentRequestsPerEnvironment` (2) of them for this environment | 429 with `Retry-After: 2`; OTLP exporters retry |

Error bodies are a `google.rpc.Status` in the request's encoding. The intake
writes a bounded batch to a durable staging file before returning 200. One
background writer commits each staged batch to SQLite and removes the file
after the durable commit. SQLite uses WAL with full commit synchronization;
the staging rename and the database directory entry are synchronized before
the stage is removed. Failed writes stay staged at the front of the queue, and
staged batches are recovered in their persisted intake order after a restart.
A transaction marker makes replay after a commit idempotent. When more than
`Telemetry:MaxQueuedRecords` (200,000) records or `Telemetry:MaxQueuedBytes`
(128 MiB) of estimated memory are waiting, new requests get 429 until the
writer catches up. One environment may fill only
`Telemetry:MaxQueueSharePercentPerEnvironment` (50 percent) of each budget, so
it cannot keep the queue full for the others; a request larger than that share
gets 413. An environment whose share is already full gets 429 before its body
is read.

Memory and CPU time per request are bounded while it is decoded, not
afterwards. Decoding stops with 413 at the first record past
`MaxRecordsPerRequest`, at the first resource or scope past
`MaxScopesPerRequest`, or once the estimated memory of the decoded records
passes `MaxDecodedBytesPerRequest`. A small gzip body that expands into many
records or scopes therefore cannot build them all. Every attribute list
(resource, data point, and log record) is read up to
`Telemetry:MaxAttributesPerRecord` (128) entries while it is decoded; later
entries are skipped without being turned into text. Arrays and key-value lists
inside a value stop once their text is longer than the value limit. Each
resource's attribute set is built once, and every scope of the resource starts
from it. All records of one resource and instrumentation scope share one
attribute set; a record with attributes of its own holds references to the
shared strings. The merged attributes of one record are capped at
`MaxAttributesPerRecord` keys and `Telemetry:MaxAttributeBytesPerRecord`
(16 KiB of UTF-8 keys and values); further attributes are dropped, which also
caps the size of a series key. The request deadline also covers decoding: the
decoders check it every few thousand steps.

What a request stores is bounded by the request as well. A shared attribute set
costs its memory once, but every log row stores its attribute JSON and every
data point renders its series key. So each record counts the bytes the writer
stores and renders for it (attribute JSON, body, severity text, event name, ids,
metric name and unit, plus a fixed row size) toward
`Telemetry:MaxStoredBytesPerRequest` (64 MiB, at most a quarter of
`LogMaxBytesPerEnvironment`). Decoding stops with 413 past it. The writer also
stores at most about one such budget per flush. Attribute JSON escapes only
quotes, backslashes, and control characters, so `<`, `&`, or non-ASCII text
store about as many bytes as they have.

Each environment can hold at most `MaxConcurrentRequestsPerEnvironment` of the
`MaxConcurrentRequests` slots, so slow uploads from one environment's token
cannot block intake for the other environments. A slot is held from the start
of the body read until the request is queued or refused.
Configure collectors to send at most 20,000 points or records per request (for
the batch processor, `send_batch_max_size`).

Points older than the hourly retention, log records and spans older than the log
retention, and anything more than 10 minutes in the future count as rejected.
Log bodies, event names, severity text, span names, and attribute names and values of logs, metrics, and spans pass through the
credential redactor before durable staging, storage, queries, or export. For each
batch, intake also reads the active secret values in the project's environment,
project, and inherited workspace scopes from the secret store. It masks those
exact values even if no workload has resolved them recently. A secret-store read
failure makes intake return 503 instead of staging an unverified batch. The
writer repeats masking before SQLite storage and export.

### Purge telemetry stored before provider-value masking

The new masking rule does not rewrite existing SQLite rows or pending journal
files. For each affected environment, stop Studio, remove
`<StoreRoot>/<project>/<environment>.sqlite` and its `-wal` and `-shm` files,
then restart Studio. The default `StoreRoot` is
`<TaskRepository>/telemetry`. This discards that environment's logs, spans, and metric
history. Before restarting, remove every file from
`<StoreRoot>/pending/`, including temporary and completed journal files, since
old journal files may contain unmasked values and their file names do not identify
the environment. This discards queued batches
for all environments. Purge copies of those files in backups and any exported
telemetry through the relevant storage retention process. Do not inspect or
print the old rows to look for a secret.

## Storage and retention

Each environment has one SQLite database at
`<TaskRepository>/telemetry/<project>/<environment>.sqlite`. Override the root
with `Telemetry:StoreRoot`.

| Data | Kept | Setting |
|---|---|---|
| Metrics at one-minute resolution | 14 days | `Telemetry:MetricMinuteRetentionDays` |
| Hourly metric aggregates | 90 days | `Telemetry:MetricHourRetentionDays` |
| Logs | 7 days, at most 256 MiB of record payload per environment | `Telemetry:LogRetentionDays`, `Telemetry:LogMaxBytesPerEnvironment` |
| Spans | 7 days, at most 256 MiB of record payload per environment | `Telemetry:LogRetentionDays`, `Telemetry:LogMaxBytesPerEnvironment` |
| Series per environment | 10,000 series and 32 MiB of estimated series cache (2 bytes per character of name and attribute JSON plus 192 bytes per series); intake refuses a request that would exceed either cap before returning 200 | `Telemetry:MaxSeriesPerEnvironment`, `Telemetry:MaxSeriesBytesPerEnvironment` |
| Minute buckets of all environments together | about 2 GiB (96 bytes estimated per bucket) | `Telemetry:MaxMinuteBucketBytes` |
| Hour buckets of all environments together | about 1 GiB (96 bytes estimated per bucket) | `Telemetry:MaxHourBucketBytes` |

Every bucket keeps count, sum, min, max, and last value. Gauges and delta sums
add their value as one observation. Cumulative sums, histograms, and summaries
are stored as differences to the previous point of the same stream. A new
start time is a reset; a falling monotonic sum or histogram count is also a
reset. The first cumulative point is a baseline unless the stream just started.
Streams with different units, kinds, or temporalities remain separate. When the log payload passes the cap, the oldest
records are removed until it is at 90 percent of the cap. A sweep every
10 minutes (`Telemetry:RetentionSweepMinutes`) removes expired data. The same
sweep then checks the minute buckets of all environments against
`MaxMinuteBucketBytes`. Above the cap it removes whole hours of minute buckets,
oldest first across every environment, until the total is at 90 percent of the
cap. Hourly aggregates stay, so a minute-resolution query over a trimmed range
shows only what is left; ask for `step=3600` to read the hourly data. Points
15 to 90 days old write only hour buckets, so the same sweep checks the hour
buckets of all environments against `MaxHourBucketBytes`. Above that cap it
removes whole days of hour buckets, oldest first across every environment,
until the total is at 90 percent of the cap.

## Query API

All routes are below `/api/projects/{project}/environments/{environment}/telemetry`
and follow the Studio session and project-membership rules.

- `GET /metrics/names`: metric names with kind, unit, and series count.
- `GET /metrics?name=&from=&to=&step=&aggregation=&maxSeries=&attr.<key>=<value>`:
  series by name and exact attribute matches. `from` and `to` are ISO 8601 and
  default to the last hour. `step` is in seconds and is rounded up to the
  bucket width; without it the API aims at about 240 points. `aggregation` is
  `avg` (default), `sum`, `min`, `max`, `count`, or `last`. Ranges that start
  before the minute retention, and steps of an hour or more, read the hourly
  aggregates. Each series includes its unit, kind, and temporality. One query
  returns at most 1,440 points per series and 500 series.
- `GET /logs?from=&to=&minSeverity=&text=&attr.<key>=<value>&limit=&cursor=`:
  newest first. `minSeverity` is 1 to 24 or a level name. `text` is a
  case-insensitive substring of the body. Pass the returned `nextCursor` to
  read the next page.
- `GET /traces?from=&to=&limit=`: failed traces and traces lasting at least
  500 ms in a window of at most 30 days, ordered by failure and duration.
  The response includes trace IDs, duration, span count, and root name.
- `GET /traces/{traceId}`: spans ordered by start time, including parent IDs,
  duration, failure state, and masked attributes for the Operations waterfall.

Attribute values are compared as text. Resource attributes, record
attributes, and the instrumentation scope name (`otel.scope.name`) are merged.

## Project Operations mapping

The project Deploy > Operations tab reads the built-in per-environment store.
Set `Telemetry:Enabled=false` only when deliberately disabling that store; in
that case the tab shows the unavailable state. A store read failure returns a
named `Operations telemetry store read failed.` error instead of an empty state.

The 30-day view reads hourly buckets. Request count is the observation count of
`http.server.request.duration` (or `http.server.duration`); when neither is
present, it uses `http.server.request.count` or `http.server.requests`. Error
rate counts requests whose `http.response.status_code` is 500 or greater
(`http.status_code` is accepted for older senders). A missing status is not
treated as an error. Duration units `s` and `ms` are rendered in milliseconds.
For newly ingested `http.server.request.duration` histograms, intake also keeps
each explicit OTLP bucket as an internal `.bucket` series with an
`otel.histogram.bound` attribute. The displayed p50 and p95 interpolate within
those bucket boundaries, both over the window and per hour for the trend. Old
data without bucket series falls back to the count-weighted mean for p50 and
the observed maximum for p95; the store cannot recover exact quantiles from
old count, sum, minimum, and maximum alone. Routes in top endpoints come only
from `http.route` and omit any query string.

CPU selects the first present convention among `process.cpu.utilization`,
`container.cpu.utilization`, `system.cpu.utilization`, `process.cpu.time`, and
`container.cpu.usage`. Utilization and `container.cpu.usage` are gauges shown
in cores. `process.cpu.time` (and `container.cpu.time`) is a cumulative counter
of CPU seconds; the store keeps its per-point deltas, so each hour's CPU seconds
are divided by the time that hour covers (the current hour so far, at least a
minute) and summed over processes and states. The summary shows the latest hour.
A CPU time series that is not a sum, such as a gauge carrying the running total,
is ignored. Memory selects `process.memory.usage`,
`container.memory.usage`, then `system.memory.usage`. Disk selects
`system.filesystem.usage`, then `container.filesystem.usage`. Used-state gauge
series are summed within the latest bucket. `system.network.io`,
`container.network.io`, or `process.network.io` supplies network bytes when
present. Unreported reserved capacity, sessions, active users, and restarts
remain absent.

Log search reads the environment's stored, masked records newest first. Time,
exact severity group, release (`agent_studio.release.id`, `service.version`, or
`release.id`), and body text
can be combined. `nextCursor` lets the tab request the next page beyond 100
lines; changing a filter starts a new search.

## Export hooks

`ITelemetryExportSink` receives every committed batch after masking. A failing
sink is logged and does not affect storage. Prometheus, Loki, and OTLP-forward
sinks are later cards.
