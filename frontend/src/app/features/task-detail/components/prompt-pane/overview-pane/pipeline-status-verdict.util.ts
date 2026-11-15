const REDUNDANT_VERDICTS: Readonly<Record<string, readonly string[]>> = {
  passed: ['ok', 'pass', 'passed', 'success', 'succeeded'],
  failed: ['error', 'fail', 'failed', 'failure'],
  running: ['active', 'run', 'running'],
  skipped: ['skip', 'skipped'],
  planned: ['planned'],
  pending: ['pending'],
  disabled: ['disabled', 'inactive'],
  notApplicable: ['n/a', 'not applicable', 'not-applicable'],
  'not-run': ['not run', 'not-run'],
};

/** Keep only verdicts that add information beyond the authoritative status icon. */
export function distinctStepVerdict(status: string, verdict: string | null): string | null {
  if (!verdict) return null;
  const normalized = verdict.trim().toLowerCase();
  return REDUNDANT_VERDICTS[status]?.includes(normalized) ? null : verdict;
}

/** Tone of the review-round finding state (AGT-3403): carried over, fixed or still open. */
export function reviewRoundTone(step: PipelineStepExecution | null | undefined): StatusTone {
  if (step?.carriedOverFrom) return 'info';
  if (step?.fixedInRun != null) return 'success';
  return step?.stillOpen === true ? 'warning' : 'neutral';
}

export function reviewRoundStatus(step: PipelineStepExecution | null | undefined): string | null {
  if (step?.carriedOverFrom) return `Carried over from review ${step.carriedOverFrom}`;
  if (step?.fixedInRun != null) return `Fixed in run #${step.fixedInRun}`;
  return step?.stillOpen === true ? 'Still open' : null;
}
import type { PipelineStepExecution } from '../../../../task-pipeline';
import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';
