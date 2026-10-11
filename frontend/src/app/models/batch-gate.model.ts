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
    closeAtUtc?: string;
    proposedMembers: string[];
    targetSize: number;
    upcoming: { taskKey: string; stage: string; etaUtc?: string; risk: string; overlapsWith: string[] }[];
    advisor?: { mode: string; verdict: string; reasons: string[]; model: string; atUtc: string };
  };
  pending: { taskKey: string; reviewAttemptId: string; resultSha: string; waitMinutes: number; risk: string; exclusion?: string }[];
  batches: {
    batchId: string;
    parentBatchId?: string;
    phase: string;
    phaseReason?: string;
    baseSha: string;
    candidateSha?: string;
    members: { taskKey: string; replay: string; released: boolean }[];
    runs: { batchRunId: string; host: string; outcome?: string; classification?: string; startedAtUtc?: string; finishedAtUtc?: string }[];
    publication?: { candidateSha?: string; publishedSha?: string; atUtc?: string; [key: string]: unknown };
  }[];
  members: Record<string, { status: BatchMemberStatus; batchId?: string; phase?: string; position?: number; size?: number }>;
}

export function batchMemberFor(state: BatchGateState | null, ...keys: (string | null | undefined)[]) {
  if (!state) return null;
  for (const key of keys) {
    if (key && state.members[key]) return state.members[key];
  }
  return null;
}
