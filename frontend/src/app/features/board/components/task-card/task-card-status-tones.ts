import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type {
  CodeReviewGradeBadge,
  CommitEmptyBadge,
  DependencyChip,
  ExecutionBadge,
  OutcomeIssueBadge,
  PhaseBadgeTone,
  ReviewBadge,
} from './task-card-view-model';

/**
 * Task card state-to-tone mapping for the shared `<app-status-indicator>`
 * (AGT-3403). The card badges show current truth only (style-guide hard rules
 * R4); every map reads the badge's own state key and falls back to `neutral`
 * for a state the backend adds later. Contract:
 * docs/quality/design/status-indicator.md.
 */
function toneFrom<K extends string>(map: Readonly<Record<K, StatusTone>>, key: string | null | undefined): StatusTone {
  return key != null && Object.prototype.hasOwnProperty.call(map, key) ? map[key as K] : 'neutral';
}

const GRADE_TONES: Readonly<Record<CodeReviewGradeBadge['tone'], StatusTone>> = {
  a: 'success',
  b: 'info',
  c: 'warning',
  d: 'danger',
};

const DEPENDENCY_TONES: Readonly<Record<DependencyChip['tone'], StatusTone>> = {
  open: 'warning',
  ready: 'success',
  cycle: 'danger',
  blocked: 'danger',
};

const PHASE_TONES: Readonly<Record<PhaseBadgeTone, StatusTone>> = {
  'human-ready': 'info',
  'steer-pending': 'warning',
  'loop-waiting': 'warning',
  'intake-running': 'running',
  'intake-blocked': 'danger',
  'intake-passed': 'success',
  'post-processing-running': 'running',
  'post-processing-blocked': 'danger',
  'awaiting-review': 'info',
  integrating: 'running',
};

const EXECUTION_TONES: Readonly<Record<ExecutionBadge['tone'], StatusTone>> = {
  running: 'running',
  failed: 'danger',
  noop: 'neutral',
  blocked: 'warning',
  'needs-input': 'warning',
  stopped: 'neutral',
};

const REVIEW_TONES: Readonly<Record<ReviewBadge['tone'], StatusTone>> = {
  generating: 'running',
  ready: 'success',
  failed: 'danger',
  degraded: 'warning',
};

const ISSUE_TONES: Readonly<Record<OutcomeIssueBadge['tone'], StatusTone>> = {
  info: 'info',
  warn: 'warning',
  high: 'danger',
};

const COMMIT_EMPTY_TONES: Readonly<Record<CommitEmptyBadge['tone'], StatusTone>> = {
  'no-code': 'neutral',
  discovery: 'warning',
  integrated: 'success',
};

export const gradeTone = (tone: string | null | undefined): StatusTone => toneFrom(GRADE_TONES, tone);
export const dependencyTone = (tone: string | null | undefined): StatusTone => toneFrom(DEPENDENCY_TONES, tone);
export const phaseTone = (tone: string | null | undefined): StatusTone => toneFrom(PHASE_TONES, tone);
export const executionTone = (tone: string | null | undefined): StatusTone => toneFrom(EXECUTION_TONES, tone);
export const reviewTone = (tone: string | null | undefined): StatusTone => toneFrom(REVIEW_TONES, tone);
export const issueTone = (tone: string | null | undefined): StatusTone => toneFrom(ISSUE_TONES, tone);
export const commitEmptyTone = (tone: string | null | undefined): StatusTone => toneFrom(COMMIT_EMPTY_TONES, tone);

/** Open decision waits on a person (warning); a decided card is settled (neutral). */
export const decisionTone = (open: boolean): StatusTone => (open ? 'warning' : 'neutral');
/** Auto-loop iteration: within budget is a fact (info), near the cap needs attention. */
export const loopTone = (hot: boolean): StatusTone => (hot ? 'warning' : 'info');
/** Merge segment: landed on that rung (success) or not yet (neutral). */
export const mergeTone = (merged: boolean): StatusTone => (merged ? 'success' : 'neutral');
