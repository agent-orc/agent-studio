import type { TaskInfo, TaskDetail, ContextUsageSnapshot, TaskSummaryState,
  TaskPromptHistoryEntry, TaskTitleHistoryEntry, TaskLogEntry, ReviewEvidenceEntry } from '../../../models/task.model';

export type ResourceName = 'git' | 'usage' | 'review' | 'documents' | 'history';
export type ResourcePhase = 'idle' | 'loading' | 'warming' | 'ready' | 'stale' | 'unavailable' | 'error';

export interface TaskCoreText {
  state: 'ready' | 'missing' | 'stale';
  text: string | null;
  originalBytes: number;
  hash: string | null;
  cursor: string | null;
}

export interface TaskCore {
  state: 'ready' | 'stale' | 'warming';
  projectId: string;
  projectName: string;
  id: string;
  taskKey: string;
  key: string | null;
  title: string;
  kind: string;
  taskType: string;
  lane: string;
  archiveState: string | null;
  enteredLaneAt: string;
  order: number;
  mode: string;
  released: boolean;
  pendingIntent: boolean;
  pins: {
    model: string | null; modelExplicit: boolean;
    thinkingLevel: string | null; thinkingLevelExplicit: boolean;
    cliType: string | null; contextMode: string | null;
    useOwnSession: boolean | null; allowWebAccess: boolean; noBranchExpected: boolean;
  };
  actions: { canEdit: boolean; canMove: boolean; canDelete: boolean; canContinue: boolean };
  blocking: {
    blockerType: string | null; blockerCondition: string | null;
    blockerStatus: string | null; blockerDescription: string | null;
    needsInput: string | null; dependencyBlocked: boolean;
    dependencyState: string; dependencies: unknown[];
    outcomeIssue?: { label: string; summary: string } | null;
  };
  runtime: { attemptId: string | null; runnerId: string | null; runnerName: string | null;
    hostname: string | null; executionStatus: string | null; location: string;
    heartbeatAt: string | null; leaseState: string; leaseId: string | null };
  runtimeVersion: string;
  statusSummary: TaskCoreText;
  prompt: TaskCoreText & { continuationUrl: string | null };
  timeline: { state: string; events: { sequence: number; ts: string;
    kind: string; actor: string; runId: string | null; summary: string }[];
    cursor: string | null; continuationUrl: string | null };
  coreVersion: number;
}

export interface TaskResource<T> {
  id: string; taskKey: string; projectId: string; attemptId: string | null;
  coreVersion: number; resource: ResourceName; version: string;
  computedAt: string | null; state: 'warming' | 'ready' | 'stale' | 'unavailable';
  data: T; reason: string | null;
}

export interface TaskDocumentData {
  name: 'prompt' | 'status'; markdown: string | null; summaryState: TaskSummaryState | null;
}
export interface TaskUsageData {
  tokenSummary: TaskInfo['tokenSummary']; lastUsage: TaskInfo['lastUsage'];
  contextUsage: ContextUsageSnapshot | null;
}
export interface TaskReviewData { reviewProjection: TaskInfo['reviewProjection']; evidence: ReviewEvidenceEntry[] | null }
export interface TaskHistoryData {
  promptHistory: TaskPromptHistoryEntry[];
  titleHistory: TaskTitleHistoryEntry[];
  log: TaskLogEntry[];
}
export interface TaskGitData {
  mergeSignal: TaskInfo['mergeSignal']; integration: TaskInfo['integration'];
  publishSignal: TaskInfo['publishSignal']; testEvidence: TaskInfo['testEvidence'];
  commit: TaskInfo['commit']; commits: TaskInfo['commits'];
}

export const emptyDetail = (info: TaskInfo, core: TaskCore): TaskDetail => ({
  info,
  promptMarkdown: core.prompt.text,
  promptHistory: [], titleHistory: [],
  statusMarkdown: core.statusSummary.text,
  contextUsage: null, log: [], summaryState: null, reviewEvidence: [],
});

export function resourceReasonLabel(reason: string | null): string {
  switch (reason) {
    case 'git-snapshot-pending': return 'The Git snapshot is still warming.';
    case 'git-snapshot-refreshing': return 'The Git snapshot is being refreshed.';
    case 'review-projection-pending': return 'The review projection is still warming.';
    case 'document-read-failed': return 'The document could not be read.';
    case 'document-access-denied': return 'The document is not accessible.';
    case 'core-generation-changed': return 'The task changed while this section loaded.';
    default: return reason ?? 'The request failed.';
  }
}
