# Pills, Chips, Badges, Tags

The shell uses small inline labels for three jobs:

| Family       | What it means                                                                | Recipe                                            |
| ------------ | ---------------------------------------------------------------------------- | ------------------------------------------------- |
| **Pill**     | Status / mode label, accent-tinted. "Running", "Failed", "Auto-review".      | SCSS mixin `m.chip($accent, $alpha-bg, $alpha-border)` in `_mixins.scss` |
| **Chip**     | Neutral inline label, no accent. Kbd hints, owner names, key paths.          | (mixin proposed; today rebuilt per consumer) |
| **Badge**    | Numeric count, often a notification dot or a "12 commits" marker.            | (mixin proposed; today rebuilt per consumer) |
| **Tag**      | Free-form short label inside a card / row (skill tag, file tag).             | (per-feature; small footprint) |

See [audit-pills.md](./audit-pills.md) for the inventory of every existing variant.

## Status projection contract

A task-card status pill describes the card now, not something that happened to
it earlier. Derive it from the current lane, current integration status, and
latest run outcome. Historical escalation, reissue, abort, and transient error
events stay in the task timeline. Never render `merged` with `Integration
error`, or Review with `Escalated`. This is the AGT-2410 Living Style Guide
contract and the task-card application of
[R4](../../design/style-guide-hard-rules.md#r4---acute-signals-only-for-acute-states).

## Pill — accent-tinted (canonical: `m.chip` mixin)

```scss
.my-feature__running-pill {
  @include m.chip(var(--studio-accent-3));        // info-blue tint
}

.my-feature__failed-pill {
  @include m.chip(var(--studio-accent-6), 0.16, 0.5);  // red tint, stronger
}
```

```html
<span class="my-feature__running-pill">Running</span>
<span class="my-feature__failed-pill">Failed</span>
```

**Geometry** (set by the mixin):

| Property      | Value                                                                                  |
| ------------- | -------------------------------------------------------------------------------------- |
| Display       | `inline-flex; align-items: center; gap: 4px`                                            |
| Height        | `18px`                                                                                 |
| Padding-x     | `8px`                                                                                  |
| Border radius | `9px` (full pill)                                                                      |
| Font          | `10.5px / 600 / uppercase / 0.04em letter-spacing`                                     |
| Background    | `color-mix(in srgb, $accent $alpha-bg%, transparent)` — accent tint                    |
| Border        | `1px solid color-mix(in srgb, $accent $alpha-border%, transparent)`                    |
| Color         | `$accent`                                                                              |

**Accent recipe** — pass any Tier-2 accent or severity token: `--studio-accent`, `--studio-accent-2`, `--studio-accent-3`, `--studio-accent-warn`, `--studio-accent-success`, `--studio-accent-6`, `--severity-pass`, `--severity-warn`, `--severity-high`, `--severity-info`, `--severity-pending`. **Do not** pass a raw hex.

**Don't** rebuild the recipe inline. If your pill needs the same shape and tint behaviour, `@include m.chip(...)`. If it genuinely needs a different shape (e.g. a square corner), discuss the variant in this doc.

## Chip — neutral (proposal)

A short label without an accent tint. Today every consumer rebuilds:

```scss
display: inline-flex; align-items: center; gap: 4px;
height: 18px; padding: 0 6px; border-radius: 4px;
background: var(--studio-bg-elevated);
color: var(--studio-fg-dim);
font-size: 11px;
```

**Proposed mixin**:

```scss
@mixin chip-neutral {
  display: inline-flex;
  align-items: center;
  gap: var(--studio-spacing-1);
  height: 18px;
  padding: 0 6px;
  border-radius: 4px;
  background: var(--studio-bg-elevated);
  color: var(--studio-fg-dim);
  font-size: 11px;
  white-space: nowrap;
}
```

Decision deferred to a follow-up slice — see [migration-status.md](./migration-status.md) "P-Pills: ship `chip-neutral` mixin".

## Badge — numeric count (proposal)

Numeric badge anchored to a tab or a row. Canonical-ish today via `.pane-tab__badge`:

```scss
min-width: 16px; height: 16px; padding: 0 5px;
background: var(--studio-accent); color: var(--studio-on-accent);
border-radius: 8px;
font-size: 10px; font-weight: 700;
display: grid; place-items: center; line-height: 1;
```

**Proposed mixin**: lift the `.pane-tab__badge` recipe into `@mixin badge-count` so it is reusable. Decision deferred — see [migration-status.md](./migration-status.md) "P-Pills: ship `badge-count` mixin".

## Tag — free-form (per-feature)

Short label inside a card / row, no tint, no count semantics. Today four call sites (`.create-dialog__enhance-tag`, `.psr-modal__file-tag`, `.psr-modal__skill-tag`, `.steer-card__upload-btn`). Footprint is small enough that a shared mixin is not yet justified.

## Status labels use `<app-status-indicator>`

A state (connected, failed, running, unknown) is not a pill any more: use the
shared `<app-status-indicator tone="..." label="...">` with its tone icon and
sentence-case label. See [status-indicator.md](../../design/status-indicator.md).
The pill recipe below remains for legacy mode labels until the
[inventory](../../design/status-display-inventory.md) migrates them.

## Open question — should we ship `<app-pill tone="...">`?

A typed Angular wrapper would give:

```html
<app-pill tone="info">Running</app-pill>
<app-pill tone="danger" leading="circle">Failed</app-pill>
```

The cost is a small standalone component + a wrapper around the mixin. The benefit over the mixin is **less SCSS-per-consumer** and a **typed tone enum** that maps to the right accent token.

**Decision deferred** — proposal in [migration-status.md](./migration-status.md) "F-Pills: Decide canonical". The mixin migration is queueable today without committing to the Angular wrapper.

## DON'Ts

- **Do not** rebuild the chip recipe inline. Reach for `@include m.chip(...)`.
- **Do not** introduce a new accent for a pill. The semantic tokens cover every meaningful state.
- **Do not** raise the height above 18px for a pill. If your label needs more height, it is a button or a tag, not a pill.
- **Do not** ship a pill without a `text-transform: uppercase`. The pill recipe is the one place we keep the uppercase convention; chips and tags stay sentence-case.

## Light + dark

Every pill flips automatically because the mixin reads tokens; the `color-mix` recipe produces the tint at runtime and works against both Mocha and the white shell. If you observe legibility issues on light, the right knob is the **strong-pigment variant** of the accent (`--studio-accent-success-strong`, `--studio-accent-warn-strong`, `--studio-accent-6-strong`, `--studio-accent-3-strong`) — see F39 in `_tokens-semantic.scss`.
