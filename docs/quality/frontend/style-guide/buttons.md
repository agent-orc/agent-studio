# Buttons

Three button families in the shell. Pick the one that matches the use case; do **not** roll a new class for each feature.

| Family       | Use it for                                                          | Canonical                                                |
| ------------ | ------------------------------------------------------------------- | -------------------------------------------------------- |
| Icon-only    | A square / near-square icon affordance with no label                | SCSS mixin `m.icon-button($size)` in `_mixins.scss`      |
| Chrome (sm)  | Inline action in the top bar / status bar / sidebar                 | `.header-btn` in `app.scss` (reads `--header-btn-*` vars) |
| Action (md)  | Dialog footer + form submit, "Primary / Cancel" action rows         | `.btn` + `.btn--primary` / `.btn--danger` / `.btn--ghost` |

See [audit-buttons.md](./audit-buttons.md) for the inventory of every existing variant in the codebase and where they live.

## Page-head action bar

Repository pages use the shared `<app-page-action-bar>` rather than assembling
their own button group. The four standard actions are always Create Task in
Project, Archive, Open in Orchestrator Chat, and Pin to Home. A page type may
append one specific action after those standards.

Pin to Home uses the pin icon and the tooltip "Curated entry point for
everyone." It must not reuse the star icon or the Starred collection. Star
controls use the tooltip "Your personal shortlist."

The component reads semantic Studio surfaces, borders, foregrounds, accent,
hover, spacing, and elevation. Its production variants and both themes are
shown in the
[Visual StyleGuide page action Dossier](../../visual-styleguide-workbench-wiki/index.html).
The interaction contract and page-context decision are documented in
[Wiki as a Cognitive Interface](../../../concepts/wiki-as-cognitive-interface.md).

## Icon-only button

```scss
.my-feature__close {
  @include m.icon-button(22px);
}
```

```html
<button type="button" class="my-feature__close" aria-label="Close">
  <app-studio-icon name="close" [size]="14"></app-studio-icon>
</button>
```

**Sizes**: 22px (default), 24px (pane-header), 26px (sheet / dialog close). Pass the size as the mixin argument; never override `width`, `height`, `padding`, or `border-radius` afterward — the mixin owns those.

**Geometry** (set by the mixin):

| Property      | Value                                          |
| ------------- | ---------------------------------------------- |
| Display       | `inline-grid; place-items: center`              |
| Background    | `transparent`                                   |
| Color         | `--studio-fg-dim` → `--studio-fg-strong` (hover) |
| Hover wash    | `--studio-bg-hover`                             |
| Border        | `0`                                             |
| Border radius | `3px` (shape step `xs`)                         |
| Focus ring    | `outline: 2px solid --studio-accent; offset 1px`|
| Disabled      | `opacity: 0.45; cursor: not-allowed`            |

**Don't** add a per-button hover color, a per-button focus ring, or a per-button radius. If a use case genuinely needs a different geometry (e.g. an active "selected" state), discuss the variant in this doc before forking the mixin.

### Open question — should we ship `<app-icon-button>`?

A typed Angular wrapper would give:

- Compile-time guarantee of `aria-label`.
- Type-checked `size` and `variant` props.
- Easier discovery — newcomers find the component in `components/` instead of needing to know the mixin exists.

The cost is a small standalone component (~30 lines TS + 5 lines HTML + a thin SCSS wrapper around the mixin). **Decision deferred** — proposal in [migration-status.md](./migration-status.md) "F-Buttons: Decide canonical".

## Chrome button (`--header-btn-*`)

Used by every interactive control in the top header (project chips, action buttons, dropdown triggers, kebab squares). The container declares four CSS custom properties and every child reads them:

```scss
.my-header-region {
  --header-btn-height: 28px;
  --header-btn-radius: 6px;
  --header-btn-padding-x: 10px;
  --header-btn-gap: 6px;
}

.my-header-region__action {
  display: inline-flex;
  align-items: center;
  gap: var(--header-btn-gap);
  height: var(--header-btn-height);
  padding: 0 var(--header-btn-padding-x);
  border-radius: var(--header-btn-radius);
}
```

The `.header-btn` class in `app.scss` is the shipped implementation; consumers add it to their `<button>` directly.

**Don't** declare a custom height for a chrome control. If you need a square icon variant, set `width: var(--header-btn-height); padding: 0;` and add the `.header-btn--icon` modifier.

### Open question — should `--header-btn-*` move to tokens?

`--header-btn-*` lives in `app.scss` today. Lifting it into `_tokens-semantic.scss` as `--studio-chrome-btn-*` would let any component read it without inheriting the `.header` block. **Decision deferred** — proposal in [migration-status.md](./migration-status.md).

## Action button (`.btn`)

Used in dialog footers and the few inline forms with explicit submit / cancel pairs.
Standalone components with encapsulated styles use `@include m.action-buttons`
from `styles/mixins` to apply these same `.btn` and `.btn--primary` recipes.
This keeps action chrome available outside the shell's own style scope.

```html
<button type="button" class="btn btn--ghost" (click)="onCancel()">Cancel</button>
<button type="button" class="btn btn--primary" (click)="onConfirm()">Save</button>
```

**Variants**:

| Class            | Use it for                                                      |
| ---------------- | --------------------------------------------------------------- |
| `.btn`           | Secondary action; safe neutral alternative in the same context  |
| `.btn--primary`  | The single preferred action in its group (accent fill)          |
| `.btn--danger`   | Destructive action such as abort, delete, discard, or revoke    |
| `.btn--ghost`    | Tertiary reveal, cancel, or dismiss action                       |
| `.btn--create`   | One-off "Create task" purple variant in the top header          |

Sibling text buttons share one height, inset, radius, type size, focus ring,
and disabled treatment. A group has at most one primary. Danger communicates
impact, not general importance. A static label beside the group does not use
any of these classes; follow the
[living label contract](./living-rules.md#labels-are-not-buttons).

**Don't** introduce a new variant (e.g. `.btn--my-feature`) inline. If the new shape is reusable, propose it here; if it is a one-off, the answer is almost always "use `.btn--ghost` instead and let the icon / position carry the per-feature meaning".

### Open question — `<app-button>`

A typed Angular wrapper around `.btn` would let consumers write:

```html
<app-button variant="primary" size="md" (click)="save()">Save</app-button>
```

instead of remembering the class names. The migration would (a) lift `.btn` from `app.scss` to a component SCSS, (b) provide a wrapper Angular component, (c) migrate the ~20 known consumers in slices. **Decision deferred** — proposal in [migration-status.md](./migration-status.md) "F-Buttons: Decide canonical".

## DON'Ts

- **Do not** use `<button>` with a fresh per-feature SCSS class for a small icon affordance. Reach for `@include m.icon-button(...)`.
- **Do not** copy-paste `.header-btn` rules into a per-component SCSS file. Add the class directly or reference the `--header-btn-*` vars.
- **Do not** introduce a new color for a button background. The palette has `--studio-accent` (orange), `--studio-accent-3` (blue), `--studio-accent-6` (red), and the elevated/hover surfaces. If your case needs another color, the answer is almost always a different variant of an existing token.
- **Do not** set `font-family: ...` on a button. The default UI family is inherited from the shell.
- **Do not** ship a button without a focus-visible style. The mixin and `.btn` both ship one; check that yours is still legible.

## Light + dark

Every button in this guide flips automatically because they all read `--studio-*` tokens. If your button uses a raw `rgba(...)`, add the light-theme bridge in `frontend/src/styles.scss` until the SCSS is migrated. **Better**: replace the raw value with a token.
