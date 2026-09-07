# Roadmap

This document describes **what is coming next** — direction, planned themes, and the boundaries we will not cross while pursuing them.

For **what the product is and what it can do today**, see [README.md](README.md) — the pitch, the principles, and the ten built-out surfaces with their current shape. Documentation drift between the two files is real risk; if you are tempted to add a "current shape" bullet here, update the README instead.

## Roadmap Themes

### Task Access Layer

The in-process filesystem layer below is the compatibility path for the legacy
Studio backend. The independently deployed Task Server now owns the durable
control-plane store described by
[Distributed Agent Studio target architecture](docs/concepts/distributed-agent-studio-target-architecture.md).
After a single-writer cutover, Studio and Runner consume that service and the
legacy filesystem layer is no longer a second task truth.

Today every backend service that touches tasks reads or writes the filesystem directly. Performance regressions surface when the kanban poll rescans disk per request; concurrent writers race; and any future multi-instance or multi-user story is gated on having one place that owns task state.

A separate **Task Access Layer** (`backend/Services/TaskAccess/`) becomes the single point of access for every task operation. It boots once, loads every project's lane folders into a typed in-memory index, watches the filesystem for external changes, and exposes a typed API for find / list / mutate / transition / subscribe. No service, hosted service, or test is allowed to touch task folders directly; everything goes through `ITaskAccess`.

What the layer enables:

- **Performance**: single index, cheap reads, no per-request disk rescans.
- **Cleaner organisation**: `JobScannerService` and `JobMutationService` become thin facades or disappear; the runner is a state machine that calls the layer.
- **Multi-instance and multi-user later**: with one API, multiple clients or multiple task-processor instances can speak the same protocol. The first cut keeps everything in-process; the typed surface keeps the door open for an HTTP-relay split.

Hard rules:

- File on disk stays the source of truth on cold start. The in-memory index is a view.
- No SQL, no LiteDB, no EF inside the legacy Studio compatibility layer. This
  restriction does not apply to the independently deployed Task Server, which
  owns its own schema migrations, backup, restore, leases, and fences.
- Single-state-machine authority moves into this layer. The runner still owns "one running task per project"; the layer enforces it on every mutation.

Phasing is detailed in the queued task `task-access-api-layer-extraction`: ADR + skeleton, in-memory store, mutations and subscribers, consumer migration, default-on with multi-instance preparation.

### Assisted Coding Harness Around CLI Runs

Provider pricing and usage boundaries can change quickly. The June 15, 2026 Claude Code Agent SDK credit change makes the product boundary more important, not less: agent-orchestrator should remain a CLI-conformant assisted-coding harness around task runs, while provider CLIs remain the execution engines.

The product model is a pre/core/post pipeline around one task:

- **Pre-step**: cut the work into a bounded task, gather context, choose the CLI/model, set acceptance criteria, prepare worktree/branch isolation when the project needs it, and state the review bar before a model starts editing.
- **Core step**: start the configured coding-agent CLI in the user's controlled environment. Claude Code, Codex, Copilot, Gemini, or another supported CLI owns the provider session, model routing, tool loop, approvals, and coding behavior.
- **Post-step**: collect the run output, stdout/stderr, structured markers, diffs, screenshots, test/check results, token or usage data where the CLI exposes it, review findings, and the human accept/reject/split/reissue decision.

What this enables:

- **Provider portability**: a task can move between Claude Code, Codex, Copilot, Gemini, or a future CLI without changing the task lifecycle.
- **Pricing resilience**: provider usage changes affect the core execution step; the task, evidence, review and routing layer stays useful.
- **Forum-ready positioning**: when developers discuss Agent SDK, `claude -p`, subscriptions or CLI harnesses, the product can say exactly where it sits: assisted coding around the provider CLI, not a hidden replacement agent runtime.
- **Usage accountability**: token and usage signals become post-run evidence where available, not a loose terminal memory.

Hard rules:

- No hidden Agent SDK dependency for the orchestration layer when the configured contract is a provider CLI.
- No promise that the product bypasses provider pricing, plan limits, terms, or enforcement.
- No raw model API coding runtime in the default product path.
- The app may prepare and inspect a run, but the provider CLI owns the code-writing loop.
- Usage capture must be explicit about source and confidence: CLI-reported, parsed from logs, inferred, or unavailable.

First implementation order:

1. Keep the README and website language aligned on "assisted-coding harness around provider CLIs".
2. Add a compact pre/core/post timeline to the task detail or run timeline surface.
3. Normalize per-run usage records so post-run evidence can say which provider, CLI mode, model, source and confidence produced the numbers.
4. Add a forum/FAQ-ready doc under `docs/` that explains Agent SDK, `claude -p`, interactive CLI and managed taskboard runs without making policy claims.
5. Add regression tests around CLI invocation paths so headless SDK-like execution cannot silently replace the configured CLI contract.

### Security First

Make security a first-class project dimension, not a one-off task:

- Project-level Security view that shows the latest security review, review date, outcome, evidence, and open risks.
- Markdown-backed security history so reviews are durable, inspectable, and easy for direct CLI agents to read.
- Security audit records that capture model, CLI, token budget or token usage where available, prompt or skill version, process checklist, evidence links, reviewer decision, and follow-up tasks.
- A token-spend timeline for security work so teams can see whether a review was a quick smoke check or a deep multi-million-token investigation.
- Standard security-review skill that can be selected for a task or project review.
- Project-specific security skills for domain assumptions, threat model, sensitive data, authentication, deployment, and known risks.
- A "security readiness" project action that can create a normal task to run or refresh a security review.
- Roadmap linkage from the existing "Projekt Dimensionen Security und Architektur" task into the project view work.
- A cautious modernization story for small internal libraries: regenerate or rewrite when the scope is bounded and review evidence is strong; avoid this default in highly sensitive areas such as PKI, TLS, cryptography, certificate handling, and authentication boundaries.

Security quality depends on model capability, sufficient token budget, the right process, and durable documentation. The app should optimize that loop instead of treating security as a vague label. A security claim without the model, spend, process, evidence, and reviewer decision is not enough.

### Quality System

Turn the quality-system mockup into product behavior carefully, without letting it become a workflow engine:

- Keep the conceptual split from the mockup: Project Audits are project-scope read-only reviews, Task Checks are task-diff read-only reviews, Performance Probes are runtime measurements, and Skills are reusable workflows.
- Do not use "Quality" as a vague top-level bucket until the vocabulary proves itself in the real app. Prefer concrete surfaces: Security on the project page, Task Checks on task review, Probes under project or settings diagnostics, Skills as the reusable workflow catalog.
- Build the first slice around evidence, not enforcement. Task Checks should produce findings and review chips, but should not block the `3-progress -> 4-review` transition in the first version.
- Default high-severity and security-sensitive Task Checks to separate spawned CLI runs. Allow injected checks only for cheap, low-risk self-checks where structured output is not critical.
- Store audit and check definitions as versioned Markdown with frontmatter, but keep runtime outputs in watched project/task folders. Definitions belong to the app library; findings and reports belong next to the project evidence they describe.
- Treat findings as review artifacts that can become normal queued tasks. A "create follow-up task" action is safer than hidden automatic fixing.
- Keep Performance Probes separate from Skills. Probes execute code and measure the running app; Skills are prompt/workflow definitions.
- Promote Security visually ahead of generic Quality. A project with no security baseline should feel unfinished, not merely unconfigured.
- Delay repository-style skill discovery until local installed skills, licenses, and project lookup are boring and reliable. No hidden internet install, no hidden auto-update, no skill execution without an explicit local record.

First implementation order:

1. Security baseline panel and review history on the project page.
2. Task Check definition model plus per-project defaults.
3. Spawned Task Check run after a main task completes, writing structured findings into the job folder.
4. Finding chips in task review and a follow-up-task action.
5. Skills catalog for installed local skills and built-in audit/check definitions.
6. Performance Probe slots after the audit/check loop is stable.

### Test Run Service

Triggering a full test run and reading back its quality verdict belongs behind its own service with a stable contract. The app should not know whether the actual runner is the local Playwright suite, a self-hosted TeamCity agent, GitHub Actions, a remote CI pool somewhere in Hesse, or whatever lands later. It should know one API:

- Request a run for a given commit (or working-tree snapshot) and a named test profile.
- Get back a run id immediately, with the run executing asynchronously somewhere else.
- Receive status + structured quality metrics through a callback API as the run progresses and finishes.
- Read durable run history, with attached evidence (logs, screenshots, traces, junit-style result trees) tied back to the originating job and commit.

What this enables:

