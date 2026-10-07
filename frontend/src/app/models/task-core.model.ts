import type { TaskInfo, TaskRunActivity, ContextUsageSnapshot, TaskSummaryState,
  TaskPromptHistoryEntry, TaskTitleHistoryEntry, TaskLogEntry, ReviewEvidenceEntry } from './task.model';

/**
 * Wire shape of `GET /api/tasks/{jobId}/core?project=PROJ-002` (AGT-2953, backend
 * `TaskCoreResponse`). The response is capped at 16 KiB: status 1 KiB, prompt
 * 2 KiB, the last five timeline events within 2 KiB. It carries no Git, usage
 * or review facts; those are the additive `/api/tasks/{id}/details/{resource}`
 * resources (AGT-2955) with their own versions.
 */
export interface TaskCore {
  /** `warming` is the 202 body while the task index re-hydrates. */
  state: 'ready' | 'stale' | 'warming';
  projectId: string;
  projectName: string;
  id: string;
  taskKey: string;
  watchPath: string;
  folderPath: string;
  key?: string | null;
  title: string;
  kind: string;
  taskType: string;
  lane: string;
  archiveState?: string | null;
  enteredLaneAt: string;
  phase?: string | null;
  phaseEnteredAt?: string | null;
  order: number;
  mode: string;
  released: boolean;
  pendingIntent: boolean;
  pins: TaskCorePins;
  actions: { canEdit: boolean; canMove: boolean; canDelete: boolean; canContinue: boolean };
  blocking: TaskCoreBlocking;
  runtime: TaskCoreRuntime;
  runtimeVersion: string;
  statusSummary: TaskCoreText;
  prompt: TaskCoreText & { continuationUrl?: string | null };
  timeline: TaskCoreTimeline;
  /**
   * 64-bit generation as a decimal string. Compare and echo it verbatim; as a
   * JavaScript number it would round and fail every `generation` check.
   */
  coreVersion: string;
}

export interface TaskCorePins {
  model?: string | null;
  modelExplicit: boolean;
  thinkingLevel?: string | null;
  thinkingLevelExplicit: boolean;
  cliType?: string | null;
  contextMode?: string | null;
  useOwnSession?: boolean | null;
  allowWebAccess: boolean;
  noBranchExpected: boolean;
}

export interface TaskCoreBlocking {
  blockerType?: string | null;
  blockerCondition?: string | null;
  blockerStatus?: string | null;
  blockerDescription?: string | null;
  outcomeIssue?: { kind: string; severity: string; label: string; summary: string } | null;
  needsInput?: string | null;
  dependencyBlocked: boolean;
  dependencyState: string;
  dependencies: {
    key: string; resolved: boolean; fulfilled: boolean;
    releaseGate: boolean; waitingForRelease: boolean; unsatisfiable: boolean;
  }[];
  dependsOn: string[];
  blockedBy: string[];
}

export interface TaskCoreRuntime {
  activity?: TaskRunActivity | null;
  executionStatus?: string | null;
  executionStartedAt?: string | null;
  processId?: number | null;
  location: string;
  runnerId?: string | null;
  runnerName?: string | null;
  hostname?: string | null;
  backendName?: string | null;
  attemptId?: string | null;
  leaseId?: string | null;
  leaseGeneration?: number | null;
  leaseState: string;
  heartbeatAt?: string | null;
  summaryState?: string | null;
}

/**
 * A bounded head. `state` is `ready`, `missing` (explicit empty state) or
 * `stale`; a non-null `cursor` marks a truncated head with more to read.
 */
export interface TaskCoreText {
  state: string;
  text?: string | null;
  originalBytes: number;
  hash?: string | null;
  cursor?: string | null;
}

export interface TaskCoreTimeline {
  state: string;
  events?: { sequence: number; ts: string; kind: string; actor: string; runId?: string | null; summary: string }[] | null;
  originalBytes: number;
  hash?: string | null;
  cursor?: string | null;
  continuationUrl?: string | null;
}

/** Outcome of one core read through the shared cache; mirrors the route's status codes. */
export type TaskCoreResult =
  | { state: 'ready' | 'stale'; core: TaskCore }
  | { state: 'warming' | 'missing' | 'denied'; core: null };

/**
 * Cache identity for a core: registry project handle plus the task id. Two
 * projects that both hold a task named `fix-login` never share an entry.
 */
export function taskCoreKey(project: string, id: string): string {
  return `${project}\u0000${id}`;
}

export type ResourceName = 'git' | 'usage' | 'review' | 'documents' | 'history';

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
