import { describe, expect, it } from 'vitest';
import {
  emptyReviewProjection,
  type ReviewProjection,
  type ReviewRoundAspect,
} from '../../../../models/review-projection.model';
import { buildReviewHeadline, formatStamp } from './review-headline.util';

/** Projection fixture: the empty card, overridden field by field. */
function projection(over: Partial<ReviewProjection> = {}): ReviewProjection {
  return { ...emptyReviewProjection('AGT-2689'), ...over };
}

function aspect(over: Partial<ReviewRoundAspect> = {}): ReviewRoundAspect {
  return {
    name: over.name ?? 'documentation-impact',
    verdict: over.verdict ?? 'block',
    summary: over.summary ?? '',
  };
}

/**
 * The card that named the bug: seven remote review rounds, a passing build and
 * a delivery gate that held the work back, announced by the old banner as
 * "0 review rounds · Grade not recorded".
 *
 * `latestAt` is written WITHOUT a zone suffix on purpose. The headline formats
 * the instant in the operator's own timezone, so a zoned fixture would assert a
 * different clock time on every CI machine; a local-time literal pins both
 * sides of the assertion to the same wall clock.
 */
const AGT_2689 = projection({
  roundCount: 7,
  plane: 'remote',
  latestOutcome: 'ProductFailure',
  latestAt: '2026-08-31T17:11:00',
  blockingAspects: [
    aspect({
      summary:
        'Public API and state-file contract changed without corresponding load-bearing doc updates',
    }),
  ],
  buildTests: {
    result: 'passed',
    reason: 'Build and tests passed in verify-1 and verify-2.',
    steps: ['verify-1', 'verify-2'],
  },
  delivery: {
    state: 'gate-failed',
    reason: 'Remote delivery gate failed before integration',
    integrationBranch: 'develop',
  },
  recommendation: 'reissue',
  recommendationReason:
    'Reissue naming documentation-impact: update the load-bearing docs for the changed API and state-file contract.',
});

describe('buildReviewHeadline (AGT-2689 shape)', () => {
  it('names the rounds, the latest outcome, the blocker and its reason', () => {
    const headline = buildReviewHeadline(AGT_2689);
    expect(headline.rounds).toBe('7 review rounds (remote)');
    expect(headline.latest).toBe('latest 31.08. 17:11 ProductFailure');
    expect(headline.blocked).toBe(
      'blocked by documentation-impact: Public API and state-file contract changed without corresponding load-bearing doc updates',
    );
  });

  it('keeps the passing build and the failed gate in the same line', () => {
    const headline = buildReviewHeadline(AGT_2689);
    expect(headline.buildTests).toBe('build and tests pass (verify-1, verify-2)');
    expect(headline.delivery).toBe('delivery gate failed, not in develop');
    expect(headline.pendingReason).toBe('delivery gate failed (documentation-impact)');
  });

  it('composes the banner one-liner from every recorded fact', () => {
    const { label } = buildReviewHeadline(AGT_2689);
    expect(label).toBe(
      [
        '7 review rounds (remote)',
        'latest 31.08. 17:11 ProductFailure',
        'blocked by documentation-impact: Public API and state-file contract changed without corresponding load-bearing doc updates',
        'build and tests pass (verify-1, verify-2)',
        'delivery gate failed, not in develop',
        'Reissue naming documentation-impact: update the load-bearing docs for the changed API and state-file contract.',
      ].join(' · '),
    );
  });

  it('never regresses to the copy the projection was built to kill', () => {
    const { label, chip, rounds } = buildReviewHeadline(AGT_2689);
    expect(label).not.toContain('0 review rounds');
    expect(label).not.toContain('Grade not recorded');
    expect(rounds).not.toContain('0 review rounds');
    expect(chip).not.toContain('no review');
  });

  it('fits the board chip into the card column', () => {
    const { chip } = buildReviewHeadline(AGT_2689);
    expect(chip).toBe('7 rounds · documentation-impact');
    expect(chip.length).toBeLessThanOrEqual(40);
  });
});

describe('buildReviewHeadline (no rounds)', () => {
  it('says nothing was recorded instead of reporting a zero', () => {
    const headline = buildReviewHeadline(emptyReviewProjection('AGT-9001'));
    expect(headline.rounds).toBe('No review round recorded');
    expect(headline.rounds).not.toContain('0 review rounds');
    expect(headline.latest).toBeNull();
    expect(headline.blocked).toBeNull();
    expect(headline.buildTests).toBe(
      'build and tests not proven: no build or test command recorded',
    );
    expect(headline.delivery).toBe('no delivery recorded');
    expect(headline.pendingReason).toBe('no delivery recorded');
    expect(headline.chip).toBe('no review');
  });

  it('drops the empty recommendation from the label instead of joining a gap', () => {
    const { label } = buildReviewHeadline(emptyReviewProjection('AGT-9001'));
    expect(label).toBe(
      'No review round recorded · build and tests not proven: no build or test command recorded · no delivery recorded',
    );
    expect(label).not.toContain('·  ·');
  });
});

