import type { StudioTab, TaskTab, TaskTabScope } from '../studio-shell.types';

/** Sentinel `projectName` carried by the cross-project ("All projects") board tab. */
export const ALL_PROJECTS_BOARD_NAME = '__all__';

/**
 * AGT-2692 — pure scope policy for task tabs.
 *
 * Two different questions used to share one answer, and that is what moved the
 * operator out of the All-projects board:
 *
 *   - "Which project's data does this detail need?" — always the task's own
 *     project. `TaskSelectionService` resolves that handle from the task
 *     record and it is not this module's business.
 *   - "Which project is the app scoped to?" — the board, the Explorer tree,
 *     the project picker, and the lane filters. That answer belongs to the
 *     context the task was opened FROM, not to the task itself.
 *
 * A task opened from the cross-project board therefore keeps the app
 * workspace-wide while still loading its own project's data.
 */

/**
 * Capture the scope a task tab is being opened in, from the tab that was
 * active at that moment.
 *
 * `undefined` means the origin carried no project context worth remembering
 * (diff, welcome, workspace settings, an empty editor). Callers read that as
 * "unknown" and fall back to resolving the task's own project, which is the
 * behaviour every task tab had before this stamp existed.
 */
export function resolveTaskTabScope(origin: StudioTab | null): TaskTabScope | undefined {
  if (!origin) return undefined;
  switch (origin.kind) {
    case 'board':
      return origin.projectName === ALL_PROJECTS_BOARD_NAME
        ? { kind: 'all-projects' }
        : { kind: 'project', projectName: origin.projectName };
    case 'feed':
    case 'chat-history':
      return { kind: 'all-projects' };
    case 'epics':
    case 'workbenches':
      return origin.projectName === null
        ? { kind: 'all-projects' }
        : { kind: 'project', projectName: origin.projectName };
    case 'hub':
    case 'workbench':
    case 'url-preview':
      return { kind: 'project', projectName: origin.projectName };
    case 'task':
      // Pager step or task-to-task reference: inherit, so a chain of opens
      // that started on the All-projects board never drifts into one project.
      return origin.scope;
    default:
      return undefined;
  }
}

/**
 * Which project the app is scoped to while `tab` is the active task tab.
 *
 * `null` = workspace-wide ("All projects"), a string = that single project,
 * `undefined` = not yet known (a tab that predates the scope stamp whose job
 * has not loaded), which callers treat as "leave the current scope alone".
 */
export function taskTabProjectScope(
  tab: TaskTab,
  lookupTaskProject: (taskKey: string) => string | null | undefined,
): string | null | undefined {
  const scope = tab.scope;
  if (scope?.kind === 'all-projects') return null;
  if (scope?.kind === 'project') return scope.projectName;
  return lookupTaskProject(tab.taskKey);
}

/**
 * Drop a persisted scope that no longer round-trips (hand-edited or older
 * payload). An unusable stamp degrades to "unknown" rather than to a wrong
 * project.
 */
export function normalizeTaskTabScope(scope: TaskTabScope | undefined): TaskTabScope | undefined {
  if (!scope) return undefined;
  if (scope.kind === 'all-projects') return { kind: 'all-projects' };
  if (scope.kind === 'project' && typeof scope.projectName === 'string' && scope.projectName)
    return { kind: 'project', projectName: scope.projectName };
  return undefined;
}
