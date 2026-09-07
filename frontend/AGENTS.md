# Frontend Instructions

## Style-guide hard rules (prompt-known)

The non-negotiable design rules live in
[docs/quality/design/style-guide-hard-rules.md](../docs/quality/design/style-guide-hard-rules.md).
New or touched admin surfaces also use the flat-surface contract and reference
mockup in
[docs/operations/admin-design-guideline/index.html](../docs/operations/admin-design-guideline/index.html).
Adoption is incremental, not a broad restyling mandate.
The technology-aware navigation entry and prompt applicability contract live in
[docs/quality/](../docs/quality/README.md); UI work uses its
[Angular component guide](../docs/quality/angular-components.md).
Read them before any visual change; they override local convention. In short:

- **R1 - No left accent lines or bars.** No coloured `border-left`,
  `border-inline-start`, or left-edge `box-shadow: inset Npx 0 0 <colour>` used
  as a decorative or status stripe on cards, panels, list rows, banners,
  callouts, or pill groups. Encode status via **background tint, badge, or dot**
  instead. The only sanctioned left bar is the navigation active-item indicator
  (`--studio-nav-active-bar`, AGT-2010) covered by the nav contract below.
- **R2 - Full-bleed views** fill the viewport; no artificial `max-width` cap on a
  top-level view.
- **R3 - Aggregate numbers = sum of visible children** (AGT-2017 sum invariant).
- **R4 - Acute signals only for acute states**; history renders quietly (AGT-2049).
- **R5 - Both themes always**, read tokens (never a one-theme hex), and every
  animation collapses to zero duration under `prefers-reduced-motion: reduce`.
- **R9 - One name and one tone per lane, from one source.** A lane's display
  name, role sentence, glyph, tone, and help topic live only in
  `src/app/models/lane-presentation.ts`. Import `laneName()` / `laneSentence()`
  / `laneGlyph()` / `laneTone()`; bind `[attr.data-lane-tone]` and include the
  `styles/_lane-tone.scss` `vars` mixin instead of writing a per-lane colour
  rule. `npm run lint:structure` fails on a hard-coded lane string. See the
  Lane presentation section below.

## UI Verification — Playwright is mandatory after visual changes

After **every** frontend change under `frontend/src/` that touches layout, spacing, styling, component templates, or interaction states, you must verify with Playwright before declaring the task done. Static type-checks and unit tests do not catch UI regressions; the E2E suite does.

Workflow:

1. Make sure backend (`http://localhost:5030`) and frontend (`http://localhost:4010`) are running. Tests do **not** spawn them — they fail fast if missing.
2. Run the relevant spec, or the full suite for cross-cutting changes (sh — never PowerShell):
   ```sh
   npm --prefix frontend run e2e                          # full suite, headless
   npm --prefix frontend run e2e -- e2e/cli-usage.spec.ts # single spec
   npm --prefix frontend run e2e:ui                       # interactive UI mode
   npm --prefix frontend run e2e:headed                   # watch the browser
   SKIP_BILLABLE=1 npm --prefix frontend run e2e          # skip CLI-quota-burning specs
   ```
3. If your change isn't covered by an existing spec, **add or extend one** as part of the same change. Regression coverage is the deliverable, not optional.
4. For CLI-execution changes (Claude / Codex / Copilot start path, model wiring, quota), run `claude-hello-world.spec.ts` end-to-end. It uses real quota (~10s, one Haiku call) but is cheap.

Browser-based smoke check (separate from E2E) when you need a visual eyeball:

- The dev server is at `http://localhost:4010`. Verify the changed state renders correctly, text is visible, spacing/alignment look intentional, and detail panels/dialogs involved in the change open and behave.

The full E2E setup, conventions, helpers, and authoring rules live in [e2e/README.md](e2e/README.md).

## Selector convention for E2E tests

When you add interactive elements that a test will need to click or assert on, prefer in this order:

1. **`data-testid="..."`** on the element. Stable, intentional, decoupled from styling.
2. ARIA role + accessible name (`getByRole('button', { name: 'Add Task' })`).
3. Visible text — only for stable user-facing copy.

Never select by CSS class names; they belong to styling and change often.

## Architectural notes

