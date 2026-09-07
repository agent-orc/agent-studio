# Style-Guide Hard Rules

Hard, non-negotiable design rules for the product surface. These are not
suggestions or "house taste": a change that breaks one of them is a regression
and should be fixed before it ships. Keep this page short. Rationale lives in the
linked docs, not here.

This page is **prompt-known**: it is referenced from [AGENTS.md](../../../AGENTS.md)
and [frontend/AGENTS.md](../../../frontend/AGENTS.md) so every coding card that
touches the UI sees it. When you add or change a load-bearing visual rule, add
it here first, then let the component follow.

For the navigable style-guide family, technology applicability, and the process
for changing a rule, start at [docs/quality/](../README.md). This page
remains the canonical non-negotiable UI baseline linked from that index.

## The rules

### R1 - No left accent lines or bars

**No coloured left accent line or bar on cards, panels, list rows, banners,
callouts, or pill groups. Nowhere.** This means no decorative or
status-encoding `border-left`, `border-inline-start`, or left-edge
`box-shadow: inset Npx 0 0 <colour>` used as a stripe.

Encode status a different way:

- **Background tint** - the whole surface takes a low-alpha tone of the state
  colour (the preferred replacement).
- **Badge** - a small labelled chip.
- **Dot** - a single status dot next to the title.

Why: the left stripe reads as visual noise and never as hierarchy; it makes
every panel look busy without adding legibility. Background tone, a badge, or a
dot all carry the same information and read cleaner in both themes.

The **one** sanctioned left bar is the navigation active-item indicator
(`--studio-nav-active-bar`, [ADR-0061](../../system/architecture/decisions/proposed/adr-0061-unified-nav-active-item-token-contract.md),
AGT-2010). That is a selection affordance on a nav rail governed by its own token
contract, not a status or decoration stripe on a card, so it is out of scope for
R1. Do not use it as a loophole to reintroduce accent stripes elsewhere.

### R2 - Full-bleed views use the viewport seamlessly

Full-surface views (board, deck, task detail, settings home) fill the
viewport with no artificial `max-width` cap. Do not centre a wide view inside a
narrow column. This is existing product policy; a `max-width` on a top-level view
is the regression.

### R3 - Aggregate numbers equal the sum of their visible children

Any total, count, or roll-up equals the sum of the children the user can
actually see under it. Chips, table footers, charts, and headers must reconcile
to the same number. This is the AGT-2017 sum invariant. A total that does not add
up is a bug, not a rounding choice.

### R4 - Acute signals only for acute states

Loud, attention-grabbing signals (pulsing dots, warning colours, urgent badges)
are reserved for states that are actually acute right now. History and settled
outcomes render quietly. A finished run, an archived item, or a past warning must
not keep shouting. This is the AGT-2049 rule: acute treatment for acute states,
history stays calm.

Status badges on task cards are a projection of current truth only: current
lane, current integration status, and the latest run outcome. Decision-journal
entries, earlier run failures, reissue tags, and other events belong in the
timeline and do not remain as quiet or acute card badges. Contradictory pairs
are invalid by construction: `integrated` / `merged` excludes `Integration
error`, and the Review lane excludes `Escalated`. This is the AGT-2410 Living
Style Guide card-status contract.

### R5 - Both themes, always; respect reduced motion

Every visual change works in **both** light and dark themes. Light is the daily
driver; dark must not regress. Read tokens (`--studio-*`), never hardcode a hex
that only works in one theme. Every animation collapses to zero duration under
`@media (prefers-reduced-motion: reduce)`.

### R6 - Action and loading feedback follows one timing contract

Reversible lane mutations paint optimistically, confirm with a quiet toast and
offer Undo. A failed persistence rolls the UI back and explains the failure.
This applies to Accept, Requeue, Archive, and future equivalent moves.

