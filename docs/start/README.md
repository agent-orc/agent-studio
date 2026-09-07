# Documentation Index

`docs/` is the physical knowledge root; this page (`docs/start/`) is its curated
entry index. The Wiki renders the real folder tree directly: categories are
directories, pages are Markdown or sandboxed HTML files, and there is no virtual
grouping layer.

Use this page as the first stop when you need the right document quickly.

## Categories

| Folder | What lives there |
|---|---|
| [architecture/](../system/architecture/README.md) | Architecture model, ADR archive, proposed ADRs, backend structure, bus docs, runner-lane constraints, and HTML maps. |
| [domains/](../system/domains/README.md) | Current system-of-record domain maps for runner, pipeline, tasks, frontend, CLI, tokens, and token pricing. |
| [contracts/](../system/contracts/README.md) | Durable filesystem, task, protocol, run outcome, code-pattern, and wiki organization contracts. |
| [design/](../quality/design/README.md) | Product-wide, prompt-known design hard rules (no left accent bars, full-bleed views, aggregate = sum, acute-only signals, both themes). |
| [quality/](../quality/README.md) | Technology-aware Angular and .NET style guides, applicability metadata, and the rule-authoring workflow used by Deck and intake prompts. |
| [frontend/](../quality/frontend/README.md) | Frontend design system, style guide, testing contract, performance playbook, and audits. |
| [cli/](../system/cli/README.md) | Supported CLI contract, per-CLI skills, audits, and investigations. |
| [operations/](../operations/README.md) | Setup, onboarding, security docs, runtime observability, git doctrine, and test workspaces. |
| [reports/](../system/reports/README.md) | Report contracts, HTML visual reports, and screenshot-backed visual documentation. |
| [concepts/](../concepts/README.md) | Architecture concepts that have not become a domain or ADR yet, plus hand-maintained living concept/knowledge pages with running knowledge logs, dated deep dives, mockups, and proposals. |
| [archive/](../archive/README.md) | Quiet historical pages retained by explicit inventory passes, with pointers from their former current locations. |
| [common-problems/](../operations/common-problems/README.md) | Known recurring problems with root-cause analysis, occurrence logs, and workarounds. Search here first on a familiar symptom. |
| [learnings/](../operations/learnings/README.md) | Per-task run learnings auto-distilled by the opt-in `post-wiki-learnings` pipeline step. Do not edit by hand. |
| [mockups/](../concepts/mockups/) | Locked design references and click-dummies (under `concepts/`). |
| [assets/](../assets) | Image assets referenced by documentation pages. |
| [proposals/](../concepts/proposals/README.md) | Dated improvement proposals with durable approval status and implementation-card references. |

## Code-Vertrag (`app/`)

`docs/app/` is **not** knowledge content and is deliberately hidden from the Wiki
tree, folder view, search, pulse, and grading. It is the code-contract area:
its path and format only change together with the code that reads it.

| Folder | Contents |
|---|---|
| [`app/schemas/`](../app/schemas/README.md) | JSON schemas for wire and disk shapes (validated by the backend). |
| `app/help/` | Short Markdown help bodies served by the app next to non-obvious UI surfaces (`GET /api/concept-docs/{topic}`). |
| `app/config/` | Wiki configuration: the curated home (`home.json`) and saved category/document order (`wiki-order.json`). |
| [`app/templates/`](../app/templates/article-document-v2.html) | Embedded document templates consumed together with their backend rendering contracts. |

**Rule:** `app/` = Code-Vertrag — Pfad und Format nur zusammen mit Code ändern.
A guard test (`WikiPathCentralizationGuardTests`) keeps every hardcoded `docs/`
path either under `app/` or registered in `WikiProducerTargets`.

## Load-Bearing Entry Points

