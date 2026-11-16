import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { TaskIntegrationStatus } from '../../git';
import { acceptanceRefusalFor, serverAcceptanceRefusal } from './acceptance-refusal.model';
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

  it('reads the hint of a code-only server refusal', () => {
    const refusal = serverAcceptanceRefusal(card(null), { status: 409, error: {
      error: 'batch-gate-evidence-missing', code: 'batch-gate-evidence-missing', hint: 'Verify its published branch.' } });
    expect(refusal).toEqual({ taskKey: 'wp::AGT-1', code: 'batch-gate-evidence-missing',
      message: 'Verify its published branch.' });
  });

  describe('content refusals (AGT-3830)', () => {
    const concerns = readinessItem('AGT-1', 'review-concerns', { taskKey: 'wp::AGT-1',
      reason: 'The requirement-fit review returned concerns: The result SHA is absent from the delivery brief.' });

    it('replaces Accept with Review again for review concerns, in the readiness words', () => {
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, concerns)).toEqual({
        reason: 'The requirement-fit review returned concerns: The result SHA is absent from the delivery brief.',
        action: 'review-again', actionLabel: 'Review again', source: 'readiness', replacesAccept: true,
      });
    });

    it('replaces Accept for a missing content verdict and names what is missing', () => {
      const none = readinessItem('AGT-1', 'no-content-verdict', { taskKey: 'wp::AGT-1',
        reason: 'The current run has no requirement-fit verdict: no review aspect ran.', missing: 'a review of the current run' });
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, none)).toMatchObject({
        action: 'review-again', replacesAccept: true,
        reason: 'The current run has no requirement-fit verdict: no review aspect ran. Missing: a review of the current run',
      });
    });

    it('keeps plain Accept for a ready card and ignores another card\'s answer', () => {
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, readinessItem('AGT-1', 'ready',
        { taskKey: 'wp::AGT-1' }))).toBeNull();
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false,
        readinessItem('AGT-9', 'review-concerns', { taskKey: 'wp::AGT-9' }))).toBeNull();
    });

    it('turns a server content-gap refusal into the same next step', () => {
      const server = { taskKey: 'wp::AGT-1', code: 'completion-content-gap',
        message: 'Completion refused: requirement-fit verdict was concerns.' };
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false)).toMatchObject({
        action: 'review-again', source: 'server', replacesAccept: true,
        reason: 'Completion refused: requirement-fit verdict was concerns.',
      });
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false, concerns)?.reason)
        .toContain('The result SHA is absent');
    });
  });
});
