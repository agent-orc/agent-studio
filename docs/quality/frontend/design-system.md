# Design System — Agent Software Studio

The visual contract behind the **Agent Software Studio** shell. Product
principles (UX rules, action-driven reports, drift, steering) live in
[design-principles.md](../design-principles.md); architecture lives in
[architecture-decisions.md](../../system/architecture/decisions/adr-archive.md). This document is
the **token + pattern + component** reference that explains what the app
looks like, why it looks that way, and which knobs flip the appearance.

## Why we have a design system

We ship a workbench for a developer who is supervising multiple agents
in parallel. The visual surface has three jobs:

1. **Reduce cognitive load.** Many tasks, many CLIs, many tabs. The
   shell has to compress that into a single environment where
   movement, status, and evidence read at a glance.
2. **Borrow muscle memory.** Developers already live in IDEs. The shell
   follows the **VS Code workbench** conventions — titlebar, activity
   bar, sidebar, tab host, status bar, panel sheets — so users don't
   re-learn navigation when they switch to our tool.
3. **Stay one product.** Every new feature should reach for the same
   token vocabulary, the same shape scale, the same motion grammar, and
   the same chrome. A Drift panel, a Token-Usage panel, and a Security
   panel must read as one product, not three.

The reference for our look is **VS Code (Catppuccin/dark default, light
theme as the daily-driver)** plus **Google Material 3 Expressive**
guidance for the underlying token vocabulary, shape scale, type ramp,
and motion grammar.

## Material 3 Expressive — what we adopt and what we ignore

Material 3 Expressive (announced May 2025) is Google's most recent
update to Material Design. We treat it as **inspiration for the
vocabulary**, not as a literal kit. The shell is a developer workbench,
not a mobile app, so we lean on the parts that translate and skip the
parts that don't.

### The five pillars

Material 3 Expressive organizes design choices into five pillars. Our
mapping:

| M3 Pillar       | What it covers                                | Where it shows up in the shell                                   |
| --------------- | --------------------------------------------- | ---------------------------------------------------------------- |
| **Color**       | Surfaces, accent roles, semantic state colors | Studio tokens (`--studio-bg-*`, `--studio-fg-*`, `--studio-accent`) |
| **Shape**       | Corner-radius scale across components          | The shell shape scale below                                       |
| **Size**        | Spacing scale, density, hit targets            | `--studio-titlebar-h`, `--studio-tabbar-h`, gap utilities         |
| **Motion**      | Spring-based transitions, intentional emphasis | Tab drag-reorder, sidesheet open/close                            |
| **Containment** | Surface elevation, grouping, separation        | Titlebar / activity bar / sidebar / editor / status bar grid     |

We **adopt**: the token-first structure (so themes can flip), the
shape scale concept (consistent corner radii), the size-as-density
idea, motion as part of the design language, and containment as a
deliberate choice.

We **skip**: the 35-shape morphing library, the full 2×15 type scale
(we use one type scale plus an emphasized variant for heroes), and the
emoji-rich icon set (the shell uses monochrome geometric Unicode for
the project rail because emoji disrupted the calm grey IDE look).

## Studio shell token vocabulary

All tokens are declared in
[frontend/src/app/features/studio-shell/studio-shell.component.scss](../../../frontend/src/app/features/studio-shell/studio-shell.component.scss)
under `:root` (dark default) and `[data-studio-theme='light']` (light
override). Flipping the `data-studio-theme` attribute on the document
root re-skins the shell without re-rendering any component.

### Surface tokens

| Token                          | Dark default | Light override | Purpose                                            |
| ------------------------------ | ------------ | -------------- | -------------------------------------------------- |
| `--studio-bg-titlebar`         | `#1e1e1e`    | `#f3f3f3`      | Top bar with brand, project pills, search box     |
| `--studio-bg-activitybar`      | `#181818`    | `#ececec`      | 48 px vertical icon column on the left            |
| `--studio-bg-sidebar`          | `#1f1f1f`    | `#f8f8f8`      | Resizable panel that follows the activity bar     |
| `--studio-bg-editor`           | `#1e1e1e`    | `#ffffff`      | Main content area (tab host + projected content)  |
| `--studio-bg-elevated`         | `#252526`    | `#ececec`      | Inputs, search box, raised cards                  |
| `--studio-bg-tab-active`       | `#1e1e1e`    | `#ffffff`      | Active tab background — matches editor for fusion |
| `--studio-bg-tab-inactive`     | `#2d2d2d`    | `#ececec`      | Inactive tabs sit one step below the editor       |
| `--studio-bg-hover`            | `rgba(255,255,255,0.04)` | `rgba(0,0,0,0.05)` | Hover wash for interactive surfaces |
| `--studio-bg-selected`         | `rgba(217,119,87,0.12)` | `rgba(217,119,87,0.15)` | Selected state — accent at low alpha |

