import type { TaskInfo } from '../../models/task.model';
import type { StudioIconName } from '../../components/studio-icon/studio-icon.component';
import { pageTypeIcon, pageTypeLabel, type PageContext } from '../../models/page-context.model';
import { projectRailLabel } from '../project-detail/components/project-shell/project-shell.config';
import type { StudioTab } from '../studio-shell';

/**
 * Presentational location context the host feeds into the composer's
 * standard footer: the large scope (project), the active surface, and an
 * optional identity detail (task key, URL id, commit…).
 *
 * The side sheet maps this host-owned location shape to the chat library's
 * automatic context chip. Keeping the shell contract separate preserves
 * nullable navigation state at the application boundary.
 */
export interface ComposerLocationContext {
  project: string | null;
  surface: string;
  detail?: string;
  /** Stable short reference for compact context chips, such as a Dossier key. */
  referenceKey?: string;
  /** Task identity carried by the same active-tab projection the footer shows. */
  taskKey?: string;
  taskId?: string;
  taskTitle?: string;
  taskState?: string;
  taskWatchPath?: string;
}

/** Canonical `contextLabel` rendering: `surface` or `surface · detail`. */
export function composerLocationLabel(context: ComposerLocationContext | null): string | null {
  if (!context) return null;
  return context.detail ? `${context.surface} · ${context.detail}` : context.surface;
}

export interface ContextChipPresentation {
  label: string;
  key: string | null;
  typeLabel: string;
  icon: StudioIconName;
}

export function buildContextChipPresentation(input: {
  project: string | null;
  page: PageContext | null;
  contextKind: 'task' | 'project';
  taskKey: string | null;
  taskTitle: string | null;
  location: ComposerLocationContext | null;
}): ContextChipPresentation {
  const { project, page, contextKind, taskKey, taskTitle, location } = input;
  if (page && page.projectName === project) {
    return { label: page.title, key: null, typeLabel: pageTypeLabel(page.pageType), icon: pageTypeIcon(page.pageType) };
  }
  if (contextKind === 'task') {
    return { label: taskTitle ?? taskKey ?? 'Current task', key: taskKey, typeLabel: 'Task', icon: 'backlog' };
  }
  if (location && (!location.project || location.project === project)) {
    return {
      label: location.detail ?? location.surface,
      key: location.referenceKey ?? null,
      typeLabel: location.surface,
      icon: composerSurfaceIcon(location.surface),
    };
  }
  return { label: 'Project overview', key: null, typeLabel: 'Project', icon: 'grid' };
}

function taskFor(tabKey: string, tasks: readonly TaskInfo[]): TaskInfo | undefined {
  return tasks.find(task => task.taskKey === tabKey || task.displayKey === tabKey || task.key === tabKey);
}

function taskContext(surface: string, tabKey: string, tasks: readonly TaskInfo[]): ComposerLocationContext {
  const task = taskFor(tabKey, tasks);
  const taskKey = task?.displayKey ?? task?.key ?? task?.taskKey ?? tabKey;
  return {
    project: task?.projectName ?? null,
    surface,
    detail: taskKey,
    taskKey,
    taskId: task?.id ?? tabKey,
    ...(task?.title ? { taskTitle: task.title } : {}),
    ...(task?.state ? { taskState: task.state } : {}),
    ...(task?.watchPath ? { taskWatchPath: task.watchPath } : {}),
  };
}

/**
 * Project the canonical active Studio tab into the side sheet's context-chip
 * contract. The composer does not inspect tabs or re-derive navigation state.
 */
export function buildComposerLocationContext(
  tab: StudioTab | null,
  tasks: readonly TaskInfo[],
): ComposerLocationContext | null {
  if (!tab) return null;

  switch (tab.kind) {
    case 'board':
      return { project: tab.projectName === '__all__' ? null : tab.projectName, surface: 'Board' };
    case 'feed':
      return { project: null, surface: 'Feed' };
    case 'chat-history':
      return { project: null, surface: 'Chat History' };
    case 'task':
      return taskContext('Task', tab.taskKey, tasks);
    case 'workbench':
      return {
        project: tab.projectName,
        surface: 'Dossier',
        detail: tab.title ?? tab.workbenchId,
        ...(tab.key ? { referenceKey: tab.key } : {}),
      };
    case 'workbenches':
      return { project: tab.projectName, surface: 'Dossiers' };
    case 'activity':
      return taskContext('Activity', tab.taskKey, tasks);
    case 'epic':
      return taskContext('Epic', tab.epicKey, tasks);
    case 'epics':
      return { project: tab.projectName, surface: 'Epics' };
    case 'hub':
      return {
        project: tab.projectName,
        surface: tab.section ? projectRailLabel(tab.section) : 'Deck',
      };
    case 'url-preview':
      return { project: tab.projectName, surface: 'URL preview', detail: tab.urlId };
    case 'diff':
      return { project: null, surface: 'Diff', detail: tab.commitSha.slice(0, 8) };
    case 'workspace-settings':
      return { project: null, surface: 'Workspace Settings' };
    case 'welcome':
      return { project: null, surface: 'Welcome' };
  }
}

function composerSurfaceIcon(surface: string): StudioIconName {
  switch (surface.toLocaleLowerCase()) {
    case 'board': return 'grid';
    case 'dossier':
    case 'dossiers': return 'eye';
    case 'wiki': return 'book';
    case 'url preview': return 'eye';
    case 'project overview': return 'grid';
    default: return 'file';
  }
}
