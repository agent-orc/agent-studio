import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';
import type { AspectVerdictTone } from '../../../../../components/aspect-findings/aspect-findings.model';
import type { PipelineGroupTone } from './pipeline-groups.util';
import type { PipelineRowVm } from './pipeline-row.vm';
import { stepVerdictTone } from '../pipeline-step-result/step-status-tone';

/**
 * Status tones of the Overview pipeline block (AGT-3403). Each table maps a
 * presentation tone onto the shared indicator; anything unknown is neutral.
 */
const GROUP_TONES: Readonly<Record<PipelineGroupTone, StatusTone>> = {
  ok: 'success',
  danger: 'danger',
  warn: 'running',
  concern: 'warning',
  muted: 'neutral',
  'not-applicable': 'neutral',
  'not-run': 'neutral',
  neutral: 'neutral',
};

const DECISION_TONES: Readonly<Record<AspectVerdictTone, StatusTone>> = {
  ok: 'success',
  warn: 'warning',
  danger: 'danger',
  neutral: 'neutral',
};

/** Section header aggregate: the "warn" group tone means the section is running. */
export function pipelineGroupStatusTone(tone: string): StatusTone {
  return GROUP_TONES[tone as PipelineGroupTone] ?? 'neutral';
}

/** Final-verdict route; a superseded attempt is history and stays neutral. */
export function decisionBadgeStatusTone(tone: string | null | undefined, historical: boolean): StatusTone {
  if (historical || !tone) return 'neutral';
  return DECISION_TONES[tone as AspectVerdictTone] ?? 'neutral';
}

/** Current pipeline run outcome counter in the runs strip. */
export type RunOutcomeCounter = 'pass' | 'fail' | 'pending';

const RUN_OUTCOME_TONES: Readonly<Record<RunOutcomeCounter, StatusTone>> = {
  pass: 'success',
  fail: 'danger',
  pending: 'neutral',
};

export function runOutcomeCounterTone(kind: RunOutcomeCounter): StatusTone {
  return RUN_OUTCOME_TONES[kind] ?? 'neutral';
}

/** Step verdict tone; a superseded attempt is history and stays neutral (R4). */
export function rowStepVerdictTone(row: Pick<PipelineRowVm, 'historical'>, verdict: string): StatusTone {
  return row.historical ? 'neutral' : stepVerdictTone(verdict);
}

/** Review-round finding tone; neutral on a superseded attempt. */
export function rowReviewRoundTone(row: Pick<PipelineRowVm, 'historical' | 'reviewRoundTone'>): StatusTone {
  return row.historical ? 'neutral' : row.reviewRoundTone ?? 'neutral';
}

/** Visible label, suffixed with "· superseded" on a historical attempt. */
export function historicalRowLabel(row: Pick<PipelineRowVm, 'historical'>, label: string): string {
  return row.historical ? `${label} · superseded` : label;
}