### Border tokens

| Token                       | Dark default | Light override | Purpose                                |
| --------------------------- | ------------ | -------------- | -------------------------------------- |
| `--studio-border`           | `#2b2b2b`    | `#d4d4d4`      | Default 1 px separator between surfaces |
| `--studio-border-strong`    | `#3c3c3c`    | `#b4b4b4`      | Drag handles, sidebar splitter         |

### Foreground tokens

| Token                  | Dark default | Light override | Purpose                                  |
| ---------------------- | ------------ | -------------- | ---------------------------------------- |
| `--studio-fg`          | `#cccccc`    | `#333333`      | Body text, label-level                   |
| `--studio-fg-strong`   | `#ffffff`    | `#1a1a1a`      | Headings, titles, emphasized states      |
| `--studio-fg-dim`      | `#9d9d9d`    | `#555555`      | Captions, secondary metadata             |
| `--studio-fg-muted`    | `#6e6e6e`    | `#888888`      | Tertiary chrome (kbd hints, separators)  |

### Accent tokens

| Token              | Value      | Purpose                                                       |
| ------------------ | ---------- | ------------------------------------------------------------- |
| `--studio-accent`  | `#d97757`  | Brand accent (orange) — selection, focus, primary CTAs        |
| `--studio-accent-2`| `#4ec9b0`  | Secondary accent (teal) — auxiliary chips, "running" states   |
| `--studio-accent-3`| `#569cd6`  | Tertiary accent (blue) — links, file-type glyphs, info chips  |

The three-step accent ramp mirrors VS Code's syntax highlighting (string
/ function / type) and gives us room to colour-code state without
introducing red/green/yellow which we reserve for **semantic state**
(error, success, warning) on top of the studio palette.

### Size tokens

| Token                       | Value   | Purpose                              |
| --------------------------- | ------- | ------------------------------------ |
| `--studio-activitybar-w`    | `48px`  | Width of the vertical icon column    |
| `--studio-titlebar-h`       | `36px`  | Height of the top bar                |
| `--studio-tabbar-h`         | `36px`  | Height of the tab strip              |

Status bar height (24 px when projected, 36 px legacy) is set on the
status-bar component itself; it has not been promoted to a token
because only one component reads it.

## Shape scale

The shell uses a deliberately small corner-radius scale. Each step is
**~1.5×** the previous, which is enough for the eye to perceive
hierarchy without breaking the IDE-flat look.

| Step | Radius   | Where it's used                                              |
| ---- | -------- | ------------------------------------------------------------ |
| `xs` | `3px`    | Icon buttons, pickers in the titlebar/status bar             |
| `sm` | `4px`    | Tabs, search box, small chips, kbd hints                     |
| `md` | `6px`    | Menus, dropdowns, secondary cards                            |
| `lg` | `8px`    | Job cards, panel cards, sheet headers                        |
| `xl` | `10px`   | Status-bar dropdown menus (single elevation step above body) |
| `2xl`| `12px`   | Sidesheets, dialogs (one elevation step above the editor)    |

We do **not** ship round / pill / fully-rounded shapes — they look
playful and disagree with the IDE convention. Material 3 Expressive's
35-shape library is overkill for a workbench; this scale of six steps
is what the shell actually uses today.

## Type scale

The shell uses two font families:

- **Inter** (with Segoe UI / system-ui fallback) for all UI chrome and
  copy. Inter is the same family the reference VS Code skins use.
- **JetBrains Mono** (with SF Mono / Menlo / Consolas fallback) for
  code, IDs, paths, hashes, and keyboard hints.

The type scale follows VS Code density (12 px is the default UI size,
not 16 px). Material 3's larger baseline scale is appropriate for
mobile; for a developer workbench we want more information per row.

