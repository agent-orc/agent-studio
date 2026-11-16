import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { TaskIntegrationStatus } from '../../git';
import {
  acceptanceRefusalFor,
  acceptanceRefusalTooltip,
  serverAcceptanceRefusal,
} from './acceptance-refusal.model';
import { readinessItem } from '../../acceptance-readiness/testing/acceptance-readiness.fixtures';

const card = (integration: Partial<TaskIntegrationStatus> | null, extra: Partial<TaskInfo> = {}): TaskInfo => ({
  id: 'AGT-1', taskKey: 'wp::AGT-1', state: '5-human-review', title: 'Concept',
  integration: integration ? { integrationBranch: 'develop', deliveryRef: null, sha: null, detail: null,
    ...integration } as TaskIntegrationStatus : null,
  ...extra,
}) as TaskInfo;

describe('acceptanceRefusalFor (AGT-3704)', () => {
  it('releases Accept for an integrated or not-applicable delivery', () => {
    expect(acceptanceRefusalFor(card({ status: 'not-applicable' }), null, false)).toBeNull();
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false)).toBeNull();
  });

  it('offers the owner override for a delivery not yet in develop', () => {
    expect(acceptanceRefusalFor(card({ status: 'pending' }), null, false)).toEqual({
      reason: 'The delivery is not in develop yet.',
      action: 'override', actionLabel: 'Accept with override…', source: 'hold',
    });
    expect(acceptanceRefusalFor(card(null), null, false)?.reason)
      .toBe('No integration verdict for this delivery yet.');
  });

  it('offers Retry integration for a failed integration, in the failure words', () => {
    const refusal = acceptanceRefusalFor(card({ status: 'conflict-skipped',
      failure: { code: 'merge-conflict', label: 'Conflict', reason: 'develop moved under the delivery.',
        rebaseRecoveryAvailable: true } as TaskIntegrationStatus['failure'] }), null, false);
    expect(refusal).toMatchObject({ action: 'retry-integration', reason: 'develop moved under the delivery.' });
  });

  it('shows a server refusal for its own card even when the client saw no hold', () => {
    const server = { taskKey: 'wp::AGT-1', code: 'integration-dead-end', message: 'Acceptance refused.' };
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false))
      .toMatchObject({ reason: 'Acceptance refused.', action: 'override', source: 'server' });
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), { ...server, taskKey: 'wp::AGT-2' }, false))
      .toBeNull();
  });

  it('adds nothing while the bounded Git wait shows its own progress, outside Human Review, or for an attempt gap', () => {
    expect(acceptanceRefusalFor(card({ status: 'pending' }), null, true)).toBeNull();
    expect(acceptanceRefusalFor(card({ status: 'pending' }, { state: '2-ready' }), null, false)).toBeNull();
    expect(acceptanceRefusalFor(card({ status: 'pending' }, { acceptanceAttemptGap: {
      authorityEpoch: 2, subjectRunAttemptId: 'run-1', resultSha: 'a'.repeat(40), reason: 'gap' } }), null, false))
      .toBeNull();
  });

  describe('ranked by the acceptance-readiness answer (F12)', () => {
    const noVerdict = readinessItem('AGT-1', 'no-content-verdict', {
      taskKey: 'wp::AGT-1',
      headline: 'No content verdict',
      reason: 'The current run has no requirement-fit verdict: Only build-tests ran.',
    });

    it('leads with the readiness reason and lists the integration hold under it', () => {
      expect(acceptanceRefusalFor(card({ status: 'no-branch' }), null, false, noVerdict)).toEqual({
        reason: 'The current run has no requirement-fit verdict: Only build-tests ran.',
        headline: 'No content verdict',
        nextStep: 'Run the requirement-fit review for the current run.',
        secondary: ['No delivery branch or commit was found for this card.'],
        action: 'override', actionLabel: 'Accept with override…', source: 'hold',
      });
    });

    it('keeps the integration action when integration is the gap to resolve', () => {
      const refusal = acceptanceRefusalFor(card({ status: 'conflict-skipped' }), null, false,
        readinessItem('AGT-1', 'not-integrated', { reason: 'Integration into develop failed.' }));
      expect(refusal).toMatchObject({ action: 'retry-integration', reason: 'Integration into develop failed.', secondary: [] });
    });

    it('shows a readiness refusal although the integration hold has none', () => {
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, noVerdict))
        .toMatchObject({ headline: 'No content verdict', secondary: [] });
    });

    it('falls back to the integration hold for a ready card, and a server refusal still leads', () => {
      expect(acceptanceRefusalFor(card({ status: 'pending' }), null, false, readinessItem('AGT-1', 'ready'))?.reason)
        .toBe('The delivery is not in develop yet.');
      const server = { taskKey: 'wp::AGT-1', code: 'completion-content-gap', message: 'Completion refused.' };
      expect(acceptanceRefusalFor(card({ status: 'pending' }), server, false, noVerdict))
        .toEqual({ reason: 'Completion refused.', action: 'override', actionLabel: 'Accept with override…', source: 'server' });
    });
  });

  it('reads the hint of a code-only server refusal', () => {
    const refusal = serverAcceptanceRefusal(card(null), { status: 409, error: {
      error: 'batch-gate-evidence-missing', code: 'batch-gate-evidence-missing', hint: 'Verify its published branch.' } });
    expect(refusal).toEqual({ taskKey: 'wp::AGT-1', code: 'batch-gate-evidence-missing',
      message: 'Verify its published branch.' });
  });
});

