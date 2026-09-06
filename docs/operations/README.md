# Operations

Operator-facing setup, runtime, security, and git workflow docs.

## Zweck & Abgrenzung

Betriebswissen für Operatoren: wie das System aufgesetzt, betrieben, abgesichert
und per Git bewegt wird.

**Gehört hierher:** Setup/Onboarding, Runtime- und Observability-Verträge,
Sicherheitsarchiv, Git-Doktrin, Test-Workspaces, Remote-Host-Lebenszyklus sowie
die generierten Betriebs-Seiten (`common-problems/`, `learnings/`).

**Gehört nicht hierher:** erklärende Konzepte und Designentscheidungen (→
`concepts/`), Systemverträge und Domänenkarten (→ `system/`), Qualitäts- und
Style-Guides (→ `quality/`). Code-Verträge (Schemas, Config, In-App-Hilfe) liegen
unter `app/` und werden nur zusammen mit Code geändert.

| Area | Contents |
|---|---|
| [setup/](setup/README.md) | Project onboarding, CLI onboarding, first task, troubleshooting, and worktree stack. |
| [security/](security/overview.md) | Security overview, requirements, state, and reviews. |
| [runtime/](runtime/) | Product runtime observability and log-capture contracts. |
| [git/](git/) | Commit, push, and attribution doctrine. |
| [workspace-repository-lifecycle.md](workspace-repository-lifecycle.md) | TaskRepository path classification, hourly drift sweep, catch-up push, Git maintenance, size guard, and manual backlog recovery. |
| [testing/](testing/) | Dedicated test workspace and probe contracts; Windows test baseline and platform gates. |
| [doku-inventur-2026-07/](doku-inventur-2026-07/README.md) | Per-document July 2026 inventory, sampled code checks, archive decisions, and the Phase 2 structure sketch. |
| [haertung-verteilte-ausfuehrung/](haertung-verteilte-ausfuehrung/index.html) | Distributed execution hardening, including runner incidents, invariants, and implementation history. |
| [lagebild-2026-08/](lagebild-2026-08/index.html) | Dated snapshot (03.08.2026) of where the system stands: component topology and hosts, CAR execution-layer status, card distribution across lanes, and open themes with card references. |
| [decision-surface/](decision-surface/README.md) | Ownership, artifact, action, and lifecycle contract for operator decisions on escalated tasks. |
| [board-statusmodell-ist-soll/](board-statusmodell-ist-soll/index.html) | German decision Dossier mapping every board lane transition and proposing integration, acceptance, and archive guards (AGT-2424, with AGT-2301 field evidence). |
| [research-deliverables/](research-deliverables/index.html) | Primary HTML report, companion-link, prompt-block, and lightweight-pipeline convention for Research tasks. |
| [workflow-sized-task-cutting.md](workflow-sized-task-cutting.md) | One-delivery card-cutting rules, bounded acceptance-scope contract, Dossier slice convention, and repeated-review escalation behavior. |
| [remote-hosts.md](remote-hosts.md) | Add, connect, drain, retire, revive, and permanently remove remote runner hosts. |
| [remote-task-server-local-studio.md](remote-task-server-local-studio.md) | Phase A architecture, security, migration, and sub-15-minute rollback plan for a private Hetzner Task Server with Robert's Angular Studio kept local. |
| [releases.md](releases.md) | Tag-driven release assets, component version matrix, guided install, drain-first update, health-gated auto-rollback, and honest CI contract. |
| [url-preview-diagnostics.md](url-preview-diagnostics.md) | URL Preview diagnosis classes, bounded evidence, recovery actions, and Project Settings quick setup. |
| [stable-release-contract.md](stable-release-contract.md) | Immutable Stable tags, build manifests, preflight comparison, rollback identity, and legacy migration. |
| [develop-main-promotion.md](develop-main-promotion.md) | Operator checklist and command for exact-SHA `develop` to `main` promotion, full gate, annotated release marker, and deploy-cron handoff. |
| [batch-gate-concept/](batch-gate-concept/index.html) | Decision dossier for batching eligible pending deliveries onto one fenced candidate, running one single-flight full suite, publishing exact-SHA results with per-member evidence, and isolating red members without racing the promotion train (AGT-2648). |
| [rebase-merge-and-steering/](rebase-merge-and-steering/index.html) | Decision dossier on stage-specific rebase versus merge through the attribution lens, exact candidate-SHA promotion, current bounce-steer practice, deterministic backend automation, strong guardian escalation, and Batch Gate interaction (AGT-2662). |
| [installer-story/](installer-story/index.html) | German concept Dossier for the guided all-Docker install on a fresh Linux or Windows machine: component matrix, per-step flow with failure modes, Compose and secrets state, test protocol template, and the cut into a Linux run, a Windows run, and the installer implementation (AGT-2503). |
| [orchestrator-waechter/](orchestrator-waechter/index.html) | Decision dossier for the global Orchestrator Watcher: trigger catalogue, inspect-rescue-analyze-report ladder, Activity visibility, model economy, authority boundaries, operating model, and recommended slices (AGT-2557). |
| [telemetry-layer/](telemetry-layer/index.html) | Decision dossier for a local-first telemetry layer across organization applications: current-state inventory, shared event model and privacy posture, hybrid file-buffer plus local-push bus, bounded orchestrator actions, Agent Studio operations-hub positioning, and a Coding Agent Chat pilot (AGT-2661). |
| [kontext-orchestrator-chats/](kontext-orchestrator-chats/index.html) | Decision dossier for context-aware project and task chats, including current evidence, context ownership, token budgets, UX sketches, and five delivery slices (AGT-2514). |
| [admin-design-guideline/](admin-design-guideline/index.html) | Decision-pending design guideline for flat, dense admin surfaces, with a light/dark Activity across projects reference, violation audit, and incremental adoption contract (AGT-2583). |
| [escalation-view-information-architecture/](escalation-view-information-architecture/index.html) | Decision-ready information architecture for escalated task details: the header owns the blocker, duplicate Pipeline and Result outcomes are removed, failed-step attention stays, and Result outcome rows become flat (AGT-2643). |
| [timeline-redesign/](timeline-redesign/index.html) | Decision-ready redesign for the Task Timeline and shared Activity presentation: event categories, incident recovery, explanatory technical popovers, bounded payload disclosure, newest-first navigation, light/dark AGT-2577 mockups, and implementation slices (AGT-2631). |
| [statusmd-konzept-karten/](statusmd-konzept-karten/index.html) | Decision dossier on missing generated `status.md` for Concept and Planning cards: complete scaffold inventory, remote V1 finalization root cause, reissue evidence, and decision-ready repair options (AGT-2555). |
| [article-document-authoring.md](article-document-authoring.md) | Authoring contract for the canonical article template, `ui` and `concept` patterns, full-bleed screenshot evidence, capture dates and provenance, and touch-only migration. |
| [demo-instanz/](demo-instanz/index.html) | Decision dossier for a public, read-only demo instance with pinned mock data, a full Dossier lifecycle gallery, a real Task Server, a replay-only Runner, hard execution denial, scrub proof, isolated hosting, reset operations, and implementation slices (AGT-2582). |
| [demo-instanz/replay-only-runner.md](demo-instanz/replay-only-runner.md) | Operator contract for slice S3: the signed replay trace schema, the sealing tool, the narrow `demo.replay` server scope and its typed denials, the claim-free service image, the Simulated labelling rule, and the compromise proofs (AGT-2668). |

Konvergenz-Probe 2026-07-30: develop ist der Arbeitsbranch.
| [deck-icon-exploration/](deck-icon-exploration/index.html) | Round 2 Deck icon alternatives for the multi-faceted project console, with light and dark proofs, recommendation, rejected Round 1 direction, and implementation seam (AGT-2355). |
