import { describe, expect, it } from 'vitest';

import {
  UNSTATED_QUESTION,
  buildParkedBadge,
  buildParkedBlockerView,
  parkedBlockerLabel,
  parkedForLabel,
  parkedOpenItems,
} from './parked-blocker-presentation';
import type { ParkedBlockerStatus, TaskInfo } from './task.model';

/**
 * AGT-2816. AGT-2736 showed `Result: Success` / `Open Items: None` for three
 * days while it was in fact parked on an operator decision. These specs pin the
 * projection that makes that impossible: the park present, the park absent, a
 * stale recall verdict, and the invariant that a parked card never reports zero
 * open items.
 */
describe('parked-blocker presentation', () => {
  const park = (overrides: Partial<ParkedBlockerStatus> = {}): ParkedBlockerStatus => ({
    blockerType: 'agent-needs-input',
    conditionKind: 'manual',
    conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
    parkedAt: '2026-09-11T14:44:00.000Z',
    parkedForSeconds: 3 * 24 * 60 * 60,
    reason: '[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan',
    recallStatus: 'blocked',
    lastEvaluatedAt: '2026-09-14T11:50:00.000Z',
    detail: 'Only a person can clear this park.',
    lane: '5e-escalated',
    decision: {
      questionId: 'choose-connector-vs-lan',
      question: 'Should the Studio backend be reached through the Connector or over the LAN?',
      options: [
        { id: 'a', label: 'Managed connector.', consequences: 'Simpler operations.', recommended: true },
        { id: 'b', label: 'LAN-reachable backend.', consequences: 'Needs network access.', recommended: false },
        { id: 'c', label: 'Ship both behind a setting.', consequences: null, recommended: false },
      ],
      documents: ['docs/operations/setup/docker-compose-connector-gap.md'],
      decisionCardKey: null,
    },
    needsInputFile: 'results/needs-input.md',
    evaluationAgeSeconds: 600,
    evaluationStale: false,
    requiresDecisionCard: true,
    ...overrides,
  });

  const info = (parkedBlocker: ParkedBlockerStatus | null): Pick<TaskInfo, 'parkedBlocker'> =>
    ({ parkedBlocker }) as Pick<TaskInfo, 'parkedBlocker'>;

  it('renders nothing for a card that is not parked', () => {
    expect(buildParkedBlockerView(info(null))).toBeNull();
    expect(buildParkedBadge(info(null))).toBeNull();
  });

  it('projects the question, the options, the documents, and the age', () => {
    const view = buildParkedBlockerView(info(park()))!;

    expect(view.blockerLabel).toBe('Agent needs input');
    expect(view.isDecision).toBe(true);
    expect(view.question).toBe(
      'Should the Studio backend be reached through the Connector or over the LAN?',
    );
    expect(view.questionMissing).toBe(false);
    expect(view.questionId).toBe('choose-connector-vs-lan');
    expect(view.options.map((option) => option.id)).toEqual(['a', 'b', 'c']);
    expect(view.documents).toEqual(['docs/operations/setup/docker-compose-connector-gap.md']);
    expect(view.needsInputFile).toBe('results/needs-input.md');
    expect(view.parkedFor).toBe('3 days');
    expect(view.recall.label).toBe('Still blocked');
    expect(view.recall.tone).toBe('warn');
  });

  it('says the run stated no question instead of presenting the slug as one', () => {
    const view = buildParkedBlockerView(info(park({
      decision: {
        questionId: 'choose-connector-vs-lan',
        question: '',
        options: [],
        documents: [],
        decisionCardKey: null,
      },
    })))!;

    expect(view.question).toBeNull();
    expect(view.questionMissing).toBe(true);
    // The slug survives as an identifier - it is never promoted to the question.
    expect(view.questionId).toBe('choose-connector-vs-lan');
  });

  it('reads an unevaluated blocker as "never checked", not as the default blocked verdict', () => {
    // The marker's default status IS `blocked`, so without this the board would
    // present "nobody has checked" as a verdict somebody reached.
    const view = buildParkedBlockerView(info(park({
      recallStatus: 'blocked',
      evaluationStale: true,
      lastEvaluatedAt: null,
      evaluationAgeSeconds: null,
    })))!;

    expect(view.recall.stale).toBe(true);
    expect(view.recall.label).toBe('Never checked');
    expect(view.recall.label).not.toBe('Still blocked');
    expect(view.recall.tone).toBe('neutral');
  });

  it('does not treat a long-held verdict as unchecked', () => {
    // An unchanged verdict is never re-persisted, so an old instant means the
    // verdict has HELD that long - evidence, not decay.
    const view = buildParkedBlockerView(info(park({
      evaluationStale: false,
      lastEvaluatedAt: '2026-09-11T15:00:00.000Z',
      evaluationAgeSeconds: 3 * 24 * 60 * 60,
    })))!;

    expect(view.recall.stale).toBe(false);
    expect(view.recall.label).toBe('Still blocked');
  });

  it('reports a cleared precondition without implying the card was requeued', () => {
    const view = buildParkedBlockerView(info(park({ recallStatus: 'recallable' })))!;
    expect(view.recall.label).toBe('Precondition cleared');
    expect(view.recall.tone).toBe('ok');
  });

  it('never reports zero open items for a parked card', () => {
    const cases: ParkedBlockerStatus[] = [
      park(),
      park({ decision: null }),
      park({ decision: null, reason: '', conditionDescription: '' }),
      park({ blockerType: 'infra-crash', requiresDecisionCard: false }),
    ];

    for (const candidate of cases) {
      const items = parkedOpenItems(candidate);
      expect(items.length).toBeGreaterThan(0);
      expect(items.some((item) => /\bnone\b/i.test(item))).toBe(false);
      expect(buildParkedBlockerView(info(candidate))!.openItems).toEqual(items);
    }

    // With neither a question nor a reason the item is honest about the gap
    // rather than silently empty.
    expect(parkedOpenItems(park({ decision: null, reason: '' }))[0]).toContain(UNSTATED_QUESTION);
  });

  it('separates a card waiting on a person from one waiting on a fix', () => {
    const decision = buildParkedBadge(info(park()))!;
    expect(decision.tone).toBe('decision');
    expect(decision.label).toBe('Waiting on you · 3 days');

    const failure = buildParkedBadge(info(park({
      blockerType: 'infra-crash',
      requiresDecisionCard: false,
    })))!;
    expect(failure.tone).toBe('parked');
    expect(failure.label).toBe('Parked · 3 days');
  });

  it('labels an unknown park category without inventing one', () => {
    expect(parkedBlockerLabel('operator-decision')).toBe('Operator decision');
    expect(parkedBlockerLabel('some-new-category')).toBe('Some New Category');
  });

  it('ages a park coarsely and never overstates the wait', () => {
    expect(parkedForLabel(30)).toBe('less than a minute');
    expect(parkedForLabel(60)).toBe('1 minute');
    expect(parkedForLabel(3 * 3600)).toBe('3 hours');
    expect(parkedForLabel(47 * 3600)).toBe('1 day');
    expect(parkedForLabel(-5)).toBe('less than a minute');
  });
});
