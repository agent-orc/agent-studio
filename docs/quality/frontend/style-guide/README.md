# Style Guide

This component vocabulary is incorporated by the
[Angular component guide](../../angular-components.md). The single
navigation entry for all technology-aware engineering guides is
[docs/quality/](../../README.md).

Operator-facing index of the **canonical UI vocabulary** the shell uses: tokens, primitives, components, and the migration status that tracks how far each surface has converged onto them.

## Product names

**Board & Deck** is the canonical name pair for the two primary project
surfaces:

- **Board** is the task-flow surface with lanes and cards.
- **Deck** is the project-level surface for overview, quality, context, and
  configuration.

Use **Deck** unchanged in English documentation. Do not expand it to "Project
Deck" and do not reintroduce the former hub label. Internal compatibility keys,
test aliases, and routes may retain `hub`; those identifiers must not leak into
user-facing copy.

The [Visual Style Guide Dossier](../../../concepts/visual-style-guide.html) is
the rendered companion to this engineering guide. Use it to inspect today's
language in both themes, compare focused variants, and record vNext decisions.
This folder continues to own the implementation vocabulary and migration
contract; the Dossier makes that vocabulary visible and reviewable.

This folder is the **second-look** for any visual or styling change. The first stop is still [`docs/quality/frontend/design-system.md`](../design-system.md) (the "why" of the shell: Material 3 mapping, color philosophy, type ramp, motion grammar). The style guide is the **"what to grab"** for a concrete change:

- "Which rules are mandatory for this styling card?" → [living rules](./living-rules.md) + [compact prompt context](../../frontend-styling.md)
- "I need a small button" → [buttons.md](./buttons.md)
- "I need predictable actions in a repository page head" → [page action Dossier](../../visual-styleguide-workbench-wiki/index.html)
- "I need a pill / badge / chip" → [pills.md](./pills.md)
- "I need a card surface" → [cards.md](./cards.md)
- "I need a modal" → [modals.md](./modals.md)
- "I need sidebar / rail navigation" → [navigation.md](./navigation.md)
- "I need a tab strip" → [tabs.md](./tabs.md)
- "I am changing the Studio app or task header" → [header.md](./header.md)
- "I need a form input" → [forms.md](./forms.md)
- "What spacing / radius / shadow token do I use?" → [tokens.md](./tokens.md)

If the canonical component fits, use it. If it does not fit, the audit pages list every adjacent implementation already in the codebase — read them before authoring a new one.

## Layering

Three layers, low-to-high. Lower layers are the source of truth; higher layers consume them.

1. **Tokens** — `--studio-spacing-*`, `--studio-bg-*`, `--studio-fg-*`, `--studio-accent`, `--studio-modal-padding`, `--studio-modal-padding-*`, `--elevation-*`, `--shadow-*`. Declared in [`frontend/src/styles/_tokens-primitives.scss`](../../../../frontend/src/styles/_tokens-primitives.scss) (Tier 1, raw palette) and [`frontend/src/styles/_tokens-semantic.scss`](../../../../frontend/src/styles/_tokens-semantic.scss) (Tier 2, semantic aliases that flip per theme). See [tokens.md](./tokens.md) for the full list with use cases.
2. **Primitives** — small reusable shapes the components compose. Today: SCSS mixins in [`frontend/src/styles/_mixins.scss`](../../../../frontend/src/styles/_mixins.scss) (`icon-button`, `chip`, `empty-state`, `thin-scroll`, `type-label`, and the `deck-panel*` family).
3. **Components** — the standalone Angular components callers reach for. See the canonical-component table below.

A change at layer N must not duplicate a fact at layer N-1. A button never hardcodes its radius; the radius comes from the shape scale. The shape scale never hardcodes a hex; the hex lives in the primitives. The primitives never live in two files. **Tokens > Primitives > Components.**

## Canonical components

| Family             | Canonical                                                                                                       | Variants                          | Status            |
| ------------------ | --------------------------------------------------------------------------------------------------------------- | --------------------------------- | ----------------- |
| Modal              | [`<app-dialog>`](../../../../frontend/src/app/components/dialog/dialog.component.ts)                                  | `size=sm\|md`, `kind=default\|danger\|primary` | ✅ shipped |
| Icon-only button   | SCSS mixin `m.icon-button($size)` in [`_mixins.scss`](../../../../frontend/src/styles/_mixins.scss)                   | `$size` (22/24/26 px today)       | ✅ shipped (no Angular wrapper yet) |
| Chip / pill        | SCSS mixin `m.chip($accent, $alpha-bg, $alpha-border)` in [`_mixins.scss`](../../../../frontend/src/styles/_mixins.scss) | per-accent (`--studio-accent`, `--studio-accent-warn`, ...) | ✅ shipped (no Angular wrapper yet) |
| Row / list item    | [`<app-row>`](../../../../frontend/src/app/components/row/row.component.ts)                                           | `variant=compact\|default\|cozy`, `interactive` | ✅ shipped |
| Tab strip          | [`<app-pane-tabs>`](../../../../frontend/src/app/components/pane-tabs/pane-tabs.component.ts)                         | `variant=header\|pill`            | ✅ shipped (F38)  |
| Sidebar navigation | [`<app-section-header>`](../../../../frontend/src/app/components/section-header/section-header.component.ts) + [`<app-tree-row>`](../../../../frontend/src/app/components/tree-row/tree-row.component.ts) | static/collapsible groups + root/child rows | ✅ shipped |
| Side sheet         | [`<app-sidesheet>`](../../../../frontend/src/app/components/sidesheet/sidesheet.component.ts)                         | `[width]`                         | ✅ shipped        |
| Page action bar    | [`<app-page-action-bar>`](../../../../frontend/src/app/features/project-detail/components/page-action-bar/page-action-bar.ts) | document / concept / Dossier / incident / report | ✅ shipped |
| Menu               | [`<app-menu>`](../../../../frontend/src/app/components/menu/menu.component.ts)                                        | text-only rows                    | ✅ shipped        |
| Notification       | [`<app-notification>`](../../../../frontend/src/app/components/notification/notification.component.ts)                | floating toast / full-bleed notice bar | ✅ shipped (F37)  |
| Tooltip            | [`[appTooltip]`](../../../../frontend/src/app/components/tooltip/app-tooltip.directive.ts) directive   | instant HTML body                 | ✅ shipped        |
| Empty state        | [`<app-empty-state>`](../../../../frontend/src/app/components/empty-state/empty-state.component.ts)                    | default + named                   | ✅ shipped        |
| Free-form button   | _none_                                                                                                          | —                                 | ⚠️ **gap** — see [buttons.md](./buttons.md) "Open question: do we need `<app-button>`?" |
| Card surface       | _none_                                                                                                          | —                                 | ⚠️ **gap** — see [cards.md](./cards.md) "Open question: do we need `<app-card>`?" |
| Form controls      | _none_                                                                                                          | —                                 | ⚠️ **gap** — see [forms.md](./forms.md) "Audit + open question" |

