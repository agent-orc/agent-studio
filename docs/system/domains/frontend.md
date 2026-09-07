# Frontend Domain Map

Version: 2026-08-12
Status: System-of-record map for frontend changes.

Use this when a change touches Angular code, visual design, task-detail,
kanban, project pages, frontend polling, model selectors, menus, or Playwright
coverage.

## Global Search

The title-bar search opens a Ctrl+K command palette. V1 covers tasks (key,
title, prompt, and status text), commit messages and SHA prefixes, and file names
or paths on each project's working branch. Task matches are ranked immediately
from the in-memory board snapshot, with an exact task key first and a warm
response target below 300 ms.

Repository-backed results use
`GET /api/search?q=<query>&domains=tasks,commits,files&limit=<count>`. The
`domains` value is a comma-separated subset of `tasks`, `commits`, and `files`;
`limit` is optional and bounded by the backend. The JSON response contains the
normalized `query`, an array for each requested domain, per-domain `errors`,
and `durationMs`. Git commit and file lookup reuse the HEAD-keyed cache rather
than maintaining a search index.

Results are grouped by domain and carry project identity. Commit results open
the diff surface, documentation files open the Wiki, and other files open the
project Git view. Queries shorter than two characters return empty result
groups, and a failed domain reports an error without hiding successful domains.

## Entry Points

- [frontend/AGENTS.md](../../../frontend/AGENTS.md) contains frontend-scoped agent
  rules and wins for files under `frontend/`.
- [Stable view URLs](../contracts/stable-view-urls.md) defines the canonical,
  agent-constructible URL grammar and compatibility policy.
- [frontend/e2e/README.md](../../../frontend/e2e/README.md) covers Playwright setup,
  fixtures, screenshots, and conventions.
- [docs/quality/frontend/design-system.md](../../quality/frontend/design-system.md) defines the visual contract.
- [docs/quality/frontend/style-guide/](../../quality/frontend/style-guide/README.md) is the UI vocabulary and component
  style source.
- [docs/quality/design-principles.md](../../quality/design-principles.md) is the UX contract.
- [docs/quality/frontend/performance.md](../../quality/frontend/performance.md) is the frontend performance
  playbook.
- [docs/quality/frontend/audits/architecture-review-2026-05-09.md](../../quality/frontend/audits/architecture-review-2026-05-09.md)
  is the maintainability map for large components and service extraction.

## Studio route restoration

The active Studio surface is described by one canonical hash path. Board and
project scope, Hub rail, Wiki page or folder, Dossier id, public task key and
active detail tabs, Epics scope, and Settings section restore from that route.
The top-level query string is no longer an application routing surface. Legacy
`task`, `job`, and `watchPath` query parameters remain read-only migration
inputs and canonicalize after resolution.

Route hydration wins over the locally persisted tab collection. State-to-route
mirroring is enabled only after public project slugs or task keys have resolved,
preventing a stale local tab from erasing a copied route during cold boot.
Surface and subview synchronization uses `replaceState`; board filters remain an
orthogonal sibling hash segment. Active board expressions render as removable
chips, and a board route without that sibling segment restores the unfiltered
view. Workspace Settings sections use the same
`#/workspace/settings[/<section>]` path convention; older loose token and
screenshot routes remain migration inputs. Orchestrator Chat visibility and
width are browser state that normal navigation never overrides. The default-on
project-entry preference applies only when an explicit Board or Project
Overview entry replaces an empty editor; task and other deep routes are
excluded. When the sheet is open, its next-message context follows the active
surface while in-flight turns retain their captured context key. The full
schema, transient-state boundary,
route map, and visual ownership diagram are in
[Studio Route Restoration](../../concepts/studio-route-restoration.md).