| Role           | Size   | Weight | Family       | Examples                                           |
| -------------- | ------ | ------ | ------------ | -------------------------------------------------- |
| `heading-xl`   | `24px` | 600    | Inter        | Empty-state heroes ("Welcome to the Studio")       |
| `heading-lg`   | `20px` | 600    | Inter        | Sheet titles, sheet eyebrows                       |
| `heading-md`   | `16px` | 600    | Inter        | Panel titles, Deck rail panel header               |
| `heading-sm`   | `14px` | 600    | Inter        | Card titles, section heads                         |
| `body`         | `13px` | 400    | Inter        | Default body text inside panels and sheets         |
| `body-strong`  | `13px` | 500    | Inter        | Inline emphasis, list-item titles                  |
| `ui`           | `12px` | 400    | Inter        | Chrome (titlebar, tab labels, status-bar items)    |
| `ui-strong`    | `12px` | 600    | Inter        | Rail labels, group headers, key chrome             |
| `caption`      | `11px` | 400    | Inter        | Subtle metadata, "age" timestamps                  |
| `kbd`          | `10px` | 500    | JetBrains Mono | Keyboard hints in the search box               |
| `code-sm`      | `12px` | 400    | JetBrains Mono | Inline code, IDs, hashes                       |
| `code`         | `13px` | 400    | JetBrains Mono | Code blocks, diff view, terminal                |

Material 3 Expressive's "emphasized" variant of every role is **opt-in
only**: a Drift heatmap or a Token-Usage hero metric can step a single
role up to a heavier weight or +2 px, but the chrome itself stays
calm.

## Color roles

The shell separates **surface colors** (chrome) from **state colors**
(meaning).

### Surface roles (drawn from the tokens above)

- **Base** — `--studio-bg-editor` is the canvas. Everything else is
  defined as one elevation step up or down from base.
- **Lowered** — activity bar (`--studio-bg-activitybar`). Sits *below*
  the base because it's chrome the eye should skip.
- **Raised — chrome** — titlebar, sidebar, tab-strip. One step up from
  base, but still chrome.
- **Raised — content** — `--studio-bg-elevated` for inputs, cards, the
  search box. This is the step that says "this is interactive".
- **Floating** — sidesheets, dropdowns, dialogs. Two steps up from
  base, with a visible shadow for elevation.

### State colors (semantic, on top of the studio palette)

| State     | Hex                  | Used for                                       |
| --------- | -------------------- | ---------------------------------------------- |
| Success   | `#4ade80`            | Pulse dot for "running", green check icons    |
| Warning   | `#facc15`            | Drift "yellow", at-risk badges                 |
| Error     | `#ef4444` (light: `#b91c1c`) | Failed-update banner, destructive hover |
| Info      | `--studio-accent-3` (`#569cd6`) | Links, info chips, file-type glyphs |
| Selected  | `--studio-bg-selected` | Selected rail item, active tab            |

Semantic state colors stay constant across themes; their **container**
flips with the theme (a red badge on white is `#b91c1c` text on a
`rgba(220,38,38,0.08)` background, the same red on dark uses
`#ef4444` text on `rgba(220,38,38,0.16)` background).

## Motion grammar

Motion is one of the five Material 3 pillars and our use of it is
intentional but lean.

| Pattern                | Duration | Easing                          | Examples                                |
| ---------------------- | -------- | ------------------------------- | --------------------------------------- |
| `hover-fade`           | `120ms`  | `ease`                          | Status-bar buttons, rail items          |
| `panel-open`           | `200ms`  | `cubic-bezier(0.2, 0, 0, 1)`    | Sidesheet width transition              |
| `tab-drop`             | `160ms`  | `cubic-bezier(0.4, 0, 0.2, 1)`  | Tab reorder settle after drag-drop      |
| `card-elevate`         | `120ms`  | `ease`                          | Job card hover shadow lift              |
| `dropdown-pop`         | `120ms`  | `ease-out`                      | Status-bar CLI/model picker open        |

We **avoid** Material 3 Expressive's spring-with-overshoot bounce. In
a developer workbench that runs alongside an IDE, overshoots feel
distracting. A flat ease-out reads as confident and gets out of the
way.

## Containment — the studio grid

The shell is one CSS grid:

```
┌────────────────────────────────────────────────────────────────────────┐
│                          studio-titlebar (36 px)                       │
├────┬─────────────┬─────────────────────────────────────────────────────┤
│    │             │                                                     │
│ 🟧 │  Sidebar    │  Editor (tab strip + projected content)             │
│    │  (resizable)│                                                     │
│ 48 │             │                                                     │
│ px │             │                                                     │
│    │             │                                                     │
├────┴─────────────┴─────────────────────────────────────────────────────┤
│                       studio-statusbar (24 px)                         │
└────────────────────────────────────────────────────────────────────────┘
```

Each region is a CSS Grid cell with `minmax(0, 1fr)` on the editor row
so the inner tab host can shrink horizontally without overflowing the
viewport. Side sheets (orchestrator chat, CLI usage, kanban filter)
are absolutely positioned overlays on top of the editor cell when the
flag is on — they do not participate in the grid so opening them
cannot push the editor down.

