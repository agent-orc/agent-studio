import { describe, expect, it } from 'vitest';

import {
  UNSTATED_QUESTION,
  UNSTATED_REASON,
  buildParkedBadge,
  buildParkedBlockerView,
  parkCauseFromTimeline,
  parkCauseView,
  parkNextStepSentence,
  parkPreconditionMet,
  parkReasonSentence,
  parkedBlockerLabel,
  parkedForLabel,
  parkedOpenItems,
  type ParkTimelineEvent,
} from './parked-blocker-presentation';
import type { ParkedBlockerStatus, TaskInfo } from './task.model';

/**
 * AGT-2816. AGT-2736 showed `Result: Success` / `Open Items: None` for three
 * days while it was in fact parked on an operator decision. These specs pin the
 * projection that makes that impossible: the park present, the park absent, a
 * stale recall verdict, and the invariant that a parked card never reports zero
 * open items.
 */
// The board card carries parkedAt only; every age below is read at this clock.
const NOW = Date.parse('2026-09-14T14:44:00.000Z');

describe('parked-blocker presentation', () => {
  const park = (overrides: Partial<ParkedBlockerStatus> = {}): ParkedBlockerStatus => ({
    blockerType: 'agent-needs-input',
    conditionKind: 'manual',
    conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
    parkedAt: '2026-09-11T14:44:00.000Z',
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
    const view = buildParkedBlockerView(info(park()), NOW)!;

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
    const decision = buildParkedBadge(info(park()), NOW)!;
    expect(decision.tone).toBe('decision');
    expect(decision.label).toBe('Waiting on you · 3 days');

    const failure = buildParkedBadge(info(park({
      blockerType: 'infra-crash',
      requiresDecisionCard: false,
    })), NOW)!;
    expect(failure.tone).toBe('parked');
    expect(failure.label).toBe('Parked · 3 days');
  });

  it('derives the age from parkedAt, so the card needs no read-time counter', () => {
    const card = info(park());
    expect(buildParkedBadge(card, Date.parse('2026-09-11T16:44:00.000Z'))!.label)
      .toBe('Waiting on you · 2 hours');
    expect(buildParkedBadge(card, NOW)!.label).toBe('Waiting on you · 3 days');
  });

  it('falls back to a carried counter only when parkedAt is unreadable', () => {
    const view = buildParkedBlockerView(info(park({ parkedAt: '', parkedForSeconds: 5 * 60 })), NOW)!;
    expect(view.parkedFor).toBe('5 minutes');
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

/**
 * AGT-3586 (walkthrough AGT-3561, F11). DEMO-6 was parked by the acceptance
 * audit and read "The parking run stated no question", badge "Operator
 * decision", reason "missing-delivery: ... rerun the gate" although no button
 * reruns a gate. The copy now follows the recorded cause.
 */
describe('park copy from the recorded cause', () => {
  const escalatedButtons = [
    { id: 'reissue-escalated', label: 'Continue (reissue)' },
    { id: 'accept-escalated', label: 'Accept with reason' },
    { id: 'manual-resolve-escalated', label: 'Resolve manually' },
    { id: 'discard-escalated', label: 'Abort' },
  ];
  const humanReviewButtons = [
    { id: 'mark-done', label: 'Accept' },
    { id: 'send-back-to-ready', label: 'Send back to Ready (re-do)' },
  ];
  const auditPark: ParkedBlockerStatus = {
    blockerType: 'operator-decision',
    conditionKind: 'manual',
    conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
    parkedAt: '2026-11-09T08:23:00.000Z',
    reason: 'missing-delivery: Recover the delivery, resolve the integration failure and rerun the gate.',
    cause: 'acceptance-integration-failed',
    causeDetail: 'missing-delivery',
    recallStatus: 'blocked',
    lastEvaluatedAt: null,
    detail: 'No recall sweep has evaluated this blocker yet.',
    lane: '5e-escalated',
    decision: null,
    needsInputFile: null,
    evaluationAgeSeconds: null,
    evaluationStale: true,
    requiresDecisionCard: true,
  };

  it('names the acceptance audit and replaces its legacy slug-and-instruction reason with the fact', () => {
    const view = buildParkedBlockerView({ parkedBlocker: auditPark }, NOW)!;

    expect(view.cause.origin).toBe('acceptance-audit');
    expect(view.blockerLabel).toBe('Acceptance audit');
    expect(view.reason).toBe('No delivery branch was found for this card, so its work cannot be integrated.');
    expect(view.chipTooltip).not.toContain('missing-delivery');
    expect(view.chipTooltip).not.toContain('rerun the gate');
  });

  it('keeps a fact sentence the audit recorded and drops only its slug prefix', () => {
    const cause = parkCauseView('acceptance-integration-failed', 'Conflict');
    expect(parkReasonSentence('Acceptance integration ended with Conflict: develop moved', cause))
      .toBe('Acceptance integration ended with Conflict: develop moved.');
    expect(parkReasonSentence('[verify-definition-missing] No verify command is defined. Then use Retry integration.',
      parkCauseView('acceptance-integration-failed', 'verify-definition-missing')))
      .toBe('No verify command is defined. Then use Retry integration.');
  });

  it('keeps the recorded integration failure after a category prefix', () => {
    const conflict = parkCauseView('acceptance-integration-failed', 'merge-conflict');
    expect(parkReasonSentence('merge-conflict: Merge conflict in src/app.ts', conflict))
      .toBe('Merge conflict in src/app.ts.');
    expect(parkReasonSentence('integration-failed: verify exited 1 in backend.Tests',
      parkCauseView('acceptance-integration-failed', 'integration-failed')))
      .toBe('Verify exited 1 in backend.Tests.');
    expect(parkReasonSentence('The delivery could not be integrated: Merge conflict in src/app.ts.', conflict))
      .toBe('The delivery could not be integrated: Merge conflict in src/app.ts.');
  });

  it('replaces only the detail-free legacy audit instruction, and fills an unstated audit reason', () => {
    const failed = parkCauseView('acceptance-integration-failed', 'integration-failed');
    expect(parkReasonSentence(
      'integration-failed: Recover the delivery, resolve the integration failure and rerun the gate.', failed,
    )).toBe('The delivery could not be integrated.');
    expect(parkReasonSentence('missing-delivery', parkCauseView('acceptance-integration-failed', 'missing-delivery')))
      .toBe('No delivery branch was found for this card, so its work cannot be integrated.');
    expect(parkReasonSentence('', failed)).toBe('The delivery could not be integrated.');
  });

  it('replaces the legacy stale-verdict instruction of a nonfatal acceptance park with its fact', () => {
    const stale = parkCauseView('acceptance-integration-failed', 'delivery-verdict-stale');
    const fact = 'The review verdict no longer matches the current delivery, so it cannot be accepted.';
    expect(parkReasonSentence('delivery-verdict-stale: Integrate the current delivery and repeat review.', stale))
      .toBe(fact);
    expect(parkReasonSentence(fact, stale)).toBe(fact);
    expect(parkReasonSentence('', stale)).toBe(fact);
  });

  it('hides a stale-verdict park once the delivery is integrated (AGT-3621)', () => {
    const stalePark: ParkedBlockerStatus = {
      ...auditPark,
      lane: '5-human-review',
      reason: 'delivery-verdict-stale: Integrate the current delivery and repeat review.',
      causeDetail: 'delivery-verdict-stale',
    };
    const integrated = { status: 'integrated' } as NonNullable<TaskInfo['integration']>;
    const pending = { status: 'pending' } as NonNullable<TaskInfo['integration']>;
    expect(parkPreconditionMet({ parkedBlocker: stalePark, integration: integrated })).toBe(true);
    expect(buildParkedBlockerView({ parkedBlocker: stalePark, integration: integrated }, NOW)).toBeNull();
    expect(buildParkedBadge({ parkedBlocker: stalePark, integration: integrated }, NOW)).toBeNull();
    // Not integrated yet: the park still describes the card.
    expect(buildParkedBlockerView({ parkedBlocker: stalePark, integration: pending }, NOW)).not.toBeNull();
    // A fatal integration park is not a stale verdict and stays.
    expect(buildParkedBlockerView({ parkedBlocker: auditPark, integration: integrated }, NOW)).not.toBeNull();
  });

  it('states a reason once, as a sentence, never as a bare slug', () => {
    const unknown = parkCauseView(null, null);
    expect(parkReasonSentence('[agent-blocked] the agent stopped', unknown)).toBe('The agent stopped.');
    expect(parkReasonSentence('escalated: no-completion-signal', unknown)).toBe(UNSTATED_REASON);
    expect(parkReasonSentence('no-completion-signal', unknown)).toBe(UNSTATED_REASON);
    expect(parkReasonSentence('', unknown)).toBe(UNSTATED_REASON);
    expect(parkReasonSentence('Note: keep this prefix', unknown)).toBe('Note: keep this prefix.');
  });

  it('chooses the copy from the cause: audit, review loop budget, stall, review gate, run, operator', () => {
    expect(parkCauseView('acceptance-integration-failed', 'missing-delivery').label).toBe('Acceptance audit');
    expect(parkCauseView('escalated', 'card-round-budget')).toMatchObject({
      origin: 'review-loop-budget', label: 'Review loop budget',
    });
    expect(parkCauseView('escalated', 'reissue-budget-exhausted').origin).toBe('review-loop-budget');
    expect(parkCauseView('escalated', 'gate-capacity')).toMatchObject({
      origin: 'stall-intervention', label: 'Gate queue stall',
    });
    expect(parkCauseView('escalated', 'aspect-verdict-infra-crash').origin).toBe('review-gate');
    expect(parkCauseView('escalated', 'completion-gate').origin).toBe('review-gate');
    expect(parkCauseView('escalated', 'agent-blocked').origin).toBe('run');
    expect(parkCauseView('operator-decision', null).origin).toBe('operator');
    expect(parkCauseView('escalated', null)).toMatchObject({ origin: 'unknown', parkedBy: null });
    expect(parkCauseView(undefined, undefined).origin).toBe('unknown');
  });

  it('suggests only buttons the lane offers', () => {
    const audit = parkCauseView('acceptance-integration-failed', 'missing-delivery');
    expect(parkNextStepSentence(audit.nextSteps, escalatedButtons)).toBe(
      'Use Continue (reissue) to build and deliver the work again, or Abort if the work is no longer needed.',
    );
    expect(parkNextStepSentence(audit.nextSteps, humanReviewButtons)).toBe(
      'Use Send back to Ready (re-do) to build and deliver the work again.',
    );
    const budget = parkCauseView('escalated', 'card-round-budget');
    expect(parkNextStepSentence(budget.nextSteps, escalatedButtons)).toBe(
      'Use Continue (reissue) to allow another round, or Accept with reason to take the current delivery.',
    );
    expect(parkNextStepSentence(audit.nextSteps, [])).toBeNull();
    expect(parkNextStepSentence(parkCauseView('escalated', 'agent-blocked').nextSteps, escalatedButtons)).toBeNull();
  });

  it('takes the cause from the lane-change row when the marker predates it', () => {
    const legacy = { ...auditPark, cause: undefined, causeDetail: undefined };
    const events: ParkTimelineEvent[] = [
      { ts: '2026-08-09T12:20:00Z', kind: 'lane_changed', details: { to: '3-in-progress', cause: 'claimed' } },
      {
        ts: '2026-11-09T08:23:00Z', kind: 'lane_changed',
        details: { to: '5e-escalated', cause: 'acceptance-integration-failed', causeDetail: 'missing-delivery' },
      },
      { ts: '2026-11-09T09:00:00Z', kind: 'lane_changed', details: { to: '5e-escalated', cause: 'escalated' } },
    ];

    expect(parkCauseFromTimeline(legacy, events)).toEqual({
      cause: 'acceptance-integration-failed', causeDetail: 'missing-delivery',
    });
    expect(buildParkedBlockerView({ parkedBlocker: legacy }, NOW, events)!.blockerLabel).toBe('Acceptance audit');
    expect(buildParkedBlockerView({ parkedBlocker: legacy }, NOW)!.blockerLabel).toBe('Operator decision');
  });
});
