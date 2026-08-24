---
id: platform-architecture-telemetry-layer
title: "Organization-wide telemetry layer (proposed event contract)"
status: proposed
category: concept
updatedAt: 2026-08-17
last-updated: 2026-08-17
reason: "Transfer the durable event contract out of the decision dossier so the architecture survives the dossier lifecycle"
taskKey: AGT-2671
tags: [telemetry, observability, agent-message-bus, privacy, orchestrator]
related-tasks: [AGT-2661, AGT-2557, AGT-2654]
related-adrs: []
related-docs:
  - "docs/concepts/platform-architecture/README.md"
  - "docs/operations/telemetry-layer/index.html"
  - "docs/operations/runtime/observability.md"
  - "docs/system/architecture/bus/agent-message-bus.md"
---

# Organization-wide telemetry layer: proposed event contract

> **Source dossier:** [Organization-wide telemetry layer](../../operations/telemetry-layer/index.html)
> (`AGT-W38`, source cards AGT-2661, AGT-2557, AGT-2654).
> Index: [Platform architecture](README.md).

## Status of this document

This page records the **proposed** telemetry contract for organization
applications. It is the durable architecture extract of the decision dossier
above.

**No operator decision has been taken.** The dossier status in
`docs/operations/telemetry-layer/workbench.json` is `decision-pending`. Nothing
under "Proposed event envelope", "Transport", "Level mapping" or "Signal to
orchestrator action" exists in code today. Those sections describe a contract
that becomes binding only if and when the decision in the final section is
approved. "What exists today" is the only part that describes shipped
behaviour; every path in it was verified against the checkout on 2026-08-17 and
the ledger row was added and re-verified on 2026-08-24.

Treat this file as the reference for what was proposed and why, not as an
implementation spec to build against without approval.

## Purpose

Agent Studio, Quality Studio, Coding Agent Chat, Coding Agent Runner, Token
Economy and its website, the Agent Studio website and voice-lint each produce
operational signal today, but there is no shared event contract *across* them
and no loop that turns a repeated signal into visible, reviewable work.

The qualifier matters. Within Agent Studio one such contract already ships: the
task timeline ledger (`TimelineEvent`, first row of the inventory below) is a
typed, closed-vocabulary JSONL event stream with a cause taxonomy, and
[`cycle-time-stage-model.md`](../cycle-time-stage-model.md) is a worked example
of deriving analysis from it. The gap this page addresses is therefore not
"no event contract exists" but "the existing contract is task-scoped and
single-product": it has no organization app identity, no actor or privacy
class, and no cross-application collection policy. The ledger is the precedent
to extend, not a competitor to replace.

The proposal adds one thin layer with three properties:

- **Local first.** No cloud account, no vendor endpoint, no mandatory
  OpenTelemetry deployment, no internet requirement.
- **Product-independent.** App adapters own domain semantics, Agent Studio owns
  cross-app operations, and an app keeps running when Agent Studio is stopped.
- **Inert bus, bounded action.** A message never performs the action it
  describes. Only an orchestrator policy acts, and only through existing
  application APIs.

It bridges two contracts that already exist and stay separate. Product Runtime
Observability records what a built app did and has no lane or task authority.
The Agent Message Bus records who observed, decided, advised or intervened.

## What exists today

The following is shipped and verifiable in the current checkout. "Gap" means
the signal is real but has no organization-level event contract or action loop
attached.