- Standalone components only. No NgModules.
- State via Angular signals; service singletons go in `app/services/`.
- Keep the dark Catppuccin-inspired direction.
- The detail view is a simple protocol view — don't add tabs or metrics grids unless the product direction changes.
- **Menus are text-only.** `<app-menu>` rows never carry decorative icons. The `MenuRow` type has no `icon` field and the template renders no icon span. The single allowed leading affordance is `leadingGlyph` (the coloured-initial chip used by the project picker). Icons elsewhere (toolbars, chips, lane glyphs, the chat model badge pill) are fine. Full rationale in the root AGENTS.md under "Menu surfaces are text-only".

## Run-Switcher UI contract

The task detail view treats every re-open, transition back to Ready, and Post Processing reissue as a new run. The Timeline tab renders chronological run cards with the transition between adjacent runs. The Overview pipeline block renders the current run first, then older archived attempts through the Run-Switcher (`Run #1`, `Run #2`, etc.).

When changing run history or pipeline display code, keep the Run-Switcher backed by archived pipeline execution records, not by the current in-memory record alone. Tests must cover that older runs stay selectable and that the current run remains the active/default pipeline view.

## Spacing: tokens, never raw px

New SCSS reads `padding`, `margin`, and `gap` from the design-token scale in [`src/styles/_tokens-semantic.scss`](src/styles/_tokens-semantic.scss); raw px in those properties is forbidden. The base scale lives at the top of that file as `--studio-spacing-1` … `--studio-spacing-7` (4 / 8 / 12 / 16 / 24 / 32 / 48 px). Semantic aliases sit alongside it: `--studio-rail-*` (collapsed lane-rail), `--studio-modal-padding-*`, `--studio-row-*` (density rows). Reach for an existing alias before introducing a new one.

Why: the lane-rail bug (operator 2026-05-28) was a symptom of per-element intrinsic sizing. Without a token vocabulary the rhythm drifts every time someone tweaks a single child; with tokens every consumer shares the same scale and `_tokens-semantic.scss` is the only place the px number is written.

Stylelint enforces this scoped per file via `scale-unlimited/declaration-strict-value` on `padding*` / `margin*` / `gap` / `row-gap` / `column-gap` in [`.stylelintrc.json`](.stylelintrc.json). Today the rule is ERROR for `src/app/features/board/components/task-column/task-column.scss` (the lane-rail block); the rest of the codebase is grandfathered while the cleanup task lifts more files into the enforced list. When you write a new SCSS file, opt it in by adding it to that override; when you touch a legacy file, prefer the migration over adding more raw px.

If a value legitimately doesn't fit the scale (true one-off like a 1 px hairline, an animation frame, a sub-pixel border), inline a `/* stylelint-disable-next-line scale-unlimited/declaration-strict-value */` with a short rationale on the same line.

## Navigation active-item: one shared token contract (AGT-2010)

Every side menu marks its current entry the same way: a clearly accent-tinted
band plus a bright accent side bar, so the active destination reads at a glance
in both themes. The look is defined once as the `--studio-nav-active-*` tokens in
[`src/styles/_tokens-semantic.scss`](src/styles/_tokens-semantic.scss)
(`-bg`, `-bg-hover`, `-fg`, `-bar`); never hand-roll a per-rail active colour and
never hard-code a hex/rgba for it (two rails used to hard-code off-brand blue and
indigo that ignored the theme).

The canonical nav item is `<app-tree-row>` (Explorer workspace tree, Deck
rail, Prompt catalogue) with `<app-section-header>` group heads and
`<app-count-badge>` counts; `<app-list-row>` is the flat variant (Open tabs).
Reach for those first. When a surface genuinely needs its own row element (the
Workspace / Orchestrator settings rails, the Wiki and Agent-Docs trees, the CLI
session list, the focused-view task rail), style its `--active`/`--selected`
state from the same tokens: `background: var(--studio-nav-active-bg)`, a
`box-shadow: inset 3px 0 0 0 var(--studio-nav-active-bar)` side bar (flush rails)
or a strengthened accent border (card rows), and a `:focus-visible` accent
outline plus `aria-current="page"`. Do not add a new bespoke active look.

## Side-sheet layout contract (`<app-orchestrator-side-sheet>`, `<app-kanban-filter-sidesheet>`)

Both right-edge side sheets are **panels that push the workspace, not overlays that float over it**. The push behaviour rides on three coordinated pieces; break any one and the panel either floats, overlays, leaves a transparent gap, or stacks on the wrong side. Locked by the `open pushes studio-shell + inner panel fills host` test in `e2e/orchestrator-side-sheet-position.spec.ts`.

