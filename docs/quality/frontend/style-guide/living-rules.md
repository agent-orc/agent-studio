# Living frontend styling rules

These rules are the decision contract for new styling work. They extend the
[product-wide hard rules](../../design/style-guide-hard-rules.md) and the
[design system](../design-system.md). When rules conflict, the hard rules win.

## Buttons

All text buttons in one action group use the same height, inset, radius, type
size, focus ring, and disabled treatment. Difference in emphasis communicates
intent, not a different local component shape.

| Variant | Use | Rule |
|---|---|---|
| Primary | The single preferred action that advances or confirms the current flow | At most one primary button per action group. Use the accent fill. |
| Secondary | A safe alternative that keeps the operator in the same decision context | Use neutral, quiet chrome. Do not introduce a second accent fill. |
| Destructive | Abort, delete, discard, revoke, or another action with destructive impact | Use the semantic danger token. Never use danger color for a non-destructive action. |
| Ghost | Cancel, dismiss, reveal, or a low-priority tertiary action | Use transparent chrome. A ghost action must remain recognizable as interactive through placement, hover, focus, and its `<button>` semantics. |

Use the canonical recipes in [buttons.md](./buttons.md). Icon-only actions use
`m.icon-button`; do not restyle a text label into an ad hoc button.

## Labels are not buttons

- A non-interactive label is never rendered with button chrome.
- Labels have no button height, raised surface, action border, hover state,
  pointer cursor, focus ring, or pressed state.
- A status label may use a semantic dot, text color, or quiet full-surface tint.
  It must not imitate a rectangular or pill action beside real buttons.
- Interactive behavior uses a native `<button>` or link and an action variant.
  Do not add click behavior to `<span>` or `<div>` labels.

## Typography and uppercase

- UI copy uses sentence case by default, including status labels and buttons.
- `text-transform: uppercase` is allowed only for micro-label selectors that use
  `.studio-label`, `.studio-metric__label`, or the shared `m.type-label` mixin.
- Eyebrows, compact section headings, and short machine status codes may use
  `m.type-label`. Titles, descriptions, badges, buttons, tabs, and state labels
  never use uppercase unless the source value is an acronym such as CLI or API.
- A component-local uppercase rule without one of those documented selectors or
  the mixin is a violation, even when a neighboring legacy surface does it.

## Spacing and panels

- Use `--studio-spacing-1` through `--studio-spacing-7`; never add a raw spacing
  value in component SCSS. Prefer an existing semantic spacing alias.
- A sibling action group shares one gap token and one control-height contract.
- Deck panels use `m.deck-panel`, with `m.deck-panel-muted` for settled context
  and `m.deck-panel-attention` only for acute context. The source contract is
  [Deck-Panel v1](../../../concepts/visual-style-guide/deck-panel-v1.md).
- Do not wrap an existing panel in another panel. Migrate its outer shell.
- Do not use a colored left edge for status. Use tint, badge, or dot.

## Color tokens

- Component SCSS reads semantic `--studio-*` or `--severity-*` tokens.
- Never add hex, RGB, HSL, or named raw colors to component SCSS.
- New semantic colors are defined centrally for dark and light themes before
  use. `color-mix()` inputs must also be semantic tokens.
- Enforcement and migration scope live in
  [`frontend/.stylelintrc.json`](../../../../frontend/.stylelintrc.json), using
  `color-no-hex` and `scale-unlimited/declaration-strict-value`.

## Open cases

- A typed `<app-button>` remains open; until it exists, use the documented
  `.btn` variants or a shared feature recipe without changing their semantics.
- Static status labels use the shared `<app-status-indicator>` (AGT-3402),
  documented in [status-indicator.md](../../design/status-indicator.md). Do not
  introduce another per-feature status pill; migrate legacy ones from the
  [status display inventory](../../design/status-display-inventory.md).
- Deck-Panel mixin adoption is incremental. Do not add nested panels while
  migrating legacy shells.

Every styling card must read this guide and the
[compact styling context](../../frontend-styling.md). If the card exposes a
reusable gap, extend the relevant rule or add a bounded item to **Open cases**
in the same change. Do not solve it with an undocumented local exception.
