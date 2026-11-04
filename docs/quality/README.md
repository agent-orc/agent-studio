# Engineering style guides

## Zweck & Abgrenzung

Qualitäts- und Designwissen: technologie-bewusste Style-Guides, verbindliche
Design-Hartregeln und der Regel-Autoren-Workflow, die in Prompts und Reviews
einfließen.

**Gehört hierher:** Angular-/.NET-Style-Guides, `design/`-Hartregeln,
Frontend-Designsystem und Audits, Architektur-/Qualitäts-Layer, Prinzipien.

**Gehört nicht hierher:** verbindliche Systemverträge und Domänenkarten (→
`system/`), erklärende Konzepte (→ `concepts/`), Betriebswissen (→
`operations/`). Code-Verträge (Schemas, Config, In-App-Hilfe) liegen unter
`app/`.

This folder is the single navigation entry for repository coding guidance that
depends on a project's technology stack. The Deck reads the frontmatter
from this family, shows only applicable guides in Wiki Pulse, and the existing
Preparation intake selector adds those same guides to coding-task prompt
context.

The family does not replace narrower sources of truth. It points to them and
turns their most important, evidenced rules into a small technology-specific
entry surface.

## Guide family

| Guide | Applies when | Canonical sources it incorporates |
|---|---|---|
| [Angular components](angular-components.md) | Angular is detected | [UI hard rules](./design/style-guide-hard-rules.md), [component vocabulary](./frontend/style-guide/README.md), [performance playbook](./frontend/performance.md) |
| [Frontend styling context](frontend-styling.md) | Angular + frontend/styling task area are detected | [living styling rules](./frontend/style-guide/living-rules.md), [button vocabulary](./frontend/style-guide/buttons.md), [Deck-Panel v1](../concepts/visual-style-guide/deck-panel-v1.md) |
| [Studio header zones](header-zones.md) | Angular + header task area are detected | [header slot and relevance guide](./frontend/style-guide/header.md) |
| [.NET backend](dotnet-backend.md) | .NET or C# is detected | [backend structure style guide](../system/architecture/backend-structure/styleguide.md), [domain maps](../system/domains/README.md) |

The [UI hard rules](./design/style-guide-hard-rules.md) remain the
non-negotiable visual baseline. This index is the one place from which a human,
Deck, and prompt enrichment discover that baseline together with the
technology-specific guidance around it.

## Styling-card creation

When a card or prompt changes layout, spacing, typography, color, panels,
buttons, labels, or another visual treatment, describe that scope explicitly
with terms such as `styling`, `SCSS`, `UI`, or `button`. Preparation detects the
`frontend` task area and appends the
[Frontend styling context](frontend-styling.md) through the existing
`intake/enriched-context.md` style-guide mechanism. For a manually assembled
prompt that bypasses Preparation, append
`Context: docs/quality/frontend-styling.md`.

The card must extend the living guide or its Open cases when it discovers a
reusable styling decision that the guide does not cover. A local exception is
not a substitute for that update.

## Applicability contract

Every selectable guide has Markdown frontmatter with a stable id and one
machine-readable `appliesTo` object:

```yaml
---
styleGuideId: angular-components
title: Angular component guide
version: 1
summary: Short Deck description.
promptSummary: Bounded rules copied into task prompt context.
appliesTo: {"projects":["*"],"technologies":["angular"],"taskAreas":["frontend"]}
---
```

- `projects` accepts `*`, the stable `ProjectRecord.Id` key such as
  `PROJ-0042`, or the current project short code as a selector alias. A display
  name is presentation only and never identifies or matches a project.
- `technologies` accepts the canonical keys `angular`, `dotnet`, and `csharp`
  (or an explicit `*`). The API returns their separate display labels:
  `Angular`, `.NET`, and `C#`.
- `taskAreas` is an additional Preparation intake filter. `*` explicitly
  matches any task area, including a general coding task; an empty list matches
  nothing. It does not add a second prompt builder.
- A project match and at least one technology match are required. Within each
  list, matching uses OR semantics. Empty lists match nothing; global scope
  must be explicit with `*`.

Discovery is deliberately bounded: at most 64 top-level Markdown files under
`docs/quality` are inspected in deterministic path order, each guide is capped
at 32 KiB, and technology detection rejects symbolic or reparse paths. Invalid,
duplicate, or oversized declarations are excluded with a relative-path warning.
The API never returns a repository root.

The catalogue is a thread-safe five-minute snapshot shared by the API and
Preparation intake. Responses include `snapshotId`, `capturedAtUtc`, and
`refreshAfterUtc`; an operator can request a fresh bounded scan with
`?refresh=true`. Intake records the same snapshot id in its enrichment manifest.
The pre-spawn renderer applies a 6,000-character ceiling
(approximately 1,500 tokens), at most two optional blocks, deterministic
selection order, and a bounded omissions manifest when relevant rules do not
fit. Mandatory project policy is accounted separately from the optional-block
allowance. Every decision is persisted in `enrichment-report.json` and shown
beside the authored prompt in the Task tab.

See [Adding or changing a rule](adding-a-rule.md) before expanding the family.
Interactive alternatives or uncertain design questions belong in a
[Dossier](../concepts/experimentier-workbench.md), not in a mandatory guide.
