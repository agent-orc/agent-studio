# Application telemetry intake (OTLP)

Agent Studio accepts OpenTelemetry metrics and logs from the applications it
deploys and keeps them in a built-in store, one store per project environment.
This implements Dossier
[integrierte-plattform](../integrierte-plattform/index.html) section 8.3 (IP-15)
with decision IP-D14 option A: a built-in store, with export hooks for external
systems. No exporter ships yet. The Studio views over this data are a separate
card (IP-16).

## Connect an application

1. Declare the environment in the project repository under
   `docs/deployments/environments/<id>.json`. Tokens are issued only for
   declared environments.
2. Create an ingest token (Owner or an Admin bound to the workspace or project
   in the networked profile; the plaintext is shown once):

   ```http
   POST /api/projects/{project}/environments/{environment}/telemetry/tokens
   {"name": "shop-api", "signals": ["metrics", "logs"], "expiresAt": null}
   ```

   The response carries `secret`, `otlpEndpoint`, and `otlpHeaders`.
3. Configure the OpenTelemetry SDK or collector of the application:

   ```sh
   OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf      # or http/json
   OTEL_EXPORTER_OTLP_ENDPOINT=https://<studio>/api/telemetry/otlp
   OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <secret>"
   ```

   The SDK appends `/v1/metrics` and `/v1/logs`. Traces are not accepted yet.

List tokens with `GET .../telemetry/tokens` and revoke one with
`DELETE .../telemetry/tokens/{tokenId}`. A revoked or expired token gets
HTTP 401 at once. A token whose environment is no longer declared (the
declaration file was removed, or the project left Studio) gets HTTP 401 within
30 seconds, the time the intake trusts the declarations it last read. The token
is rejected, not revoked: if the declarations cannot be read, the intake answers
503 with `Retry-After` and the token keeps working once they can be read again.
The networked profile requires `project.settings.write` to list, create, or
revoke ingest tokens in that project. Telemetry queries require `project.read`;
the OTLP intake authenticates the environment ingest token itself.

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

Points older than the hourly retention, log records older than the log
retention, and anything more than 10 minutes in the future count as rejected.
Log bodies, event names, severity text, and the attribute values of logs and metrics pass through the
credential redactor before durable staging, storage, queries, or export. Values held by the secret provider (AGT-3222) will
be masked once that provider exists.

## Storage and retention

Each environment has one SQLite database at
`<TaskRepository>/telemetry/<project>/<environment>.sqlite`. Override the root
with `Telemetry:StoreRoot`.

| Data | Kept | Setting |
|---|---|---|
| Metrics at one-minute resolution | 14 days | `Telemetry:MetricMinuteRetentionDays` |
| Hourly metric aggregates | 90 days | `Telemetry:MetricHourRetentionDays` |
| Logs | 7 days, at most 256 MiB of record payload per environment | `Telemetry:LogRetentionDays`, `Telemetry:LogMaxBytesPerEnvironment` |
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

Attribute values are compared as text. Resource attributes, record
attributes, and the instrumentation scope name (`otel.scope.name`) are merged.

## Export hooks

`ITelemetryExportSink` receives every committed batch after masking. A failing
sink is logged and does not affect storage. Prometheus, Loki, and OTLP-forward
sinks are later cards.