| Signal | Location | What it does today | Gap |
|---|---|---|---|
| **Task timeline ledger** | `backend/Shared/Models/TimelineEvent.cs`, written to task-local `logs/timeline.jsonl` | The unified per-task ledger of the whole task lifetime, and the closest thing the platform already has to the contract this page proposes. Roughly 70 `TimelineEventKinds` constants, a **deliberately closed** `Kind` enum, no free-form kind string and no `extras` bag: a new kind means enum plus writer plus test in one commit. Since 2026-08-23 it also carries a closed lane-change **cause** vocabulary (`LaneChangeCauses`, stamped as `details.cause` / `details.causeDetail` at every automatic lane-change site) plus the `integration_started` and `review_attempt_claimed` kinds. Consumed by the cycle-time stage model. | Task-scoped and Agent-Studio-internal: no organization app identity, no actor or privacy class, no cross-app collection policy. Local post-processing writes pipeline steps without `post_step_*` rows, so the ledger is not yet the single source for both the local and the remote flow. |
| Product runtime events | `backend/Features/Runtime/ProductRuntimeEventStore.cs`, `RuntimeEventPaths.cs`, `RuntimeEventWriter.cs`, `RuntimeEventValidator.cs`, schema `docs/app/schemas/product-runtime-event.schema.json` | Validated JSONL per day under the job or workspace runtime log folder: level, stable kebab-case event name, subsystem, timing, status, structured error, correlation and trace ids, tags, bounded payload. Read via `GET /api/runtime/{project}/events`. | No organization app identity, no actor, no privacy class, no fingerprint, no cross-app collection policy. Intentionally cannot trigger work. |
| Agent Message Bus | `backend/Features/Bus/AgentMessageBusStore.cs`, `AgentMessageBusBridge.cs`, `AgentMessageValidator.cs`, schema `docs/app/schemas/agent-message.schema.json` | Typed observation, decision, advisory, intervention, lifecycle, error, heartbeat, token and artifact-reference messages, persisted as JSONL and fanned out as `busMessageAdded`. | Producers are Agent Studio runtime actors only. No generic product-app collector, no telemetry cluster producer. |
| Runner log ingestion | `backend/Features/Diagnostics/LogIngestionEndpoints.cs` | `POST /api/runner/logs`. A fenced-lease runner ships raw output lines, the server appends them to the task's `logs/cli-output.log` after ANSI stripping and `backend/Features/Security/CredentialRedactor.cs` redaction. | Text log shipping, not structured event ingestion. No event name, no fingerprint, no clustering. |
| Runner event ingestion | `backend/Features/Diagnostics/RunnerEventIngestionEndpoints.cs`, `RunnerEventJournal.cs`, `backend/Features/Projection/Sources/RunnerEventSource.cs` | `POST /api/runner/events` accepts a normalized kind and journals it to task-local `logs/runner-events.jsonl`. | Scoped to runner session, turn, token and diagnostic events. Authority and recovery record, not a general product bus. |
| Runner durable outbox | `runner/DurableRunOutbox.cs`, `runner/HostOrchestratorJournal.cs` | Fenced write-ahead delivery facts and sequenced host reports. | Exact semantics must not be weakened by any normalization layer. |
| Git process telemetry | `backend/Features/Git/GitProcessTelemetry.cs` | Per-request spawn count, summed Git time, wall time, command breakdown, slow-spawn warning through `ILogger`. | Not durable as a cross-app event, no stable friction fingerprint. |
| Prompt call telemetry | `backend/Features/Prompts/PromptCallTelemetryService.cs`, `RuntimePromptService.cs` | Prompt id, content hash, model, estimated tokens, daily buckets, dead-prompt signal, written to `logs/prompt-calls.jsonl`. | Visible in its own catalogue, not correlated with outcomes or retries on the shared bus. |
| Wiki usage telemetry | `backend/Features/Docs/WikiAgentReadStore.cs`, schema `docs/app/schemas/wiki-usage-events.schema.json` | Visited, used and changed document facts with bounded evidence labels in gitignored runtime state. | Useful local-first precedent, scoped to docs. |
| Review evidence | `backend/Features/Runner/AspectRunnerService.cs`, `EvidenceGate.cs`, `backend/Features/Review/CodeReviewStepService.cs` | Structured verdicts plus human-readable twins, `results/review-evidence.jsonl`, missing-evidence gating. | Task-scoped. Cross-app defect recurrence is not clustered. |
| Visual QA verdicts | `backend/Features/Runner/VisualQa/VisualQaService.cs`, `VisualQaPolicy.cs` | Deterministic capture, bounded verdict, one restart-safe defect steer, owned retry budget. | The reference shape for telemetry-triggered action, but not yet fed by a shared signal. |
| Run records | `backend/Features/Runner/RunTimeline.cs` | Read projection over task-local `session-events.jsonl` and `logs/cli-output.log`, served by `/api/tasks/{id}/runs`. | Execution telemetry about product work, not product-usage telemetry. |
| Token facts | `backend/Features/Tokens/BusBackedTokenSummaryReader.cs`, `TokenAggregationService.cs` | Per-job and project summaries, participant attribution, timeline APIs. | Coverage is uneven. Token facts are cost-policy input, never a generic usage proxy. |
| Alarms and banners | `backend/Features/Pipeline/PipelineHealthService.cs`, `AcceptedIntegrationBackstopPolicy.cs` | Hanging gates, repeated failure fingerprints, stalled lanes, queue starvation, accepted-without-integration conditions. | Alarm types are feature-specific. No common intake, cluster receipt or card linkage. |
| Host telemetry | `backend/Features/Clients/HostTelemetryStore.cs`, `runner/HostTelemetrySampler.cs` | Host resource sampling from runners. | Host-level only. |

