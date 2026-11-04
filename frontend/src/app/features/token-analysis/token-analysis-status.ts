import type { StatusTone } from '../../components/status-indicator/status-indicator.component';
import type { CardOutcome } from './models/token-analysis.model';

/** Status tone of a card outcome in the token analysis (AGT-3403); unknown outcomes stay neutral. */
const CARD_OUTCOME_TONES: Readonly<Record<CardOutcome, StatusTone>> = {
  integrated: 'success',
  completed: 'success',
  delivered: 'success',
  escalated: 'warning',
  blocked: 'warning',
  'in-progress': 'running',
  open: 'neutral',
};

export function cardOutcomeTone(outcome: string | null | undefined): StatusTone {
  return CARD_OUTCOME_TONES[outcome as CardOutcome] ?? 'neutral';
}

/**
 * Status tone of a free-form step result in the card drill-down. Pass-like
 * results read as success; a fail-like result is a past step, so it is a
 * warning rather than an acute danger (hard rule R4). Anything else is neutral.
 */
export function stepResultTone(result: string | null | undefined): StatusTone {
  const value = (result ?? '').trim().toLowerCase();
  if (/^(pass|passed|ok|success|succeeded|approved|green)$/.test(value)) return 'success';
  if (/^(fail|failed|failure|error|rejected|red)$/.test(value)) return 'warning';
  return 'neutral';
}
