import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { PromptReviewFinding, PromptReviewStatus } from '../../../../services/prompt-admin.service';

interface StatusPresentation {
  label: string;
  tone: StatusTone;
}

const REVIEW_STATUS: Record<PromptReviewStatus, StatusPresentation> = {
  current: { label: 'current', tone: 'success' },
  'needs-review': { label: 'needs review', tone: 'warning' },
  stale: { label: 'stale', tone: 'danger' },
};

const NOT_REVIEWED: StatusPresentation = { label: 'not reviewed', tone: 'neutral' };

/** Label and tone of a prompt review status; a missing or unknown status is neutral. */
export function promptReviewStatus(status: string | null | undefined): StatusPresentation {
  if (!status) return NOT_REVIEWED;
  return REVIEW_STATUS[status as PromptReviewStatus] ?? { label: status, tone: 'neutral' };
}

const FINDING_SEVERITY_TONES: Record<PromptReviewFinding['severity'], StatusTone> = {
  error: 'danger',
  warning: 'warning',
  info: 'info',
};

/** Tone of a review finding severity; an unknown severity is neutral. */
export function promptFindingTone(severity: string): StatusTone {
  return FINDING_SEVERITY_TONES[severity as PromptReviewFinding['severity']] ?? 'neutral';
}
