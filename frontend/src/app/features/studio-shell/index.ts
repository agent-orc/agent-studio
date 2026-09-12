export { StudioShellComponent } from './studio-shell.component';
export { ProjectHubViewComponent } from './components/project-hub-view/project-hub-view.component';
export { StudioDiffViewComponent } from './components/diff-tab-view/diff-tab-view.component';
export { StudioActivityViewComponent } from './components/activity-tab-view/activity-tab-view.component';
export { StudioTabStateService } from './services/studio-tab-state.service';
export type {
  DocumentTabHistory,
  DocumentTabTarget,
  StudioTabReusePolicy,
  WikiTabNavigationRequest,
} from './studio-shell.types';
export { ProjectHubUrlService } from './services/project-hub-url.service';
export { StudioPanelStateService } from './services/studio-panel-state.service';
export { ThemeService } from './services/theme.service';
export {
  TASK_DETAIL_TABS,
  isTaskDetailTab,
  isTaskInspectorTab,
  parseStudioRoute,
  navigateStudioRoute,
  replaceStudioRouteQuery,
  replaceTaskViewRoute,
  studioProjectSlug,
  studioRouteForTab,
  type StudioRoute,
  type TaskDetailRouteTab,
  type TaskInspectorRouteTab,
} from './services/studio-route';
// NB: AppearanceSettingsComponent is intentionally NOT re-exported here. The
// consolidated settings view (shell feature) mounts it via a direct path so it
// does not pull StudioShellComponent through this barrel and re-form the
// shell <-> studio-shell import cycle (AGT-2035).
export { studioTabKey } from './studio-shell.types';
export type { StudioTab, StudioTabKind, StudioPanelKind, TaskTabScope, WikiTabTarget } from './studio-shell.types';
export {
  ALL_PROJECTS_BOARD_NAME,
  resolveTaskTabScope,
  taskTabProjectScope,
} from './services/task-tab-scope';