- The board can mark a task as "tests requested → pending → green / red" without owning the executor. A long remote run (15-90 min) does not block the main process.
- Multiple executors can serve the same contract — local Playwright for fast feedback, remote TeamCity for full coverage, an ad-hoc cloud pool for large fan-out — and the app stays the same.
- Quality metrics (suite count, duration, flake rate, coverage delta, perf budget verdicts) become first-class data the supervisor and analysis surfaces can consume the same way they consume security findings or audit reports.
- A failed remote run produces evidence in a known shape; the follow-up-task action can pre-fill from it just like security/uxui follow-ups do today.

Hard rules:

- The contract is the only integration surface. No backend logic in the consumer; no consumer logic in the executor. New executors are HTTP plugins that implement the contract, nothing else.
- Run results are append-only and tied to the commit (not the branch). Re-runs produce new ids; a commit can have N completed runs from N profiles or N executors.
- The callback channel is asynchronous and lossy by design — clients reconcile from the run-id endpoint, not from "did the webhook arrive." Webhooks are an optimisation, not a source of truth.
- Run evidence (logs, screenshots, traces) is fetched lazily via the run id; the contract carries pointers, not blobs.
- No coupling to a specific CI vendor's data model. The contract uses neutral shapes; vendor-specific fields go into a typed `executor` envelope inside the run record.

First implementation order:

1. Define `docs/test-run-service-contract.md` and `docs/system/schemas/test-run.schema.json` (request + run-record + quality-metrics shape).
2. In-process executor that wraps the existing local Playwright suite, so the app can talk to its own contract end-to-end before any remote integration lands.
3. Run-history surface on the project page: list of runs per commit, status chips, drill-down to evidence.
4. Webhook + GET-run-by-id callback API so a remote executor can push status updates.
5. TeamCity plugin: shells out to a TeamCity REST API, maps the project's build configurations onto the contract's profile concept, posts back via the callback API.
6. Quality-metrics consumption — supervisor + meta-cycle treat run history as a first-class signal alongside security findings and audit reports.

This is sibling to Performance Probes (which measure the running app) and to Product Runtime Observability (which records what the built software did). Test Runs answer a different question: "did the built software pass its declared test suite at this commit?" — the gate, not the live behaviour.

### Visual Regression Evidence

Visual regression should become part of the normal feature loop, not a manual screenshot hunt after a UI change. When a task changes a feature, the relevant Playwright specs should be able to run against a named application state, capture the important screenshots, compare them with approved baselines where they exist, and publish a compact evidence record back to the originating task.

The product should make it easy to answer:

- Which Playwright specs ran for this task, commit, viewport, theme, and feature flag set?
- Which app state was used: seed data, selected project, selected job, route, open panels, theme, viewport, and browser?
- Which screenshots, traces, videos, console logs, and diff images were produced?
- Was this a strict regression run, an exploratory before/after run, or a design-iteration run where a new baseline may be accepted?
- Which visual differences are expected product changes, and which look like accidental regressions?

Core concepts:

- **Visual state profile**: a named setup recipe for route, seed data, project/job selection, feature flags, theme, viewport, and open UI panels. The profile is the stable input, not whatever happened to be open in a developer browser.
- **Visual evidence record**: spec id, profile id, commit, run id, viewport, theme, screenshot paths, diff paths, trace/video paths, threshold verdict, and human review verdict.
- **Baseline lifecycle**: approved baseline, candidate baseline, rejected candidate, and expected-diff note. A UI change can update the baseline, but that update is explicit and reviewable.
- **Task attachment**: visual evidence appears on the task detail and project Test Quality surface, linked to the run and commit that produced it.

Hard rules:

- Visual regression must not become a hidden flaky pixel gate. The app records exact evidence first; strict blocking can be enabled per profile only after the profile is stable.
- Baselines are versioned by visual state profile, viewport, theme, and relevant feature flags. A dark-theme baseline cannot validate a light-theme run.
- Evidence is stored as files with pointers in the run record. Do not store screenshot blobs in app state.
- A design change and a regression are different outcomes. The UI must support "accept new baseline" without pretending the old baseline passed.
- Playwright traces remain available for debugging; the default task view shows a simple "what ran and what changed" summary.

First implementation order:

1. Define `docs/reports/visual-regression-evidence.md` and `docs/system/schemas/visual-evidence-run.schema.json` for state profiles, screenshot records, diff records, and baseline verdicts.
2. Extend the Playwright reporter so selected specs can publish visual evidence into a task or Test Run Service record.
3. Add a small baseline store under project evidence, keyed by profile, viewport, theme, and feature flags.
4. Add a task-detail Visual Evidence panel that shows run status, screenshot thumbnails, diff thumbnails, trace links, and baseline status.
5. Add explicit baseline actions: accept candidate, reject candidate, annotate expected diff, and create follow-up task.
6. Feed visual verdicts into the Test Run Service quality metrics so CI, local Playwright, and project review all speak one evidence language.

This builds on Test Run Service, Creativity And Design, and Product Runtime Observability. Test Run Service owns the run lifecycle; Visual Regression Evidence owns the screenshots, comparisons, and review semantics.

### CI/CD Status Integrations

External CI/CD status should be visible on tasks and projects. The first concrete target is TeamCity, because it is already part of the user's working environment. The product should still model this as a neutral CI/CD adapter layer so TeamCity does not leak into core task logic.

The task detail should answer:

- Has the relevant commit been built by the configured CI server?
- Which build configuration or pipeline profile ran?
- Is the build queued, running, green, failed, canceled, or unknown?
- Which tests failed, which artifacts were produced, and which logs or screenshots are worth opening?
- Did the failure come from the task's commit, a later branch state, infrastructure, missing credentials, or an unrelated queued build?

TeamCity V1 should support:

- Project-level mapping from watched project to TeamCity project id and build configurations.
- Commit-bound status lookup through TeamCity's REST API.
- Build queue and running-build status.
- Test result summary, failed-test list, artifact links, and build log links.
- Optional explicit "trigger CI run" action for selected profiles, never automatic hidden triggering.
- Webhook receiver later, with polling reconciliation as the source of truth.

Hard rules:

- CI status is commit-bound. Branch-only status is too ambiguous for task review.
- TeamCity is an adapter behind the Test Run Service contract. Task UI consumes the neutral run record, not TeamCity's raw model.
- Secrets and server URLs live in local configuration or project settings, not committed docs or job artifacts.
- Read-only status lands first. Triggering remote builds is explicit, audited, and tied to a human or orchestrator action.
- CI artifacts stay remote unless fetched deliberately. The app stores pointers and selected summaries, not whole build archives by default.

First implementation order:

1. Extend the Test Run Service contract with an `executor` envelope for CI/CD providers and a commit-bound lookup API.
2. Add project settings for CI provider, TeamCity server, project id, build configuration mapping, and default profiles.
3. Implement a TeamCity adapter that maps build queue, running build, finished build, failed tests, and artifacts into the neutral run record.
4. Show CI/CD status chips on task detail, project Test Quality, and project overview.
5. Add drill-down from a failed CI run to failed tests, artifacts, logs, screenshots, and follow-up-task creation.
6. Add optional explicit trigger actions once read-only status is stable.

This is sibling to Visual Regression Evidence. Visual evidence can be produced locally or by TeamCity; the task sees one quality record either way.

### Drift Control

Make Drift a first-class project dimension beside Architecture. The most important drift is not document-to-document drift; it is when the actual software no longer follows the documented intent, architecture, guidelines, tests, runtime expectations, or product promises.

The Drift surface should answer:

- Does the software still do what the specs, README, roadmap, task prompts, and acceptance criteria say?
- Does the source tree still match the ADRs, architecture notes, module boundaries, and high-level system architecture?
- Do the tests and QA history still cover the areas the docs call risky?
- Does runtime behavior match the expected domain behavior and performance signals?
- Do marketing and website claims still match what the product actually does?

Architecture Drift needs a special model. A project can define a compact high-level architecture map, represented in UI as a marble-style diagram or architecture board. The model should have a hard readability limit of at most ten elements per map. Each element records its role, ownership boundary, allowed dependencies, important guidelines, evidence sources, and current drift score.

Example elements:

- Frontend app shell.
- Backend API.
- Task Access layer.
- Runner / CLI execution layer.
- Agent Message Bus.
- Project organization store.
- Analysis Reports.
- Drift Control.
- Runtime Observability.
- Schema-backed in-memory layer.

Each architecture element can then accumulate drift:

