# Migration Status

Tracker for the design-system convergence work. Each row is a **queueable task** that lands as its own small slice. The roll-up exists so the user can see progress without having to grep the codebase.

**Hard rule** (from the parent task `feature-central-style-guide-and-design-system-consolidate-buttons-controls-modals`): no big-bang. The audit + docs + modal-padding fix land in one PR; everything below is a follow-up slice.

## Status legend

- 🟢 done — slice shipped, the canonical is used by every consumer it should be
- 🟡 in progress — slice queued or partially shipped
- ⚪ proposed — slice not queued yet; the row is the proposal
- ⚫ deferred — slice intentionally not queued because the canonical decision is open

## Tokens

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| `--studio-spacing-1..7` shipped                                   | 🟢     | Added in this PR; `_tokens-semantic.scss`                                                    |
| `--studio-modal-padding / -body / -header / -footer / -body-sm` shipped | 🟢  | Added in this PR; `<app-dialog>` reads them                                                  |
| Promote shape scale to `--studio-radius-xs..2xl` tokens           | ⚪     | Today the scale is documented in `docs/quality/frontend/design-system.md` but radii are still raw px         |
| Promote type ramp to `--studio-font-size-*` tokens                | ⚪     | Same as radii — documented, not tokenised                                                   |
| Lift `--header-btn-*` from `app.scss` into `_tokens-semantic.scss` as `--studio-chrome-btn-*` | ⚪ | Small slice; benefits every chrome control       |
| Add `--studio-input-height / -padding-inline / -radius` tokens     | ⚪     | Pre-mixin step for the form-control convergence (see F-Forms below)                          |

## Family — Modal (`<app-dialog>`)

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Modal padding token contract                                       | 🟢     | Body 24px / header 16px / footer 16px / sm-body 16px; this PR                                |
| `<app-dialog>` gets `size="sm"` input                              | 🟢     | This PR; confirm-dialog opts into `sm`                                                       |
| Per-feature overlays read `--studio-modal-padding-*` + `--studio-scrim` (verbose-debug, orchestrator-settings, cli-usage-detail, update-center, media-lightbox) | ⚪ | Follow-up — file as `M-Modal: overlay tokens` |
| Open question — `size="lg"` variant                                | ⚫     | Not shipped; deferred until a real consumer needs it                                         |

## Family — Buttons

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Icon-only button — mixin canonical                                 | 🟢     | `m.icon-button($size)` in `_mixins.scss`; 5 sites already use it                              |
| Chrome button — `--header-btn-*` shared vars                       | 🟢     | Works as a vars contract; lifts to tokens is the next slice                                  |
| Action button — `.btn`, `.btn--primary`, `.btn--danger`, `.btn--ghost` | 🟡 | Lives in `app.scss`; reads raw `rgba()` and is on the Stylelint warning baseline             |
| **F-Buttons: Decide canonical** — `<app-button>` Angular component or stay on mixin + `.btn` class | ⚪ | Decision needed before any further migration. See [buttons.md](./buttons.md) |
| Migrate `.evidence-btn`, `.ov-copy-btn`, `.cli-usage-modal__icon-button`, `.chat__icon-btn`, `.md-editor__icon-btn`, `.upd-center__icon-btn` to `@include m.icon-button` | ⚪ | Per-file slices; no big-bang |
| Migrate `.devtools-menu__trigger` to `.header-btn--icon`           | ⚪     | Single-file slice                                                                            |
| Deprecate `.studio-button` / `.studio-button--primary` / `.studio-button--ghost`; consumers move to `.btn` family | ⚪ | One-file slice (one file ships these); ~3 consumer rules in `studio-shell.component.html` |
| `.btn--create` (purple variant) reviewed — keep as canonical or fold into accent variant | ⚪ | Decision needed before migration |
| Family E sweep — ~35 feature-scoped per-component button classes migrate **opportunistically** when their owning file is touched | ⚪ | Not a single slice; tracked here so the eventual count goes down |

## Family — Pills / Chips / Badges

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Pill canonical via `m.chip` mixin                                  | 🟢     | Already shipped in `_mixins.scss`; `.column__status-pill` is the reference consumer           |
| **P-Pills: ship `chip-neutral` mixin**                             | ⚪     | Captures ~11 inlined neutral-chip recipes (`.commandbar__field--chip`, `.detail__key-chip`, ...) |
| **P-Pills: ship `badge-count` mixin**                              | ⚪     | Captures ~12 inlined count-badge recipes (`.column__subsection-count`, `.proj-detail__banner-count`, ...) |
| **P-Pills: ship `lane-pill($lane)` mixin**                          | ⚪     | Lane-pill geometry duplicated across column header / job card / filter sidesheet            |
| **F-Pills: Decide canonical** — `<app-pill tone="...">` Angular component or stay on mixins | ⚪ | Decision needed before extracting an Angular wrapper |
| Migrate `.job-card__*-pill`, `.evidence-row__ack-pill`, `.pdov__score-pill` to `@include m.chip(...)` | ⚪ | Per-file slices, after deciding canonical |

