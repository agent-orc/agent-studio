// Run: node --test tools/studio-op/test/accept-policy.test.mjs
//
// OA-D5 (Dossier operator-agent-level): the operator agent accepts a card only
// with green evidence and no open decision. Red, missing or disputed evidence
// and decision cards stay with the owner.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { evaluateAcceptance } from '../src/accept-policy.mjs';
import { card, remoteAttempt, reviewProjection } from './fake-studio.mjs';

const codes = result => result.reasons.map(reason => reason.code);

test('green remote review, passed build-tests and no decision is acceptable', () => {
  const result = evaluateAcceptance(card('DEMO-1'), reviewProjection());
  assert.equal(result.acceptable, true);
  assert.deepEqual(result.reasons, []);
  assert.equal(result.evidence.attemptId, 'review-1');
});

test('the Human Review park itself is the acceptance decision, not an open one', () => {
  const laneChange = reviewProjection({ decisionRequired: { required: true, source: 'lane-change',
    reason: 'Lane changed from 4-auto-review to 5-human-review.' } });
  assert.equal(evaluateAcceptance(card('DEMO-1'), laneChange).acceptable, true);
  // Every card entering Human Review is parked as an operator decision.
  const parked = card('DEMO-1', { parkedBlocker: { blockerType: 'operator-decision', reason: 'Parked with no recorded cause.' } });
  const marker = reviewProjection({ decisionRequired: { required: true, source: 'parked-blocker', reason: 'Parked.' } });
  assert.equal(evaluateAcceptance(parked, marker).acceptable, true);
});

test('a local pass verdict without proven build-tests is missing evidence', () => {
  const attempt = remoteAttempt({ plane: 'Local', outcome: 'pass', buildTestsResult: 'not-proven' });
  assert.deepEqual(codes(evaluateAcceptance(card('DEMO-1'), reviewProjection({ attempts: [attempt] }))),
    ['evidence-missing']);
});

const matrix = [
  ['no review attempt', card('DEMO-1'), reviewProjection({ attempts: [] }), ['evidence-missing']],
  ['no projection at all', card('DEMO-1'), null, ['evidence-missing']],
  ['latest outcome is a product failure', card('DEMO-1'),
    reviewProjection({ attempts: [remoteAttempt({ outcome: 'ProductFailure' })] }), ['evidence-red']],
  ['local concerns verdict is disputed', card('DEMO-1'),
    reviewProjection({ attempts: [remoteAttempt({ plane: 'Local', outcome: 'concerns', buildTestsResult: 'passed' })] }),
    ['evidence-disputed']],
  ['build-tests failed', card('DEMO-1'),
    reviewProjection({ attempts: [remoteAttempt({ buildTestsResult: 'failed' })] }), ['evidence-red']],
  ['build-tests not proven', card('DEMO-1'),
    reviewProjection({ attempts: [remoteAttempt({ buildTestsResult: 'not-proven' })] }), ['evidence-missing']],
  ['a blocking aspect', card('DEMO-1'),
    reviewProjection({ blockingAspects: [{ aspect: 'code-quality', reason: 'Blocked.' }] }), ['evidence-red']],
  ['the delivery gate failed', card('DEMO-1'),
    reviewProjection({ delivery: { status: 'gate-failed', reason: 'Gate red.' } }), ['evidence-red']],
  ['the card was escalated', card('DEMO-1'),
    reviewProjection({ decisionRequired: { required: true, source: 'escalation-event', reason: 'Escalated.' } }),
    ['decision-open']],
  ['a human decision park', card('DEMO-1', { parkedBlocker: { blockerType: 'human-decision-needed', reason: 'Pick one.' } }),
    reviewProjection(), ['decision-open']],
  ['an unanswered steer park', card('DEMO-1', { parkedBlocker: { blockerType: 'steer-unanswered', reason: 'Waiting.' } }),
    reviewProjection(), ['decision-open']],
  ['the agent needs input', card('DEMO-1', { needsInput: { firstLine: 'Which column?' } }),
    reviewProjection(), ['decision-open']],
  ['an escalated orchestrator verdict', card('DEMO-1', { orchestratorVerdict: 'escalate' }),
    reviewProjection(), ['decision-open']],
  ['an unresolved orchestrator verdict', card('DEMO-1', { orchestratorVerdict: 'pending' }),
    reviewProjection(), ['decision-open']],
  ['a reissue verdict', card('DEMO-1', { orchestratorVerdict: 'reissue' }), reviewProjection(), ['evidence-red']],
  ['a pending steer', card('DEMO-1', { steerPendingSince: '2026-11-05T09:00:00Z' }), reviewProjection(),
    ['decision-open']],
  ['a decision card', card('DEMO-1', { kind: 'decision' }), reviewProjection(), ['decision-card']],
  ['a card outside Human Review', card('DEMO-1', { state: '4-auto-review' }), reviewProjection(),
    ['not-in-human-review']],
];

for (const [name, subject, projection, expected] of matrix) {
  test(`refuses: ${name}`, () => {
    const result = evaluateAcceptance(subject, projection);
    assert.equal(result.acceptable, false);
    assert.deepEqual(codes(result), expected);
    for (const reason of result.reasons) assert.ok(reason.detail.length > 0, 'every refusal explains itself');
  });
}

test('every refusal is reported, not only the first', () => {
  const result = evaluateAcceptance(card('DEMO-1', { kind: 'decision', state: '3-progress' }),
    reviewProjection({ attempts: [] }));
  assert.deepEqual(codes(result), ['not-in-human-review', 'decision-card', 'evidence-missing']);
});
