import type { TaskDetail, TaskInfo } from '../../../models/task.model';
import type { ResourceName, TaskCore } from '../../../models/task-core.model';

export type ResourcePhase = 'idle' | 'loading' | 'warming' | 'ready' | 'stale' | 'unavailable' | 'error';
export type ResourceStates = Record<ResourceName, { phase: ResourcePhase; reason: string | null }>;

export const idleResources = (): ResourceStates => ({
  git: { phase: 'idle', reason: null }, usage: { phase: 'idle', reason: null },
  review: { phase: 'idle', reason: null }, documents: { phase: 'idle', reason: null },
  history: { phase: 'idle', reason: null },
});

export const emptyDetail = (info: TaskInfo, core: TaskCore): TaskDetail => ({
  info,
  promptMarkdown: core.prompt.text ?? null,
  promptHistory: [], titleHistory: [],
  statusMarkdown: core.statusSummary.text ?? null,
  contextUsage: null, log: [], summaryState: null, reviewEvidence: [],
});

export function resourceReasonLabel(reason: string | null): string {
  switch (reason) {
    case 'git-snapshot-pending': return 'The Git snapshot is still warming.';
    case 'git-snapshot-refreshing': return 'The Git snapshot is being refreshed.';
    case 'review-projection-pending': return 'The review projection is still warming.';
    case 'document-read-failed': return 'The document could not be read.';
    case 'document-access-denied': return 'The document is not accessible.';
    case 'task-index-warming': return 'The task index is still warming.';
    case 'core-generation-changed': return 'The task changed while this section loaded.';
    default: return reason ?? 'The request failed.';
  }
}