> History: a third sheet, `<app-cli-usage-sheet>`, used to live here. Its quota glance and per-CLI session inventory were folded into the global Workspace Settings home (the CLI Management / "Usage caps" section, `features/cli/components/cli-admin-panel`) so CLI usage has a single hub; the loose sidesheet was retired. The status-bar "Usage" button now opens that home section instead of a parallel sidesheet.

1. **Flex parent.** In `app.html` the `vsCodeLayout` branch wraps `<app-studio-shell>` + the side sheets in `<div class="app-shell">`, styled `display: flex; flex-direction: row-reverse`. Row-reverse keeps the studio shell on the left (consuming the remaining space via `flex: 1 1 auto`) while the side sheets dock on the right edge in natural DOM order. Without the flex parent the sheets fall back to block layout and stack vertically.

2. **Caller `:host` width animation.** Each side sheet's own SCSS owns the open/close animation:
   ```scss
   :host { display: block; width: 0; transition: width 0.22s ease;
           overflow: hidden; flex: 0 0 auto; }
   :host(.is-open) { width: min(<callerWidth>px, 9Xvw); }
   ```
   `overflow: hidden` is load-bearing — without it the inner panel can render past the host's collapsed 0px width and float over the workspace. `flex: 0 0 auto` keeps the host honouring its specified width (no flex-grow surprises). Callers pick their own px width (kanban-filter 320, orchestrator 640); the host has no business setting a fixed width on the inner panel.

3. **Inner `<app-sidesheet>` width: 100 %.** `components/sidesheet/sidesheet.component.scss` ships `.sidesheet { width: 100% }` so the inner chrome (background, border, header / body / footer) tracks the host's animating width. A previous hard-coded `width: 360px` default left a transparent gap inside any host wider than 360px (visible as unreadable empty space in the orchestrator's 640px slot). Callers that need an explicit px width can still drive it via the `[width]` input.

Anti-patterns to reject in review:
- `position: fixed` on either sheet in `src/styles.scss` or component SCSS. Out-of-flow positioning makes push impossible. (An old workaround did this pre-`app-shell`; the new contract is now described in `styles.scss` where the workaround used to live.)
- A fixed `width: ...` on the inner `.sidesheet` BEM root (the inner panel must mirror the host).
- Adding `flex-direction: row` (non-reverse) to `.app-shell` without also reordering the HTML to put `<app-studio-shell>` first. Current order is `kanban-filter, orchestrator, studio-shell` and relies on row-reverse to dock the sheets right.

When you add a new right-edge side sheet, follow the same three-piece contract and add it to the regression spec.

## Detail-view lane control: dropdown navigates, context menu moves

The task-detail header carries a lane `<select>` (studio shell: `data-testid="studio-lane-select"`; legacy kanban header: the projected `<app-detail-header>` lane select). It is **navigation-only**: picking a lane re-points the Prev/Next pager at that lane and pages to a task already living there — it never changes the current task's lane. The single source of this behaviour is `TaskSelectionService.navigateToLane` (`features/task-detail/state/task-selection.service.ts`); the studio shell wires it through `App.onStudioLaneChange`.

**Moving a task to another lane** lives in the `⋯` overflow context menu (`data-testid="triage-overflow-btn"` → `triage-overflow-item-*`), built by `overflowActionsFor` in `features/task-detail/state/triage-actions.model.ts`. Per the menu-text-only rule above, these rows carry no icons.

**Orchestrator-controlled lanes are never manual targets.** `3-progress` (In Progress) and `4-auto-review` (Post Processing, compatibility key) are owned by the runner / `ReviewDecisionOrchestrator` - a task lands there because it was picked up or is being judged, never because an operator parked it. They are therefore excluded from **both** surfaces:
- the navigation dropdown options (`App.studioLaneOptions`, and the legacy `DetailHeaderComponent.laneOptions`) omit them;
- the context-menu move targets are stripped by `overflowActionsFor`, which filters any move action whose target is in `ORCHESTRATOR_CONTROLLED_LANES` (the canonical set in `triage-actions.model.ts`).

When you add a new manual-target lane or a new move affordance, route it through `ORCHESTRATOR_CONTROLLED_LANES` / `studioLaneOptions` rather than re-deriving the exclusion list inline, so the two surfaces stay in lockstep.

