import { describe, expect, it } from 'vitest';
import { acceptancePrimaryReason, readinessNextStep, secondaryAcceptanceGaps } from './acceptance-primary-reason';
import { readinessItem } from '../testing/acceptance-readiness.fixtures';

describe('acceptancePrimaryReason (F12)', () => {
  it('takes the headline and reason of the readiness answer and names the step that resolves it', () => {
    const item = readinessItem('a', 'no-content-verdict', {
      headline: 'No content verdict',
      reason: 'The current run has no requirement-fit verdict: Only build-tests ran.',
    });

    expect(acceptancePrimaryReason(item)).toEqual({
      readiness: 'no-content-verdict',
      headline: 'No content verdict',
      reason: 'The current run has no requirement-fit verdict: Only build-tests ran.',
      nextStep: 'Run the requirement-fit review for the current run.',
    });
  });

  it('has no primary reason for a ready or unchecked card', () => {
    expect(acceptancePrimaryReason(readinessItem('a', 'ready'))).toBeNull();
    expect(acceptancePrimaryReason(null)).toBeNull();
  });

  it('reads an unknown class from a newer backend as a refusal', () => {
    expect(readinessNextStep({ readiness: 'later-class' as never, aspect: null }))
      .toBe('Resolve the refusal, or accept with an override.');
  });

  it('lists the other gaps once, without the primary sentence', () => {
    const primary = acceptancePrimaryReason(readinessItem('a', 'review-concerns'));
    expect(secondaryAcceptanceGaps(primary, [
      'No delivery branch or commit was found for this card.',
      primary!.reason,
      null,
      ' No delivery branch or commit was found for this card. ',
    ])).toEqual(['No delivery branch or commit was found for this card.']);
  });
});