The workspace Activity Feed is the embedded `#/feed` main view and is opened
by the Activity icon. Its 500-event backend snapshot is rendered through a
bounded, variable-height history window based on the Activity scroll fix, so
live prepends preserve the operator's reading position without FixedSize
virtual-scroll assumptions. The entry default is all projects and all event
types, newest first. Day separators may segment the chronology, but projects
never form sections: every event carries the shared project-identity dot and
registry short code, and its project chip writes the existing
`filters=projects:...` URL contract. Explicit shared URL filters remain active.
Only fresh `alert` events contribute to the icon badge. The global orchestrator
occupies a flat, one-line status header with scope, model, boot time, and a
copyable resume command; cumulative session details disclose below it. The
Activity canvas and its filter, stream, and detail columns are separated as a
flat data structure, not rendered as Deck-Panel cards, including at narrow
widths. The older project-scoped modal remains a quick-access compatibility
surface and shares the same live feed store. Alert treatment follows the
AGT-2410 acute-only status contract, and every event kind uses the same
row-width grid so Watcher Problem and Decision projections can join the stream
without a parallel surface.

## Key Code

- `frontend/src/app/features/board/`: kanban lanes, task cards, project tabs,
  filters, and task creation. Post Processing cards project the live
  auto-review status snapshot into a compact current-step or elapsed-wait
  indicator; the lane header reconciles those visible cards as active versus
  waiting, with machine-lock gate queueing remaining a distinct waiting state.
- `frontend/src/app/features/board/components/epic-overview-screen/`: the
  read-only Epics overview (`#/epics`, studio tab `epics:<project|__all__>`).
  Its main list is the active list, without a redundant Active section header.
  Completed history is collapsed by default and shows the lightweight
  `GET /api/epics/completed/count` result in its header; full completed rollups
  are fetched from `GET /api/epics?status=completed` only on first expansion.
  A rollup counts as Completed only when `subTaskTotal > 0` and every child is
  done. Archived zero-member cleanup epics are hidden, while completed epics
  with historical children stay visible as quiet, dated history. Each card
  shows an `x / y done` count, a done/in-progress/open progress bar, and expands
  (`epic-overview-expand`) to child rows carrying lane status and a project
  colour dot (`epic-overview-open-sub` / `epic-overview-sub-project`). The
  screen only navigates (it emits `openTask`); epic assignment stays on the
  board (create dialog + card context menu).
  `EpicCreateDialogComponent` requires a title and goal description before it
  posts. Contract locked by `e2e/board/epic-overview-history.spec.ts` (mocked
  routes, both themes) and `e2e/board/epic-overview-screen.spec.ts` (real
  backend seed + navigate).
