# Forms

**No canonical Angular form-control component today.** Inputs, selects, and textareas are bare HTML elements with feature-scoped SCSS. See [audit-forms.md](./audit-forms.md) for the 17+ existing variants.

This page proposes a **minimum-viable convergence** that does not require committing to ControlValueAccessor or a typed component API.

## The convergence target

Every form control in the shell should match this shape:

| Slot           | Token / value                                                           |
| -------------- | ----------------------------------------------------------------------- |
| Background     | `var(--studio-bg-elevated)`                                              |
| Border         | `1px solid var(--studio-border)`                                          |
| Border (focus) | `1px solid var(--studio-accent)` with `outline: 2px solid color-mix(in srgb, var(--studio-accent) 35%, transparent); outline-offset: 0;` |
| Border radius  | `4px` (shape step `sm`)                                                  |
| Color          | `var(--studio-fg)`                                                       |
| Placeholder    | `var(--studio-fg-muted)`                                                 |
| Height         | `28px` (chrome) / `32px` (form body)                                     |
| Padding-x      | `var(--studio-spacing-2)` 8px (chrome) / `var(--studio-spacing-3)` 12px (form body) |
| Font           | `12px` (chrome) / `13px` (form body); `var(--font-ui)`                   |

Two heights, two paddings, the rest shared.

## Shipped mixins (AGT-4029)

`m.form-control($size)` and `m.form-field` are in
[`_mixins.scss`](../../../../frontend/src/styles/_mixins.scss). They follow the
table above, with two deliberate readings of it: the background is
`--studio-bg-surface` and the resting border `--studio-border-strong`, so a
field stays visible on the flat admin surface in both themes; heights and type
sizes read `--studio-action-height-sm`, `--studio-row-min-h-interactive` and
`--studio-font-size-*` instead of raw px. The Access page is the first
consumer. The sketch below is the original proposal.

## Proposed mixin

Today every form-control SCSS rebuilds the recipe. The right next step is a mixin:

```scss
@mixin form-control($size: 'md') {
  display: inline-flex;
  align-items: center;
  background: var(--studio-bg-elevated);
  color: var(--studio-fg);
  border: 1px solid var(--studio-border);
  border-radius: 4px;
  font-family: var(--font-ui);
  box-sizing: border-box;
  transition: border-color 0.12s ease, box-shadow 0.12s ease;

  @if $size == 'sm' {
    height: 28px;
    padding: 0 var(--studio-spacing-2);
    font-size: 12px;
  } @else {
    height: 32px;
    padding: 0 var(--studio-spacing-3);
    font-size: 13px;
  }

  &::placeholder { color: var(--studio-fg-muted); }

  &:hover:not(:disabled) { border-color: var(--studio-border-strong); }

  &:focus, &:focus-visible {
    border-color: var(--studio-accent);
    outline: 2px solid color-mix(in srgb, var(--studio-accent) 35%, transparent);
    outline-offset: 0;
  }

  &:disabled { opacity: 0.45; cursor: not-allowed; }
}
```

**Decision deferred** — see [migration-status.md](./migration-status.md) "F-Forms: ship `form-control` mixin". A mixin slice is queueable today; an Angular component slice waits.

## Usage (post-mixin)

```scss
.my-feature__input {
  @include m.form-control('sm');
  width: 100%;
}

.my-feature__select {
  @include m.form-control('md');
  appearance: none;
  padding-right: var(--studio-spacing-5); // room for the chevron
  background-image: url('data:image/svg+xml;...'); // chevron data-uri
  background-repeat: no-repeat;
  background-position: right 8px center;
}

.my-feature__textarea {
  @include m.form-control('md');
  height: auto;
  min-height: 80px;
  padding-block: var(--studio-spacing-2);
  resize: vertical;
}
```

## Special-case form-controls (intentionally bespoke)

A few search-as-you-type combo controls stay bespoke because their interaction is non-standard:

- `<app-board-search>` — search icon + expanding field + dropdown suggestions.
- The top-header command bar — autocomplete + multi-step entry.

These are intentionally outside the convergence target. The mixin is for **plain** input / select / textarea controls; the bespoke ones own their geometry.

## Toggle switches and checkboxes

Native `<input type="checkbox">` is fine for most forms. The few visual toggle switches in the codebase (`autonomy-slider`, dev-tools enable/disable rows) are one-offs.

A future `<app-toggle>` component is a clear extraction target once `<app-input>` lands, but the present footprint is small enough that it stays per-feature.

## Open question — should we ship `<app-input>` / `<app-select>` / `<app-textarea>`?

A typed Angular component family would give:

```html
<app-input
  type="text"
  [(value)]="title"
  placeholder="Task title"
  size="sm"
  [error]="titleError()"
  helperText="Required">
</app-input>
```

The cost is meaningfully more than the button / pill ones (ControlValueAccessor, error / helper-text slots, a11y `aria-describedby` wiring, async-pipe friendly value bindings). The mixin slice captures most of the visual consistency without committing the API.

**Decision deferred** — see [migration-status.md](./migration-status.md) "F-Forms: Decide canonical". Realistic sequence: mixin first; Angular wrapper later.

## DON'Ts

- **Do not** introduce a new background for an input. The palette is `--studio-bg-elevated` for raised inputs, `--studio-bg-editor` for body-canvas inputs in a search box. Pick one.
- **Do not** add a per-feature focus ring. The contract above is the shared focus style.
- **Do not** use raw `rgba(...)` for the border. `--studio-border` / `--studio-border-strong` cover the cases.
- **Do not** raise the height above 32px without naming a reason. Tall inputs disagree with the IDE density.

## Light + dark

The mixin reads `--studio-*` tokens; both themes flip automatically. Verify with Playwright + theme toggle on every new form.