**Pager position is stable across a lane move.** The Prev/Next pager iterates a snapshot captured on detail entry (`LanePagerService`), not the live lane. Moving the current task out of its lane shrinks that snapshot by one and auto-advances to the next captured slug in the *original* lane — the `X / N` context does not jump to the destination lane. See `advanceAfterMutation` / `removeAndAdvance`.

Regression coverage (run with `PW_TARGET=stable`):
- `e2e/task-detail/detail-lane-dropdown.spec.ts` — dropdown pages without moving; orchestrator lanes absent from the nav options.
- `e2e/task-detail/detail-view-lane-pager.spec.ts` — context-menu moves keep the pager anchored on the original lane with the count shrinking by one.

## Lane presentation: one name, one tone, one source (AGT-2715)

A lane is named and tinted in exactly one place:
[`src/app/models/lane-presentation.ts`](src/app/models/lane-presentation.ts).
It carries, per lane state, the display `name`, the prose `sentence`, the
`tone` key, the `glyph`, and the lane-guide `docTopic` — including the virtual
`2-ready-intake` sub-lane and the legacy `4-review` / `1b-needs-human-review`
keys, which borrow their parent lane's word and tone.

Every surface that shows a lane reads it: board column headers, the detail
header chip, the Overview and Description lane pills, the Result tab header,
verdict signals, the decision badge, the task-status card, the Explorer lane
counters, the cycle-time matrix, the project workflow section, and the lane
pickers. **No component keeps its own lane string or its own per-lane colour
rule.**

Colour goes through the tone tokens: bind
`[attr.data-lane-tone]="laneTone(state)"` and `@include lane-tone.vars;` from
[`src/styles/_lane-tone.scss`](src/styles/_lane-tone.scss), then style from
`--lane-tone-fg` / `-bg` / `-border`. The hue itself is still declared once per
theme in the `--lane-*` palette in `_tokens-semantic.scss`.

There is deliberately **no short-name field**. A second, shorter name is what
caused the reported defect: the header chip said "Review" while the Result tab
said "Human review lane", a badge normaliser rewrote that into "Human review",
and the project workflow section said "Awaiting human review." One lane, one
word — `5-human-review` is "Human review" everywhere.

Enforced by `npm run lint:structure` →
[`scripts/check-lane-strings.mjs`](scripts/check-lane-strings.mjs). It fails on
a literal equal to a canonical lane name or role sentence, and on any retired
wording. Genuine non-lane uses of the same word live in
`scripts/lane-string-allowlist.json` with a reason.

Locked by `src/app/models/lane-presentation.spec.ts`,
`.../overview-pane/lane-wording-agrees.spec.ts`, and
`e2e/task-detail/lane-presentation-one-source.spec.ts` (board column, header
chip, and Result header agree on word and resolved colour, both themes).

## Explorer lane metrics mirror the board, and use the lane's own hue (AGT-2676)

The Explorer tree's per-project Board metrics (`<app-explorer-lane-dashboard>`,
numbers **and** dots) are a projection of the flat lane board, so they must count
exactly what the board draws. `buildProjectSidebarRows` therefore runs the board's
own `excludeEpics` over the grouped feed instead of re-deriving `kind !== 'epic'`;
an epic is a container with its own Epics view, not a board work-item. Deriving the
same number twice is what caused the reported bug: an epic parked in
`5-human-review` lit a lane dot on a project whose every board lane read 0.

Lane pigments come from the shared `--lane-*` palette. Human Review reads
`--lane-human-review`; **green is reserved for Delivered** (`--lane-completed`).
The Review counter briefly borrowed the success green and so signalled "done" for
the one lane that means "needs you".

Locked by `studio-shell.project-rows.spec.ts` (dot count == visible board lane
count, with an epic in the fixture) and
`e2e/workspace/explorer-lane-dots-match-board.spec.ts` (both themes).

## Feature folders + barrel imports (ADR-0034, Cycle 9h)

The frontend is organised by **feature** under `app/features/<name>/` with a uniform shape: `models/`, `state/`, `components/`, `services/`, plus a top-level `index.ts` barrel. Cross-cutting capabilities (e.g. `polling/`) follow the same shape.

**Hard rule for cross-feature imports**: import from the **barrel**, not from internal paths.

```ts
// ✅ correct — uses the barrel
import { BoardFiltersService, JobColumnComponent } from './features/board';
import { ProjectOverlaysComponent } from './features/project-detail';

// ❌ wrong — pierces the feature boundary
import { BoardFiltersService } from './features/board/state/board-filters.service';
import { JobColumnComponent } from './features/board/components/job-column';
```