The **activity bar** is monochrome by design. Each icon is a single
geometric Unicode glyph at the same colour as the foreground muted
token; the active icon flips to `--studio-fg-strong`. We tried emoji
glyphs once and the visual rhythm broke — see the rail icon decision
in [project-shell.config.ts](../../../frontend/src/app/features/project-detail/components/project-shell/project-shell.config.ts).

## Component inventory

The shell is composed of the following standalone components. Each is
expected to use the token vocabulary above and follow the shape /
type / motion rules. New components should be added to this list.

### Shell chrome

- **Titlebar**
  [frontend/src/app/features/studio-shell/studio-shell.component.html](../../../frontend/src/app/features/studio-shell/studio-shell.component.html)
  — brand logo, project pills, search box, command actions, theme
  toggle. Always renders.
- **Activity bar** — vertical 48 px column with the panel switcher
  (Explorer, Filters, Agents / CLI, Activity, Runbook) plus a settings
  gear that opens the one consolidated Workspace-settings view as an
  editor tab (AGT-2035; the standalone chat-rail toggle was removed).
- **Sidebar** — resizable panel projected via
  `<ng-content select="[studioSidebar]">`. Hosts the Explorer
  (workspaces / projects tree), search results, or other navigation
  surfaces.
- **Editor / tab host**
  [frontend/src/app/features/studio-shell/services/studio-tab-state.service.ts](../../../frontend/src/app/features/studio-shell/services/studio-tab-state.service.ts)
  — tab strip with drag-reorder + localStorage persistence, projected
  content per tab kind (board, Deck, task detail, diff,
  activity).
- **Status bar**
  [frontend/src/app/features/shell/components/status-bar.ts](../../frontend/src/app/features/shell/components/status-bar.ts)
  — CLI quota chips, "N running" / "N/N auto" stats, default CLI &
  model picker, sidesheet toggles. Always renders.

### Projected views (one per tab kind)

- **Board view** — the three-super-column kanban (Backlog / Active /
  Done & Decide) projected as the default tab content.
- **Deck**
  [frontend/src/app/features/studio-shell/components/project-hub-view/project-hub-view.component.ts](../../../frontend/src/app/features/studio-shell/components/project-hub-view/project-hub-view.component.ts)
  — embeds `<app-project-shell>` with all rail items (Overview,
  Design, Visual Evidence, Architecture, Drift, UX/UI, Observability,
  Security, Test Quality, Audits & Checks, Jobs, Token Usage, Product
  Runtime, Activity, Steering Docs, Orchestrator, Settings) per the
  [quality layer architecture](../../concepts/architecture-quality-layer.md).
- **Task detail** — the existing job detail with prompt / protocol /
  evidence panes, projected when the user opens a task tab.
- **Diff tab** — placeholder for future per-task diff view.
- **Activity tab** — placeholder for project-feed views.
- **Presenter deck** — the Team magazine view embeds a 16:9 slide viewer with
  `--studio-*` surfaces and foreground tokens. It shows one slide at a time and
  uses the same flat, bordered card grammar in both themes.

### Sheets and overlays

- **Orchestrator side sheet** — chat with the orchestrator agent,
  pinned to the right edge of the editor in studio mode.
- **CLI usage sheet** — quota / token roll-up across all CLIs.
- **Kanban filter sidesheet** — project / CLI / state filters.
- **Add Task dialog** — primary create-task surface, used from any
  view.
- **Confirm / Error / Update / Lightbox dialogs** — standardised
  centred modals.

### Deck rails

The Deck rail items live in
[project-shell.config.ts](../../../frontend/src/app/features/project-detail/components/project-shell/project-shell.config.ts).
They are grouped into four buckets:

- **Insight** — what the project *is* and does (Overview, Visual
  Evidence, Drift, Observability, Token Usage).
- **Quality** — what guards the project (Security, Test Quality,
  Audits & Checks).
- **Context** — documentation and agent-readable project context
  (Architecture, Wiki, "Agent Docs", and Prompts).
- **Config** — how the project is set up (Pipeline, Workflow,
  Orchestrator, and Settings, which expands to Workspace Defaults and
  Project Overrides).

The four buckets render as collapsible segments, and Settings is a tree
parent with a disclosure twisty. `project-shell.config.ts` is the
authoritative inventory; its `parent` and `navigable` flags decide nesting
and whether a row routes to a panel.

