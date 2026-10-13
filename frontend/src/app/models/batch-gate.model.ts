export type BatchDecision = 'wait' | 'close' | 'per-task' | 'idle' | 'disabled';
export type BatchMemberStatus = 'pending' | 'in-batch' | 'published' | 'fallback';

export interface BatchGateSettings {
  enabled: boolean;
  scope: 'documentation' | 'code';
  planner: 'threshold' | 'look-ahead';
  minimumSize: number;
  maximumSize: number;
  deadlineMinutes: number;
  lookAheadMinutes: number;
  urgentTags: string[];
  riskPolicy: 'conservative' | 'balanced' | 'aggressive';
  overlap: 'adjacent' | 'separate';
  reviewTestLevel: 'work-package' | 'build-only';
  gateTimeoutMinutes: number;
  advisor: { mode: 'off' | 'advise' | 'enforce'; cliType: string; model?: string; timeoutSeconds: number };
}

export interface BatchGateConfiguration {
  settings: BatchGateSettings;
  limits: { hardMaximumSize: number };
}

export interface BatchGateState {
  plan: {
    decision: BatchDecision;
    reason: string;
    closeAtUtc?: string | null;
    proposedMembers: string[];
    targetSize: number;
    upcoming: { taskKey: string; stage: string; etaUtc?: string | null; risk: BatchRisk; overlapsWith: string[] }[];
    advisor?: { mode: string; verdict: string; reasons: string[]; model: string; atUtc: string } | null;
  };
  pending: { taskKey: string; reviewAttemptId: string; resultSha: string; waitMinutes: number; risk: BatchRisk; exclusion?: string | null }[];
  batches: {
    batchId: string;
    parentBatchId?: string | null;
    phase: string;
    phaseReason?: string | null;
    baseSha: string;
    candidateSha?: string | null;
    members: { taskKey: string; replay: BatchReplay; released: boolean }[];
    runs: { batchRunId: string; host: string; outcome?: string | null; classification?: string | null; startedAtUtc?: string | null; finishedAtUtc?: string | null }[];
    publication?: BatchPublication | null;
  }[];
  members: Record<string, BatchMemberState>;
}

/**
 * One card's batch status. An ejected member carries why it went to the
 * per-task gate, `conflict` or `unattributable`, with the replay detail in
 * `reasonDetail`.
 */
export interface BatchMemberState {
  status: BatchMemberStatus;
  batchId?: string | null;
  phase?: string | null;
  position?: number | null;
  size?: number | null;
  reason?: string | null;
  reasonDetail?: string | null;
}

/** The backend reports risk as a probability between 0 and 1. */
export type BatchRisk = number | string | null;
/** The backend reports a member's replay as an object with an outcome, or null before replay. */
export type BatchReplay = { outcome?: string | null } | string | null;
export interface BatchPublication {
  verifiedRemoteSha?: string | null;
  testedCandidateSha?: string | null;
  publishedSha?: string | null;
  candidateSha?: string | null;
  atUtc?: string | null;
  [key: string]: unknown;
}

export function batchRiskLabel(risk: BatchRisk | undefined): string {
  if (typeof risk === 'number' && Number.isFinite(risk)) return `${Math.round(risk * 100)} %`;
  return typeof risk === 'string' && risk ? risk : 'unknown';
}

export function batchReplayLabel(replay: BatchReplay | undefined): string {
  if (typeof replay === 'string') return replay;
  return replay?.outcome ?? 'not replayed';
}

/** Short card label for why a member left its batch. */
export function batchEjectionLabel(reason: string | null | undefined): string | null {
  switch (reason) {
    case 'conflict': return 'replay conflict';
    case 'unattributable': return 'unattributable history';
    default: return reason || null;
  }
}

export function batchPublishedSha(publication: BatchPublication | null | undefined): string | null {
  return publication?.verifiedRemoteSha ?? publication?.publishedSha ?? null;
}

export function batchMemberFor(state: BatchGateState | null, ...keys: (string | null | undefined)[]) {
  if (!state) return null;
  for (const key of keys) {
    if (key && state.members[key]) return state.members[key];
  }
  return null;
}
