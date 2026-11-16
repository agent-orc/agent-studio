import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { TaskIntegrationStatus } from '../../git';
import { acceptanceRefusalFor, contentVerdictHold, serverAcceptanceRefusal } from './acceptance-refusal.model';

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

  it('holds Accept on a content verdict that is not pass, in the verdict summary (AGT-3874)', () => {
    const reviewed = (status: string, summary: string, extra: Partial<TaskInfo> = {}) => card({ status: 'integrated' }, {
      reviewProjection: { attempts: [{ aspects: [
        { aspect: 'code-quality', status: 'block', classification: 'product', summary: 'Other aspect.' },
        { aspect: 'requirement-fit', status, classification: 'product', summary },
      ] }] } as unknown as TaskInfo['reviewProjection'],
      ...extra,
    });
    const target = 'The 830-card first-paint target remains unmet and lacks idle-host confirmation';
    expect(acceptanceRefusalFor(reviewed('concerns', target), null, false)).toEqual({
      reason: target, action: 'override', actionLabel: 'Accept with override…', source: 'hold',
    });
    expect(acceptanceRefusalFor(reviewed('block', ''), null, false)?.reason)
      .toBe('The requirement-fit review returned block.');
    expect(acceptanceRefusalFor(reviewed('pass', 'Fine.'), null, false)).toBeNull();
    expect(acceptanceRefusalFor(reviewed('concerns', 'Gap.', { mode: 'concept' }), null, false)).toBeNull();
    expect(contentVerdictHold(card(null))).toBeNull();
  });

  it('reads the hint of a code-only server refusal', () => {
    const refusal = serverAcceptanceRefusal(card(null), { status: 409, error: {
      error: 'batch-gate-evidence-missing', code: 'batch-gate-evidence-missing', hint: 'Verify its published branch.' } });
    expect(refusal).toEqual({ taskKey: 'wp::AGT-1', code: 'batch-gate-evidence-missing',
      message: 'Verify its published branch.' });
  });
});