- `frontend/src/app/features/task-detail/`: task detail shell, protocol pane,
  prompt pane, git pane, timeline, pipeline overview, and command surfaces.
  The middle inspector uses the fixed `Task | Activity | Result` order. Task
  renders `prompt.md` and the read-only refinement projection from run/log and
  steering-history evidence. When `enrichment-report.json` exists, Task keeps
  the authored prompt independently readable at the pane's full available width
  up to an 84ch reading limit. A quiet, single-line enrichment summary sits
  above it with status, token balance, decision and appended-block counts, and
  any message count. The summary truncates with an ellipsis on narrow panes and
  expands to the full-width report with detected areas, every append/reject
  decision, selector-ledger attribution, nullable input-cost estimate, exact
  blocks, audit details, and warnings/errors. The disclosure defaults closed
  and remembers its state for the browser session. Activity and Result retain
  their existing live and settled-run defaults. Activity groups each
  contiguous run of task-result artifacts in an agent message into one mixed
  block. Multiple PNG, JPEG, WebP, or GIF results use lazy WebP thumbnails from
  `GET /api/tasks/{jobId}/thumbnail` and open their full sources in the shared
  keyboard-navigable lightbox; a single image keeps the established inline
  treatment. Diff, patch, Markdown, JSON, and log results have typed on-demand
  previews, while HTML delegates to the shared task artifact viewer route and
  unknown extensions keep the canonical chat row.
  Escalated tasks render a borderless, collapsible decision section that
  keeps one bounded essence line visible: typed review-round count, latest
  grade, open-finding count, and escalation-reason class. Markdown bodies never
  feed that line. The expanded section renders structured council findings,
  complete council reactions and `orchestrator-follow-up.md`, every
  `code-review-grade-*.md` artifact with file history, reissue history, and
  delivery context. Long artifact blocks scroll within their own container and
  never truncate the source text. The primary reissue, accept-as-is, and abort
  decisions remain beside the recommendation. When findings, review artifacts,
  and delivery context are absent, the section renders one compact empty line.
  The Runs modal also shows the current operator-owned review-attempt epoch and
  the closed cycle history, including requeue reason, lane crossing, and rotated
  artifact count.
  The Git pane treats commits without `supersededByAttempt` or `supersededBySha`
  as the current delivery. Its aggregate diff and count exclude replaced rounds, while a
  separate history disclosure keeps those commits selectable and labels their
  relationship, for example `Round 1, replaced by round 2`.
  Timeline and steering text is ANSI-sanitised before rendering. Timeline rows
  project each fact once across title, summary, and badges, omit permanent
  defaults and zero counts, and disclose the exact members behind source counts.
  Execution-context rows keep model and thinking level visible without repeating
  the implied CLI. Code Review keeps the last available grade visible with its
  date when it belongs to an older delivery. The task-detail Docs tab presents
  rendered result documents before prompt and raw artifacts, with per-document
  anchors and technical file metadata disclosed from the document details menu.
  Each review row also shows its council reaction, including
  per-finding rulings and the linked follow-up round. Reviews without a reaction
  sidecar expose that audit gap explicitly.
- `frontend/src/app/features/project-cycle-time/`: the Cycle Time rail (Insight
  group). It reads `GET /api/projects/{projectName}/cycle-time?window=7d|30d|all`
  and renders the per-stage aggregates (tasks, median, p90, max), a median
  composition bar, round counts, the integration outcome distribution, the lane
  transition section (backward moves by cause with rework, from x to matrix,
  dwell per lane, tasks with the most backward moves), and a sortable per-task
  drill-down that opens the task and expands its lane change history from
  `GET .../cycle-time/tasks/{taskKey}`. The stage model, the transition
  taxonomy, and their data sources are documented in
  [concepts/cycle-time-stage-model.md](../../concepts/cycle-time-stage-model.md).
- `frontend/src/app/features/project-detail/`: project shell and project-level
  quality, settings, architecture, runtime, drift, and supervisor panels. The
  left rail (`project-shell`) is a collapsible-segment tree
  (Insight / Quality / Context / Config). Its inventory and grouping are
  defined once in `project-shell/project-shell.config.ts`; edit that file, not
  the template, to add or move a rail entry. Context contains Architecture,
  Project Graph, Wiki, Agent Docs (the AGENTS.md-style instructions agents read
  on their own, key `steering`), and Prompts. Project Graph (`project-graph`)
  consumes the read-only
  `GET /api/projects/{projectName}/graph` catalog and offers a bounded component
  graph plus a complete component list. The catalog retains unavailable managed
  projects instead of omitting them, resolves internal manifest references only,
  and reports independent repository revision / dirty state with snapshot schema,
  generator version, and capture time. It deliberately does not infer a code-call
  graph, runtime behavior, or architecture grade. The prompt-readable companion
  and regeneration command live in
  [architecture/project-map.md](../architecture/project-map.md); each regeneration
  also writes a dated JSON envelope under `architecture/project-map-history/`.
  The former Runtime Prompts placeholder rail is intentionally removed. The Wiki / Docs rail
  (`project-detail/components/project-wiki-section/`) renders the physical
  `docs/` folder tree from the project's checkout or configured
  `wikiSourceBranch`. The Wiki header shows the effective branch and commit;
  non-checkout sources are read-only. Checkout sources support real create / move / rename / delete
  operations, and shows a per-doc History panel (model / when / why + git log);
  its endpoints and tree contract are documented in
  [docs/system/contracts/wiki-tree.md](../contracts/wiki-tree.md).