describe('buildReviewHeadline (build failure)', () => {
  const failing = projection({
    roundCount: 1,
    plane: 'local',
    latestOutcome: 'block',
    latestAt: '2026-09-02T08:04:00',
    buildTests: {
      result: 'failed',
      reason: 'npm test exited with 1 in verify-2',
      steps: ['verify-2'],
    },
    delivery: { state: 'not-attempted', reason: '', integrationBranch: null },
    recommendation: 'reissue',
    recommendationReason: 'Reissue: make verify-2 green before the next review round.',
  });

  it('prefers the build failure over any semantic aspect', () => {
    const headline = buildReviewHeadline({
      ...failing,
      blockingAspects: [aspect({ name: 'test-coverage', summary: 'no regression added' })],
    });
    expect(headline.blocked).toBe('build failed: npm test exited with 1 in verify-2');
    expect(headline.buildTests).toBe('build and tests failed');
  });

  it('uses the singular round noun and the failing build as the chip tail', () => {
    const headline = buildReviewHeadline(failing);
    expect(headline.rounds).toBe('1 review round (local)');
    expect(headline.chip).toBe('1 round · build failed');
    expect(headline.delivery).toBe('no delivery recorded');
  });
});

describe('buildReviewHeadline (integrated and passing)', () => {
  const passing = projection({
    roundCount: 2,
    plane: 'mixed',
    latestOutcome: 'Pass',
    latestAt: '2026-09-05T11:30:00',
    grade: 'B',
    buildTests: {
      result: 'passed',
      reason: 'Build and tests passed in verify-1.',
      steps: ['verify-1'],
    },
    delivery: { state: 'integrated', reason: '', integrationBranch: 'develop' },
    recommendation: 'none',
    recommendationReason: '',
  });

  it('reports the landing branch and leaves the delivery chip unqualified', () => {
    const headline = buildReviewHeadline(passing);
    expect(headline.rounds).toBe('2 review rounds (mixed)');
    expect(headline.blocked).toBeNull();
    expect(headline.delivery).toBe('in develop');
    expect(headline.pendingReason).toBeNull();
    expect(headline.chip).toBe('2 rounds · Pass');
    expect(headline.label).toBe(
      '2 review rounds (mixed) · latest 05.09. 11:30 Pass · build and tests pass (verify-1) · in develop',
    );
  });
});

describe('buildReviewHeadline edge cases', () => {
  it('admits a blocking aspect that carries no reason', () => {
    const headline = buildReviewHeadline(
      projection({ roundCount: 3, plane: 'remote', blockingAspects: [aspect({ summary: '  ' })] }),
    );
    expect(headline.blocked).toBe('blocked by documentation-impact (no reason recorded)');
  });

  it('caps an over-long chip instead of stretching the board card', () => {
    const headline = buildReviewHeadline(
      projection({
        roundCount: 4,
        plane: 'remote',
        blockingAspects: [aspect({ name: 'documentation-impact-and-contract-surface' })],
      }),
    );
    expect(headline.chip.length).toBeLessThanOrEqual(40);
    expect(headline.chip.startsWith('4 rounds · documentation-impact')).toBe(true);
  });

  it('keeps the outcome when no instant was recorded, and vice versa', () => {
    expect(buildReviewHeadline(projection({ roundCount: 1, latestOutcome: 'Pass' })).latest).toBe(
      'latest Pass',
    );
    expect(
      buildReviewHeadline(projection({ roundCount: 1, latestAt: '2026-09-05T11:30:00' })).latest,
    ).toBe('latest 05.09. 11:30');
  });
});

describe('formatStamp', () => {
  it('zero-pads day, month, hour and minute in 24h form', () => {
    expect(formatStamp('2026-01-05T09:07:00')).toBe('05.01. 09:07');
    expect(formatStamp('2026-12-31T23:59:00')).toBe('31.12. 23:59');
  });

  it('returns an empty segment for a missing or unparseable instant', () => {
    expect(formatStamp(null)).toBe('');
    expect(formatStamp('')).toBe('');
    expect(formatStamp('not-a-date')).toBe('');
  });
});
