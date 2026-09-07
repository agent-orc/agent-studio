import { ALL_PROJECTS_BOARD, type StudioTab } from './studio-shell.types';

/**
 * Which project the **app** is scoped to while a given tab is active.
 *
 * - `string`    - narrow every scope-following surface to that project.
 * - `null`      - workspace-wide ("All projects"): drop the project scope.
 * - `undefined` - context unknown (diff / welcome, or a task whose job has not
 *   loaded yet). Leave whatever scope is currently set untouched.
 *
 * This is the "which project is the app scoped to" half of the split introduced
 * by AGT-2692. The other half - "which project's data does this view need" -
 * lives with the view (`StudioShellComponent.currentProjectName`, and the task
 * detail's own project handle) and is deliberately not derived from here.
 */
export type StudioProjectScope = string | null | undefined;

/**
 * Pure scope policy for the active studio tab.
 *
 * `taskProjectOf` resolves a task key to its project name through the loaded job
 * index; it returns `undefined` while the job is still loading, which maps to
 * "leave the scope alone" rather than a spurious workspace-wide reset.
 *
 * AGT-2692: a task tab carrying `originScope: 'all-projects'` was opened off the
 * cross-project board, so it stays workspace-wide. Its own project is only a
 * data scope for the detail view. Without this branch, opening a task from the
 * All-projects board silently switched the sidebar, picker and board behind it
 * into that single project, and the operator could not get back by closing it.
 */
export function projectScopeForTab(
  tab: StudioTab | null | undefined,
  taskProjectOf: (taskKey: string) => string | undefined,
): StudioProjectScope {
  if (!tab) return undefined;
  switch (tab.kind) {
    case 'board':
      return tab.projectName === ALL_PROJECTS_BOARD ? null : tab.projectName;
    case 'feed':
    case 'chat-history':
      return null;
    case 'workbenches':
    case 'workbench':
    case 'hub':
    case 'epics':
      return tab.projectName;
    case 'task':
      return tab.originScope === 'all-projects' ? null : taskProjectOf(tab.taskKey);
    case 'activity':
      return taskProjectOf(tab.taskKey);
    default:
      return undefined;
  }
}
