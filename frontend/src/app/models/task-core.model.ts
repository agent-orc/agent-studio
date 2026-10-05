import type { TaskInfo, TaskKind, TaskMode, TaskRunActivity } from './task.model';

/**
 * Wire shape of `GET /api/tasks/{jobId}/core?project=PROJ-002` (AGT-2953, backend
 * `TaskCoreResponse`). The response is capped at 16 KiB: status 1 KiB, prompt
 * 2 KiB, the last five timeline events within 2 KiB. It carries no Git, usage
 * or review facts; those are separate resources with their own versions.
 */
export interface TaskCore {
  state: 'ready' | 'stale';
  projectId: string;
  projectName: string;
  id: string;
  taskKey: string;
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
  coreVersion: number;
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

/**
 * Identity, lane, runtime and pin facts the resident board record already
 * holds. Painted synchronously on selection; the bounded heads (status,
 * prompt, timeline) are the only part that has to come from `/core`.
 */
export interface TaskCoreSeed {
  /** Registry handle the core request is addressed with. */
  project: string;
  projectName: string;
  id: string;
  taskKey: string;
  key: string | null;
  title: string;
  kind: TaskKind;
  mode: TaskMode;
  taskType: string | null;
  lane: string;
  archiveState: string | null;
  enteredLaneAt: string | null;
  order: number;
  released: boolean;
  pins: {
    model: string | null;
    modelExplicit: boolean;
    thinkingLevel: string | null;
    thinkingLevelExplicit: boolean;
    cliType: string | null;
    contextMode: string | null;
    useOwnSession: boolean | null;
  };
  runtime: {
    activity: TaskRunActivity | null;
    executionStatus: string | null;
    location: string;
    runnerId: string | null;
    heartbeatAt: string | null;
  };
}

/**
 * Selection-facing core state. `seeded` has only board facts; `ready` and
 * `stale` carry a core; `warming`, `missing` and `denied` are the backend's
 * explicit non-ready answers; `error` is a failed request.
 */
export type TaskCoreViewState =
  | 'seeded' | 'ready' | 'stale' | 'warming' | 'missing' | 'denied' | 'error';

export interface TaskCoreView {
  seed: TaskCoreSeed;
  core: TaskCore | null;
  state: TaskCoreViewState;
}

/** Outcome of one core read; mirrors the route's status codes. */
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

export function seedTaskCore(info: TaskInfo, project: string): TaskCoreSeed {
  const location = info.executionLocation;
  return {
    project,
    projectName: info.projectName,
    id: info.id,
    taskKey: info.taskKey,
    key: info.key ?? null,
    title: info.title,
    kind: info.kind ?? 'task',
    mode: info.mode ?? 'coding',
    taskType: info.taskType ?? null,
    lane: info.state,
    archiveState: info.archiveState ?? null,
    enteredLaneAt: info.enteredLaneAt ?? null,
    order: info.order,
    released: info.released ?? false,
    pins: {
      model: info.model ?? null,
      modelExplicit: info.modelExplicit ?? false,
      thinkingLevel: info.thinkingLevel ?? null,
      thinkingLevelExplicit: info.thinkingLevelExplicit ?? false,
      cliType: info.cliType ?? null,
      contextMode: info.contextMode ?? null,
      useOwnSession: info.useOwnSession ?? null,
    },
    runtime: {
      activity: info.runActivity ?? null,
      executionStatus: info.execution?.status ?? null,
      location: location?.executionKind ?? 'none',
      runnerId: location?.runnerId ?? null,
      heartbeatAt: location?.lastHeartbeat ?? null,
    },
  };
}