The written contracts for the existing runtime stream live at
[`docs/operations/runtime/observability.md`](../../operations/runtime/observability.md)
and [`docs/operations/runtime/log-capture.md`](../../operations/runtime/log-capture.md).

Nothing named `OrgTelemetryEvent`, `TelemetrySignal` or a cross-app telemetry
collector exists in the repository today.

## Proposed event envelope

The shared unit would be `OrgTelemetryEvent/v1`: a small immutable envelope
produced directly by a logging adapter, or derived from an existing
`ProductRuntimeEvent`.

This is a future schema, not an in-place edit.
`docs/app/schemas/product-runtime-event.schema.json` declares
`"additionalProperties": false` at the root, so organization fields cannot be
smuggled into the existing envelope or hidden inside `payload`. Adding them
requires an explicit schema decision.

| Field | Shape | Contract |
|---|---|---|
| `schemaVersion`, `id` | Required | Versioned envelope plus sortable UUID v7 or ULID. A retry preserves the id. |
| `occurredAt`, `observedAt` | Required, UTC | Producer time stays separate from collector time so offline replay stays honest. |
| `source` | Required object | `appId`, component, app version, local instance id, environment, optional repository id. `appId` is stable across display-name changes. |
| `eventName` | Required, stable | Kebab-case segments, for example `chat.message-send.failed`. Renames need an overlap period. Human prose is not an event name. |
| `category` | Required enum | `usage`, `friction`, `error`, `performance`, `lifecycle`, `quality`, `delivery`, `security`, `health`. |
| `level` | Required enum | `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`. Describes the record, not action authority. |
| `actor` | Required object | Kind is `human`, `agent`, `system` or `unknown`. Default identity is an app-local pseudonym. No account name required. |
| `contextRefs` | Optional typed array | References only: project, task, job, run, correlation, trace, session, route, workbench, artifact. Heavy evidence stays at the referenced path. |
| `fingerprint` | Required for clusterable events | Hash of stable dimensions: app, event, normalized error type and code, component, route family. Excludes timestamps, prose and raw ids. |
| `privacy` | Required object | Classification, applied redactions, export policy, content-presence flags. The collector rejects any record flagged `containsSecret`. |
| `payload` | Optional bounded object | Small allowlisted dimensions only. Each event contract documents its keys and their cardinality. |

Structured logging is the seam. Adapters attach to the logging system each app
already uses: `ILogger` in .NET, a small typed logger in TypeScript, equivalent
native adapters elsewhere. An event is emitted only when the record has a
stable `eventName` and structured dimensions, or when a narrowly configured
adapter maps a known legacy template. Free-form text scanning is a migration
aid, never the target contract.

## Privacy boundary

These rules are the hard boundary of the proposal. They are not tunable per app.

- **Local first, export never by default.** No collector requires a cloud
  account, vendor endpoint, telemetry backend or internet connection.
- **Never collect secrets.** Authentication headers, cookies, access tokens,
  credentials, private keys, environment secrets and raw authorization failures
  are rejected or redacted before append. This extends the existing
  `backend/Features/Security/CredentialRedactor.cs` posture to the event path.
