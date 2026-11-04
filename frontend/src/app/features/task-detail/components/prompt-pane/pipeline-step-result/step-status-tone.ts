import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

/**
 * Status tones for pipeline steps (AGT-3403), shared by the Overview step
 * table, the step result popover and the post-step attempt history. Every
 * table falls back to neutral for a value the backend adds later.
 */
const STEP_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  passed: 'success',
  failed: 'danger',
  running: 'running',
  needsDecision: 'warning',
  skipped: 'neutral',
  notApplicable: 'neutral',
  'not-run': 'neutral',
  planned: 'neutral',
  disabled: 'neutral',
  pending: 'neutral',
};

/** Tone of a pipeline step display status; a skip that requires attention is danger. */
export function pipelineStepStatusTone(status: string | null | undefined, attentionRequired = false): StatusTone {
  if (status === 'skipped' && attentionRequired) return 'danger';
  return lookup(STEP_STATUS_TONES, status ?? '');
}

const POST_STEP_ATTEMPT_TONES: Readonly<Record<string, StatusTone>> = {
  ok: 'success',
  passed: 'success',
  warn: 'warning',
  failed: 'danger',
};

/** Tone of an on-demand post-step attempt status (`ok` / `warn` / `failed` / raw). */
export function postStepAttemptTone(status: string | null | undefined): StatusTone {
  return lookup(POST_STEP_ATTEMPT_TONES, (status ?? '').trim().toLowerCase());
}

const STEP_VERDICT_TONES: Readonly<Record<string, StatusTone>> = {
  pass: 'success',
  success: 'success',
  clean: 'success',
  intended: 'success',
  concerns: 'warning',
  concern: 'warning',
  'at-risk': 'warning',
  looping: 'warning',
  block: 'danger',
  blocked: 'danger',
  drift: 'danger',
  'loop-detected': 'danger',
};

/** Tone of a raw step verdict (aspect, drift, loop guard). */
export function stepVerdictTone(verdict: string | null | undefined): StatusTone {
  return lookup(STEP_VERDICT_TONES, (verdict ?? '').trim().toLowerCase());
}

function lookup(table: Readonly<Record<string, StatusTone>>, key: string): StatusTone {
  return Object.hasOwn(table, key) ? table[key] : 'neutral';
}
