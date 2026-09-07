# Concept and Knowledge Pages

## Zweck & Abgrenzung

Hand-gepflegte, lebende Erklär- und Wissens-Seiten: das *Warum*, *Was* und *Wie*
eines Bereichs, plus laufendes Wissenslog, dazu datierte Deep-Dives, Mockups und
Proposals.

**Gehört hierher:** Architektur-Konzepte vor der ADR-/Domänen-Reife, lebende
Wissens-Seiten, Mockups (`mockups/`), Proposals (`proposals/`) und die
generierten Designated-Topic-Seiten (`designated-topics/`).

**Gehört nicht hierher:** verbindliche Systemverträge und Domänenkarten (→
`system/`), Betriebs-/Setup-Wissen (→ `operations/`), Qualitäts- und Style-Guides
(→ `quality/`). Code-Verträge (Schemas, Config, In-App-Hilfe) liegen unter `app/`.

> Since 2026-07-18 this folder also holds the architecture concept pages that
> previously sat directly in `docs/concepts/` while the living knowledge pages
> lived in `docs/wiki/concepts/`; the two sets were merged when the `docs/wiki/`
> subfolder was dissolved.

Hand-maintained, living explainer pages for a domain area. Unlike
[`common-problems/`](../operations/common-problems/) (one incident pattern per folder) and
[`learnings/`](../operations/learnings/) (auto-distilled per-task pages, do not hand-edit),
pages here are durable concept write-ups that accumulate knowledge over time.

Each page explains *why* a design exists, *what* the moving parts are, and *how*
to work with the area, in language aimed at both operators and any LLM instance
picking up a task in that domain. Every page ends with a **Living knowledge
log** section: append new findings there (newest on top) rather than letting
hard-won context evaporate into commit messages.

These pages are the conceptual companion to the system-of-record domain docs in
`docs/` (linked from each page). The domain doc owns the plan and the current
contract; the concept page owns the explanation and the running knowledge log.

## Pages

| Page | Area | System-of-record doc |
|---|---|---|
| [completion-review-and-remote-runner-stability.html](completion-review-and-remote-runner-stability.html) | Living umbrella analysis for semantic completion, exact-revision Auto Review, runner/host provenance, controlled cross-host continuation, build/test and visual-evidence gates, retry identity, CLI aborts, and parallel Remote Host stability. | [`docs/system/domains/runner.md`](../system/domains/runner.md), [`docs/system/domains/pipeline.md`](../system/domains/pipeline.md), [`docs/system/contracts/run-outcome.md`](../system/contracts/run-outcome.md) |
| [cycle-time-stage-model.md](cycle-time-stage-model.md) | Per-project cycle-time view: the additive stage model (queue wait, coding, post-processing wait, build/test gate, review aspects, integration, human review), which ledger and pipeline timestamps feed each stage, occurrence-based aggregation, and the known recording gaps. | [`docs/system/domains/tasks.md`](../system/domains/tasks.md), [`docs/system/contracts/filesystem.md`](../system/contracts/filesystem.md) |
| [docs-structure-migration.md](docs-structure-migration.md) | Archive pointer for the completed June/July structure migration. The historical record is retained under `docs/archive/`; it is not a current organization guide. | [`docs/system/contracts/wiki-tree.md`](../system/contracts/wiki-tree.md) |
| [model-escalation-and-companion-routing.md](model-escalation-and-companion-routing.md) | Class-scoped recommendation, economics, pipeline contract, trigger policy, rollout gates, and follow-up slices for stronger-model reissue and companion roles. | [`docs/system/domains/pipeline.md`](../system/domains/pipeline.md), [`docs/system/domains/cli.md`](../system/domains/cli.md), [`docs/system/contracts/run-outcome.md`](../system/contracts/run-outcome.md) |
| [token-aggregation.md](token-aggregation.md) | Token aggregators -> bus-backed shims (ASS-881): one canonical `ITokenAggregator` over the Agent Message Bus, the legacy shims, and the architecture guard test. | [`docs/system/domains/tokens.md`](../system/domains/tokens.md) |
| [tree-project-indicator-alternatives.md](tree-project-indicator-alternatives.md) | Eight alternatives and recommendation for a project-level Explorer state indicator that shows situation instead of a total. | [`docs/quality/design/tree-indicator-exploration-2026-07.html`](../quality/design/tree-indicator-exploration-2026-07.html) |
| [wiki-hosting-options-2026-07.html](wiki-hosting-options-2026-07.html) | Decision memo for hosting the live Wiki: weighted comparison of full networked Studio, a read-only Wiki service, and static export, with the recommended control-plane topology and follow-up cards. | [`docs/system/contracts/wiki-tree.md`](../system/contracts/wiki-tree.md), [`docs/operations/setup/networked-task-server.md`](../operations/setup/networked-task-server.md) |
| [studio-route-restoration.md](studio-route-restoration.md) | Canonical Studio URL contract, full surface route map, hydration precedence, transient-state boundary, and verification matrix. | [`docs/system/domains/frontend.md`](../system/domains/frontend.md) |
| [platform-architecture/README.md](platform-architecture/README.md) | Index of the durable execution and delivery architecture, mostly extracted from decision dossiers: fencing and leases, integration invariants, Task Server topology, Batch Gate, telemetry layer, Remote Gate. Each page links back to its source dossier. | [`docs/system/domains/runner.md`](../system/domains/runner.md), [`docs/system/domains/tasks.md`](../system/domains/tasks.md) |
| [visual-style-guide.html](visual-style-guide.html) | Living visual Style Guide Dossier: rendered current-state tokens and patterns, focused comparison pages, and vNext candidate decisions. | [`docs/quality/frontend/style-guide/`](../quality/frontend/style-guide/README.md), [`docs/quality/design/style-guide-hard-rules.md`](../quality/design/style-guide-hard-rules.md) |
| [visual-style-guide/deck-audit.html](visual-style-guide/deck-audit.html) | Screenshot-backed audit of the 23 July Project Deck sections, interactive panel-style configurator, and Deck-Panel v1 convergence recommendation. | [`docs/quality/frontend/style-guide/README.md`](../quality/frontend/style-guide/README.md), [`docs/quality/design/style-guide-hard-rules.md`](../quality/design/style-guide-hard-rules.md) |

## Designated topics (AGENTS/wiki-sync)

[`designated-topics/`](designated-topics/README.md) holds the machine-maintained
"Current State / Progress" pages for a set of designated topics, kept fresh by the
opt-in `post-agents-wiki-sync` pipeline step so agents read the current state of a
topic instead of re-discovering it. The operator-owned topic list is
[`designated-topics/registry.json`](designated-topics/registry.json); each entry
pins an AGENTS-surface pointer to one of the concept pages below plus a
`<slug>.md` state page. Unlike the concept pages, the state pages and the
`designated-topics/README.md` index are generated - do not hand-edit them.