Why: the barrel is the feature's **public API**. Anything not exported from it is private and can be moved/renamed/refactored without external breakage. Deep imports turn every internal file into a contract.

**Inside the same feature**, use relative imports (`./components/...`, `../services/...`) as usual — barrels are about the cross-feature boundary.

When you add a new component or service that needs to be reachable from outside its feature, add it to that feature's `index.ts`. If you find yourself wanting a deep import from outside, that's a signal: either (a) the symbol belongs in the barrel, or (b) what you're trying to do should live inside the target feature, not at the call site.

Existing deep imports across the codebase still work (they're just paths). Convert them to barrel imports as you touch the surrounding code; don't churn for its own sake.

**Enforced by lint** (Cycle 10j): `eslint.config.js` carries a `no-restricted-imports` rule that blocks `**/features/*/{components,services,models,state}/**` from outside the feature. `npm run lint` runs the full ruleset. Files under the same feature and `**/*.spec.ts` / `**/e2e/**` are exempt.

## Folder-per-component layout

Every Angular component lives in its own folder under `components/` (or at the feature root for the feature's entry component). The folder holds the component's `.ts`, `.html`, `.scss`, and `.spec.ts` together — never spread them across the parent directory.

```
components/
  cli-console/
    cli-console.ts
    cli-console.html
    cli-console.scss
    cli-console.spec.ts
```

Shared utility files (e.g. `*.util.ts`, `*.parser.ts`, `*-types.ts`, fixtures) that several components in the area depend on may stay at the parent `components/` level. The rule is one **component** per folder, not one file per folder.

**Enforced by `npm run lint:structure`**: `scripts/check-component-folders.mjs` scans every `.ts` file under `src/app/` that declares `@Component` and fails the build if two component files share a folder or if a new component file's basename doesn't match its containing folder. Existing `<folder>-panel.component.ts` descriptor cases are explicit baseline exceptions and should be removed when those components are renamed.

## Component size budgets

Component size is a lint gate, not a review preference. `npm run lint:components` runs `scripts/check-component-size.mjs` and counts each Angular component across its controller (`.ts`), template (`.html`), stylesheet (`.scss`), and total lines. New or already-small components must stay under the global limits in `scripts/component-size-baseline.json`; existing oversized components are baseline debt and may not grow. Split templates, controllers, or styles before raising a baseline.

The same guard requires external `templateUrl` files and rejects inline `template`, `style`, and `styles` metadata. This is intentionally redundant with ESLint so generated or LLM-authored components fail even before template-specific lint has to reason about the markup.

CSS linting runs with `npm run lint:css` (Stylelint, configured in `.stylelintrc.json`). CSS, component-size, and structure checks all run as part of `npm run lint`.

## Chat surfaces (`coding-agent-chat` — `<cac-chat>` is canonical)

The chat stack lives in the standalone **`coding-agent-chat`** Angular library. The app is a **host**: it consumes the library and owns only the app-specific wiring. The former in-app copies (`components/chat/**`, `components/chat-row/`, `components/markdown-view/`, `components/markdown-utils.ts`, `directives/markdown-image-lightbox.directive.ts`, `features/project-chat/`, `features/workforce/`, the `parseActivityLog`/`buildConversationTurns` halves of `activity-log.parser.ts`) were deleted when the host adoption landed — do not re-create them; change the library instead.

Dependency: use an exact published registry version of `coding-agent-chat`. Never restore a local `file:` / `dist` dependency: Stable release identity and reproducible installs depend on the pinned version plus lockfile integrity.

Entry points and what the app uses from them:
- `coding-agent-chat/composer` — `<cac-chat>` (`ChatComponent`), `RoleBadgeComponent` (`<cac-role-badge>`), workforce-role catalogue + `resolveRole`, `groupIntoPhases`/`groupIntoSuperPhases` (`ChatPhase`).
- `coding-agent-chat/conversation` — `<cac-conversation-view>`, `<cac-conversation-session-card>`, `<cac-tool-burst-chip>`.
- `coding-agent-chat/core` — pure kernel: `ChatMessage`/`ChatEvent` types, `ConversationEvent` wire contract, `projectConversation(...)` (note: its context field is `task:`, not `job:`), `parseActivityLog`, `buildConversationTurns`, `mergeByTimestamp`, session/rate-limit meta parsing.
- `coding-agent-chat/markdown` — `<cac-markdown>` (`MarkdownViewComponent`, the canonical markdown surface), `markdownToHtml`/`htmlToMarkdown`/`sanitizeHtml`, `MarkdownTaskReference`.
- `coding-agent-chat/history` — `<cac-chat-row>`, `<cac-project-chat-list>` (virtualised history), `<cac-project-chat-rail>`, `<cac-phase-summary-list>`, the `PROJECT_CHAT_DATA_SOURCE` + `CHAT_HISTORY_CONFIRM` seams, `ProjectChatTurn` + scroll/search/stats/turn response types.
- `coding-agent-chat/shared` — `[cacTooltip]` (`TooltipDirective`) for library-owned surfaces, `[cacStickToBottom]`, `[cacMarkdownLightbox]`, `CHAT_MEDIA_LIGHTBOX`.

Studio-owned Angular templates use the lightweight standalone `[appTooltip]`
directive in `src/app/components/tooltip/`. Keep `[cacTooltip]` inside library
surfaces and at existing library integration points; do not recreate the old
chat-owned tooltip controller or component in the host app.

Host wiring (all in `src/app/app.config.ts`):
- `provideCodingAgentChat({ taskReferences: TaskReferenceNavigationService, mediaLightbox: MediaLightboxService })` — markdown task-key auto-linking + click-to-enlarge images.
- `PROJECT_CHAT_DATA_SOURCE` → `ProjectChatDataSourceAdapter` (`services/project-chat-data-source.adapter.ts`) over `TaskService`'s `/api/projects/{p}/chat/*` endpoints.
- `CHAT_HISTORY_CONFIRM` → `ConfirmDialogService` (structural match).

### Task reference microcards

`<app-task-reference-microcard>` in
`src/app/components/task-reference-microcard/` is the shared live-or-ghost task
reference control. Reuse it for structured task-reference lists instead of
reimplementing lane, merge, project-colour, review-grade, tooltip, or task-tab
navigation semantics. Its required `status` input is the compact projection
returned by `POST /api/tasks/reference-status`.

Rendered prose is upgraded by
`TaskReferenceMicrocardHydratorService`: it scans `<cac-markdown>` and rendered
wiki HTML, batches uncached keys into one endpoint request, and mounts the same
component. `app.config.ts` starts the hydrator and registers
`TaskReferenceNavigationService` at the `coding-agent-chat` CAC-3
`taskReferences` host seam. Keep the library responsible for identifying and
linking reference candidates; keep Studio responsible for registry validation,
status hydration, the visual control, and task-tab navigation. Do not add
per-reference API calls.

Live turns still flow through the host: the orchestrator side sheet holds a `viewChild` of `<cac-project-chat-list>` and calls `resetAndLoad()` / `appendLive(turn)` when SignalR delivers new turns.

New `ChatEvent` kinds, renderer changes, selector changes: **make them in the library repo** (`C:\Projects\coding-agent-chat`), rebuild its dist, `npm install` here. App-only helpers that intentionally stayed behind: `features/task-detail/components/activity-log.parser.ts` (filters, `buildChatMessages`, live-status derivation, tool-burst rollups, `[steer]` parsing).

## Conversation view (`<cac-conversation-view>` — next-gen chat projection)

`<cac-conversation-view>` (library entry `coding-agent-chat/conversation`) renders the **next-gen single-pane transcript** from a parsed event model. It never pattern-matches raw CLI lines in the template; the renderer only consumes `ConversationEvent[]` produced by `projectConversation(...)` (`coding-agent-chat/core`). Keep that split: parsing/classification lives in the projection (pure, unit-tested against fixtures), presentation lives in the component.

**Single-pane grouping, one actor header per turn.** Consecutive same-actor agent outputs coalesce into one `messageGroup` row (`<ol class="conv__feed">` → one `<li>` per group, an inner `<ol class="msg__items">` per turn). The actor header (`conversation-message-head`) renders **once** at the head of a run; continued groups carry `data-show-header="false"` and emit no header element. A tool burst between two agent turns preserves role continuity (the second agent group stays header-less); a `USER` turn, a non-tool reset event, or a mid-task **model switch** breaks the run into a new header. Do not reintroduce a per-message "Agent" label.

**Timestamps are clock-only with the date on hover.** The visible `<time>` shows the clock; the calendar date is disclosed via the `[cacTooltip]` binding (`groupTimeTooltip` / `formatDateTime`). `formatTime` must stay date-free; reach for `formatDateTime` when you need the full calendar+clock string.

**Markers are parsed, never shown raw.** Terminal sentinels (`[[TASK_DONE]]`, `[[TASK_BLOCKED:…]]`, `[[TASK_NOOP]]`, `[[TASK_NEEDS_INPUT:…]]`) and bracketed runtime markers (`[worktree-containment]`, `[environment-blocker]`, `[capture-fail]`, `[schema-drift]`, `[watchdog…]`, `[heuristic]`, …) are classified by the projection into semantic events (`system.status` result chips, `agent.needsInput`, `supervisor.wait`, `system.captureFail`, `system.schemaDrift`, `system.parserWarning`) and rendered as chips/rows. Any human prose sharing a line with a sentinel is preserved as a normal agent message with the token stripped. New marker shapes get a classifier branch in `projectGroup` (or `parseOrchestratorStatus`) plus a `@case` in the template — not a raw passthrough.

**Session info is subtle.** A run's captured session id rides on the agent bubble as `data-session-id` and renders as a truncated `session <8-char>` chip (`conversation-message-session`); the full id and init/rate-limit metadata live behind the header `[cacTooltip]` (`metaTooltip`) and the `<cac-conversation-session-card>` sidecar row. Never put a full session id inline in the message flow.

**Tool runs are CLI-agnostic and unified.** Tool activity from **both** CLIs folds into the same `toolBurst` event rendered by `<cac-tool-burst-chip>`:
- **codex** emits JSONL frames — `parseCodexJsonlFrame` maps `item.{started,completed}` of `command_execution` (with `command` / `aggregated_output` / exit code) into an activity-log group of kind `command`;
- **claude** emits action-line text — `parseActionLine` → `classifyAction` maps `Read(...)` / `Edit(...)` / `Run …(shell)` / `Search …` into `read` / `edit` / `command` / `search` groups.

The projection's `classifyToolGroup` then collapses any contiguous run of those tool groups (across either CLI) into a single `toolBurst` carrying per-family counts, failures, file paths, parsed command executions, and test rollups. There is no codex-only or claude-only tool path in the renderer. When you add a new CLI driver, route its tool output into one of the existing activity-log tool kinds so it inherits the same burst chip automatically.

**Orchestrator decisions are elevated.** `decision.orchestrator` events render with their own semantic background tint and a structured body (`.decision__body`), distinct from a plain agent bubble, carrying `data-decision-type` and `data-severity` so the tint can shift (info / warn / error / accept). R1 applies here too: do not add an accent rail. The raw kebab/snake kind never reaches the UI: `decisionTypeLabel` maps known kinds (`auto-review` to "Auto-Review", `reissue-open-items` to "Reissue · Open items", `worktree-containment`, `escalate`, `accept`, and others) to presentable labels and title-cases unknown kinds so a new decision reads cleanly without code changes.

Regression coverage is acceptance-level (DOM render via `TestBed`) in the library's `conversation-view.component.spec.ts` (grouping, header-once continuation, dated-time hover, decision elevation+label, subtle session chip, parsed status/needs-input rows) and projection-level in the library's `conversation-projection.spec.ts` (sentinel/marker parsing, codex+claude tool unification). Extend both (in the library repo) when you change event classification or rendering.

## Tests

`npm test` runs Vitest via `@angular/build:unit-test` (Angular 21's first-party runner). All `src/**/*.spec.ts` are picked up; `e2e/` is Playwright-only and not part of `npm test`.

**Smoke tests** (Cycle 11c). Every standalone component has a generated `<name>.spec.ts` that mounts the component with the standard provider stack (`provideZonelessChangeDetection`, `provideHttpClient` + `provideHttpClientTesting`, `provideRouter([])`) and checks the constructor + `inject()` wiring + decorator metadata don't throw. They DO NOT exercise the full render path — `detectChanges()` is wrapped in try/catch so a missing required input or per-component service stub surfaces as a console note instead of a red test. Re-generate after adding components: `node scripts/generate-smoke-specs.mjs` (skips files that already have a `.spec.ts`).

When you need a real render-path test, hand-tune the spec: seed the required `input.required<...>()` defaults via `fixture.componentRef.setInput(name, value)` and add any per-component service stubs to `providers`. Two specs are currently `it.skip` for that reason (`git-pane`, `protocol-pane`) — they need stubs for `GitPaneService` / `ClaudeSessionPollService`.