- Expected role vs current code responsibility.
- Allowed dependencies vs actual dependencies.
- Documented data contracts vs current JSON schemas and DTOs.
- Expected runtime behavior vs logs and tests.
- Ownership boundary vs files touched by recent jobs.
- Evidence freshness: last analysis, last test, last architecture review.

The Drift report JSON should support both dimension-level scores and architecture-element-level scores. The UI should show the marble diagram as a scan surface: green/yellow/red elements, score trend, latest finding, source coverage, and a drill-down to evidence. Clicking an element opens its current contract, files, docs, reports, and follow-up tasks.

First implementation order:

1. Extend `drift-report.schema.json` with an optional architecture model: max ten elements, per-element expected role, source refs, score, severity, source coverage, evidence refs, and follow-up suggestions.
2. Define a Markdown architecture-model authoring contract so projects can write the high-level system map without a drawing tool. Contract lives in [`docs/system/architecture/model.md`](./docs/system/architecture/model.md), validated by [`docs/system/schemas/architecture-model.schema.json`](docs/system/schemas/architecture-model.schema.json).
3. Add the Drift surface marble view with per-element scores and drill-down.
4. Implement **Software / Architecture Drift** analysis: compare architecture model and ADRs against source tree, schemas, runtime events, tests, and recent job evidence.
5. Let each architecture element create normal follow-up tasks for code cleanup, ADR updates, missing tests, missing runtime signals, or documentation sync.

Queued at `agent-taskboard/2-ready/architecture-model-drift-contract/`, `agent-taskboard/2-ready/architecture-marble-drift-surface/`, and `agent-taskboard/2-ready/software-architecture-drift-analysis-action/`.

### Creativity And Design

Make "beautiful software" a first-class product outcome, not a lucky side effect of implementation:

- Treat software as product. Features should be useful, but also visually coherent, pleasant to operate, and able to carry a brand or product idea.
- Add creative design loops that can generate, compare, and iterate UI variants before or during implementation. A loop can ask for "the next version" instead of treating the first usable screen as done.
- Maintain design references: screenshots, markdown briefs, images, accepted examples, rejected alternatives, and product/brand notes that can guide later design loops.
- Use screenshots as the core design evidence. Every visual iteration should capture the current state, the proposed variant, and the critique that led to the next step.
- Add council-style critique as a structured review mode: product, visual design, interaction design, frontend engineering, accessibility, and marketing/positioning can each provide a focused opinion before the orchestrator chooses the next move.
- Keep design councils advisory. They may create findings, design briefs, and follow-up tasks, but they must not create parallel coding work inside one project.
- Model design Skills separately from read-only checks: Visual Direction, UI Polish Pass, Screenshot Critique, Copy Tone Pass, Brand Fit Review, Accessibility Design Review, and Product Story Review are reusable workflows that help produce better design decisions.
- Add explicit Testing and QA actions for backend tests, end-to-end tests, tuning tests, coverage, and run history. The user triggers the action; the app stores structured evidence.
- Add source-code perspectives that an LLM or script-backed Skill can generate: lines of code, modules, dependencies, ownership areas, coverage, hotspots, and organization risks.
- Treat Skill output as a report contract: Markdown for humans plus structured JSON for the app. If parsing fails, show the raw report with an unstructured-output warning.
- Put UX/UI, Test Quality, and Token Usage directly on the project page as first-class menu entries. UX/UI collects design references and iteration evidence; Test Quality collects test runs, coverage, tuning results, source maps, and code metrics; Token Usage shows inference spend across jobs and supporting loops.
- Track token spend as a major project signal. Split usage into Job Tokens, Supporting Jobs Tokens, and Orchestrator Tokens, then aggregate by project, job, run, and time window.
- Add token-usage heatmaps for large boards. Each job can be a square; intensity shows token spend, with drill-down into the exact job, supporting runs, orchestrator turns, and timeline position.
- Let the orchestrator steer a design loop explicitly: accept a version, request another version, ask for a harsher critique, or turn council feedback into normal queued implementation tasks.
- Preserve design history in task evidence: screenshot sets, variant notes, council verdicts, chosen direction, rejected alternatives, and follow-up tasks.

First implementation order:

1. UX/UI, Test Quality, and Token Usage project menu surfaces beside Security and Architecture.
2. Design Loop concept and task-evidence shape for screenshot variants, references, QA runs, source maps, and council notes.
3. Design reference library for screenshots, markdown briefs, images, accepted examples, and rejected alternatives.
4. Screenshot comparison panel on the task detail surface.
5. Local design Skill definitions for screenshot critique, UI polish, copy tone, and accessibility design review.
6. Testing and QA run history for backend tests, end-to-end tests, tuning tests, coverage, and code metrics.
7. Source-code map action that visualizes modules, lines of code, ownership areas, and organization concerns.
8. Token Usage surface with totals, category split, heatmap, timeline, expensive-job list, and drill-down.
9. Council review prompt that produces role-separated critique and a final orchestrator recommendation.
10. "Next version" action that creates a follow-up design iteration task from the current screenshots and council notes.
11. Project-level design memory: chosen visual direction, brand notes, UI principles, and examples of accepted screens.

The design-loop, QA, source-metric, and token-usage concepts live in the single integrated mockup under [docs/mockups/quality-system/](docs/mockups/quality-system/). Do not maintain a second sibling mockup for this product direction.

### Project Control

Make each watched project easier to inspect and operate:

- Project detail pages with path, configuration, status, and quick actions.
- Project dimensions for Security and Architecture, with current status plus historical Markdown records.
- Clearer manual start vs. auto-pickup behavior.
- Safer locking once a task has started, so completed or running work does not drift to another project by accident.
- Better visibility into active CLI sessions that may already be working in the same project.
- Repository hygiene for accepted tasks: surface dirty and unpushed work, support prompt commits of accepted task changes and task evidence, and prevent completed work from quietly piling up on disk.

### Next-Generation Embedded Chat Surfaces

Make chat the primary human review surface for long-running, multi-actor agent work, but keep it inside the existing application. There are two homes: the task detail Chat tab for task-scoped evidence and follow-up, and the resizable side sheet for project-scoped steering and cross-task context. There is no new global chat window.

The current Activity Log is valuable evidence, but real Stable jobs already produce hundreds of conversation entries and more than a hundred tool chips. The next surface should keep the raw trace available while making the default view a compact developer chat, closer to GitHub Copilot Chat in VS Code than to an operations dashboard. The richer dashboard-style view from the first mockup remains valuable as a read-only fullscreen developer debugging view.

Light theme is a first-class default because it is the user's primary working mode. Dark theme remains supported, but both themes share the same spacing, actor grammar, compact rows, warning treatment, and debug affordances. This work should also align with the VS Code-style layout direction: activity bar, compact tabs, resizable panels, status bar, low padding, and high information density. The production visual layer should become the internal Found Next Workbench design system rather than a generic dashboard kit.

The chat should render:

- The user, task agent, project orchestrator, supervisor, supporting agents, tool runner, and system warnings as distinct actors.
- Runs as thin separators in the conversation, with detailed CLI, model, duration, token usage, tool count, tests, commits, and outcome in the collapsible inspector.
- Tool use as compact inline bursts by default, with counts by family, failure count, duration, touched files, artifacts, and one-click raw detail.
- Orchestrator and supervisor output as terse inline decision/advisory rows first. Expanded cards show reason, evidence, action, budget, and next step.
- User intervention as explicit steering: continue, interrupt, stop, accept, or create follow-up.
- Task starts and continuations as subtle markers in a continuous project chat, with task metadata available on hover or click.
- Bottom composer controls for current chat, model, agent mode, permission level, start, stop, configuration, jobs, debug view, context chips, and slash actions.
- Conversation mode as the default, trace mode as the raw debugging view, artifacts as a dedicated evidence view, the meta layer as a docked or collapsed inspector, and Verbose Debug as a fullscreen read-only history analysis.

First implementation order (plan: [docs/research/embedded-chat-integration-2026-05.md](docs/research/embedded-chat-integration-2026-05.md)):

