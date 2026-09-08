import type { ChatContextAttachment } from 'coding-agent-chat/core';
import type { TaskInfo } from '../../models/task.model';
import type { StudioIconName } from '../../components/studio-icon/studio-icon.component';
import { pageTypeIcon, pageTypeLabel, type PageContext } from '../../models/page-context.model';
import { projectRailLabel } from '../project-detail/components/project-shell/project-shell.config';
import type { StudioTab } from '../studio-shell';
import type { OrchestratorContextSourceOption } from './models/orchestrator-context-source.model';

/**
 * Presentational location context the host feeds into the composer's
 * standard footer: the large scope (project), the active surface, and an
 * optional identity detail (task key, URL id, commit…).
 *
 * The side sheet maps this host-owned location shape to the chat library's
 * `composerContext` input. Keeping the shell contract separate preserves
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
  contextKind: 'task' | 'workbench' | 'project';
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

/** Stable chip id for the automatic (current tab) context block. */
export const AUTOMATIC_CONTEXT_ATTACHMENT_ID = 'context:automatic';

/**
 * Planning size the automatic context block is budgeted at. Its real cost is
 * only known once the backend resolves the envelope at send time, so the chip
 * shows this stable figure instead of a number that jitters per keystroke.
 */
export const AUTOMATIC_CONTEXT_ESTIMATE_TOKENS = 1_600;

/** `~940` / `~1.6k` — the compact estimate every composer context chip carries. */
export function formatCompactTokens(tokens: number): string {
  if (tokens < 1_000) return `~${tokens}`;
  const thousands = tokens / 1_000;
  return `~${thousands.toFixed(Number.isInteger(thousands) ? 0 : 1)}k`;
}

/**
 * Project the host's context state onto the chat library's chip row: the
 * automatic current-tab block first, then each explicitly attached source.
 * Every chip carries its own estimate, so the row is the single place the
 * operator reads what the next message will carry and what it costs.
 *
 * A mandatory automatic block (task scope) still renders a chip, and the hint
 * says so — the library gives every chip the same remove affordance, and the
 * host answers that removal with a no-op rather than breaking the rule that a
 * task chat always carries its task.
 */
export function buildComposerContextAttachments(input: {
  automatic: ContextChipPresentation;
  automaticIncluded: boolean;
  automaticMandatory: boolean;
  attachments: readonly OrchestratorContextSourceOption[];
}): ChatContextAttachment[] {
  const chips: ChatContextAttachment[] = [];
  if (input.automaticIncluded) {
    const estimate = formatCompactTokens(AUTOMATIC_CONTEXT_ESTIMATE_TOKENS);
    const suffix = input.automaticMandatory ? ' · always included' : '';
    chips.push({
      id: AUTOMATIC_CONTEXT_ATTACHMENT_ID,
      label: `${input.automatic.key?.trim() || input.automatic.label} · ${estimate}`,
      hint: `Current tab · ${input.automatic.typeLabel}: ${input.automatic.label} · ${estimate}${suffix}`,
    });
  }
  for (const source of input.attachments) {
    const estimate = formatCompactTokens(source.estimateTokens);
    chips.push({
      id: source.id,
      label: `${source.key?.trim() || source.label} · ${estimate}`,
      hint: `${source.label} · ${source.detail} · ${estimate}`,
    });
  }
  return chips;
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
 * Project the canonical active Studio tab into CAC's presentational context
 * contract. The composer receives this value and does not inspect tabs or
 * re-derive navigation state itself.
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