- `frontend/src/app/features/project-detail/components/project-url-preview-tab/`
  owns each online and offline Project URL preview. Its shared context header
  reads repository name, current branch, HEAD, and ahead/behind distance from
  `GET /api/projects/{projectId}/urls/{urlId}/context`. The backend resolves Git
  at the preview command's effective working directory, prefers the branch
  upstream as comparison line, and falls back to the project's integration
  line. The header derives its open-task count and expandable task links from
  the existing Task Server-backed grouped task snapshot, excluding completed
  and archived cards. Readiness and last-start diagnosis remain independent, so
  a failed preview keeps both the source context and its compact failure detail.
- `frontend/src/app/features/project-detail/components/workbench-viewer/` is the
  isolated Dossier host. The workspace-wide `#/workbenches` overview and
  project-scoped `#/projects/<project>/workbenches` overview share one ordered
  projection: decision-pending items sort by open gate count, active items sort
  by latest movement, and discarded and completed history remain separate.
  One live text field filters the visible key, title, project, and status
  values. A compact shared sort row orders each lifecycle group by status, last
  movement, project, key, or open decision count and toggles direction on a
  repeated selection. The decision-first projection remains the reset default.
  Filter and sort state use one encoded route-local `dossier` query value and
  separate session entries for the global and each project scope; explicit URL
  state wins when a link is opened.
  Pending overview cards expand the existing isolated viewer and inline decision
  controls in place, while retaining a direct link to the full viewer.
  Explorer project children stay available as quick links. Created, updated,
  decision-recorded, and status-changed events travel over the existing jobs
  hub and converge the Explorer catalogue, both overview scopes, and an open
  viewer without a page reload. The viewer header has no permanent refresh
  control. Its details menu reports the live connection state, connection age,
  and the last successful Dossier read. A disconnected viewer adds a quiet
  as-of line to the header and exposes a manual read fallback only inside that
  menu. Task-reference chips and lane dots share the viewport-positioned
  application tooltip so long titles and merge-status lines wrap without
  clipping in the viewer, lists, and Wiki surfaces. Pulse reuses the same
  catalogue as a thinking inbox. The Explorer projects that catalogue into a first-class Dossiers
  branch rather than exposing the descriptor's physical `operations/` path.
  Current entries are grouped as `Needs a decision` and `In implementation`;
  `Documented` and `Discarded` stay under a History branch. Every
  lifecycle header and leaf uses one compact 24 px tree row: its pattern icon
  aligns with every sibling at that depth, its title receives the flexible
  width, and only a quiet status dot or compact open-decision count trails it.
  Catalogue key, complete title, lifecycle state, phase, and freshness stay in
  the viewport-safe shared tooltip instead of a second tree line. The Dossiers
  count is the number of open decisions, not the number of documents.
  Dossier overview and Explorer lifecycle headers are keyboard-accessible disclosures whose per-project collapsed state persists locally; a newly populated section opens once with its quiet count visible.
  Lifecycle-group counts equal
  their visible leaves and zero counts stay hidden. The catalogue's
  `docs/operations/admin-design-guideline/index.html` entry is promoted once as
  the permanent `Style Guide` project destination and is omitted from the
  nested Dossiers list. This applies the AGT-2607 navigation-citizenship
  direction without introducing a second lifecycle or taxonomy source.
  The lifecycle branch uses one 12 px nesting step for both a group chevron and
  its leaf icons instead of stacking a second full tree indent. At the
  Explorer's narrow width, project and URL names own the remaining row width
  before optional decoration: project activity overlays the avatar, zero totals
  stay absent, hover-only jump actions collapse, gaps and gutters tighten, and
  the project count disappears only at the resize floor. Ellipsis remains the
  last fallback, and the complete name plus current status stays in the shared
  title tooltip.
  An active Dossier is
  the selected leaf in the Explorer rather than making its Dossiers disclosure
  parent current. Opening one expands the owning workspace, project, and
  owning lifecycle group, persists those disclosure choices,
  and scrolls the selected leaf into view. It does not open or close unrelated
  branches. The Explorer-head Collapse all action closes workspace, project,
  Dossiers, lifecycle, and History branches. Settled Dossiers keep the same
  selection semantics but are revealed under History. Repository
  HTML runs only
  in an opaque-origin `srcdoc` iframe with the Dossier CSP. A source-checked
  message boundary maps docs-relative links to the in-app Wiki and opens absolute
  HTTP(S) links in a new tab without exposing host APIs or credentials. An inert
  DOM parse moves artifact nodes into a fixed policy-first wrapper. Dossier
  pages expose a Maximize action, and dirty working-tree
  content is labelled as uncommitted instead of receiving the current HEAD
  revision. Canonical entries expose their stable project reference key as a
  compact copyable mono chip in the viewer header and lifecycle lists; Explorer
  leaf tooltips include the same key. A task's `references.workbenches` entries
  render as linked key chips that open the existing viewer tab. Decision
  mutations use the two-phase decision gate. Unfinished feature preparation
  retains its title, goal, actor, option responses, comments, and operation id
  for the browser session. The details panel shows that retained state and an
  explicit discard action. A decided item
  remains current while its referenced cards are still moving. References are
  derived from the Slice-1 `references.workbenches` index, with descriptor
  `sourceTaskKeys`, `relatedTaskKeys`, and decision receipts retained as
  compatibility bridges. The compact viewer head shows every resolved card as
  a live lane dot. While a decision-pending or decided Workbench has at least
  one reference beyond Ready and not every reference is terminal, it also shows
  the derived `In implementation` summary marker. This is a live read-model
  marker and does not add a descriptor lifecycle state.
  Once every referenced card is in `6-completed` or `7-archive`, the catalogue
  projects a quiet `Ready to document` suggestion in the viewer header and list
  surfaces. `POST /api/projects/{projectName}/workbenches/{id}/document`
  revalidates that policy and atomically writes the canonical descriptor.
  Schema v1 stores `status=documented`; schema v2 stores
  `lifecycleState=documented` and appends lifecycle history. Documented results
  and discarded `archived` results remain readable in separate history groups.
  The archive decision and documented transition both follow the AGT-2375 rule:
  lifecycle truth lives in `workbench.json`, never in a Wiki classification
  sidecar. Chat pinning is intentionally not mounted.
