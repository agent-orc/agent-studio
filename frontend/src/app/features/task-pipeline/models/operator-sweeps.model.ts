/** AGT-3011: the in-product operator sweeps and their health projection. */
export type OperatorSweepKind = 'fix-rounds' | 'gate-triage' | 'salvage';

export interface OperatorSweepRecentAction {
  atUtc: string;
  taskKey: string;
  reason: string;
  detail: string;
}

export interface OperatorSweepStatus {
  sweep: OperatorSweepKind | string;
  paused: boolean;
  pausedAtUtc?: string | null;
  pausedBy?: string | null;
  pauseReason?: string | null;
  lastRunStartedAtUtc?: string | null;
  lastRunFinishedAtUtc?: string | null;
  lastRunError?: string | null;
  isOverdue: boolean;
  lastActed: number;
  lastHeld: number;
  lastWaitingForPerson: number;
  recentActions: OperatorSweepRecentAction[];
}

export interface OperatorSweepCardDecision {
  sweep: OperatorSweepKind | string;
  action: 'Act' | 'Hold' | 'WaitForPerson' | string;
  reason: string;
  detail?: string | null;
  subjectKey?: string | null;
}

export interface OperatorSweepCardState {
  taskKey: string;
  jobId: string;
  title: string;
  lane: string;
  roundsUsed: number;
  roundsAllowed: number;
  roundsRemaining: number;
  decisions: OperatorSweepCardDecision[];
}

export interface OperatorSweepWaitingCard {
  taskKey: string;
  title: string;
  lane: string;
  sweep: OperatorSweepKind | string;
  reason: string;
  detail: string;
}

export interface OperatorSweepProjection {
  project: string;
  capturedAtUtc: string;
  status: 'healthy' | 'paused' | 'disabled' | 'alarm' | string;
  enabled: boolean;
  tickIntervalSeconds: number;
  maxRoundsPerCard: number;
  lastTickAtUtc?: string | null;
  sweeps: OperatorSweepStatus[];
  cards: OperatorSweepCardState[];
  waitingForPerson: OperatorSweepWaitingCard[];
}
