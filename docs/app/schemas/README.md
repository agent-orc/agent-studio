# JSON Schemas

Canonical home for cross-cutting structured-data formats. Each file is one Draft 2020-12 JSON Schema describing one concept that flows between layers (disk, backend, frontend, supervisor, companion app).

## Why this folder exists

Before this folder, structured records lived only as C# types and TypeScript interfaces. That meant:

- The disk format and the in-memory format could drift silently.
- Layer 3 review skills had no contract to read against.
- The companion app had to reverse-engineer types from the SignalR payloads.

Schemas in this folder are the single contract. C# records, TypeScript interfaces, and the in-memory store all derive from these schemas.

## Contents

- `supervisor-advisory.schema.json` - one entry in `logs/meta/<project>/observations.jsonl`.
- `supervisor-intervention.schema.json` - one entry in `logs/meta/<project>/interventions.jsonl`.
- `token-aggregate.schema.json` - per-project rolling totals of tokens, dollars, time-window.
- `agent-message.schema.json` - one record on the Agent Message Bus, append-only in `logs/bus/<project>/<date>.jsonl`. Contract in [`docs/system/architecture/bus/agent-message-bus.md`](../architecture/bus/agent-message-bus.md).
- `agent-participant.schema.json` - one actor on the bus (user, orchestrator, supervisor, coding agent, supporting agent, system-review, runtime, external).
- `agent-artifact-ref.schema.json` - typed pointer from a bus message to evidence on disk or another structured stream.
- `client-identity.schema.json` - one client of the Task Access Layer (human, agent instance, external tool, service). Stored under `<workspace>/identities/<id>.json`.
- `token-aggregate-by-client.schema.json` - per-client variant of `token-aggregate.schema.json`, additionally keyed by `clientId` for per-client legends in the workspace timeline.
- `token-timeline-bucket.schema.json` - one (project, time-bucket) cell served by `GET /api/workspace/tokens/timeline`. Powers the workspace token timeline view at `#/workspace/tokens`.
- `drift-report.schema.json` - one project-level drift analysis report with scores, dimensions, evidence references, optional architecture-element projection, and follow-up task suggestions. Stored at `<workspace>/logs/drift/<project>/<reportId>.{md,json}`. Prose contract in [`docs/system/reports/drift-reports.md`](../reports/drift-reports.md).
- `architecture-model.schema.json` - authoring contract for a project's compact high-level architecture map (max ten elements). Validates the YAML frontmatter of an architecture-model Markdown file. Drift reports reference one model by `modelId`. Prose contract in [`docs/system/architecture/model.md`](../architecture/model.md).
- `task-find-result.schema.json` - one job record plus the optimistic-concurrency token returned by the Task Access Layer's find / list / snapshot calls. ADR-0024.
- `task-mutation-request.schema.json` - one narrowly-typed mutation request consumed by the Task Access Layer (field update, prompt attach, log-line append, create). Lane transitions have their own typed entry point inside the layer. ADR-0024.
- `task-execution-location.schema.json` - canonical `TaskInfo.executionLocation` API projection for actual local/remote ownership, health, routing context, provenance, and quiet historical attribution.
- `analysis-report.schema.json` - one inspection report. Generic shape used for manual, scheduled, meta-cycle, supporting-agent, and external-monitor analyses (queue health, docs drift, roadmap alignment, stale jobs, security posture, architecture drift, QA status, token-spend review). The Markdown sibling is the durable human artifact; the JSON is the additive app contract. Storage and producer rules are in [`docs/system/reports/analysis-reports.md`](../reports/analysis-reports.md).
- `wiki-document-companion.schema.json` - adjacent sidecar metadata for one Wiki document. Stored as `<source-file>.meta.json` next to the Markdown, HTML, or JSON source document; the generated `<source-file>.report.html` explains the findings and drift classification.
- `wiki-page-lifecycle.schema.json` - shared lifecycle contract for designs, concepts, explorations, and Dossiers. Markdown uses leading frontmatter; HTML Dossiers use the same lifecycle fields in `workbench.json`. The optional presentation `pattern` is tolerant: current readers support `ui` and `concept`, with missing or unknown values falling back to `concept`. Companion sidecars never duplicate this state.
- `pipeline-definition.schema.json` - one versioned task-processing pipeline definition for a project (ordered pre/post steps, common envelope, llm/script step types). Source of truth at `<TaskRepository>/.metadata/pipelines/<projectId>/<version>.json`; mirrored into the derived `pipeline-history.db`. ADR-0051.
- `step-run.schema.json` - one (task, step, attempt) telemetry row. Source of truth is the per-job append-only `logs/step-runs.jsonl`; the derived `pipeline-history.db` `step_runs` table is a rebuildable cross-task projection over it (the `ProjectChatIndex` precedent). ADR-0051.
- `quality-analysis-policy.schema.json` - repository-owned `.quality/agent-studio.json` overrides for named Quality Studio analysis-step activation. The runtime does not read central project settings or environment variables for this policy.
- `remote-run-result.schema.json` - one immutable infrastructure-verification result per remote scenario run. It combines authoritative Task Server state with fenced Runner evidence, separates queue and execution timing, and records unavailable token telemetry explicitly. It is not a model-comparison format.
- `model-qualification-event.schema.json` - one decision or actual CORE outcome row in a task's append-only `model-qualification.jsonl` benchmark stream.
- `project-execution.schema.json` - repository-owned `.agent-studio/project.yml` preparation and verification composition read at the subject commit.
- `preparation-manifest.schema.json` - one preparation outcome with observed tool versions, lockfile hashes, and content-addressed cache states.
- `pickup-failure.schema.json` - one row in `<workspace>/logs/pickup-failures.jsonl`. Appended when the per-project pickup loop dead-letters a `3-progress` folder after the configured retry budget without producing a CLI output line. Pairs with `orphan-recovery.schema.json` (boot sweep). ADR-0028.
- `product-runtime-event.schema.json` - one structured event emitted by the software the agents are building. Append-only JSONL under `<job>/logs/runtime/<date>.jsonl` for job-scoped runs and `<project>/logs/runtime/<date>.jsonl` for project-scoped runs. Separate stream from the Agent Message Bus; joined by reference, never mixed. Prose contract in [`docs/operations/runtime/observability.md`](../../operations/runtime/observability.md).
- `update-run-snapshot.schema.json` - pre/post snapshot captured at the start (kind=pre) and end (kind=post) of every Update Service pipeline run. Written to `<RunsDirectory>/<runId>/{pre,post}-snapshot.json`. ADR-0031.
- `update-run-verification.schema.json` - one row per phase-6 verification check (healthz-stable, runner-status, jobs-grouped, clients, cli-quota, db-touch). The forward pass appends to `<RunsDirectory>/<runId>/verification.jsonl`; the rollback re-run (ADR-0031 reissue-2026-05-11) writes the same row shape to a sibling `<RunsDirectory>/<runId>/rollback-verification.jsonl` so the two passes never mix on disk. ADR-0031.
- `build-manifest.schema.json` - immutable Stable runtime identity for Agent Studio plus exact CodingAgentRunner and Coding Agent Chat release provenance and integrity.
- `protocol-header.schema.json` - structured header carried at the top of a job's protocol document (`status.md`). Sits under the free-form prose summary inside an HTML comment (`<!-- header-json: {...} -->`) or a fenced `json header` block. The parser is tolerant: every field is optional below `phase` + `summary`, and the UI falls back to the document prose when the block is absent or malformed. Drives the in-app protocol header card. Prose contract in [`docs/system/contracts/protocol-style.md`](../contracts/protocol-style.md) §4.1.6.
- `executive-summary.schema.json` - workspace-level roll-up returned by `GET /api/workspace/summary?windowHours=N` (default 24; accepts 1 / 6 / 24 / 168). Folds per-project job moves, decisions, advisories, commits, crash evidence, and open human decisions into one short read. Never invents events: every row references an on-disk record. The decisions block is folded from `logs/decisions/<project>.jsonl` (`orchestrator-decision.schema.json`). Drives the `#/workspace/summary` overlay. Prose contract in [`docs/system/contracts/protocol-style.md`](../contracts/protocol-style.md) §4.1.6.