1. Add the `Frontend:NextGenChat` integration bridge. It inventories and preserves the current Activity Log parser, Trace mode, run timeline, auto-eval banner, task composer, reusable `app-chat`, project side sheet, Status Bar quota, CLI Usage sheet, Workspace Token Timeline, and project token summaries. **Done**: flag, `ConversationEvent` data contract, pure `projectConversation()` projection, fixtures and unit tests, `app-tool-burst-chip` presentational component, and `app-verbose-debug-overlay` are in. Hosts still render the legacy Activity Log when the flag is on; the renderer wiring is the next slice.
2. Add a `ConversationEvent` projection above the existing Activity Log parser. **Done**: `frontend/src/app/components/chat/conversation-projection.ts` with watchdog/capture-fail/schema-drift classification, run-aware tool-burst collapsing, and workbench summary/debug aggregates.
3. Render the projection in the existing Protocol pane Activity tab behind the flag while preserving raw trace access and current task controls. **Done** (`chat-conversation-event-projection` host adapter, 2026-05-14): `app-conversation-view` renders `ConversationEvent[]` when `Frontend:NextGenChat` is on; off-state unchanged; Trace fallback button swaps the body back to the legacy `app-activity-log-view` for the same lines; `frontend/e2e/next-gen-chat-task-host.spec.ts` locks both states.
4. Group tool calls into compact inline `ToolBurst` events in conversation mode while preserving raw trace mode.
5. Add persistent actor rails and labels for all participant types.
6. Add compact decision/advisory rows for orchestrator and supervisor events, with expandable `DecisionCard` details.
7. Adapt the existing project side sheet to the shared message grammar without removing project picker, task tab, roadmap intake, attachments, or make-task behavior.
8. Add bottom composer controls and interactive configuration overlays.
9. Add continuous-chat task markers with hover/click metadata.
10. Add a fullscreen Verbose Debug view for actor counts, duration, task markers, tool density, tokens, warnings, artifacts, and orchestrator explanations.
11. Fix layout reservations so auto-eval banners, run timeline, mode controls, stream, and composer cannot overlap.
12. Add Playwright coverage using Stable evidence cases: tool-heavy archive, review job with orchestrator output, analysis report job, empty run, failed tool retry, user intervention, light theme, dark theme, side sheet wide mode, and both layout flags.

Queued at `agent-taskboard/2-ready/chat-layout-integration-bridge/`, `agent-taskboard/2-ready/chat-conversation-event-projection/`, `agent-taskboard/2-ready/chat-tool-burst-collapsing/`, `agent-taskboard/2-ready/chat-actor-decision-cards/`, `agent-taskboard/2-ready/chat-window-playwright-regression-suite/`, and `agent-taskboard/2-ready/chat-verbose-debug-view/`.

### Expanded Lifecycle Lanes

Separate human intent from orchestrator and AI processing without breaking the runner's slot-admission model. Today a task in `2-ready` means both "the human says this can run" and "the runner may pick it up". That is too coarse once the app starts doing intake checks, duplicate detection, prompt shaping, post-processing, security feedback, QA checks, and orchestrator review.

The board should make these phases visible:

- Human Ready: the user dragged or created a task as ready from their point of view.
- Orchestrator Intake: an AI or orchestrator lane checks whether the task already exists, is understandable, has enough context, has obvious questions, and can be executed safely.
- Task Execution: the main coding CLI works on the core task.
- Orchestrator Post Processing: a different orchestrator, supporting, or review CLI checks the result, runs requested post-processing, creates findings, asks follow-up questions, or triggers explicit QA, security, design, or runtime-observability checks.
- Human Review: the user reviews the final task evidence and accepts, continues, or sends it back.

Column labels on the board are the shorter forms `Human Ready`, `Intake`, `Execution`, `Post Processing`, and `Human Review`; the longer phrasing above appears in the in-product help and in the concept doc.

V1 implements this as virtual lanes derived from a hybrid model: the six existing folder states stay the durable skeleton, a new optional `phase` field on `JobInfo` (backed by a sidecar `lifecycle.json` in the job folder when richer data is needed) carries the orchestrator-driven substate, and the kanban projection computes the visible lane from `(state, phase, execution.status, summaryState)`. No new filesystem states. Existing jobs with no `phase` render in the default lane of their state. The Agent Message Bus, once it lands, can subsume the sidecar by emitting `lifecycle` kind messages. The full plan lives in [docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md](docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md).

The UI should support lane grouping and collapse. Users should be able to collapse orchestration-only lanes into a slim left-side rail or compact group, while keeping the main human decision lanes visible. The board should preserve scanability when there are many lanes: group headers, counters, active-run badges, CLI badges, post-processing indicators, and a quick way to expand only the lanes that currently need attention.

Hard boundary: expanded lanes do not bypass ADR-0052. Intake and post-processing are visible phases in the same project pipeline. If a post-processing check uses another CLI, it must be visible as a distinct supporting or orchestrator run and must not edit the same code concurrently with the main task execution run unless it has been admitted through the same slot policy and isolation model.

First implementation order:

1. Write the lifecycle-lane concept and state model, including virtual lane vs filesystem state tradeoffs. Done; see [docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md](docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md).
2. Land the wire-level `phase` field plus sidecar `lifecycle.json` and a backend test that confirms an existing job renders in the right default lane (no UI lanes yet). This is the migration-and-compatibility step; do it before any UI lane changes so step 3 has data to render.
3. Add grouped and collapsible Kanban lanes with active counters and compact left-rail collapsed state, driven off the `phase` field landed in step 2.
4. Add Orchestrator Intake after Human Ready, with duplicate, clarity, missing-context, and executable-shape checks. Default off per project; pass-through when no checks are configured.
5. Add Orchestrator Post Processing after Task Execution, with explicit support for different CLI identity and typed findings. Auto-commit and Haiku summary become built-in post-processing kinds.

Queued at `agent-taskboard/2-ready/expanded-lifecycle-lanes-concept/`, `agent-taskboard/2-ready/lifecycle-substate-migration-compatibility/`, `agent-taskboard/2-ready/kanban-lane-grouping-collapse/`, `agent-taskboard/2-ready/ready-orchestrator-intake-lane/`, and `agent-taskboard/2-ready/post-processing-orchestrator-lane/`.

### Task Finding And Shape

Make large boards easier to understand:

