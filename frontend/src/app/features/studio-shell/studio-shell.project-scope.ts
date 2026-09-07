/**
 * Which project the workspace is *scoped* to while a given tab is active.
 *
 * This is deliberately not the same question as "which project's data does
 * this tab load". A task detail always needs its own project handle to fetch
 * the job, but a task opened from the cross-project board belongs to the
 * All-projects context: the picker, the Explorer highlight, and the board
 * filter must stay workspace-wide until the operator picks a project
 * themselves (AGT-2692).
 */

import type { StudioTab } from './studio-shell.types';

/** Sentinel `projectName` of the cross-project ("All projects") board tab. */
export const ALL_PROJECTS_TAB = '__all__';

/**
 * Scope a tab imposes on the global project selection.
 *
 * - a project name: the workspace narrows to that project
 * - `null`: the workspace stays cross-project ("All projects")
 * - `undefined`: the tab carries no scope of its own, so the caller keeps the
 *   scope that was already active or applies its own fallback
 */
export type StudioProjectScope = string | null | undefined;

/**
 * Resolve the workspace scope a tab stands for.
 *
 * Task and Activity tabs answer with the scope they were *opened from*
 * ({@link TaskTab.originScope}), never with the project their detail reads.
 * A tab with no recorded origin - a cold `#/tasks/<key>` deep link, or a
 * snapshot persisted before origins existed - answers `undefined` so the
 * caller can fall back to the task's own project.
 */
export function tabProjectScope(tab: StudioTab | null | undefined): StudioProjectScope {
  if (!tab) return undefined;
  switch (tab.kind) {
    case 'board':
      return tab.projectName === ALL_PROJECTS_TAB ? null : tab.projectName;
    case 'feed':
    case 'chat-history':
      // Workspace-wide surfaces: an explicit "All projects" context.
      return null;
    case 'epics':
    case 'workbenches':
      // `projectName: null` already means workspace-wide for these kinds.
      return tab.projectName;
    case 'hub':
    case 'workbench':
      return tab.projectName;
    case 'task':
    case 'activity':
      return tab.originScope;
    default:
      // Epic, diff, url-preview, workspace-settings, welcome: no scope claim.
      return undefined;
  }
}