- `frontend/src/app/features/project-detail/components/project-overview-dashboard/`:
  the operator-first Project Overview composition. It presents project outcomes,
  important runtime entry points, deployment readiness, and work requiring
  attention. It delegates URL status and start-in-place behavior to
  `project-overview-urls/`, and delegates publishing actions to the existing
  `project-publish-panel/` instead of introducing competing state or commands.
- `frontend/src/app/features/project-detail/components/project-deployment-panel/`:
  the first-class Deployment destination. It consumes the same
  `GET /api/projects/{projectName}/deployment/summary` contract as Overview,
  renders repository-derived targets, launches runnable targets as visible CLI
  tasks, and keeps the bounded `deploy-stable` audit trail separate from the
  guided definition editor. The editor collects a repository script and typed,
  operator-labelled parameters, validates them through the deployment compiler,
  and previews the generated operator form before any definition is saved or run.
- `frontend/src/app/features/project-detail/components/project-test-runs-panel/`:
  the Test Quality run pipeline. It shows planned, running, and completed
  commit-bound runs in product order, including scope, host, duration, result,
  and cards attached by the backend ancestry projection. Board cards render the
  same project-run projection as perfect, diff included, diff not included,
  pending, or no assigned run. A missing-evidence block stays absent in Backlog,
  Preparation, Ready, and an active run until an attributed delivery exists; it
  becomes relevant in Post Processing, Human Review, Completed, and Archive.
  Recorded evidence remains visible regardless of lane. The card evidence block
  also renders SHA-linked task-owned Remote Review build-tests grades and
  build/test gate logs supplied by the backend. It names their source and tested
  SHA instead of showing the project-run default when task-owned evidence exists.
