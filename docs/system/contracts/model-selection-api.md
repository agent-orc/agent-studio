# Model selection API

The [model-selection Dossier](../../model-selection-and-fallback/index.html) defines the selection ledger and its working decisions. These routes expose selection receipts, routing policy, admission and the token-free routing drill. The [routing domain](../domains/model-routing-policy.md#pipeline-classes-quota-reserve-and-provider-switches-agt-4035) describes equivalence, reserve and hard-pin behavior. JSON property names and string enum values below use camel case.

## Access

Reading `/api/model-selection/policy` requires `workspace.read`. Reading the quota pace inventory and changing policy settings require `workspace.settings.write`. Other model selection reads require `workspace.operate`. Both `POST /api/model-selection/events` and `POST /api/model-selection/plan` require a runner service principal with the `Events` runner scope; a normal user session cannot call them. The Task Server uses the plan route as the installation's routing authority and remote execution hosts report completion through the events route.

## Read routes

| Route | Query | Response |
|---|---|---|
| `GET /api/model-selection/events` | Optional `provider`, `project`, `consumer`, `rule`, `from`, `to`, `taskKey`, `eventId`, `since`, `until`, `cursor` | `{ "events": [ModelSelectionEvent], "nextCursor": string \| null }` |
| `GET /api/model-selection/active` | None | `ModelSelectionEvent[]` for open fallbacks and unexpired health marks |
| `GET /api/model-selection/effective` | Optional `project`, `pipelineType` | `{ "project": string \| null, "pipelineType": string \| null, "routes": [EffectiveModelSelection] }` |
| `GET /api/model-selection/policy` | None | Full `ModelSelectionPolicy`, including `shortStepReservePoints` and migration counts. The first read initializes policy and migrates legacy settings. |
| `GET /api/model-selection/drill` | Optional `project`, `pipelineType` | `RoutingDrillReport` described below. Uses fake quota snapshots without dispatching a CLI or writing selection receipts. |

The events route sorts by `at` descending, then `id` descending. Pages contain up to 100 events. Pass `nextCursor` as the next request's `cursor`; an unknown cursor returns an empty page. Filters use case-insensitive exact matches: `provider` matches either `from.cliType` or `to.cliType`, while `from` and `to` match their respective model IDs. `taskKey` and `eventId` match the card key and event ID. `since` and `until` are inclusive ISO timestamp bounds on `at`. The other filters match the event fields of the same name. The cursor is an event ID, not a timestamp.

Each effective route has `consumer`, nullable `stepId`, `configured` (`cliType`, nullable `model`, nullable `thinkingLevel`), nullable `effective` with the same shape, `rule`, and nullable `reason`. `effective: null` means admission would wait. An unknown `pipelineType` returns HTTP 400 with `{ "error": "Unknown pipelineType." }`. This is a read-only projection and does not create a ledger event.

## Execution-host model catalogue

`GET /api/cli/codex/models` returns a `CliModelCatalog` with `models`,
`source: "execution-hosts"`, and `fetchedAt` (the time the projection was built).
Each request reads the latest coding and review host advertisements already
recorded by the installation. In a split installation Studio serves this
GET route and reads Task Server's host inventory with a five-second deadline.

`?refresh=true` lets the picker bypass its own cache. The API uses the same
host-inventory read for both request forms. Probe schedules and CLI process
state are independent of this read. Callers with project access can read the
catalogue. A request with `refresh=true` requires
workspace-wide `workspace.operate` and returns HTTP 403 for scoped callers.

A model is available when at least one active host seen within the last
three minutes has fresh, usable Codex execution evidence and either advertises
the model or has fresh, usable acceptance for it. Fresh model-specific
acceptance evidence takes precedence over that host's CLI model list.
If no qualifying host advertises a known model, or the host inventory cannot
be read, the API returns HTTP 200 with the relevant registry entries marked
`available: false`. `isDefault` identifies the policy default independently
of `available`. Pickers use both fields when preselecting a model. The create
form also reevaluates its automatic selection when a refreshed catalogue
omits the selected model or marks it unavailable. If no available policy default is
listed, the form delegates the selection to the server and omits the model
and thinking level from the create request. Explicit selections remain
visible with their availability status.

## Reserve setting

`PUT /api/model-selection/policy/reserve` accepts `{ "points": 10 }` and returns the full updated policy. `shortStepReservePoints` defaults to 10 and accepts integers from 0 through 50. An out-of-range value returns HTTP 400 with `{ "error": "Reserve must be between 0 and 50 percentage points." }`.

The reserve subtracts percentage points from the configured quota cap for new long work. Review aspects, grade, summary, enrichment and bounded decisions may use that headroom up to the cap; an available same-session continuation may also use it. For example, a 95% cap with a 10-point reserve switches new coding work at 85%. Saving this setting affects later admission decisions and does not interrupt running work. Other policy fields and consumer mutations are documented in the [domain API table](../domains/model-routing-policy.md#model-selection-api-contract).

`GET /api/model-selection/policy/pace` lists reported and configured provider windows. Each row includes the saved `pace`, the reading time, usage and the percentage available to new long work after the reserve. `suspicious` means a confirmation is required.

`PUT /api/model-selection/policy/pace/{cliType}/{windowLabel}` accepts `{"mode":"steady"}`, `{"mode":"useBy","useBy":"2026-11-28T21:49:00Z"}` or `{"mode":"unpaced"}` and returns the updated row. The endpoint requires a known provider and window and an ISO date and time with an offset for `useBy`. A successful write is recorded on the operator audit stream. Both pace routes require `workspace.settings.write`.

Steady is the default for existing policies. Use by moves the end of the allowance line to the requested instant, capped at reset. Once the instant passes, the whole effective cap is available. The absolute deadline stays configured until edited. Unpaced removes the projection for that window. Both settings retain strict caps, reserve and reading validation. Each fresh admission decision reads the current policy.


## Routing authority

`POST /api/model-selection/plan` accepts `ModelRouteAdmissionRequest`:

```json
{
  "request": {
    "consumer": "review-aspect",
    "workClass": "review-aspect",
    "project": "example",
    "pipelineType": "task",
    "stepId": "aspect-code-quality",
    "taskKey": "EXAMPLE-1",
    "attemptId": "attempt-1",
    "pins": {
      "cliType": "claude",
      "model": "configured-model-id",
      "thinkingLevel": "low",
      "mode": "preference"
    },
    "isExplicitPin": true
  },
  "acceptedRoutes": [
    { "cliType": "claude", "model": "configured-model-id", "thinkingLevel": "low" }
  ],
  "unavailableProviders": []
}
```

The example model ID is a placeholder, not a declared equivalent. `acceptedRoutes` supplies fresh host model-acceptance facts; the authority checks provider and model membership. `unavailableProviders` excludes unhealthy providers on that host. Additional request fields are optional `round`, `taskType`, `taskMode`, `taskTitle`, `taskPrompt`, `correctnessFloor`, `required` capability flags, `host`, and `continuation`. A continuation contains `sessionCli`, `sessionModel` and optional `sessionThinkingLevel`. Work classes are `coding`, `review-aspect`, `grade`, `summary`, `enrichment`, `conflict-resolution`, `concept`, `decision`, `analysis` and `deterministic`. When workClass is omitted, a known catalogue step supplies it; otherwise the consumer supplies the default. Class is independent of the preferred provider/model pair.

The authority resolves the card's current pipeline type and mode when `taskKey` identifies a card. A card with a hard pin forces `pins.mode: "hard"` and clears continuation routing. Coding fallbacks must clear the card's correctness floor; review and grade require the same capability class. Cross-provider routes must be published by Token Economy. A missing equivalent makes admission wait rather than creating a mapping.

HTTP 200 returns `SelectionResult`, including for a wait:

| Field | Meaning |
|---|---|
| `outcome` | `primary`, `fallback`, `wait`, `throttle` or `refused`. |
| `route`, `primary` | Selected and originally preferred routes, each with `cliType`, nullable `model` and nullable `thinkingLevel`. A route in a wait result is not authorization to launch. |
| `rule`, `reason` | The deciding rule and explanation, including hard-pin or provider-switch reasons. |
| `equivalence`, `equivalentRoutes` | Nullable qualification receipt (`capabilityClass`, `catalogueVersion`, `thinkingMapping`, `source`) and published alternate routes for model-acceptance probes. |
| `quota` | Nullable deciding `window`, `usedPct`, `projection` and `projectionWarning`. |
| `retryAt`, `returnToPrimaryAt`, `nearbyResetWait` | Nullable retry/return timestamps and whether admission waits for a nearby reset. |
| `fallback`, `ledgerId` | Nullable run-scoped fallback receipt and selection event ID used to report execution. |
| `isFallback`, `shouldLaunch` | Computed flags; only `primary` and `fallback` authorize launch. |

This route records a selection receipt with execution path `task-server-claim`; it does not dispatch or interrupt a process. A healthy continuation keeps its session route. A necessary provider switch requires a new provider session; native session IDs never cross providers.

## Dry-run drill

`GET /api/model-selection/drill` returns `{ "dryRun": true, "passed": boolean, "catalogueVersion": string, "shortStepReservePoints": number, "cases": [...], "missingEquivalence": [...] }`. Each case contains `scenario`, `workClass`, `selection` (the `SelectionResult` above) and `passed`. Scenarios exercise primary exhaustion, both providers exhausted, and exhausted follow-up sessions in both directions, across coding, all four review aspects, grade, summary, enrichment, conflict resolution and concept.

Each missing-equivalence entry has `consumer`, nullable `stepId`, `preferred` (an effective-route shape) and `reason`. The drill uses available same-capability published pairs for its scenarios. `passed` means at least one case ran and every generated case passed; it does not mean that every configured route has an equivalent. Inspect `missingEquivalence` separately for upstream qualification gaps. The operator wrapper is `studio-op routing drill --dry-run --project <project> --json`. It spends no model tokens and writes no selection receipts.

## Ingestion

`POST /api/model-selection/events` accepts one `ModelSelectionEvent` JSON object. `id`, `kind`, and `consumer` must be nonempty; otherwise the route returns HTTP 400 with `{ "error": "Missing selection event fields." }`. On a valid request it returns HTTP 200 with `{ "added": boolean, "id": string }`. A repeated `id` returns `added: false`, including an ID absorbed into a collapsed waiting event. Identical waiting events for one card on the same UTC day collapse into one stored event with an incremented `count` and `collapsedIds`.

The base event fields are `id`, `at` (UTC timestamp), `kind`, `consumer`, optional `host`, `project`, `pipelineType`, `stepId`, `taskKey`, `attemptId`, `round`, `from`, `to`, `rule`, `reason`, `evidence`, `quota`, `equivalence`, `returnToPrimaryAt`, `closesId`, `pricePerMTok`, and `count`. `from` and `to` each contain `cliType`, `model`, and `thinkingLevel`. `evidence` contains `excerpt` and `classifierKind`; the writer redacts secrets and truncates the excerpt to 300 characters. `quota` contains `provider`, `window`, `usedPct`, `capPct`, `projectedPct`, `resetAt`, `snapshotAgeSeconds`, and the `windowPaces` map for the primary provider. `equivalence` contains `capabilityClass`, `catalogueVersion`, and `source`. `pricePerMTok` contains `from` and `to`. The kinds are `switched`, `waiting`, `refused`, `returned`, `health-marked`, `health-cleared`, and `operator-switch`. A closing event identifies the open event with `closesId`.

Execution tracking adds `selected` and `executed` kinds and these fields:

| Field | Meaning |
|---|---|
| `executionId` | Original selection event ID, not an execution process ID. |
| `executionProvider` | Provider on which the call completed. |
| `providerFailure` | Boolean, default false; reports a provider failure to the bounded provider-error circuit. |
| `nearbyResetWait` | Boolean, default false; marks opted-in reset waits for attention filtering. |
| `collapsedIds` | IDs absorbed into a collapsed waiting event, when present. |

To report completion, post an event with nonempty `id`, `consumer`, `kind: "executed"`, `executionId` set to the selection's `ledgerId`, and optional `executionProvider` and `providerFailure`. The service derives the stored receipt from the original `selected`, `switched` or `returned` event, retaining the actual `to` route and switch reason. The supplied `executionProvider` feeds health observation; the stored provider comes from the original selection. Its stored ID is `executed:<selectionId>`; a retry returns `added: false`. An unknown selection ID also returns `added: false`. The response `id` echoes the submitted ID. An `executed` event without `executionId` follows normal append behavior rather than this completion path. Ordinary selection IDs are ULIDs; execution receipt IDs use the prefix above.

The usage cockpit response (`GET /api/usage/cockpit`) adds `shortStepReservePoints` and `routingUsage`, an array of `{ "provider": string, "executions": number, "switchExecutions": number }`. It counts `executed` receipts for the current usage-calendar week in visible projects; unscoped receipts require permission to see unattributed usage. A switched execution has different `from.cliType` and `to.cliType`. Return receipts use the returned route as `from`, so they do not count as switches. Historical calls without execution receipts are not inferred.

The monolith stores events as daily JSONL under `<TaskRepository>/.runtime/model-selection/`. Event files are retained for 90 days; daily count summaries by provider, rule, and consumer are retained for 400 days. The active projection is rebuilt from the last 14 days at startup.
