import type { AcceptanceReadinessClass, AcceptanceReadinessItem } from './acceptance-readiness.model';

/**
 * The one reason a Human Review card is not ready, and what resolves it. The
 * toolbar refusal, the acceptance grounds box and the board note each read it
 * from the same acceptance-readiness result, so a card never shows three
 * unranked blockers (F12). Other gaps the card shows are secondary to it.
 */
export interface AcceptancePrimaryReason {
  readonly readiness: AcceptanceReadinessClass;
  readonly headline: string;
  readonly reason: string;
  readonly nextStep: string;
}

const NEXT_STEPS: Readonly<Record<AcceptanceReadinessClass, string>> = {
  'ready': 'Accept the delivery.',
  'review-concerns': 'Send a round to close the gap the review raised.',
  'integrated-unverified': 'Wait for a gate to pass on the merged tree, or accept with an override.',
  'not-integrated': 'Integrate the delivery, or accept with an override.',
  'verdict-stale': 'Review the delivery again against the current brief.',
  'no-content-verdict': 'Run the content review for the current run.',
  'blocked': 'Resolve the refusal, or accept with an override.',
  'waiting-deploy': 'Wait for the deploy, then check again.',
  'owner-decision': 'Answer the owner decision.',
};

/** The resolving step for a readiness class; unknown classes read as a refusal. */
export function readinessNextStep(item: Pick<AcceptanceReadinessItem, 'readiness' | 'aspect'>
  & Partial<Pick<AcceptanceReadinessItem, 'stage' | 'overrideAllowed' | 'round'>>): string {
  if (item.stage === 'batch-gate')
    return 'Send a round to close the batch gate evidence gap, or wait for a gate to pass.';
  if (item.overrideAllowed === false && item.round)
    return 'Send a round to close the gap.';
  if (item.readiness === 'no-content-verdict' && item.aspect)
    return `Run the ${item.aspect} review for the current run.`;
  return NEXT_STEPS[item.readiness] ?? NEXT_STEPS.blocked;
}

/** The primary reason of a card that is not ready, or null when it is ready or unchecked. */
export function acceptancePrimaryReason(item: AcceptanceReadinessItem | null | undefined): AcceptancePrimaryReason | null {
  if (!item || item.readiness === 'ready') return null;
  return {
    readiness: item.readiness,
    headline: item.headline,
    reason: item.reason,
    nextStep: readinessNextStep(item),
  };
}

/** Other gaps under the primary reason, without repeats or the primary sentence itself. */
export function secondaryAcceptanceGaps(
  primary: AcceptancePrimaryReason | null,
  gaps: readonly (string | null | undefined)[],
): string[] {
  const seen = new Set(primary ? [primary.reason.trim()] : []);
  const result: string[] = [];
  for (const gap of gaps) {
    const text = gap?.trim();
    if (!text || seen.has(text)) continue;
    seen.add(text);
    result.push(text);
  }
  return result;
}