- Project Settings owns the project-dedicated execution assignment. The
  execution card selects `local` or a healthy runner identity and persists it
  through the runtime-owned
  `PUT /api/projects/{projectName}/execution-runner` contract. A null runner is
  the local default; a remote identity makes the remote daemon the sole
  auto-pickup owner for that project. The guided check reports code channel,
  `develop`, toolchain, and no-op readiness from the host registry snapshot.
  Board cards deliberately show the actual live runner from the fenced run
  lease, not merely this configured target, so assignment and attribution
  cannot be confused. A fresh connected remote lease also drives the card's
  CURRENT running copy. The status bar reads live occupied slots and role-local
  ceilings from the Execution Hosts heartbeat projection, rendering compact
  `remote N/M` and `review N/M` plane utilization with per-host detail in the
  tooltip; a zero plane renders as `remote idle` or `review idle`;
  a disconnected, expired, or recovering location remains an acute orphan
  candidate. These consumers reuse the grouped-board and execution-location
  snapshots and do not add another polling path. The historical target is the
  ordered, immutable route
  defined by [Runner provenance and host handoff](../../concepts/completion-review-and-remote-runner-stability.html#provenance):
  task Overview and run/pipeline detail show actual placement per agent run and
  executed step, preserve A → B → A returns, and label missing legacy data as
  unknown rather than inferring local execution.
- Project Settings starts with an editable **Project basics** section. It owns
  the workspace, display name, short code, project colour, repository checkout,
  CLI working directory, repository URL, and default coding CLI/model. It
  deliberately does not own runtime assignment state. These are the same basic
  groups shown during onboarding. Saving uses one
  `PUT /api/projects/{PROJ-NNN}` request, and clearing an optional value uses
  its explicit `clear*` field rather than an empty path or URL. The adjacent
  execution-assignment card remains the UI owner for the runner and uses its
  dedicated `execution-runner` endpoint. Save feedback must not imply
  local-runner hot reload: changing the display name, repository checkout, or
  working directory requires a backend restart before the already-instantiated
  local runner may pick up work.
- `frontend/src/app/services/task.service.ts`: task API integration, optimistic
  lane moves, reorder, and rollback.
- `frontend/src/app/services/cli-catalog.store.ts`: boot-hydrated CLI model
  catalog cache.
- `frontend/src/app/features/orchestrator/state/orchestrator-composer-model.service.ts`:
  workspace-persistent GPT model and reasoning selection for the canonical
  coding-agent-chat footer. It projects the complete live Codex catalogue and
  distinguishes an explicit operator choice from an inherited default.
- `frontend/src/app/components/menu/`: text-only menu component.
- `frontend/src/app/components/cli-model-selector/`: shared CLI/model picker.
- `frontend/src/app/components/task-reference-microcard/`: compact, accessible
  task reference control shared by Wiki and coding-agent-chat markdown. The
  host hydrator batches bare registry-key candidates and owns task-tab
  navigation; code blocks and unknown shortcodes remain plain text.
- `frontend/src/app/features/polling/`: bounded polling services for detail
  panes and runtime data.
- `frontend/src/app/features/shell/components/workspace-overlays/`: the global
  Workspace Settings home. Its rail is the single navigation surface for CLI
  Management, system prompts, token usage, visual evidence, and the workspace
  summary. It does not own project onboarding or a project-source catalogue.
  Legacy CLI-admin and usage links resolve to the CLI Management section at
  `#/workspace/settings/caps`.
- The Execution Hosts destination at `#/workspace/settings/execution-hosts`
  uses one sortable machine row for status, load, activity, and exact release
  identity. Coding and Review runner processes advertised with the same host id
  are role sub-rows with their own slot ceilings and lifecycle actions. Retired
  roles are hidden until the compact history filter is enabled. The persisted
  machine disclosure opens first to one-line summaries for identity,
  connection, capabilities, capacity, project access, host work, and system
  load; each section discloses its internals independently.
- The System prompts destination is the prompt registry and observability
  surface. Its overview groups runtime-step, orchestrator, drift, and framing
  templates, explains application and project pipeline override precedence, and
  provides a sortable activity table. `RuntimePromptService.Render` appends one
  row per use to `<TaskRepository>/logs/prompt-calls.jsonl` with the effective
  content hash, estimated rendered-input tokens, timestamp, and any available
  project, step, and model context. The API aggregates total and seven-day
  calls, last call, a 14-day series, current and historical versions, and
  historical theoretical input cost through `TokenPricing`. Unknown models
  remain explicitly unpriced. Review actions check the static usage catalogue,
  repository references, and project pipeline overrides, then persist
  `prompts/runtime/<name>.md.meta.json`.
  The durable source, precedence, companion, telemetry, and cost rules are
  defined by the
  [runtime prompt registry contract](../contracts/runtime-prompts.md).
  Result-quality benchmarking by prompt version is deliberately vNext. The
  versioned call ledger is its data foundation, but this surface does not claim
  that call volume or cost measures outcome quality.
- `frontend/src/app/features/shell/components/onboard-project-dialog/`: the
  project onboarding workflow. Its roomy, scrollable form groups project
  identity, repository paths/URL, and execution defaults without a source-type
  selector. It calls `POST /api/projects`, then refreshes the registry-backed
  workspace tree so the new project appears immediately. Required-field,
  short-code, absolute-path, and HTTP(S)-URL errors stay visible without
  discarding the values already entered.

## Project Overview Contract

The default `#/projects/<project-id>` rail is an operator dashboard. It answers what
was delivered, what changed, what is reachable, and what deserves attention.
Machine configuration does not belong in this view. Watch path, working
directory, repository path, CLI readiness and status, clean-context settings,
and project sessions remain in Project Settings.
Project regression signals remain available from the Test Quality rail rather
than competing with the Overview's operator summary.

The dashboard is a projection over existing domain truths:

| Dashboard block | Read model or capability | Detail owner |
|---|---|---|
| Delivered work | `GET /api/projects/{projectName}/throughput`, including archived task history and exact rolling 24-hour and 7-day windows | Board and task history |
| Token use | `GET /api/projects/{projectName}/token-usage/summary`, including rolling 24-hour and 7-day totals | Token Usage rail |
| Project URLs | Embedded project URLs from `GET /api/workspaces`; command-working-directory Git identity from `GET .../context`; Task Server-backed open-card links; host-side readiness probes; per-embed URL/start settings; and owned process start, snapshot, output, and stop through `POST .../start` plus `GET/DELETE .../process` | Project URL embed, Project URLs rail, registry, and grouped task snapshot |
| Deployment readiness | `GET /api/projects/{projectName}/deployment/summary`, the shared DEP-1 read model for the last stable deployment and current pending commit delta | Deployment domain |
| Wiki activity | Initial `GET /api/projects/{projectName}/wiki/pulse?feedLimit=6`, then a visible-only conditional poll of `GET /api/projects/{projectName}/wiki/recent?limit=6` | Wiki rail |
| Planning work | Active planning-mode tasks from the current board snapshot | Task detail and Board |
| Visual evidence | `GET /api/projects/{projectName}/visual-evidence`, with append-only review receipts shared with task detail | Existing Visual Evidence detail surface |
| Publishing | Publish targets from `GET /api/projects/{projectName}/snapshot`, rendered by the existing publish panel | Publishing panel |

The Overview limits URL, Wiki, planning-task, and commit lists to compact
previews and links to the owning detail surface. Each data request fails
independently so one unavailable source does not blank the dashboard. Numeric
metrics use tabular figures.

Overview and Deployment both use the DEP-1 summary contract; neither parses
deployment history in the frontend. Runnable targets default to the latest
successful test run and carry its id, exact commit, and Head distance into the
durable visible deployment task. Selecting Head is an explicit exception that
requires an operator reason. Publishing controls remain owned by the existing
publishing surface.

The Overview owns a compact Visual Evidence review queue over delivered task
screenshots. Acknowledgements reuse the append-only review-evidence log, so the
Overview and task detail share one durable unseen/reviewed truth. The queue
preserves task and artifact provenance, keeps reviewed receipts from becoming
unseen again, and renders missing reviewed artifacts as no longer actionable.

## Invariants

- Angular components are standalone. Do not introduce NgModules.
- State should use Angular signals and existing stores before new state
  mechanisms.
- Durable user-owned frontend mutations are optimistic by default: snapshot,
  local signal update, fire request, rollback plus toast on error.
- Destructive operations and runner side effects stay spinner-backed rather than
  optimistic.
- Menus are text-only. Do not add leading icons to menu rows.
- Before adding visual variants, check the style guide and update it if a new
  pattern is truly needed.
- Use stable `data-testid` hooks for Playwright selectors.
- The Epics overview keeps completed and archived epics visible as history
  instead of dropping them once finished; history renders quietly with no acute
  status signals (R4), and each epic's progress counts are the sum of that
  epic's visible children (R3). The only rollups withheld from the overview are
  empty archived cleanup epics (zero members). See the epic domain contract in
  [docs/system/domains/tasks.md](./tasks.md#epic-lifecycle).
- Workspace-level CLI administration is not a separate sheet. Model and
  environment management, completion contracts, sessions, usage caps, and
  token spend belong to Workspace Settings under CLI Management.
- The Orchestrator uses the standard `<cac-chat>` footer model control. In
  GPT-only mode it renders every available model and reasoning level from the
  live Codex catalogue, preserves the full selection across navigation context
  changes, and sends the effective selection with every message. Its CLI row
  still lists the complete Studio CLI vocabulary; non-Codex entries are
  disabled with the GPT-only host-policy reason so they are not mistaken for a
  quota or installation detection failure. Do not replace it with a
  routing-only badge, reduced host list, or Orchestrator-only picker.
- Chat History is the workspace-level projection of the central Task Server
  context store. It lists permanent project contexts and non-archived task
  contexts with the store-owned short summary and latest activity. Opening a
  row selects that stable context in Orchestrator Chat. The view owns no second
  transcript, summary, or visibility state and refreshes from TaskHub hints.
- Project Overview remains operator-first. Do not add watch paths, repository
  paths, working directories, CLI health, clean-context controls, or session
  administration back to the Overview; those facts belong to Project Settings.
- Backlog Triage is not a project navigation surface. Do not add a project
  Backlog tab, Explorer entry, activity-bar entry, or project-scoped board
  filter coupling. Persisted legacy `backlog` studio tabs are discarded during
  tab-state restoration. This does not affect the Board's `0-backlog` lane,
  which remains the lifecycle landing lane for new tasks.

## Verification

- Visual or behavioral changes require relevant Playwright specs. Add or extend
  a spec when none covers the changed behavior.
- Capture screenshots for review-relevant states and persist them in the task
  `results/` folder when they must survive test cleanup.
- UI performance regressions are measured in the browser using the helpers in
  `frontend/e2e/helpers/timing.ts`.
- Pure frontend refactors still need component or unit tests when they move
  state, inputs, outputs, or service contracts.
- The focused component contract for Project Overview is
  `frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.spec.ts`.
  Compact URL status, start gating, and project-switch safety are covered by
  `frontend/src/app/features/project-detail/components/project-overview-urls/project-overview-urls.spec.ts`.
  Its production dashboard navigation, URL start reuse, partial read models,
  both themes, overflow guard, and review screenshots are covered by
  `frontend/e2e/project/project-overview-dashboard.spec.ts`. The interactive
  design contract is covered separately by
  `frontend/e2e/mockups/project-overview-dashboard-mockup.spec.ts`.