/**
 * AGT-3867, F10: an integrated card whose content review returned concerns
 * said only that the work was merged, then the click was refused with a toast
 * about a brief mismatch although both brief versions matched.
 */
describe('acceptance readiness before and after the click (AGT-3867)', () => {
  const concerns = { ...readinessItem('AGT-1', 'review-concerns'), taskKey: 'wp::AGT-1' };

  it('says before the click why Accept on an integrated card with concerns will be refused', () => {
    const refusal = acceptanceRefusalFor(card({ status: 'integrated' }), null, false, concerns);
    expect(refusal).toEqual({
      reason: 'The requirement-fit review returned concerns: The lane hides the reason.',
      action: 'override', actionLabel: 'Accept with override…', source: 'hold',
      headline: 'Review concerns', nextStep: 'Send a round to close the gap the review raised.', secondary: [],
    });
    expect(acceptanceRefusalTooltip(refusal!)).toBe('Accept will be refused. The requirement-fit review returned '
      + 'concerns: The lane hides the reason. Next step: Accept with override.');
  });

  it('adds no hold for a ready card, an admitted dry run, or a run-attempt gap', () => {
    const ready = { ...readinessItem('AGT-1', 'ready'), taskKey: 'wp::AGT-1' };
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, ready)).toBeNull();
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false,
      { ...concerns, readiness: 'owner-decision', stage: 'admitted' })).toBeNull();
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false,
      { ...concerns, readiness: 'blocked', stage: 'lineage' })).toBeNull();
  });

  it('offers the round instead of an override the policy cannot admit', () => {
    const report = { ...concerns, readiness: 'blocked' as const,
      refusal: 'Completion refused: the required report is missing (results/report.md).' };
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, report))
      .toMatchObject({ action: 'send-round', actionLabel: 'Send a round…' });
  });

  it('leads with the round over a pending integration and lists the integration under it (F12)', () => {
    const report = { ...concerns, readiness: 'blocked' as const,
      refusal: 'Completion refused: the required report is missing (results/report.md).' };
    expect(acceptanceRefusalFor(card({ status: 'conflict-skipped' }), null, false, report)).toMatchObject({
      reason: concerns.reason, action: 'send-round', secondary: ['Integration into develop failed.'],
    });
  });

  it('records a content refusal in the server words, never as a brief mismatch', () => {
    const server = serverAcceptanceRefusal(card({ status: 'integrated' }), { status: 409, error: {
      code: 'completion-content-gap', error: 'Completion refused: requirement-fit verdict was concerns.' } });
    expect(server).toEqual({ taskKey: 'wp::AGT-1', code: 'completion-content-gap',
      message: 'Acceptance refused: requirement-fit verdict was concerns.' });
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false, concerns))
      .toMatchObject({ reason: 'Acceptance refused: requirement-fit verdict was concerns.', source: 'server', action: 'override' });
  });
});
