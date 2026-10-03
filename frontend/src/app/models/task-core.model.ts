import type { TaskInfo, ContextUsageSnapshot, TaskSummaryState,
  TaskPromptHistoryEntry, TaskTitleHistoryEntry, TaskLogEntry, ReviewEvidenceEntry } from './task.model';

/**
 * Wire contract of the bounded `/api/tasks/{id}/core` projection and the
 * additive `/api/tasks/{id}/details/{resource}` replies.
 */
export type ResourceName = 'git' | 'usage' | 'review' | 'documents' | 'history';

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
  watchPath: string;
  folderPath: string;
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
  /**
   * 64-bit generation as a decimal string. Compare and echo it verbatim; as a
   * JavaScript number it would round and fail every `generation` check.
   */
  coreVersion: string;
}

export interface TaskResource<T> {
  id: string; taskKey: string; projectId: string; attemptId: string | null;
  coreVersion: string; resource: ResourceName; version: string;
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