More schemas land here as concepts get formalised (audit findings, performance probes, companion snapshots). Keep one concept per file. Filename is `<concept-kebab>.schema.json`.

## Conventions

- Draft 2020-12. Set `$schema` accordingly.
- `$id` is `https://agent-taskboard.local/schemas/<concept>.schema.json`. The host is fictional; the path is what consumers compare against.
- All field names are camelCase to match the Web JSON serialisation policy in the backend (`JsonSerializerDefaults.Web`) and the TypeScript model files.
- All timestamps are ISO 8601 UTC with `Z` suffix. Type `string`, `format: date-time`.
- Enums spell the values in PascalCase to match the C# records (e.g. `Severity = High | Warn | Info`).
- Every schema has a top-level `description` and per-field `description`. The schema is doc, not just validation.

## Validation

The backend reads these documents through the file-backed in-memory store at [`backend/Services/State/InMemoryStore.cs`](../../backend/Services/State/InMemoryStore.cs). It validates every append (strict, rejects bad records), is lenient on read (skips a single bad legacy line so the projection never breaks), and exposes typed access by id, filtered queries, append-cursor reads, and optimistic concurrency. The full design rationale, including what intentionally stays out of scope (no database engine, no aggregate documents, no codegen step), lives in [ADR-0023](../architecture/decisions/adr-archive.md#adr-0023---json-schema-first-communication-formats-and-a-file-backed-in-memory-data-layer-2026-05-05).

Round-trips between the schemas and the C# records are locked by [`backend.Tests/SchemaRoundTripTests.cs`](../../../backend.Tests/SchemaRoundTripTests.cs); the store contract itself is locked by [`backend.Tests/InMemoryStoreTests.cs`](../../../backend.Tests/InMemoryStoreTests.cs).