- **Never collect content by default.** Chat messages, prompts, transcripts,
  audio, document bodies, source file contents, clipboard contents and Quality
  Studio input artifacts stay out of the shared event payload.
- **Reduce identity.** Human actors use rotating app-local pseudonyms unless a
  task already carries an authenticated principal that the audit record
  requires.
- **Bound paths and stacks.** Prefer project-relative route or component ids.
  Absolute home paths and full stacks stay in local evidence, the bus carries a
  redacted summary and a reference.
- **Reviewed publication only.** A task result may cite selected telemetry
  evidence through existing artifact review. That is an explicit delivery
  action, not telemetry export.

Proposed retention: raw edge buffers rotate daily and keep 30 days, `Trace` and
`Debug` keep 3 days when enabled, derived bus observations and action receipts
follow the workspace's task and Activity retention. The collector checkpoints
by app id, file identity, byte offset and last event id. Retention never
deletes an unacknowledged tail.

## Level mapping

Level controls volume. Policy controls action. The two are deliberately
decoupled.

| Level | Local file | Collector and bus behaviour | Default UI |
|---|---|---|---|
| Trace | Optional diagnostic buffer, short retention | Never forwarded by default, only through an explicit diagnostic capture | Hidden |
| Debug | Local buffer when debug capture is enabled | No bus message. May serve as evidence after a higher-level event points to the same correlation id | Hidden |
| Info | Persist | Forward only allowlisted usage, lifecycle, quality and completion events. Routine request logs stay local | Collapsed counters and domain timeline |
| Warn | Persist | Collect immediately, deduplicate, emit an observation only after the policy threshold or when explicitly acute | Observation, alarm only if policy says acute |
| Error | Persist with bounded error facts | Collect immediately, cluster by fingerprint, emit one bus Problem observation with count and evidence refs rather than one message per repeat | Problem in Activity |
| Fatal | Persist and flush synchronously where safe | Emit one High observation immediately after durable append, may open a needs-decision item, does not invent restart authority | Persistent alarm plus Activity |

An `Error` does not automatically create a task, and an `Info` usage event is
not automatically harmless. Event name, category, privacy class, fingerprint,
distinct correlation count, cooldown, open-card state and recipe authority are
evaluated together.

## Transport

Three architectures were assessed. The recommendation is the hybrid.

| Criterion | A: file plus collector | B: local push | C: hybrid (recommended) |
|---|---|---|---|
| Offline operation | Full | Weak unless every app builds its own outbox | Full |
| Loss boundary | Atomic file append | HTTP acknowledgement, per-app retry burden | Atomic file append, then optional HTTP receipt |
| Live latency | File watcher or short poll | Immediate | Immediate when reachable, replay otherwise |
| File-first fit | Strong, text is inspectable and diffable | Weak as sole source | Strong, raw files plus derived bus files preserve both layers |
| Operational complexity | Collector discovery and checkpoints | Endpoint security, availability, per-app retry | Collector plus a small push optimization and parity tests |
| Failure behaviour | Delayed visibility | Potential loss or app coupling | Delayed visibility, no loss, no app blocking |

### Hybrid write contract

1. The app redacts and atomically appends the event to its configured local
   daily JSONL file. This completes first, always.
2. The app optionally sends a tiny loopback wake-up carrying `appId`, file
   identity, offset and event id. It does not resend private payload by default.
3. The collector validates the event, advances a durable checkpoint only after
   acceptance, and computes a stable fingerprint. File watching and polling
   recover missed wake-ups.
4. Agent Studio persists a telemetry intake receipt, then emits one derived
   observation or error bus message carrying raw evidence references.
5. The existing `busMessageAdded` SignalR fan-out updates Activity. A reconnect
   replays from durable bus files, not from transient SignalR memory.

### Truth boundaries

| Layer | Authoritative for |
|---|---|
| App-local JSONL file | The raw event |
| Agent Message Bus | The derived observation and its cluster |
| Orchestrator decision receipt | Any resulting action |

### Security boundary