Each rail item carries a monochrome Unicode icon (`▤ ◇ ◉ ⊞ ↯ ◐ ⌁ ⊡ ✓ ⊟
☰ ▦ ⊜ ⌖ ⊕ ◈ ⚙`), a panel title, a one-line description, and an empty
state. Real content for each rail lands in a follow-up slice per the
quality-system mockup.

## Theme switching

The theme toggle in the titlebar writes `data-studio-theme="light"` or
`data-studio-theme="dark"` on the `<html>` element and persists the
choice in `localStorage`.

Two layers of CSS react:

1. **Token flip** — the `[data-studio-theme='light']` block in
   `studio-shell.component.scss` re-binds every `--studio-*` variable.
   Any component that reads tokens automatically re-skins.
2. **Legacy bridge** — `frontend/src/styles.scss` has a large
   `html[data-studio-theme='light'] { … }` block that hard-overrides
   the loudest dark hexes on legacy components (job-card, column,
   dashboard, header, pane, sheet, panel, dialog, status-bar,
   project-shell, etc.). This is a safety net until each component
   migrates to the token vocabulary.

When you add a new component, **read tokens, don't hardcode hexes**.
If you must hardcode, also add a light override in the styles.scss
bridge.

## Light theme is the daily driver

The light theme is the default. The dark theme is shipped because some
users prefer it and some screens read better at night, but the
**primary look the app is designed for is light**. New mockups, new
screenshots, and new component decisions should be evaluated in light
mode first. Dark mode is the variant we make sure doesn't regress, not
the look we optimise.

This decision came after the first round of layout migration when the
reference layout's dark default looked good in isolation but the
embedded legacy panels (project-shell, drift, observability,
token-usage) were dark-on-light mismatches in production. Flipping the
default to light and treating dark as the secondary theme is what
gives us a coherent shell today.

## Reference patterns

A handful of recurring patterns are worth naming because every new
panel ends up reaching for one of them.

### Pattern: titled card with hero metric

A card with a small uppercase eyebrow label, a large hero number, and a
one-line caption underneath. Used in the Overview rail (project
health, queue depth, pipeline status) and the Token-Usage panel
(spend, top CLI, top model).

```
┌─────────────────────────────┐
│  EYEBROW LABEL              │
│                             │
│      42                     │
│                             │
│  one-line caption text      │
└─────────────────────────────┘
```

### Pattern: action-driven panel

Per the [design principles](../design-principles.md), mounting a panel
must not run analysis. Panels open with an empty state that names the
action ("Run drift scan" / "Generate baseline" / "Capture evidence")
and the user explicitly triggers work. This rule is what makes the
shell cheap to navigate.

### Pattern: visible CLI task

Product actions that drive an agent CLI use the existing task chat as their
execution substrate. The shared `VisibleCliTaskRequest` contract supplies a
scope title and reason, the exact CLI input, command, expected duration, and
context. Its standard card creates a lightweight Ready task and then opens the
normal task detail. Live output, progress, operator input, completion, and
history therefore come from the canonical task chat and activity rendering.
Do not build a second console or process-specific streaming view. The Remote
Hosts onboarding card is the reference consumer.

### Pattern: monochrome rail with one accent

Vertical lists of navigation items (the activity bar, the Deck
rail, the Steering Docs list) use a single muted foreground colour for
icons and labels, with `--studio-accent` reserved for the active /
selected state. No two rail items share a colour.

### Pattern: evidence belongs in Protocol

Per user direction, evidence (screenshots, runtime captures, agent
output) stays in the Protocol pane of the task detail — it is not
split into a separate Description / Evidence tab. The Description
pane stays focused on the task brief.

## How to extend

When you ship a new view or panel:

1. **Start from a token.** Don't hardcode a colour without a fallback.
   `var(--studio-bg-editor, #ffffff)` is fine; bare `#ffffff` is not.
2. **Pick a shape step.** Match an existing component's corner radius
   instead of picking a new one. If you must add a new step, propose
   it here first.
3. **Pick a type role.** Don't introduce a new font size; if the
   information doesn't fit a role above, the panel needs a redesign,
   not a new size.
4. **Honour the action-driven panel rule.** Empty states name the
   action. Mounting must not run analysis.
5. **Add a light override if you hardcode anything.** The light bridge
   in `styles.scss` is the catch-all; new dark hexes that don't flip
   are bugs.
6. **Document the new component here.** Anything user-facing that
   isn't in this file should be added under "Component inventory".

If a change requires a new token, propose it in a short ADR-style note
before shipping — once a token exists, every panel can read it, and
the cost of getting the name wrong is high.