## Family — Cards

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Shape-and-token contract documented                                | 🟢     | [cards.md](./cards.md) — `--studio-bg-elevated` + `--studio-border` + `--studio-spacing-3` + `--elevation-card` |
| **F-Cards: Decide canonical** — `<app-card variant="default\|elevated\|accent">` or stay on per-feature SCSS with the shape contract | ⚪ | Decision needed |
| Audit card SCSS files for shape compliance                         | ⚪     | Mechanical sweep — every `.foo-card` reads the four tokens; convert raw values            |
| Bring `.cli-usage-modal__headroom-card`, `.pdov__report-card`, `.ux-panel__ref-card` onto the shape contract | ⚪ | Three one-off cards |

## Family — Tabs

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| `<app-segmented-control>` for compact pick-one-of-N choices | 🟢 | One quiet track and raised thumb; template lint rejects new hand-built choices |
| Pill tabs share segmented track and thumb mixins | 🟢 | `pane-tabs--pill` keeps `tablist` semantics |
| `<app-pane-tabs>` canonical                                        | 🟢     | F38; `variant="header"` + `variant="pill"`                                                   |
| Prompt-pane / protocol-pane on `<app-pane-tabs>`                   | 🟢     | Already migrated                                                                             |
| **T-Tabs: studio-shell editor tab strip evaluation** — migrate to `<app-pane-tabs variant="strip">` (new variant) or keep `.studio-tab` bespoke | ⚪ | Decision needed; the editor tab strip carries drag-reorder + close-per-tab |
| **T-Tabs: top-header project switcher (`.project-tab`)** — bring onto `<app-pane-tabs>` tokens even if it stays a separate class | ⚪ | Small slice |

## Family — Navigation

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Sidebar / rail navigation canonical                               | 🟢     | `<app-section-header>` + `<app-tree-row>`; documented in [navigation.md](./navigation.md) |
| Deck rail uses shared navigation controls                          | 🟢     | `project-shell` renders grouped rails through section-header + tree-row |
| Deck-style Workspace Settings variant                              | 🟢     | General / Global / Workspace groups reuse section-header + tree-row with monochrome glyphs |
| Prompts registry inventory uses shared navigation controls         | 🟢     | Workspace and project views mount one `prompt-catalogue-nav` with shared origin, stale, and telemetry metadata |
| Audit remaining per-feature navigation lists                       | ⚪     | Opportunistic sweep before adding a separate audit-navigation page |

## Family — Forms

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Shape-and-token contract documented                                | 🟢     | [forms.md](./forms.md)                                                                         |
| **F-Forms: ship `form-control` mixin**                             | ⚪     | Mixin signature in [forms.md](./forms.md); first slice toward convergence                      |
| **F-Forms: Decide canonical** — `<app-input>` / `<app-select>` / `<app-textarea>` Angular components or stay on mixins | ⚪ | Bigger commitment than the others (ControlValueAccessor + error / helper-text slots) |
| Migrate `.commandbar__field`, `.chat-compose__input`, `.cli-config__input`, ... to the mixin | ⚪ | Per-file slices after the mixin lands |
| `<app-toggle>` for visual toggle switches                          | ⚫     | Deferred — only two consumers today; not worth a separate component yet                      |

## Lint enforcement

| Slice                                                             | Status | Notes                                                                                       |
| ----------------------------------------------------------------- | ------ | ------------------------------------------------------------------------------------------- |
| Stylelint `color-no-hex` on new SCSS                               | 🟢     | Already shipped; legacy files on a warning baseline                                          |
| Stylelint `scale-unlimited/declaration-strict-value` for color    | 🟢     | Already shipped; same baseline                                                               |
| Stylelint warning on common hardcoded spacing literals (`padding: 12px`, `gap: 8px`, ...) | 🟡 | Added in this PR as `warning` severity; promotion to `error` after baseline is shrunk |
| ESLint warning on `style="..."` inline-style attributes in `*.html` templates | 🟡 | Added in this PR as `warning` severity; programmatic `[style.foo]=...` bindings are exempt |
| Promote spacing warning to error                                   | ⚪     | After the per-family migrations shrink the baseline                                          |

## Phase 2 (not queued; intentionally deferred)

- **Live `/style-guide` page** in the app (dev-only). Useful once the canonical-component decisions settle. Today the docs are the source of truth.
- **Storybook integration**. Same trigger — settle the components first.
- **Token-level extraction** of radii and font sizes (see "Tokens" section).

## Phase 3 (not queued; far-future)

- **Whole-theme rebuild**. Out of scope — we stay on Catppuccin (dark) + the F19 light shell.
- **Animations / motion tokens**. The shell has a small motion vocabulary documented in `docs/quality/frontend/design-system.md`; a token layer is not currently load-bearing.

## How to update this page

When you ship a slice from this list:

1. Flip the status emoji (⚪ → 🟢, or 🟡 → 🟢).
2. Add a one-line note that names the commit / PR.
3. If the slice opened a new follow-up, add a new ⚪ row beneath.
4. **Do not** delete a 🟢 row — the visible history matters; it is what the user sees as progress.

If a slice gets explicitly cancelled, change the row to ⚫ and add a one-line reason.
