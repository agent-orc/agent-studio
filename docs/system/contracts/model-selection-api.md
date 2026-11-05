# Model selection API

The [model-selection Dossier](../../model-selection-and-fallback/index.html) defines the selection ledger and its working decisions. These routes expose the MS-2 implementation. JSON property names below use camel case.

## Access

`GET` routes require `workspace.operate`. `POST /api/model-selection/events` requires a runner service principal with the `Events` runner scope; a normal user session cannot ingest events. The Task Server and remote runner use this route to report decisions made outside the monolith.

## Read routes

| Route | Query | Response |
|---|---|---|
| `GET /api/model-selection/events` | Optional `provider`, `project`, `consumer`, `rule`, `from`, `to`, `cursor` | `{ "events": [ModelSelectionEvent], "nextCursor": string \| null }` |
| `GET /api/model-selection/active` | None | `ModelSelectionEvent[]` for open fallbacks and unexpired health marks |
| `GET /api/model-selection/effective` | Optional `project`, `pipelineType` | `{ "project": string \| null, "pipelineType": string \| null, "routes": [EffectiveModelSelection] }` |

The events route sorts by `at` descending, then `id` descending. Pages contain up to 100 events. Pass `nextCursor` as the next request's `cursor`; an unknown cursor returns an empty page. Filters use case-insensitive exact matches: `provider` matches either `from.cliType` or `to.cliType`, while `from` and `to` match their respective model IDs. The other filters match the event fields of the same name. The cursor is an event ID, not a timestamp.

Each effective route has `consumer`, nullable `stepId`, `configured` (`cliType`, nullable `model`, nullable `thinkingLevel`), nullable `effective` with the same shape, `rule`, and nullable `reason`. `effective: null` means admission would wait. An unknown `pipelineType` returns HTTP 400 with `{ "error": "Unknown pipelineType." }`. This is a read-only projection and does not create a ledger event.

## Ingestion

`POST /api/model-selection/events` accepts one `ModelSelectionEvent` JSON object. `id`, `kind`, and `consumer` must be nonempty; otherwise the route returns HTTP 400 with `{ "error": "Missing selection event fields." }`. On a valid request it returns HTTP 200 with `{ "added": boolean, "id": string }`. A repeated `id` returns `added: false`, including an ID absorbed into a collapsed waiting event. Identical waiting events for one card on the same UTC day collapse into one stored event with an incremented `count` and `collapsedIds`.

The event fields are `id` (ULID), `at` (UTC timestamp), `kind`, `consumer`, optional `host`, `project`, `pipelineType`, `stepId`, `taskKey`, `attemptId`, `round`, `from`, `to`, `rule`, `reason`, `evidence`, `quota`, `equivalence`, `returnToPrimaryAt`, `closesId`, `pricePerMTok`, and `count`. `from` and `to` each contain `cliType`, `model`, and `thinkingLevel`. `evidence` contains `excerpt` and `classifierKind`; the writer redacts secrets and truncates the excerpt to 300 characters. `quota` contains `provider`, `window`, `usedPct`, `capPct`, `projectedPct`, `resetAt`, and `snapshotAgeSeconds`. `equivalence` contains `capabilityClass`, `catalogueVersion`, and `source`. `pricePerMTok` contains `from` and `to`. The kinds are `switched`, `waiting`, `refused`, `returned`, `health-marked`, `health-cleared`, and `operator-switch`. A closing event identifies the open event with `closesId`.

The monolith stores events as daily JSONL under `<TaskRepository>/.runtime/model-selection/`. Event files are retained for 90 days; daily count summaries by provider, rule, and consumer are retained for 400 days. The active projection is rebuilt from the last 14 days at startup.