- Global search is delivered as a Ctrl+K palette across tasks, Dossiers,
  commits, and working-branch files. Task results cover keys, titles, prompts,
  and status text and target a warm response below 300 ms; repository results
  use cached git primitives through
  `GET /api/search?q=...&domains=tasks,commits,files,dossiers`. Exact task and
  Dossier key matches rank first and domain failures preserve partial
  results. The current contract is documented in
  [docs/system/domains/frontend.md#global-search](docs/system/domains/frontend.md#global-search).
- Project-level tags with defaults such as Backend, Frontend, UI Improvement, and Bugfix.
- Better ordering interactions with stronger drag feedback and less visible internal bookkeeping.
- Cleaner archive browsing for completed and historical work.

### Roadmap And Intent

Turn a pile of tasks into a useful product view:

- A project roadmap view that groups open tasks by theme.
- Security and Architecture should be recognized as project-level themes, not just tags.
- Automatic intent extraction from task prompts.
- Follow-up prompts such as "what should be next?", "what is duplicated?", or "what should be split?"
- A path from planning output into a new task draft.

### Agent Feedback

Make agent work easier to judge while it is still running:

- Short protocol summaries at the top of the detail view.
- Mid-run status requests where a CLI supports safe intervention.
- Stronger Activity Log parsing across all supported CLIs.
- Better usage, quota, and model feedback, including edge cases such as model-specific limits.

### Stale Session Reliability

Make continuation after idle, stale, lost, or partially-corrupted sessions a first-class reliability target, especially for Claude Code and Codex:

- Treat stale-session continuation as a product-critical path, not a nice-to-have resume feature.
- Prefer structured session evidence on disk (`session-events.jsonl`, `sessionChain`, `cli-output.log`, `status.md`, `prompt-N.md`) over trusting a provider chat to remain semantically intact forever.
- Add a deterministic daily-session probe suite for Claude and Codex: fresh run, resume within minutes, resume after an idle threshold, resume after backend restart, rejected resume target, recovery run with user follow-up, and no-op-after-recovery reissue.
- Track the age of the resumed session and expose it in logs/UI so stale-resume behavior can be diagnosed from evidence.
- Keep Codex and Claude as the reference implementations. Other CLIs inherit the contract only after the two primary paths are stable.

### Deterministic Orchestration

Treat orchestrator-to-CLI communication as a core capability instead of a side-effect of prompt wording. The orchestrator parses CLI output for typed signals, makes deterministic decisions, and speaks for itself in the chat when it does.

Future orchestrator controls must extend the existing scope contract instead of
reintroducing flat global configuration: project overrides win over defaults for
their owning `WorkspaceRecord`, which in turn win over platform constants. Only
controls evaluated inside a project or workspace context belong in that chain;
process-wide hosted-service lifecycle gates remain global. The shipped foundation,
persistence boundary, resolver, and retired-modal routing are recorded in
[ADR-0061](docs/system/architecture/decisions/adr-archive.md#adr-0061---orchestrator-settings-are-a-two-tier-config-project-override-wins-over-workspace-default-wins-over-platform-constant-2026-07-11).

- Hard agent signals (`[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, `[[TASK_NOOP]]`) parsed from CLI output. Authoritative when present.
- A post-run policy that re-issues a follow-up the agent did not honor, instead of accepting the inconsistency. Bounded retry budget; meta message into the chat on every action.
- An `Orchestrator` participant in the activity log so the user sees the system's decisions next to the agent's replies. Heuristic fallback always surfaces a warning.
- Recovery after a session loss carries the user follow-up as the primary instruction, not a footer the agent can ignore.

### Multi-Loop Supervision

Add a meta-orchestrator loop above the per-project job-pickup loop, plus an external system review monitor. Today the app runs two loops (the CLI agent's internal loop and the orchestrator's job-pickup loop). Both decide things the user only sees afterwards. The supervision layer is the watcher above:

- A **per-project supervisor** that observes the orchestrator's runner in real time and asks fixed questions every tick: is the run progressing, is quota close to exhausted, is the agent's current activity aligned with the prompt scope, are findings accumulating that should pause the queue. Cooperative-signal model by default, with a small set of emergency primitives (cancel run, pause pickup, force fail, resume) for clearly broken behaviour. Each intervention is typed, logged, and visible in the activity feed as a separate participant.
- A **meta-cycle** that runs at quiet batch boundaries, after N jobs reach review or when the user manually asks for an inspection. It pauses pickup, compares recent jobs and evidence against the roadmap and health rules, writes a structured report, then resumes, queues a fix, updates stable through the external helper, or escalates.
- A **continuous protocol** per project: append-only structured logs the user can replay to see what the supervisor saw and decided. A panel on the project page shows the live state.
- A **system review monitor**, run from outside on a multi-hour cadence, that produces structured "after ten hours, this is what the system did and this is what looks off" reports. Stand-alone, not part of the app's runtime, so it survives any app failure mode.

Hard boundary: supervision is advice-first, force-rare. The deterministic post-run policy in `RunOutcomePolicy.cs` stays the authoritative path for routine outcomes; the supervisor adds an outer kill-switch and a soft-reasoning second opinion, not a parallel orchestrator.

The full conceptual analysis - loop-to-loop control options, communication contract sketch, execution-model tradeoffs (in-process vs sidecar vs CLI-driven), open conceptual problems, and recommended task spinout - lives in [docs/research/orchestrator-meta-loop-analysis-2026-05-04.md](docs/research/orchestrator-meta-loop-analysis-2026-05-04.md). The recommended first slice is the system review monitor (Layer 3) because it ships value immediately on stable without touching the runtime.

### Agent Workforce and Role Specialization

Treat the active agents in a project as a **workforce of specialized roles**, not as one generic CLI invocation per task. The human operator is the manager; the workforce is LLM-based. Different work needs different system prompts, different tool sets, different budgets, sometimes different models.

Realistic role catalogue, derived from observed work patterns:

- **Task Executor** - the primary coding agent (Claude Code, Codex, Copilot, Gemini); makes the file edits and runs the tools.
- **Code Reviewer** - audits diffs from Task Executor runs; different system prompt, often a different model tier.
- **Architecture Custodian** - periodic; checks code against ADRs and the architecture marble; surfaces drift.
- **Security Auditor** - on-demand or scheduled; checks diffs for secrets, unsafe patterns, dependency risks.
- **Test Author** - reads tool-calls from the Task Executor and adds the coverage the executor missed.
- **Documentation Maintainer** - keeps doc-vs-code consistency, proposes updates.
- **Plan Curator** - in plan-mode workflows: reviews plan proposals, suggests variants.
- **Diagnostician** - on pickup-failure or hang: reads logs, classifies, proposes action under the contract pattern (ADR-0032).
- **Health Officer** - periodic, system-wide: what looks unhealthy, where does the human need attention.

Hard rules:

- **Sequential rotation per task, bounded parallelism across tasks.** Within one task, roles run one after another on the same artifact: Task Executor writes, Code Reviewer audits, Architecture Custodian checks drift, Security Auditor inspects last. Parallel within a task produces conflicts that erase the throughput gain. Throughput parallelism happens across projects or across orchestrator-admitted worktree slots (ADR-0052).
- **The human stays the manager.** Role architecture (which roles exist, what they own, who escalates to whom) and tradeoff decisions (more security vs more speed, refactor phase vs feature phase) stay human. The workforce executes; the manager structures.
- **Roles are versioned artifacts in the repository**, like skills (ADR-0026). Not config, not prompts on the fly. A role definition that lives in the repo is reviewable, diffable, comparable across runs.
- **Per-role budgets and routing.** Each role declares a token-budget tier, a wall-clock cap per run, an allowed model list, and a downgrade strategy when quota is tight. First-class, code-level constants; not implicit.
- **Operator allowance per phase.** Same role, different importance per project phase. Before a release: Security Auditor turns up, runs on every diff. In a refactor phase: Architecture Custodian turns up, Security Auditor turns down. The operator's allowance setting is the lever; the runner respects it.

Today's state: building blocks exist (per-job token spend in `tool-calls.jsonl`, cliType per job, Subagent concept in skills architecture, Aspect Runner for multi-aspect review). What is not yet a first-class surface: per-role budgets as configuration, per-role performance dashboards, per-role model routing as a deliberate setting, and the allowance-per-phase lever.

The full marketing context lives in `agent-studio-marketing/06-website-planung/agent-workforce-und-rollen.md`. The roadmap entry here is the engineering counterpart: build the missing surfaces incrementally, in a sequence that respects the sequential-per-task role rule and the ADR-0052 slot-admission model.

### Analysis Reports and Meta-Actions

Make manual and scheduled analyses first-class product output. The user should be able to ask a project-level question such as "are we on track?", "which jobs are stale?", "does the queue match the roadmap?", "which docs need sync?", "what changed in the last few hours?", or "what should become a follow-up task?" The orchestrator, a supporting agent, the meta-cycle, or an external monitor can answer, but the result lands in the same report system.

The report model:

- Markdown is the durable human-readable report.
- Structured JSON is required when the app needs to aggregate, filter, trend, or create badges from the result.
- Reports carry scope: workspace, project, task, run, time window, source prompt, trigger, producer, schema version, tags, and artifact references.
- Reports can reference Agent Message Bus records, runtime events, screenshots, commits, task folders, test runs, and previous reports.
- If JSON parsing fails, the Markdown remains visible with an unstructured-report warning.
- Findings can create normal queued tasks. Reports do not silently mutate source code or move jobs around the board.

The UI should expose a project-level **Analysis Reports** area:

- Manual action buttons for roadmap alignment, security posture, architecture drift, QA status, token spend review, stale jobs, and docs drift.
- Scheduling controls for daily or every-few-hours analyses, default off.
- Report history with status, severity, producer, scope, time window, parse status, and follow-up links.
- Drill-down to Markdown, structured JSON, raw artifacts, and referenced jobs or bus messages.
- A clear split between project-scoped reports and task-scoped reports. Task reports stay beside task evidence; project reports live at project level.

The orchestrator should also use this layer to learn from repeated failure patterns across jobs. A meta-analysis can read recent job outputs, status files, Agent Message Bus records, test reports, and previous findings, then say: "this class of error keeps happening; the steering docs or process are missing guidance." The result is a report with evidence links and proposed README, AGENTS, task-contract, skill, or process updates. The first version should not silently rewrite those files. It should create a reviewable documentation task or proposed patch so the user can see exactly what steering change is being suggested.

This needs a project-level **Steering Docs / Project Knowledge** surface beside Analysis Reports:

- Show the agent-facing documents that shape work: README, AGENTS, ADR index, task contract, runtime prompts, skills lookup, project settings, and project-specific steering notes.
- Add a human summary layer on top: "what the agents are currently told", "what matters for this project", "what looks stale or contradictory", and "what recent failures suggest should change".
- Link every warning to evidence: repeated CLI errors, failed tests, recurring blocked reasons, ambiguous prompts, stale instructions, or reports that disagree with current docs.
- Offer explicit actions: summarize steering docs, check docs drift, compare queue to docs, propose README update, propose AGENTS update, and create follow-up task.
- Keep raw technical docs visible. The summary is an abstraction layer, not a replacement for the source files.

Queued at `agent-taskboard/2-ready/analysis-report-contract-and-storage/`, `agent-taskboard/2-ready/project-analysis-reports-surface/`, `agent-taskboard/2-ready/roadmap-alignment-analysis-action/`, `agent-taskboard/2-ready/orchestrator-output-pattern-learning/`, `agent-taskboard/2-ready/project-steering-docs-surface/`, and `agent-taskboard/2-ready/steering-docs-summary-and-drift-action/`.

### Context Engineering

Make context a first-class product concept, not a side effect of how agents happen to run.

Agents are only as good as what they see before they act. Context Engineering is the discipline of deciding what goes into that view, how much, in what form, when, and how it is kept current. Today the app supports individual pieces of this — Steering Docs, Skills, task context, Analysis Reports — but the product has no surface that makes context visible as a whole, or that treats context health as a project metric.

The product should support:

- **Constitutional Layer surface.** AGENTS.md, README, ADRs, Skills, and project-specific steering notes visible together as the agent-facing knowledge base for each project. Editable and durable from within the app, not just from a text editor. A human summary layer on top: what agents are currently told, what matters for this project, what looks stale or contradictory, and what recent failures suggest should change.
- **Per-task context panel.** Before a task runs, show what context it carries: which steering docs it inherits, which task-specific Spec and context is attached, which files are in scope, and what history is visible. Make missing or conflicting context detectable before the run, not after.
- **Context enrichment in the Intake phase.** As part of Orchestrator Intake (see Expanded Lifecycle Lanes), a planning step enriches a task with relevant project knowledge before the agent touches code: README, Roadmap, ADRs, Specs, relevant prior tasks, related code areas. The output is a richer task context, not just the original prompt.
- **JIT-retrieval awareness.** Surface which files and documents an agent is likely to read during a run (based on task scope, prior runs, and steering docs). This is not pre-loading; it is making the agent's expected read surface transparent to the human reviewer.
- **Memory tier surfaces.** Make the four memory tiers visible as product concepts — Working Memory (active context), Episodic Memory (job-folder history, tool-calls.jsonl), Semantic Memory (Steering Docs, project knowledge), Procedural Memory (Skills, runtime prompts). Each tier has a product surface. The human should be able to see and edit each tier.
- **Context health metric.** Detect stale, contradictory, or missing context before it becomes a failure. A project with no current AGENTS.md entry, conflicting ADRs, or a task with no Spec should surface a warning, the same way a project with no security baseline already does.
- **Steering Docs Feedback Loop as a product action.** Today the meta-analysis can suggest steering doc updates (see Analysis Reports). Make this more explicit: a "Context Health" action produces a report on stale, contradictory, or missing context, and proposes specific patches as reviewable follow-up tasks. No silent rewriting of steering files.

Hard rules:

- Context is shown, not hidden. Every agent run should have a readable record of what context it was given, not just what it produced.
- Context updates are explicit and reviewable. Nothing rewrites AGENTS.md, Skills, or ADRs silently. Every proposed context change becomes a task or a diff the human can accept or reject.
- Context health is a project metric, not an afterthought. A project with poor context health (stale docs, missing Spec, contradicting ADRs) should be visually distinguishable from a project with healthy context.

The full marketing framing lives in `agent-studio-marketing/06-website-planung/context-engineering.md`.

First implementation order:

1. Constitutional Layer surface on the project page: list all agent-facing documents, show freshness, flag conflicts and gaps.
2. Per-task context panel in the task detail view: inherited steering docs, attached Spec, task contract, Nicht-Ziele, Akzeptanzkriterien.
3. Context enrichment as a Planning step in the Intake phase, producing a richer task context before agent execution begins.
4. Context health action: scheduled or manual report on stale, contradicting, or missing context, with proposed follow-up tasks.
5. Memory tier surfaces: job-folder Episodic history, Steering Docs Semantic view, Skills Procedural catalog — unified as a readable project knowledge layer.

### Prompt And Context Optimization

The product should analyze how agents actually gather context, then help the human improve prompts, steering docs, skills, and project knowledge layout. This is not about guessing whether a prompt "sounds good." It is about observing the agent's tool behavior: which files it reads, which docs it repeatedly opens, which search patterns it uses, where it scans too broadly, and which missing or stale context causes extra work.

The first useful signal is simple access telemetry:

- Reads of `README.md`, `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`, skill files, runtime prompts, ADRs, roadmap, setup docs, and project-specific steering docs.
- `rg`, file-list, glob, find, and directory-list operations, including repeated queries and broad searches.
- Tool access around task start, planning, implementation, verification, and review.
- Repeated reads of the same documents across similar tasks.
- Missed high-value documents that were relevant to the task but only discovered late or not at all.
- Heavy context-gathering loops that cost time and tokens before the agent can act.

What the surface should provide:

- **Context access report** per task: top files read, steering docs read, skills read, search queries, repeated reads, late discoveries, and likely context gaps.
- **Project context efficiency report**: which docs are used constantly, which are never used, which are too broad, which should be split into a skill, and which should move into an index.
- **Prompting pattern analysis**: detect repeated task shapes and recommend reusable prompt patterns, intake templates, acceptance-criteria templates, or skill entries.
- **Context packaging suggestions**: propose a smaller "start here" index, a project-specific AGENTS section, a skill lookup table, or task-template improvements when telemetry shows the agent keeps searching for the same information.
- **Before/after comparison**: measure whether a steering-doc or prompt-pattern change reduced repeated searches, time to first edit, token spend, or failed pickups.

Hard rules:

- Observation first. The app may propose prompt, skill, README, AGENTS, or ADR edits, but it must not rewrite them silently.
- Tool telemetry should capture paths, operation types, query text, timestamps, and rough phase. Full file contents are not duplicated unless the source artifact already exists in task evidence.
- Different CLIs expose different tool-call detail. The analysis must handle partial telemetry and label confidence accordingly.
- Recommendations become reports, patches, or follow-up tasks. They do not directly alter future prompts without review.
- Do not optimize for fewer reads at the expense of safety. Reading the right contract twice is better than skipping it.

First implementation order:

1. Define a tool-access event model over existing `tool-calls.jsonl`, CLI logs, and future Agent Message Bus events.
2. Build a task-level Context Access report that summarizes reads, searches, skills, steering docs, repeated lookups, and likely gaps.
3. Add a project-level Prompt And Context Optimization report that aggregates recent tasks and identifies recurring context patterns.
4. Add recommendation types: improve index, split skill, add task template, update AGENTS, update README, add ADR pointer, add acceptance-criteria pattern.
5. Add follow-up-task creation and proposed patch generation for context improvements.
6. Add before/after metrics so context optimizations can prove they reduced friction without hiding evidence.

This extends Context Engineering and Analysis Reports. Context Engineering makes the intended context visible; Prompt And Context Optimization compares that intent with what agents actually had to do with tools.

### Agent Message Bus and Observability

Use a central, append-only Agent Message Bus as the product's communication spine. The bus is not a workflow engine and does not relax the one-coding-task-per-project boundary. It is a schema-first event log that records who observed, asked, decided, answered, intervened, spent tokens, and produced evidence.

The useful mental model is "many agents, one visible conversation layer":

- A per-project orchestrator owns queue movement and project context.
- A per-task coding agent is the active Claude, Codex, Copilot, or Gemini run that edits the repository.
- Supporting agents run explicit user-triggered meta work such as QA, security audit, architecture review, source map generation, UX/UI critique, design council feedback, and token analysis.
- Supervisor agents observe health, quota, progress, and stuck loops. The deterministic runner policy remains authoritative for routine outcomes.
- A Layer 3 system health agent, working title "Master of Disaster", periodically reviews the bus and reports whether the whole system looks healthy after hours of work.
- The app runtime also writes messages when it creates jobs, starts runs, stops runs, moves states, parses sentinels, or bridges token usage into the project view.

Message records are small JSON documents, preferably JSONL on disk. The first schema should be `agent-message.schema.json` with stable fields for ids, timestamp, project, optional job/run/session, participant, role, message kind, severity, references, token usage, payload, and schema version. Expected message kinds include `observation`, `question`, `decision`, `advisory`, `intervention`, `artifact`, `token-usage`, `lifecycle`, `error`, and `heartbeat`.

The Project Screen should make the bus visible:

- A communication timeline showing user, task agent, project orchestrator, supporting agents, supervisors, and system review as separate participants.
- A participant graph that answers "who talked to whom" and "which job or run caused this".
- Heatmaps and counters for message volume, intervention rate, expensive token events, error bursts, and long silent periods.
- Drill-down from any aggregate to the raw JSON message, linked job, run, artifact, screenshot, diff, or markdown report.
- Filters by participant, message kind, severity, time window, job, run, skill, and CLI.

Storage stays deliberately simple: many small documents on disk as source of truth, backed by a strongly typed in-memory projection for query, aggregation, and UI speed. No SQL, SQLite, LiteDB, or EF until the file-backed model is proven insufficient. If the bus grows into tens of thousands of documents, the next optimization is indexing and snapshotting inside the in-memory layer, not a premature database migration.

The contract lives in [`docs/system/architecture/bus/agent-message-bus.md`](./docs/system/architecture/bus/agent-message-bus.md) with schemas under [`docs/system/schemas/`](docs/system/schemas/README.md) (`agent-message`, `agent-participant`, `agent-artifact-ref`). Subsequent slices implement the projection, bridge writers, UI panel, supporting-agent emitters, and system-health reader on top of that contract.

First implementation order:

1. Document the contract in `docs/system/architecture/bus/agent-message-bus.md` and add JSON schemas under `docs/system/schemas/`.
2. Extend the planned in-memory layer with an Agent Message Bus projection over JSONL on disk.
3. Bridge existing streams into the bus: `cli-output.log`, orchestrator chat messages, supervisor advisories and interventions, lifecycle moves, and token usage summaries.
4. Add the Project Screen Observability surface with timeline, participant graph, filters, and raw JSON drill-down.
5. Teach supporting agents and project-level skill actions to emit bus messages and token usage records.
6. Let the Layer 3 system health agent read the bus and produce health reports from the same evidence the user can inspect.
7. Consider external Agent-to-Agent protocol adapters later, only when there is a concrete CLI or tool integration that benefits from them.

Queued at `agent-taskboard/2-ready/agent-message-bus-contract/`, `agent-taskboard/2-ready/agent-message-bus-store/`, `agent-taskboard/2-ready/bridge-existing-events-to-message-bus/`, `agent-taskboard/2-ready/project-observability-message-bus-panel/`, `agent-taskboard/2-ready/supporting-agents-message-bus-events/`, and `agent-taskboard/2-ready/system-health-agent-message-bus-review/`.

### Product Runtime Observability

The product should help users understand not only what the agents did, but how the software being built behaves. Software on the workbench should be observable by default: it should emit structured logs, expose useful runtime signals, preserve failure evidence, and make performance and domain behaviour inspectable while the agent is still building it.

This is a separate layer from the Agent Message Bus:

- Agent Message Bus answers: which agent, supervisor, skill, or orchestrator acted, and why.
- Product Runtime Observability answers: what did the built application do when it ran, where did it fail, how fast was it, and which domain events happened.

The first version should be build-time first, production-later. Every serious generated or modified application should have enough observability for local testing, debugging, QA, performance probes, and review. Production deployment hooks can come later, but the build bench should already capture what the app says about itself.

Core layer:

- A project-level observability contract in Markdown that tells agents how this software should log, name domain events, expose metrics, preserve errors, and correlate user actions with runtime effects.
- A small structured runtime event envelope for built software: timestamp, level, event name, subsystem, operation, correlation id, optional job/run/task id, duration, status, error, tags, and payload.
- Default sinks that stay simple: JSONL file, stdout/stderr capture, browser console capture, test-run attachments, and optional HTTP diagnostics endpoint when the app already has a backend.
- Optional adapters for OpenTelemetry or native platform logging later. Do not make OpenTelemetry a hard dependency for the first slice.
- Base prompt guidance that asks coding agents to add or preserve practical observability when they build features: meaningful structured logs, stable event names, error context, performance timings for expensive paths, and enough domain signals to understand what happened.
- An analysis skill that reads runtime logs and answers: what happened, what looks slow, what failed, which errors repeat, which paths are noisy, and which user-visible workflow needs attention.
- A project-level Runtime Observability surface that shows recent product events, error groups, latency summaries, counters, domain-event timelines, and links back to tasks, test runs, screenshots, and agent messages.

This should stay proportional. A tiny script does not need a telemetry platform. A web app, backend service, data pipeline, or product-like tool should have structured logging and a way for the task processor to collect and inspect it while it is under construction.

First implementation order:

1. Define `docs/operations/runtime/observability.md` and `docs/system/schemas/product-runtime-event.schema.json`.
2. Update base prompts and task contracts so agents consider build-time observability when adding meaningful software behaviour.
3. Add capture paths for local runs, Playwright runs, backend logs, and browser console events into task evidence or project observability folders.
4. Add a Runtime Observability project surface that reads structured product events and summarizes errors, latency, counters, and domain timelines.
5. Add an analysis skill or project action that turns logs into reviewable Markdown plus structured JSON findings.
6. Connect runtime events to the Agent Message Bus only by reference, not by mixing the two data models. The bus can say "this run produced runtime log artifact X"; product events remain their own schema.

Queued at `agent-taskboard/2-ready/product-runtime-observability-contract/`, `agent-taskboard/2-ready/base-prompts-observability-guidance/`, `agent-taskboard/2-ready/product-runtime-log-capture/`, `agent-taskboard/2-ready/runtime-observability-project-surface/`, and `agent-taskboard/2-ready/runtime-log-analysis-skill/`.

### Companion App

Make the running task board reachable from a phone without exposing the local processor to the internet:

- A small public relay service holds the latest snapshot and a small command queue. The local processor pushes snapshots and pulls commands on a single outbound HTTPS tick (default 10 s). The phone PWA reads the snapshot and posts commands.
- Pull-pull on the processor side is non-negotiable. The local box never accepts inbound connections; the relay is the only public surface.
- V1 surface: pipeline overview per project, current task, token-spend summary, quota windows, open NEEDS_INPUT decisions, decision-answer command, new-task command, start-job command. No live log stream, no diff viewer, no push notifications.
- V1 secrecy is shared bearer token over TLS. End-to-end encryption with a phone-paired symmetric key is V2; the relay sees plaintext until then.
- The companion shipped on Fly.io. Railway is a documented fallback. The PWA is a separate Angular build, deployed as static assets.
- Default-off in the local processor. Enabling it is a `Companion:Enabled=true` flip in `appsettings.Local.json`, so a fresh checkout never tries to phone home.

The full V1 contract (endpoints, snapshot shape, command shape, sync cadence, file map) lives in [docs/concepts/companion-app-design.md](./docs/concepts/companion-app-design.md). [ADR-0018](./docs/system/architecture/decisions/adr-archive.md) captures the architectural decision.

### Schema-First Communication and In-Memory Data Layer

The product is accumulating cross-cutting structured data: agent messages, participant records, product runtime events, token aggregates per project, supervisor advisories and interventions, audit findings, architecture-quality scores, componentisation metrics. These auxiliary evidence streams remain many small JSON-schema-validated documents on disk, plus a strongly-typed in-memory layer that loads them at boot and supports query and aggregation. This rule does not describe the separated Task Server control-plane store.

- One schema per concept, named `<concept>.schema.json`, under `docs/system/schemas/`. Draft 2020-12. English. No em dashes.
- An in-memory store is a typed view over disk; the file is always the source of truth.
- No SQL. No SQLite, no LiteDB, no EF. The repo deliberately avoids a database engine.
- First slice: schemas for supervisor advisory, supervisor intervention, token aggregate, agent message records, and product runtime events, plus an `InMemoryStore` consumed by `AutoInterventionHostedService`, the Agent Message Bus projection, and runtime observability projections to replace direct file reads.

Queued at `agent-taskboard/2-ready/json-schemas-and-in-memory-layer/` and extended by the Agent Message Bus and Product Runtime Observability jobs.

### Continuous Decision Visibility

The orchestrator should not just react at run-end. While a job is in `3-progress`, it should scan recent CLI output every few seconds and surface a "decision required" signal when the agent emits `[[TASK_NEEDS_INPUT:...]]` or another decision sentinel. Today such moments are visible only deep in the activity log; they should be a loud, distinct banner on the project view, with a one-click reply.

- Reuse `AgentOutcomeAnalyzer.SentinelRegex` for detection.
- Piggyback on the existing CLI output poll rather than adding a parallel ticker.
- Banner has its own visual treatment, distinct from supervisor advisories and from regular activity entries.

Queued at `agent-taskboard/2-ready/orchestrator-continuous-decision-visibility/`.

### Dev-Stable Role Split

Dev is a regression-test target, not a self-task target. After a coherent batch ships on stable, dev receives an end-to-end task that exercises the changed surfaces. Dev does not appear in its own watched-projects list. Runtime artefacts (per-project supervisor jsonl, system-review reports, backend logs) belong in `.gitignore`, not in commits.

Queued at `agent-taskboard/2-ready/separate-dev-from-stable-roles/`.

### Backend Observability

The recent silent dev-backend crash exposed an observability gap: the on-disk `.api.log` was four days stale because the running backend redirects to `.api.log.out` / `.api.log.err`, neither of which is rotated, surfaced, or summarised when something dies. After the fix:

- Structured rolling logs at `logs/backend/<date>.log` with traceId, project, jobId fields.
- A `last-crash.json` marker and `/api/diagnostics/last-crash` endpoint so the supervisor and Layer 3 review can pick up crash evidence.
- Layer 3 system-review reads the marker and surfaces it in the next run.

Queued at `agent-taskboard/2-ready/backend-observability-real-logs/`.

### In-Product Concept Documentation

Concepts (Orchestrator, Supervisor, Skills, Audits, Probes, Companion) are documented under `docs/`, but a user looking at a panel cannot reach the docs without leaving the app. A reusable `app-concept-help` component renders an "i" icon on every panel that introduces a concept; the popover shows a short paragraph plus a "Learn more" link to the canonical `docs/` page.

Queued at `agent-taskboard/2-ready/in-product-concept-docs/`.

### Persistent Orchestrator Chat

Make the orchestrator a durable conversation partner, not only a backend decision service. The user should be able to keep contact with the global board orchestrator or the current project's orchestrator across reloads, restarts, and days.

Planned capabilities:

- Scope selector for Global vs current Project orchestrator.
- Conversation-first timeline where technical session continuity appears as compact, expandable event bubbles instead of primary rows.
- Chat log loaded from the orchestrator event log so reloads preserve the conversation.
- Context view showing model, CLI, session id, boot source, last activity, memory snapshot, and recent summarized job evidence.
- Project memory snapshots built from README, ROADMAP, AGENTS, architecture decisions, job results, open tasks, recent decisions, and review outcomes.
- Project search across chat turns, orchestrator decisions, task prompts, status protocols, commits, files, screenshots, result artifacts, project docs, and memory snapshots.
- Typed app actions from chat, starting with safe actions such as create task draft, open job detail, refresh memory, and summarize recent results.
- Explicit fork semantics for research or speculative planning. The canonical project orchestrator remains the default chat partner.

The redesign handoff is [docs/concepts/orchestrator-chat-redesign-handoff.md](./docs/concepts/orchestrator-chat-redesign-handoff.md). The load-bearing UI boundary is archived in [ADR-0036](./docs/system/architecture/decisions/adr-archive.md#adr-0036---session-mechanics-render-as-timeline-events-not-primary-chat-objects-2026-05-17): session mechanics are audit events, not the primary chat object.

Delivered (Multichat, Concept §4): the side sheet follows the operator's navigation and can pin a context (MC-2), per-context transcript history is served by `GET/POST /api/runner/{contextKey}/orchestrator-chat` — a `task:<PROJ>/<KEY>` context keeps its own thread while `project:<PROJ>` resolves to the canonical board thread — and the side sheet reads and sends through the context-keyed route, so a pinned task now shows its own transcript in the app while the board is byte-for-byte unchanged. See [docs/concepts/orchestrator-chat.md](./docs/concepts/orchestrator-chat.md#per-context-chat-transcript).

Delivered (execution locality): side-sheet project and task chat turns follow
the project's execution assignment. A remote-assigned project is claimed by
its Agent Runner and Codex starts inside a runner-host checkout from the
project's git cache; projects without a remote assignment continue locally.
The compact chat header reports local or hostname, checkout path, branch, and
HEAD revision from the actual execution context. See
[docs/concepts/orchestrator-chat.md](./docs/concepts/orchestrator-chat.md#execution-location-and-checkout-context).

### In-App Orchestrator (Sight, Hands, Anchor)

Move the operator inside the application. The concept ([docs/concepts/orchestrator-in-app.md](./docs/concepts/orchestrator-in-app.md), v1) is that the chat *is* the orchestrator that has the whole board (the whole application) in view and keeps it running from inside the app, not an external console watching a patient. This is the sibling direction to Persistent Orchestrator Chat: that theme makes the conversation durable; this one gives it operational eyes and, later, hands. Three pillars, gated in order:

- **Sight (ORCH-1)**: the chat receives a current, read-only application digest covering the board pulse (lane counts plus latest transitions), active runs and lifecycle phase, cached CLI quota, PUB-1 publish-target status, backend and watcher health, and recent decision-journal verdicts. The digest follows the `global | project:<id> | task:<project>/<task>` multichat context key: global sees every registered project, project and task scopes stay project-isolated, and an unknown project or task is a 404, never a wider fallback.
- **Hands (ORCH-2)**: journaled operational tools for reconciliation, requeue, park/promote, post-processing restart, publish, and parallelism changes. Every action typed, logged, and visible as a distinct participant. Not started.
- **Anchor (ORCH-3)**: standing-orders operational policy plus the minimal outside anchor needed when the host itself is down. Host-death recovery stays outside the in-app chat until then. Not started.

Hard boundary: sight grants no mutation authority. ORCH-1 only reads; intervention arrives with ORCH-2 and must be journaled and admitted through the existing slot and worktree-isolation model ([ADR-0052](docs/concepts/parallel-task-execution.md)), never as a hidden parallel actor editing a project outside admission.

Delivered (Sight, ORCH-1): one backend `OrchestratorContextDigestService` builds the bounded digest and is shared by both the visible side-sheet chat turn and the canonical session-turn API, so the two entry points cannot drift into different views of application state. `GET /api/orchestrator/context/{key}` inspects the digest cheaply from cached quota; `POST .../refresh` is the only path that re-probes quota. The read-only, single-builder, fail-closed-scope boundary is archived in [ADR-0062](docs/system/architecture/decisions/adr-archive.md#adr-0062---the-in-app-orchestrator-read-context-orch-1-is-one-shared-scoped-read-only-digest-builder-2026-07-11). See [docs/concepts/orchestrator-chat.md](./docs/concepts/orchestrator-chat.md#application-read-context-orch-1).

### Focused UX

Keep the app dense, fast, and pleasant to use:

- Compact headers and status bars.
- Better model and CLI defaults.
- Completion notifications that do not interrupt the workflow.
- Layout polish for detail panes, rows, cards, tooltips, and screenshots.

## Hard Boundaries

The core execution model stays intentionally narrow:

- Sequential by default: one coding task runs per project at a time unless a project opts into parallelism.
- **Intra-project parallelism is opt-in and orchestrator-gated (ADR-0052):** a per-project `maxParallelism` may run N tasks concurrently, each isolated in its own git worktree on a short-lived `task/<id>` branch off the integration branch (default `develop`); the orchestrator decides parallelisability (too-big tasks run `exclusive`). Parallelism also exists across projects.
- Branch + worktree handling lives entirely in pre/post pipeline steps (create worktree/branch, commit, merge or open PR, cleanup) - never in the run agent. Reversed the former "no branches / no worktrees" non-goal (ADR-0052).
- The app does not become a workflow engine.
- The app does not implement its own API-backed coding-agent runtime while subscription CLI agents remain the primary value path.
- Runtime job artifacts belong in watched task folders, not in this source repository.

Planning and research tasks have a read-only fast path in the slot model because they do not change source code. That distinction must stay explicit. Coding tasks stay one-at-a-time by default and require ADR-0052 worktree isolation plus orchestrator admission when `maxParallelism` is greater than 1.

## Agent Decision Principles

When changing this product, prefer work that:

- Reduces human babysitting.
- Makes security review more repeatable, evidence-backed, and frequent.
- Improves review quality.
- Makes the current task state easier to see.
- Preserves the default-simple slot model while allowing bounded, explained parallelism.
- Uses owned files, controlled runner environments, and existing subscriptions instead of a hidden hosted model broker.
- Treats Codex, Claude Code, and other provider-owned agents as the primary execution engines.
- Keeps the UI compact, legible, and calm.

Be cautious with work that:

- Adds bookkeeping before it removes friction.
- Turns a simple queue into a workflow system.
- Encourages multiple coding agents to edit one project at the same time.
- Rebuilds the provider-owned agent loop before the existing subscription agents have been exhausted.
- Hides important evidence from the reviewer.

## Documentation Drift

After any CLI-executed task finishes, check whether the README, this roadmap, AGENTS.md, [docs/system/architecture/decisions/adr-archive.md](./docs/system/architecture/decisions/adr-archive.md), or other docs need to be updated. Update them in the same task when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, agent workflow, or established a non-goal worth archiving. The ADR file is the chronological log of decisions; README / ROADMAP / AGENTS are the narrative surfaces that describe the current shape. The two must stay in sync. If no documentation update is needed, say so briefly in the task report.