The "gap" rows are intentional. The audit pages enumerate every existing implementation so we can decide whether to extract a canonical component or stay with per-feature SCSS. The decision lands in [migration-status.md](./migration-status.md) as a queued task, never inline here.

## Audits — what already exists

The audit pages are the **inventory** of existing implementations of each family in the codebase. They are the source the operator's "look if there's something similar" rule consults. Read them before authoring a new SCSS class.

- [audit-buttons.md](./audit-buttons.md) — every small-button class found
- [audit-pills.md](./audit-pills.md) — every pill / chip / badge / tag / count class found
- [audit-cards.md](./audit-cards.md) — every card surface found
- [audit-modals.md](./audit-modals.md) — every modal / dialog / overlay found
- [navigation.md](./navigation.md) — canonical sidebar and rail navigation recipe
- [audit-tabs.md](./audit-tabs.md) — every tab-strip / segmented-control found
- [audit-forms.md](./audit-forms.md) — every input / select / textarea / checkbox / toggle found

When you migrate a surface, update the audit row (or mark it ✅) and add the migration target to [migration-status.md](./migration-status.md). The audit pages are living docs; they do not freeze in time.

## How to use this guide

1. **Pick a canonical component if one fits.** The table above is the first stop. If the component lives in `frontend/src/app/components/`, import it from there; if it is a mixin in `_mixins.scss`, `@use 'styles/mixins' as m;` and `@include m.<name>;`.
2. **Read the audit for that family.** If your case looks like one of the existing implementations, prefer convergence: extend the canonical with a variant rather than introduce a third option.
3. **Read tokens.md** if your change needs a px / hex / shadow value. Tokens come from a fixed scale; a new px in a component template is almost always wrong.
4. **If you need a new variant**, propose it in the canonical-component's doc (`buttons.md` / `pills.md` / ...) before shipping. A variant lands once and gets reused; a one-off SCSS class lands once and rots.

## Hard rules

The product-wide, prompt-known design hard rules (no left accent bars,
full-bleed views, aggregate = sum of visible children, acute-only signals, both
themes) live in
[docs/quality/design/style-guide-hard-rules.md](../../design/style-guide-hard-rules.md).
This section holds the design-system-specific rules on top of them.

- **No new hex / rgb / hsl values in component SCSS.** Color tokens live in [`_tokens-primitives.scss`](../../../../frontend/src/styles/_tokens-primitives.scss); aliases in [`_tokens-semantic.scss`](../../../../frontend/src/styles/_tokens-semantic.scss). Stylelint's `color-no-hex` + `scale-unlimited/declaration-strict-value` enforce this on new SCSS; legacy files are baseline debt (see [`frontend/.stylelintrc.json`](../../../../frontend/.stylelintrc.json) overrides).
- **No new hardcoded spacing px in component SCSS** when the surface is part of the canonical vocabulary (modal padding, row gap, card inset). Use `--studio-spacing-*` tokens. Stylelint will surface common-literal violations as a warning starting from this guide's first version; promotion to error is a follow-up slice.
- **No inline `style="..."` in component templates** for design-system attributes (color, font-size, padding, margin). One-off geometry like `[style.width.px]` for a programmatic side-sheet width is fine; a `style="color: #d97757"` is not.
- **Light + Dark must both work.** If you add a token, add the light theme override in `[data-studio-theme='light']` in `_tokens-semantic.scss`. If you add a component, screenshot both themes in the PR.

See AGENTS.md "Code Conventions" for the broader code-style rules; this guide is the design-system slice.

## Phase 2 / Phase 3 — what is intentionally out of scope here

- A live in-app `/style-guide` product route that renders Angular components.
  The Wiki Dossier linked above is deliberately self-contained HTML and does
  not add a product route or runtime dependency.
- Storybook integration. Same trigger: useful once the components stabilise.
- A new theme. We stay on Catppuccin (dark default) + the F19 light shell.

If those become near-term concerns, raise them through the standard task-board flow, not via inline expansion of this folder.