The loopback intake is disabled or token-authenticated by default, binds only
to the configured local interface, enforces registered app roots, rejects path
traversal and unknown app ids, caps line and request size, and never permits a
producer to choose a task lane or an action. SignalR stays server-to-client
fan-out only.

## Signal to orchestrator action

The orchestrator does not read raw lines. A deterministic cluster service
produces a `TelemetrySignal` carrying policy version, source app, fingerprint,
window, distinct correlation count, sample evidence refs, privacy summary and
any linked open card. The orchestrator then picks from a bounded vocabulary:
observe, prepare, intervene, ask.

| Pattern | Initial rule | Orchestrator behaviour | Authority and breaker |
|---|---|---|---|
| Repeated usage friction | Same fingerprint at least 5 times across 3 distinct correlations or local sessions in 7 days | Activity observation plus a prepared feature card with counts, affected flow, evidence refs, privacy-safe reproduction hints and a proposed acceptance signal | Default is needs decision, no queueing. One open card per (`appId`, `fingerprint`, `policyVersion`) |
| Error cluster | Same Error fingerprint 3 times across 2 correlations in 30 minutes, or one Fatal | Create or update one healing card in Backlog, surface a Problem entry, link the raw event slice, recent version, first and last occurrence | Card creation uses the Task API with an idempotency key and never starts a run. Fatal may also request an operator decision |
| Performance regression | Three complete windows where p95 exceeds both a declared absolute budget and the rolling baseline by 50 percent | Publish a trend observation, prepare a card only after a second window set confirms it | No single slow request can create work. Baseline version and sample count are stored in the receipt |
| Noisy warning | High count, no user-visible failure, identical fingerprint inside cooldown | Collapse into one digest, suggest logging or dedupe cleanup only when volume crosses a configured disk or review-noise budget | At most one digest per app and fingerprint per day |
| Visual QA clear defect | Consume durable verdict receipts from the AGT-2654 path, not screenshots or model prose alone | Keep the existing single precise steer. On repeated defect or unavailable capture, publish a guardian Problem with task, review epoch, iteration and evidence fingerprint | `backend/Features/Runner/VisualQa/VisualQaPolicy.cs` owns the one-retry budget. Telemetry cannot replenish it |
| Pipeline or delivery alarm | Reuse current hanging-gate, repeated fingerprint, stalled-lane, queue-starvation and accepted-integration facts | Project into the same cluster and Activity vocabulary, optionally ask the Watcher to inspect an exception | Existing pipeline and runner policies remain authoritative |

Every action carries the signal fingerprint and the policy version. An open
card or a prior dismissal suppresses duplicate preparation until the cooldown
expires or the evidence materially changes. Recovery, recurrence, dismissal and
human reversal all become measured feedback.

Every policy needs a window, a threshold, a distinct-correlation minimum, a
cooldown, a duplicate-card lookup, a maximum-actions-per-day cap and a
versioned receipt.

### Card contract

A telemetry-created or prepared card includes `sourceAppId`,
`telemetryFingerprint`, `policyVersion`, the time window, occurrence and
distinct-correlation counts, first and last seen, the app version range, a
redacted summary, evidence references, the privacy classification, the linked
Activity message id and the dedupe key. It never copies raw chat, prompt,
audio, source or product analysis content into a prompt.

### Guardian relationship

The Global Orchestrator Watcher concept lives at
[`docs/operations/orchestrator-waechter/`](../../operations/orchestrator-waechter/index.html)
(`AGT-W15`, also `decision-pending`). Telemetry supplies its missing cross-product
trigger substrate. The Watcher consumes clusters and receipts, never unbounded
raw streams. Observe-only signals stay cheap, ambiguous diagnosis follows the
Watcher's own analysis floor and decision surface.

## Agent Studio as the operations hub

The proposal makes an observed convergence explicit: Agent Studio is the
organization management layer for software delivery and operations, where
CI/CD state, telemetry, alarms and orchestration form one inspectable picture
across products.

