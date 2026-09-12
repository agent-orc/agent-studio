# project-detail

The full per-project surface: the operator-first Overview, project shell, rail
panels, legacy project-detail settings, and per-project overlays.

`project-shell` and `project-detail` share this feature folder because they are
one project surface in the user's mental model.

## Public API

Import via `from './features/project-detail'`. See [`index.ts`](./index.ts).

**State services**:

- `ProjectOverlaysService`: open/close and URL-hash sync for the four per-project
  overlays (orch-feed / project-detail / project-shell / analysis-report).

**Container components**:

- `ProjectOverlaysComponent`: renders the four overlays in one place; the shell
  mounts it once.
- `ProjectShellComponent`: the project page's left rail and body shell.
- `ProjectOverviewDashboardComponent`: operator-first default Overview.
- `ProjectDetailComponent`: legacy per-project settings overlay.
- `AnalysisReportDrilldownComponent`: drill-down for one analysis report.
- `AutonomySliderComponent`: the autonomy slider used in project settings.
- `WorkbenchViewerComponent` and `WorkbenchDecisionPanelComponent`: the isolated
  Dossier host, inline Decision bridge, and compact feature-card confirmation.
- `WorkbenchDecisionStore`: prepare/confirm request state for one Dossier
  decision against the repository-backed decision service.

**Rail panels**:

- `SecurityPanelComponent`: security quality view.
- `UxuiPanelComponent`: UX and UI quality view.
- `ProjectObservabilityPanelComponent` and
  `ProjectProductRuntimePanelComponent`: observability and runtime telemetry.
- `ProjectSteeringDocsSectionComponent`: steering docs viewer, also embedded in
  ProjectShell.
- `RegressionRadarComponent`: project regression signals under Test Quality.

**Project-shell config**:

- `DEFAULT_PROJECT_RAIL_KEY`, `ProjectRailKey`, `isProjectRailKey`, and
  `toProjectSlug`: URL hash slug and rail-key validation.

**Types**: security and UX/UI panel response shapes, re-exported from their
`.types.ts` files for the API services that wrap the backend.

## Operator Overview

`components/project-overview-dashboard/` composes compact projections of the
project's existing detail truths:

- token totals from `GET /api/projects/{projectName}/token-usage/summary`;
- last deployment and pending delta from
  `GET /api/projects/{projectName}/deployment/summary`;
- recent Wiki activity from
  `GET /api/projects/{projectName}/wiki/pulse?feedLimit=6`;
- active planning-mode tasks from the current task snapshot; and
- delivered screenshots and their durable review receipts from
  `GET /api/projects/{projectName}/visual-evidence`; and
- publish targets from `GET /api/projects/{projectName}/snapshot`.

`components/project-overview-urls/` is a compact adapter over the project
registry, `ProjectUrlProbeService`, and the existing URL start endpoint. It
shows at most four configured URLs, emits navigation to the full Project URLs
rail, and does not duplicate URL configuration or process-start logic.

`components/project-url-preview-tab/` owns the embedded-preview state machine.
It keeps HTTP readiness separate from process ownership: the backend probe
decides whether an iframe is safe to mount, while `ProjectUrlProcessService`
owns commands started by Studio. The tab exposes start/restart, a bounded
stdout/stderr console, explicit stop, and URL/command/CWD/port settings in
place. Closing the console or tab does not detach the process; URL/project
removal and backend shutdown stop its whole process tree.
Before a configured command starts, the backend checks its local target port.
An existing listener prevents the spawn and the same readiness card identifies
the occupying process and PID, while retaining the normal retry and settings
recovery actions.

Every Overview request has an independent unavailable state. Detail links emit
rail navigation or task navigation through the shell. The Visual Evidence queue
projects delivered task screenshots and stores review receipts in each task's
existing append-only `results/review-evidence.jsonl`; it does not introduce a
second screenshot store. The projection checks a bounded set of recent
delivered tasks and returns at most four of the newest screenshots. The backend
keeps a ten-second per-project snapshot. The
Overview refresh bypasses this cache, acknowledgements invalidate it, and the
client leaves the loading state after fifteen seconds if the filesystem read
does not finish. Git branch inventory has its own three-second backend cache;
do not add a second client cache for either read model. Deployment uses the same summary for DEP-1 history and
DEP-2 targets, launches runnable templates through the shared visible CLI-task
substrate, and compiles only bounded repository-script prompts with typed slots.
The existing publishing panel keeps ownership of package release actions.

The Project Proposals rail is a management surface over `docs/concepts/proposals`: it
shows topic, categories, source, generations, and prior decisions; creates a
repository-grounded draft from an operator topic via the proposal-management
CLI; records both refined and raw rejection feedback; and exposes explicit
destructive confirmation for individual removal and older-generation cleanup.

Machine facts stay in Project Settings: watch path, working directory,
repository path, CLI readiness and status, clean-context configuration, and
project sessions. Do not move them back into the operator Overview.

## Notable

- **Cross-overlay navigation** lives in
  `ProjectOverlaysService.openFeedFromShell` and `openFeedFromDetail`, the two
  patterns where clicking one overlay swaps to another.
- **Per-rail follow-ups** bubble up to the shell because they trigger the create
  task dialog whose form state is in
  `features/board/state/create-job-form.service.ts`.
- **Dossier decisions** are authored as readable `data-decision-id` markup in
  the sandboxed document, while state and mutation authority stay in trusted
  host chrome. The viewer bridge reserves an action slot immediately after the
  decision markup and anchors the shared `WorkbenchDecisionPanelComponent`
  there; the header renders the same component over the same stores. **Start
  task** accepts only complete decision answers, opens the shared CLI, model,
  and thinking picker plus task prompt preview, then prepares and confirms a
  Ready card through the existing APIs. The durable receipt and
  `relatedTaskKeys` retain the created key. **Request rework** accepts a partial
  answer when it includes a comment, records a pending receipt and revision
  request, and sends one configured turn to the permanent
  `workbench:<PROJ>/<DOSSIER-KEY>` orchestrator session. A changed Dossier entry
  fingerprint clears the pending presentation and restores responses from the
  new decision markup. Canonical Dossiers do not use the generic Wiki archive
  action.
- The Studio Project Hub URL hash is `#/projects/<project-id>` or
  `#/projects/<project-id>/<rail-key>`. The immutable registry id is canonical;
  the former display-name slug remains an input-only legacy alias. The complete
  public grammar lives in
  [the stable view URL contract](../../../../../docs/system/contracts/stable-view-urls.md).
  The legacy overlay still exposes `syncShellFromHash(watchPaths)` for its
  compatibility route handling.

## Focused Verification

- Component contract:
  `components/project-overview-dashboard/project-overview-dashboard.spec.ts`
- Compact URL adapter contract:
  `components/project-overview-urls/project-overview-urls.spec.ts`
- Production Playwright flow:
  `frontend/e2e/project/project-overview-dashboard.spec.ts`
- Interactive mockup contract:
  `frontend/e2e/mockups/project-overview-dashboard-mockup.spec.ts`

## Sub-folders

- `components/`: the Overview, shell, rail panels, legacy project-detail,
  analysis-report drilldown, and focused section components that compose the
  larger panels.
