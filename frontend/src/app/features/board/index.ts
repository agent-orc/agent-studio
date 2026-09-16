/**
 * Board feature public API. Cycle 9h / ADR-0034: anything outside
 * `features/board/` should import from this barrel, not from internal
 * files. Enforced softly by the `no-restricted-imports` ESLint rule.
 */

// state
export { BoardFiltersService, type ActiveFilterPill } from './state/board-filters.service';
export { LaneCollapseService } from './state/lane-collapse.service';
export { CreateTaskFormService } from './state/create-task-form.service';
export { BoardMutationsService } from './state/board-mutations.service';
export { BoardDragStateService } from './state/board-drag-state.service';
export { EpicOverviewService } from './state/epic-overview.service';

// components
export { EpicOverviewScreenComponent, type EpicOverviewScope } from './components/epic-overview-screen/epic-overview-screen.component';
export { BoardSearchIconComponent } from './components/board-search-icon/board-search-icon.component';
export { ActiveBoardFiltersComponent } from './components/active-board-filters/active-board-filters.component';
export { CreateTaskDialogComponent, type PendingAttachment } from './components/create-task-dialog/create-task-dialog.component';
export { DecisionBacklogHintComponent } from './components/decision-backlog-hint/decision-backlog-hint.component';
export { FiltersDropdownComponent, type TypeFilterOption } from './components/filters-dropdown/filters-dropdown.component';
export { KanbanFilterSidesheetComponent } from './components/kanban-filter-sidesheet/kanban-filter-sidesheet.component';
export { TaskCardComponent } from './components/task-card/task-card.component';
export { TaskColumnComponent } from './components/task-column/task-column';
export { EpicGroupBoardComponent } from './components/epic-group-board/epic-group-board.component';
export {
  ProjectTabsComponent,
  type ProjectAutoInfo,
  type ProjectTokenChipInfo,
  type ProjectRunnerIndicator,
} from './components/project-tabs/project-tabs.component';
export {
  buildProjectTokenChip,
  projectAutoInfo,
  projectRunnerIndicator,
} from './components/project-tabs/project-chip-view-model';
export {
  buildGitStateBadge,
  buildMergeSignal,
  type GitStateBadge,
  type GitStateBadgeKind,
  type MergeSignalView,
  type MergeSignalSegment,
} from './components/task-card/task-card-view-model';

// utilities
export { splitReadyByPhase } from './components/ready-lane-split.util';
export {
  buildFocusLanes,
  buildLaneGroups,
  laneChrome,
  type BoardLane,
  type BoardLaneGroup,
} from './components/board-lane-layout.util';
export { groupReviewJobs } from './components/review-grouping.util';
export { buildEpicGroups, flattenGrouped, excludeEpics, type EpicGroupView } from './components/epic-grouping.util';