| Agent Studio owns | Each product keeps |
|---|---|
| App registry, stable ids, collector configuration, local trust | Domain event names, semantic dimensions, logging calls, local raw evidence, product-specific retention overrides |
| Cross-app normalization, redaction enforcement, checkpoints, dedupe, clustering, retention visibility | Quality Studio's analysis engine, scoring, rule packs and report meaning |
| Derived bus observations and Activity projection | Coding Agent Chat's conversation state and message content |
| Guardian and orchestrator policy, decision packets, Task API card creation, action receipts, audit links | Token Economy's pricing and token-accounting logic |
| CI/CD convergence: promotion train status, Actions hygiene, delivery alarms, deployment evidence, cross-project health | voice-lint's audio and linguistic analysis, and the websites' audience analytics and consent surfaces |

Agent Studio does not become a runtime dependency for using an app, a warehouse
for private product data, or a replacement for product-owned debugging. Apps
keep running when Studio is stopped, and their domain cores stay independently
testable and releasable.

One operations surface, several truths:

| Plane | Canonical truth | Agent Studio projection |
|---|---|---|
| Application runtime | App-local structured JSONL and referenced evidence | Recent events, clusters, latency, privacy-safe drill-down |
| Delivery and CI/CD | Git SHAs, gate receipts, workflow results, promotion and deployment records | Promotion train, Actions hygiene, pipeline alarms, linked remediation work |
| Orchestration | Task store, runner leases and fences, policy receipts, lane transitions | Activity, decision needs, guardian actions, task linkage |
| Product analysis | Product-owned core, for example a Quality Studio analysis output | Only bounded status, health and artifact references needed for operations |

## Non-goals

- No cloud telemetry vendor, central SaaS account, data lake or mandatory
  OpenTelemetry deployment.
- No automatic feature implementation, task queueing, Git mutation or lane
  movement triggered by an event or a bus message.
- No replacement of runner journals, review evidence, task records, CI receipts
  or product-owned analysis cores.
- No full-text capture of user activity, conversations, prompts, code, audio or
  documents.
- No single global log level that turns all application logs into events.

## Open decision

Seven operator decisions are outstanding. Until they are answered, this
document describes intent only.

| # | Decision requested |
|---|---|
| 1 | Architecture: hybrid file-first buffering plus best-effort local push, no cloud dependency |
| 2 | Truth boundaries: raw app JSONL as event truth, bus observations as cluster truth, orchestrator receipts as action truth |
| 3 | Positioning: Agent Studio as the operations and management hub for CI/CD, telemetry, alarms and orchestration |
| 4 | Authority: bus stays inert, only the orchestrator acts, through existing APIs and approved recipes |
| 5 | Pilot scope and its first instrumented event category |
| 6 | Privacy: local-only defaults and the never-collect content list above |
| 7 | Rollout order across the organization applications |

The dossier recommends approving all seven. That recommendation, the weighted
option scoring behind it, and the pilot-versus-broad-rollout argument stay in
the dossier as decision material and are deliberately not reproduced here,
except where they define the contract itself.

Once a decision is recorded, update the status block at the top of this file,
move the affected sections from proposed to current, and update
`docs/operations/telemetry-layer/workbench.json`.

## Living knowledge log

- **2026-08-24 (AGT-2671):** Re-verified when the curation branch was rebased
  onto a develop that had meanwhile hardened the task timeline ledger
  (`integration_started` and `review_attempt_claimed` kinds, the closed
  `LaneChangeCauses` vocabulary stamped as `details.cause`). The original
  inventory omitted the ledger entirely, which made the purpose section's
  "there is no shared event contract" read as broader than the evidence
  supports: within Agent Studio a typed, closed-vocabulary event contract does
  ship. Added the ledger as the first inventory row, narrowed the thesis to the
  cross-application gap that is actually open, and cross-linked
  [`cycle-time-stage-model.md`](../cycle-time-stage-model.md) as the worked
  consumer of that ledger. The proposal itself is unchanged and still
  undecided.
- **2026-08-17 (AGT-2671):** Page created by the Dossier curation sweep. The
  content is the durable extract of `AGT-W38`, which stays `decision-pending`
  in the Dossiers list. Nothing here is delivered code.
