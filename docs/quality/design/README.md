# Design

Product-wide, prompt-known design hard rules. These are the rules that every
coding card touching the UI is expected to already know, so they live at the top
level and are referenced from `AGENTS.md`.

The single cross-technology navigation entry is the
[engineering style-guide family](../README.md). This folder owns the
short visual baseline that the Angular guide incorporates.

| Page | What lives there |
|---|---|
| [style-guide-hard-rules.md](style-guide-hard-rules.md) | The hard, non-negotiable design rules (no left accent bars, full-bleed views, aggregate = sum of visible children, acute-only signals, both themes). |
| [app-survey-2026-07-11.html](app-survey-2026-07-11.html) | Self-contained, screenshot-by-screenshot visual findings for the 2026-07-11 Stable application sweep. |
| [angular-performance-report-2026-07.html](angular-performance-report-2026-07.html) | Measured Angular 21 performance and best-practice review, focused on the 123-card Human Review lane observed on Stable. |
| [tree-indicator-exploration-2026-07.html](tree-indicator-exploration-2026-07.html) | Interactive light/dark Explorer mockup comparing eight project-level state indicators. |
| [status-indicator.md](status-indicator.md) | Shared `<app-status-indicator>`: six tones with their icons and tokens, the state-to-tone mapping, and when a tag, count, lane or banner is meant instead. |
| [status-display-inventory.md](status-display-inventory.md) | Every other status display in the app by surface, with its tone mapping and whether it moves to the shared indicator (AGT-3402 follow-up input). |
| [model-level-indicator.md](model-level-indicator.md) | Shared model-family colours, abbreviations, thinking-level codes, and tooltip contract for taskboard and coding-agent-chat indicators. |
| [narrative-task-view/index.html](narrative-task-view/index.html) | Concept Dossier for proactive PRE/POST narrative pipeline steps, a versioned material-state cache, fixed AHP presentation, real-card mock reports, and measured per-card economics. |

The concrete component vocabulary (tokens, primitives, canonical components) and
the "why" behind the shell look live under
[frontend/design-system.md](../frontend/design-system.md),
[frontend/style-guide/](../frontend/style-guide/README.md), and
[product/design-principles.md](../design-principles.md). This folder is
only the short, hard-rule layer above them.
