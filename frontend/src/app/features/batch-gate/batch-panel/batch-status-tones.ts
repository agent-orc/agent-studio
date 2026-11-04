import type { StatusTone } from '../../../components/status-indicator/status-indicator.component';
import type { BatchDecision, BatchMemberStatus } from '../../../models/batch-gate.model';

/** Batch member status to status tone (AGT-3403); unknown states are neutral. */
const MEMBER_STATUS_TONES: Readonly<Record<BatchMemberStatus, StatusTone>> = {
  pending: 'neutral',
  'in-batch': 'running',
  published: 'success',
  fallback: 'warning',
  'pre-check-red': 'danger',
};

/** Batch plan decision to status tone (AGT-3403). */
const DECISION_TONES: Readonly<Record<BatchDecision, StatusTone>> = {
  wait: 'info',
  close: 'running',
  'per-task': 'warning',
  idle: 'neutral',
  disabled: 'neutral',
};

/** Run or replay outcome words the backend reports, lower-cased. */
const OUTCOME_TONES: Readonly<Record<string, StatusTone>> = {
  passed: 'success',
  pass: 'success',
  green: 'success',
  applied: 'success',
  failed: 'danger',
  fail: 'danger',
  red: 'danger',
  conflict: 'danger',
  running: 'running',
  'not replayed': 'neutral',
  waiting: 'neutral',
};

const ADVISOR_VERDICT_TONES: Readonly<Record<string, StatusTone>> = {
  approve: 'success',
  veto: 'danger',
};

export function batchMemberStatusTone(status: string | null | undefined): StatusTone {
  return MEMBER_STATUS_TONES[status as BatchMemberStatus] ?? 'neutral';
}

export function batchDecisionTone(decision: string | null | undefined): StatusTone {
  return DECISION_TONES[decision as BatchDecision] ?? 'neutral';
}

export function batchOutcomeTone(outcome: string | null | undefined): StatusTone {
  return OUTCOME_TONES[(outcome ?? '').trim().toLowerCase()] ?? 'neutral';
}

export function batchAdvisorVerdictTone(verdict: string | null | undefined): StatusTone {
  return ADVISOR_VERDICT_TONES[(verdict ?? '').trim().toLowerCase()] ?? 'neutral';
}