| Topic | Start here |
|---|---|
| Contribution and style conventions for all agents | [contribution-and-style-guide.html](contribution-and-style-guide.html) |
| Deployment regression scenario and report contract | [operations/testing/deployment-scenario.md](../operations/testing/deployment-scenario.md) |
| Runner | [domains/runner.md](../system/domains/runner.md) |
| Pipeline | [domains/pipeline.md](../system/domains/pipeline.md) |
| Quality Studio pipeline analysis policy | [pipeline domain](../system/domains/pipeline.md#quality-studio-analysis-steps) · [JSON schema](../app/schemas/quality-analysis-policy.schema.json) |
| Runtime prompt registry, review companions, call telemetry, and costs | [contracts/runtime-prompts.md](../system/contracts/runtime-prompts.md) |
| Tasks | [domains/tasks.md](../system/domains/tasks.md) |
| Frontend | [domains/frontend.md](../system/domains/frontend.md) |
| Stable view URL contract | [contracts/stable-view-urls.md](../system/contracts/stable-view-urls.md) |
| Global search palette and API | [domains/frontend.md#global-search](../system/domains/frontend.md#global-search) |
| Frontend navigation style guide | [frontend/style-guide/navigation.md](../quality/frontend/style-guide/navigation.md) |
| CLI | [domains/cli.md](../system/domains/cli.md) |
| Model routing policy (model, thinking level, risk floors, benchmark evidence) | [domains/model-routing-policy.md](../system/domains/model-routing-policy.md) |
| Tokens | [domains/tokens.md](../system/domains/tokens.md) |
| Remote execution outcome and recovery | [contracts/run-outcome.md](../system/contracts/run-outcome.md#remote-execution-outcome-adapter) |
| Remote infrastructure scenario result contract | [contracts/remote-run-result.md](../system/contracts/remote-run-result.md) |
| ADR archive | [architecture/decisions/adr-archive.md](../system/architecture/decisions/adr-archive.md) |
| Architecture model | [architecture/model.md](../system/architecture/model.md) |
| Architecture and Quality layer (Project Map, mapped guides, analysis inventory, component grading) | [concept](../concepts/architecture-quality-layer.md) · [interactive Dossier](../quality/architecture-quality-layer/index.html) |
| Token Economy Task Server evidence (field inventory, validity classes, controlled-comparison path; AGT-2293) | [interactive Dossier](../quality/token-economy-task-data/index.html) · [brief](../quality/token-economy-task-data/brief.md) |
| Pipeline time and token economy evidence | [interactive Dossier](../quality/pipeline-time-economy/index.html) · [brief](../quality/pipeline-time-economy/brief.md) |
| Reissue prompt sharpness and convergence evidence (AGT-2380) | [interactive Dossier](../quality/reissue-prompt-convergence/index.html) · [evidence snapshot](../quality/reissue-prompt-convergence/evidence-snapshot.json) · [analysis script](../quality/reissue-prompt-convergence/analyze.mjs) |
| Remote Run infrastructure testsuite report (acceptance matrix, phase timing, incident recovery, and raw evidence) | [2026-07-29 acceptance canary](../quality/remote-run-testsuite-report/2026-07-29/index.html) · [report workbench](../quality/remote-run-testsuite-report/index.html) · [validated fixture](../../tools/remote-test-suite/fixtures/report/validated-runs.json) · [schema](../../tools/remote-test-suite/run-result.schema.json) |
| Finding-first reissue prompt experiment | [design and predeclared analysis](../quality/pipeline-time-economy/reissue-prompt-experiment.md) · [current report](../quality/pipeline-time-economy/reissue-prompt-experiment-analysis.md) |
| Async validation and test staging lane proposal | [interactive Dossier](../operations/async-validation-staging-lane/index.html) · [brief](../operations/async-validation-staging-lane/brief.md) |
| Batch Gate decision dossier: one single-flight full suite for a closed delivery wave, with exact-SHA publication, per-member evidence, bounded red isolation, and promotion-train authority (AGT-2648) | [decision dossier](../operations/batch-gate-concept/index.html) |
| Rebase, merge, and bounce steering decision dossier: stage-specific Git policy through the attribution lens, exact candidate-SHA promotion, deterministic conflict requeue, guardian escalation, and Batch Gate interaction (AGT-2662) | [decision dossier](../operations/rebase-merge-and-steering/index.html) |
| Concept task pipeline and sight-review defaults | [interactive Dossier](../operations/concept-pipeline/index.html) |
| Article-style decision and evidence documents, including the full-bleed screenshot evidence standard | [authoring contract](../operations/article-document-authoring.md) · [copyable template](../app/templates/article-document-v2.html) |
| Board status model, lane-transition map, and integration/archive guard decisions (AGT-2424) | [interactive Dossier](../operations/board-statusmodell-ist-soll/index.html) |
| Deck icon alternatives for a multi-faceted project console (AGT-2355, Round 2) | [interactive Dossier](../operations/deck-icon-exploration/index.html) · [standalone SVG proof](../operations/deck-icon-exploration/results/deck-icon-options-round-2.svg) |
| Guided installer story for an all-Docker install on a fresh Linux or Windows machine, with per-step failure modes and a test protocol template (AGT-2503) | [interactive Dossier](../operations/installer-story/index.html) |
| Global Orchestrator Watcher decision dossier: triggers, recovery authority, Activity visibility, model economy, and observe-first slices (AGT-2557) | [decision dossier](../operations/orchestrator-waechter/index.html) |
| Organization-wide telemetry layer: current signals, shared event model, local-first hybrid bus, orchestrator action rules, Agent Studio operations-hub boundary, and rollout slices (AGT-2661) | [decision dossier](../operations/telemetry-layer/index.html) |
| Public demo instance decision dossier: pinned content, Dossier lifecycle gallery, execution denial, security, hosting, reset operations, and slices (AGT-2582) | [decision dossier](../operations/demo-instanz/index.html) · [replay-only Runner](../operations/demo-instanz/replay-only-runner.md) |
| Context-aware Orchestrator Chats as the project workspace, with Task Chat boundaries, token budgets, UX sketches, and six linked delivery slices (AGT-2514) | [decision dossier](../operations/kontext-orchestrator-chats/index.html) |
| Admin surface design guideline: flat composition, compact disclosure, one stream grid, light/dark Activity reference, and incremental adoption (AGT-2583) | [decision dossier](../operations/admin-design-guideline/index.html) · [visual dos and don'ts](../operations/admin-design-guideline/pages/dos-and-donts.html) |
| Escalated task-detail information architecture: blocker-first header, one owner per failure fact, retained failed-step attention, and flat Result rows (AGT-2643) | [decision dossier](../operations/escalation-view-information-architecture/index.html) |
| Task Timeline redesign: event categories, copyable explanatory technical popovers, recoverable incident history, bounded payload disclosure, newest-first navigation, and light/dark AGT-2577 mockups (AGT-2631) | [decision dossier](../operations/timeline-redesign/index.html) |
| Accepted naming decision for the living decision-to-documentation artifact: Dossier remains the product noun, “living dossier” is explanatory copy, Living Dossier is the runner-up, and consequence delivery remains tracked to documented (AGT-W33) | [decision dossier](../operations/living-document-naming/index.html) |
| Missing generated status.md on Concept and Planning cards: scaffold inventory, root-cause chain, and repair decision (AGT-2555) | [decision dossier](../operations/statusmd-konzept-karten/index.html) |
| GPT quota and retry-tax analysis for the 11 to 12 August wave: exact token ledger, review incidents, session continuation, control coverage, and decision-ready savings (AGT-2652) | [decision dossier](../operations/token-retry-tax-analysis/index.html) |
| Managed project manifest map | [architecture/project-map.md](../system/architecture/project-map.md) |
| Agent message bus | [architecture/bus/agent-message-bus.md](../system/architecture/bus/agent-message-bus.md) |
| Design hard rules (prompt-known) | [design/style-guide-hard-rules.md](../quality/design/style-guide-hard-rules.md) |
| Living frontend styling rules and compact prompt context | [living rules](../quality/frontend/style-guide/living-rules.md) · [styling context](../quality/frontend-styling.md) |
| Project Deck visual audit and Deck-Panel v1 recommendation | [interactive Deck Audit](../concepts/visual-style-guide/deck-audit.html) · [implementation contract](../concepts/visual-style-guide/deck-panel-v1.md) |
| Model and thinking-level indicator vocabulary | [design/model-level-indicator.md](../quality/design/model-level-indicator.md) |
| Engineering style guides (technology-aware and prompt-injected) | [quality/README.md](../quality/README.md) |
| Application visual survey (2026-07-11) | [design/app-survey-2026-07-11.html](../quality/design/app-survey-2026-07-11.html) |
| Angular performance review (2026-07) | [design/angular-performance-report-2026-07.html](../quality/design/angular-performance-report-2026-07.html) |
| Explorer project state indicator exploration (2026-07) | [design/tree-indicator-exploration-2026-07.html](../quality/design/tree-indicator-exploration-2026-07.html) and [alternatives catalog](../concepts/tree-project-indicator-alternatives.md) |
| Visual Style Guide Dossier (current tokens and live patterns, Empty State / Mini Indicator / Runner Card comparisons, and vNext decisions) | [visual Style Guide](../concepts/visual-style-guide.html) · [Dossier folder](../concepts/visual-style-guide/README.md) · [Empty States](../concepts/visual-style-guide/empty-states.html) · [Mini Indicators](../concepts/visual-style-guide/mini-indicators.html) · [Runner Cards](../concepts/visual-style-guide/runner-cards.html) · [vNext](../concepts/visual-style-guide/vnext.md) |
| Narrative task view (proactive PRE/POST pipeline generation, versioned cache, fixed AHP report template, measured cost model) | [interactive Dossier](../quality/design/narrative-task-view/index.html) |
| UX doctrine | [product/design-principles.md](../quality/design-principles.md) |
| Persistent orchestrator chat and session-turn API | [product/orchestrator-chat.md](../concepts/orchestrator-chat.md) |
| In-app orchestrator sight, tools, and anchor slices | [concepts/orchestrator-in-app.md](../concepts/orchestrator-in-app.md) |
| Wiki Pulse dashboard (change feed + inbox + drift grading; PULSE-1) | [concepts/wiki-pulse-dashboard.md](../concepts/wiki-pulse-dashboard.md) |
| Wiki as a cognitive interface (AIP-4 page backchannel, page context, archive semantics) | [concept](../concepts/wiki-as-cognitive-interface.md) · [Visual StyleGuide Dossier](../quality/visual-styleguide-workbench-wiki/index.html) |
| Wiki grading run (global LLM grade per page; trigger + critical pages; GRADE-1) | [concepts/wiki-grading-run.md](../concepts/wiki-grading-run.md) |
| Run-liveness & slot semantics (heartbeat, process-lost demotion) | [concepts/run-liveness-and-slot-semantics.md](../concepts/run-liveness-and-slot-semantics.md) |
| UI task iteration pipeline and Human Gate hand-off | [contracts/ui-task-pipeline.md](../system/contracts/ui-task-pipeline.md) |
| Operator decision surface for escalated tasks | [operations/decision-surface/README.md](../operations/decision-surface/README.md) |
| Parked-card recall (machine-readable blocker, Wiedervorlage sweep, lane aging; AGT-2492) | [concepts/parked-card-recall.md](../concepts/parked-card-recall.md) |
| Per-project cycle time by pipeline stage and lane transitions (queue wait, coding, gate, review, integration, human review; transition matrix and bounce causes; `GET /api/projects/{project}/cycle-time`) | [concepts/cycle-time-stage-model.md](../concepts/cycle-time-stage-model.md) |
| Result view and case templates | [concepts/result-view-and-case-templates.md](../concepts/result-view-and-case-templates.md) |
| Publishing workflows (publish-target derivation + pending badges; PUB-1) | [concepts/publishing-workflows.md](../concepts/publishing-workflows.md) |
| Git-info performance measurements and cache invalidation (AGT-2007) | [reports/git-info-performance-agt-2007.md](../system/reports/git-info-performance-agt-2007.md) |
| Planning-task lifecycle (plan → spawn → accept; spawn-contract gate; AGT-2069) | [concepts/planning-task-lifecycle.md](../concepts/planning-task-lifecycle.md) |
| Experiment workbenches (Explorer, orchestrator chat, decision-to-task) | [concept](../concepts/experimentier-workbench.md) · [interactive mockup](../concepts/mockups/experimentier-workbench.html) |
| Decoupled agent-session lifecycles (holder, consumer channel, multi-client attach) | [concept](../concepts/decoupled-lifecycles.md) · [interactive Dossier-family mockup](../concepts/mockups/decoupled-lifecycles.html) |
| Distributed Agent Studio target architecture (Studio, Task Server, Runner, security, lifecycle, and component projects) | [concepts/distributed-agent-studio-target-architecture.md](../concepts/distributed-agent-studio-target-architecture.md) |
| Distributed runtime packaging and installation decisions | [distributable concept](../operations/haertung-verteilte-ausfuehrung/target-architecture/distributable.html) |
| Remote gate target architecture (claimable gate steps vs Review Plane vs capability-only dispatch; AGT-2369) | [operations/remote-gate-zielbild/](../operations/remote-gate-zielbild/index.html) |
| OSS setup path decision and clean-host evidence | [setup path decision](../operations/haertung-verteilte-ausfuehrung/target-architecture/setup-scenarios.md) |
| Release assets, version matrix, install, update, rollback, and honest CI | [operations/releases.md](../operations/releases.md) |
| Develop to main promotion train, full gate, release marker, and deploy cron | [operations/develop-main-promotion.md](../operations/develop-main-promotion.md) |
| Orchestrator control-plane migration plan | [target architecture plan](../operations/haertung-verteilte-ausfuehrung/target-architecture/orchestrator-plan.md) |
| Wiki hosting options and recommendation (full networked Studio vs read-only service vs static export; AGT-2276) | [concepts/wiki-hosting-options-2026-07.html](../concepts/wiki-hosting-options-2026-07.html) |
| Deployment as a first-class citizen (Deployment page, scenario templates, prompt-defined dynamic UI over CLI tasks; DEP-1..5) | [concept](../concepts/deployment-first-class.md) · [interactive mockup](../concepts/mockups/deployment-first-class.html) |
| Release semantics (integration vs acceptance vs release vs stable freeze; transparent watering-can model) | [concept](../concepts/release-semantics.md) |
| Project Overview operator dashboard (metrics, Visual Evidence Queue, Project URLs, deployment summary, Wiki entry) | [mockup contract](../concepts/project-overview-dashboard/README.md) · [interactive mockup](../concepts/project-overview-dashboard/ui.html) |
| Studio route restoration (Board, Hub, Wiki pages, Dossiers, Task tabs, Epics, Settings) | [concept and route map](../concepts/studio-route-restoration.md) · [ownership diagram](../concepts/studio-route-restoration-diagram.html) |
| Wiki classification | [product/wiki-document-classification.md](./wiki-document-classification.md) |
| Wiki editing flow | [product/wiki-editing-and-branch-flow.md](./wiki-editing-and-branch-flow.md) |
| Wiki document companion schema | [schemas/wiki-document-companion.schema.json](../app/schemas/wiki-document-companion.schema.json) |
| Wiki page lifecycle schema | [schemas/wiki-page-lifecycle.schema.json](../app/schemas/wiki-page-lifecycle.schema.json) |
| Model qualification benchmark event schema | [schemas/model-qualification-event.schema.json](../app/schemas/model-qualification-event.schema.json) |
| Model escalation and companion routing concept | [concepts/model-escalation-and-companion-routing.md](../concepts/model-escalation-and-companion-routing.md) |
| Supported CLIs | [cli/supported-clis.md](../system/cli/supported-clis.md) |
| CLI frame compatibility and capture corpus | [cli/frame-compatibility-matrix.md](../system/cli/frame-compatibility-matrix.md) |
| Getting started (new install, step by step) | [operations/setup/getting-started.md](../operations/setup/getting-started.md) |
| Contributor source-build setup | [operations/setup/contributor-setup.md](../operations/setup/contributor-setup.md) |
| GitHub repository metadata recommendations | [repo-metadata.md](../repo-metadata.md) |
| Setup | [operations/setup/README.md](../operations/setup/README.md) |
| Standalone remote runner / agent host daemon (Linux) | [operations/setup/linux-runner-host.md](../operations/setup/linux-runner-host.md) |
| Guided multi-machine setup (Control Plane, join token, Agent Hosts) | [operations/setup/multi-machine.md](../operations/setup/multi-machine.md) |
| Website onboarding source copy (Demo, Single Machine, Multi Machine) | [operations/setup/website-onboarding-template.md](../operations/setup/website-onboarding-template.md) |
| Runner-host resource governance (Linux cgroups, coding/review role defaults, AIMD capacity boundary) | [target architecture](../operations/haertung-verteilte-ausfuehrung/target-architecture/resource-governance.md) |
| Execution hosts operator lifecycle | [operations/remote-hosts.md](../operations/remote-hosts.md) |
| Remote runner persistent connection (tunnel-as-a-service + health-check) | [operations/setup/remote-runner-persistent-connection.md](../operations/setup/remote-runner-persistent-connection.md) |
| Runner link as part of the application (dossier: link ownership, LinkSupervisor design, tunnel retirement with the remote Task Server) | [operations/runner-link/](../operations/runner-link/index.html) |
| Remote three-unit Compose infrastructure harness | [operations/setup/remote-compose-test-harness.md](../operations/setup/remote-compose-test-harness.md) |
| Private Hetzner Task Server with local Angular Studio (Phase A architecture, migration, security, and rollback) | [operations/remote-task-server-local-studio.md](../operations/remote-task-server-local-studio.md) |
| Common problems | [common-problems/README.md](../operations/common-problems/README.md) |
| Designated topics (AGENTS/wiki-sync current-state index; post-agents-wiki-sync) | [concepts/designated-topics/README.md](../concepts/designated-topics/README.md) |
| API project identity / watchPath | [concepts/api-project-identity-and-watchpath.md](../concepts/api-project-identity-and-watchpath.md) |
| Workflow arguments become unbounded fan-out | [common-problems/workflow-args-json-string-fanout/](../operations/common-problems/workflow-args-json-string-fanout/) |
| Workflow-sized task cutting and Dossier slice acceptance | [operations/workflow-sized-task-cutting.md](../operations/workflow-sized-task-cutting.md) |
| Services killed by a harness sweep | [common-problems/services-killed-by-harness-sweep/](../operations/common-problems/services-killed-by-harness-sweep/) |
| api.sh restart reports success while the old backend keeps serving | [common-problems/hollow-api-restart/](../operations/common-problems/hollow-api-restart/) |
| Integration push blocked on main lineage starves origin/develop, causing infinite reclaim loops (AGT-2688) | [common-problems/lineage-blocked-integration-push/](../operations/common-problems/lineage-blocked-integration-push/) |
| Orchestrator drive-to-conclusion & CLI-crash resilience | [concepts/orchestrator-drive-to-conclusion.html](../concepts/orchestrator-drive-to-conclusion.html) |
| Task integration & worktree/merge workflow | [concepts/task-integration-and-merge-workflow.md](../concepts/task-integration-and-merge-workflow.md) |
| Merge config analysis (parallelism coupling) | [concepts/task-integration-merge-config-analysis.html](../concepts/task-integration-merge-config-analysis.html) |
| Auto-review reissue / evidence-gate analysis | [concepts/auto-review-evidence-gate-analysis.html](../concepts/auto-review-evidence-gate-analysis.html) |
| Completion, review, runner provenance, host handoff, and Remote Runner stability | [concepts/completion-review-and-remote-runner-stability.html](../concepts/completion-review-and-remote-runner-stability.html) |
| Planning & Research task type | [concepts/planning-research-task-type.html](../concepts/planning-research-task-type.html) |
| AGT-2380 reissue-prompt convergence research | [research/reissue-prompt-konvergenz.html](../research/reissue-prompt-konvergenz.html) |
| MVP presentation screen-tooling evaluation and recommendation | [research/screen-tooling-mvp-presentation-2026-07.md](../concepts/screen-tooling-mvp-presentation-2026-07.md) |
| MVP presentation capture runbook | [operations/setup/presentation-capture.md](../operations/setup/presentation-capture.md) |
| MVP presentation storyboard and shot list | [product/mvp-presentation-storyboard.md](../concepts/mvp-presentation-storyboard.md) |
| Quota snapshot events at run start/end (cap-forecast data collection) | [concepts/quota-snapshot-run-events.md](../concepts/quota-snapshot-run-events.md) |
| Runtime prompt usage audit | [concepts/runtime-prompt-usage-audit.html](../concepts/runtime-prompt-usage-audit.html) |
| Admin CLI onboarding | [concepts/admin-cli-onboarding.html](../concepts/admin-cli-onboarding.html) |
| Orchestrator supervision loop | [concepts/orchestrator-supervision-loop.html](../concepts/orchestrator-supervision-loop.html) |
| Runner stability & incident chronicle (incidents, invariants, sessions; supersedes the retired runner-stability / overnight / claude-termination pages) | [workbenches/haertung-verteilte-ausfuehrung/historie.html](../operations/haertung-verteilte-ausfuehrung/historie.html) |
| Agent fencing (platform-owned Git history, trust-graded oversight, real incidents, open proof point) | [operations/haertung-verteilte-ausfuehrung/agent-fencing.html](../operations/haertung-verteilte-ausfuehrung/agent-fencing.html) · [separate AHP-style diagram](../operations/haertung-verteilte-ausfuehrung/agent-fencing-diagram.html) |
| Process termination & abort scenarios (test suite) | [concepts/process-termination-scenarios.html](../concepts/process-termination-scenarios.html) |
| Documentation inventory and cleanup (July 2026) | [inventory](../operations/doku-inventur-2026-07/README.md) · [archive convention](../archive/README.md) |
| Wiki consolidation analysis 2026-07-18 (inventory, redundancy clusters, cleanup plan) | [konsolidierung-analyse-2026-07-18.md](konsolidierung-analyse-2026-07-18.md) |
| Research task delivery convention (research cards deliver exactly one primary HTML document at `results/report.html`) | [operations/research-deliverables/index.html](../operations/research-deliverables/index.html) |

## Organization Rules

- Keep source-of-truth contracts in `domains/`, `contracts/`, or
  `architecture/`.
- Keep dated evidence in `system/reports/`, `quality/frontend/audits/`, or
  `system/cli/audits/`; dated deep dives live under `concepts/`.
- Keep explanatory and evolving notes in `concepts/`, recurring incident
  patterns in `common-problems/`, and auto-distilled run learnings in
  `learnings/` (wiki conventions: [wiki-conventions.md](wiki-conventions.md)).
- Treat `app/` as a code contract, not knowledge: its path and format change only
  together with code. Never file a knowledge page under `app/`, and never move a
  schema, help body, or config out of it without the matching code change.
- Use Markdown by default.
- Use HTML only for visual or spatial pages that need layout, such as maps or
  reports. HTML pages must be self-contained and readable in the Wiki iframe.
- When adding a new document, put it in its real category and add it to the
  nearest category index.
