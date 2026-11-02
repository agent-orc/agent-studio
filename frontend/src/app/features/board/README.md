# board

Kanban surface: lanes, cards, drag-and-drop, search, filters, lane collapse, and the create-job dialog.

## Public API

Imports go via `from './features/board'` (the barrel). See [`index.ts`](./index.ts).

**State services** (cross-shell singletons):

- `BoardFiltersService` — search query + 4 faceted filter axes (owner / project / type / tags), URL hash + query-param round-trip, `filteredGrouped` derivation.
- `LaneCollapseService` - per-lane collapse, the independently persisted Delivery group collapse, and container focus-expand state.
- `CreateJobFormService` (Cycle 10a) - every field the create-job dialog binds + open / cancel / submit + the pre-filled entry points (default / security-follow-up / uxui-follow-up / planning-task promotion).
- `BoardMutationsService` (Cycle 10b) — drag/drop move, within-lane reorder, delete (board + detail), lane-dropdown move, archive-all, file-saved refresh, project-changed reopen.

**Components**:

- `JobColumnComponent` — one lane (header + virtualised list).
- `JobCardComponent` — one card (heavy: see [job-card.ts](./components/job-card.ts) — split planned in Cycle 10e).
- `KanbanFilterSidesheetComponent` — VS Code-style right-edge filter panel.
- `ActiveBoardFiltersComponent` - compact active-filter chips and the named zero-result state above the board.
- `FiltersDropdownComponent` — header type/tag dropdown.
- `CreateJobDialogComponent` — the create-task dialog itself.
- `BoardSearchIconComponent` — header search affordance.
- `ProjectTabsComponent` — per-project chip strip.

**Utilities**:

- `splitReadyByPhase` — splits the 2-ready lane into "Human Ready" / "Orch Intake" sub-lanes.
- `groupReviewJobs` — splits 4-review into the two swim-lane sub-sections.

## Notable patterns

- **Optimistic-snapshot** for moves and reorders: paint immediately, persist in background, revert on error. The lifecycle is owned by `BoardMutationsService`; the shell just forwards events.
- **Filter URL contract**: `#filters=owner:X;projects:A,B;type:Y;tags:t1,t2` (current) + `#filter=type:Y,tag:t1` (legacy, still honoured). Active expressions render as removable chips; a board route without `filters=` clears the in-memory facets.
- **Lane state on disk** = filesystem state on `info.state`; virtual sub-lanes (e.g. `2-ready-intake`) collapse to the parent for backend mutations.
- **Delivery visibility** follows the displayed group count, including the furthest-environment option. An empty group leaves the board grid unless a pending approval or failed run needs attention. Collapsing the group retains its total and per-environment counts in a right-edge rail.