Actions that cannot safely paint ahead use the shared `appPendingButton`
capability: react immediately, disable while pending, expose `aria-busy`, and
show the standard spinner plus action-specific label. Do not hand-roll a new
pending button at each call site.

Main surfaces use `app-loading-surface`, not a blocking page spinner. It stays
hidden for the first 200 ms, shows a structural skeleton after that threshold,
and adds contextual loading copy after one second. Loading feedback never
blocks unrelated navigation or controls.

### R7 - Admin surfaces use the flat admin grammar

New or touched admin surfaces follow the binding
[Admin Surface Design Guideline](../../operations/admin-design-guideline/index.html).
They stay full-bleed and flat, use one grid for comparable stream entries,
keep normal headers to one line with detail disclosed on demand, avoid
repeating implied facts, and use sober operational copy. Cross-project
identity is the shared project colour mark plus registry short code. The
guideline also owns compact density, reading-width, theme-token, and
self-hosted-font rules for this surface family.

Adoption is incremental. Apply the rule to the new or touched surface and its
immediate component family. Record unrelated legacy drift separately instead
of turning one UI card into a broad restyling pass.

### R8 - Panels contain vertical scroll keys

A panel that owns scrolling also owns its vertical viewport keys while focus is
inside its content surface. Make that surface focusable and handle
`ArrowUp`, `ArrowDown`, `PageUp`, `PageDown`, `Home`, and `End` against its own
scroll owner. Prevent the handled browser default and stop propagation,
including at a scroll boundary, so parent board or workspace navigation cannot
consume the same key.

Move focus into the active content surface when a panel opens. Parent keyboard
navigation remains available only while its own board, list, or tree surface
has focus. Preserve the native semantics of editable and composite controls,
and do not contain `Tab` or `Escape`. The detailed embedding contract and its
CAC-22 precedent are recorded in the
[Admin Surface Design Guideline](../../operations/admin-design-guideline/index.html#panel-keyboard-containment).

### R9 - One name and one tone per lane, from one source

A workflow lane is named and tinted in exactly one place:
`frontend/src/app/models/lane-presentation.ts` plus the `--studio-lane-*` tone
tokens it maps onto. Every surface that shows a lane - board column header,
task header chip, Result tab header, verdict signals, badges, settings lists,
lane pickers, info buttons - reads that module. No component keeps its own
lane label or its own per-lane colour rule.

Bind `[attr.data-lane-tone]="laneTone(state)"` and include the shared
`lane-tone.vars` mixin rather than writing a per-lane selector. R1 still
applies: a lane is encoded by its word, a dot, or a background tint, never a
left accent bar.

Why: one lane once carried four wordings and two tones on a single screen -
"Review" on the header chip, "Human review lane" on the Result header, "Human
review" on the decision badge, "Awaiting human review." in the project workflow
section (operator 2026-09-06, AGT-2715). Each surface had grown its own label
map, so the drift was structural rather than a typo.

Enforced by `npm run lint:structure`
(`frontend/scripts/check-lane-strings.mjs`), which fails the build on a
hard-coded lane string. The full contract is in
[docs/system/domains/frontend.md](../../system/domains/frontend.md#lane-presentation-one-name-one-tone-one-source).

## How this is enforced

- **Prompt anchoring:** referenced from [AGENTS.md](../../../AGENTS.md) and
  [frontend/AGENTS.md](../../../frontend/AGENTS.md), which the coding CLIs load as
  project instructions on every run.
- **Design system:** the token and component vocabulary that makes these rules
  cheap to follow lives in
  [docs/quality/frontend/design-system.md](../frontend/design-system.md) and
  [docs/quality/frontend/style-guide/](../frontend/style-guide/README.md).
- **Product principles:** the "why" behind R2/R4/R5 lives in
  [docs/quality/design-principles.md](../design-principles.md).
- **Admin reference:** the fully specified light/dark Activity surface,
  violation audit, and adoption contract live in the
  [Admin Surface Design Guideline](../../operations/admin-design-guideline/index.html).
