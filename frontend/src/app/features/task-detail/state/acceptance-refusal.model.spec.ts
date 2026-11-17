import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { TaskIntegrationStatus } from '../../git';
import {
  AcceptanceRefusalStore,
  acceptanceRefusalConcern,
  acceptanceRefusalFor,
  acceptanceRefusalOverridable,
  acceptanceRefusalTooltip,
  contentVerdictHold,
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
      expect(acceptanceRefusalFor(card({ status: 'no-branch' }), null, false, noVerdict)).toMatchObject({
        reason: 'The current run has no requirement-fit verdict: Only build-tests ran.',
        headline: 'No content verdict',
        nextStep: 'Run the requirement-fit review for the current run.',
        secondary: ['No delivery branch or commit was found for this card.'],
        action: 'review-again', actionLabel: 'Review again', source: 'readiness', replacesAccept: true,
        secondaryAction: { action: 'override', actionLabel: 'Accept with a reason…' },
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
        .toMatchObject({ reason: noVerdict.reason, action: 'review-again', source: 'server' });
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false,
        readinessItem('AGT-1', 'ready'))).toMatchObject({
        reason: 'Completion refused.', action: 'review-again', source: 'server', replacesAccept: true,
      });
    });
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

  describe('content refusals (AGT-3830)', () => {
    const concerns = readinessItem('AGT-1', 'review-concerns', { taskKey: 'wp::AGT-1',
      reason: 'The requirement-fit review returned concerns: The result SHA is absent from the delivery brief.' });

    it('replaces Accept with Review again for review concerns, in the readiness words', () => {
      expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, concerns)).toMatchObject({
        reason: 'The requirement-fit review returned concerns: The result SHA is absent from the delivery brief.',
        action: 'review-again', actionLabel: 'Review again', source: 'readiness', replacesAccept: true,
        secondaryAction: { action: 'override', actionLabel: 'Accept with a reason…' },
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

/**
 * AGT-3867, F10: an integrated card whose content review returned concerns
 * said only that the work was merged, then the click was refused with a toast
 * about a brief mismatch although both brief versions matched.
 */
describe('acceptance readiness before and after the click (AGT-3867)', () => {
  const concerns = { ...readinessItem('AGT-1', 'review-concerns'), taskKey: 'wp::AGT-1' };

  it('says before the click why Accept on an integrated card with concerns will be refused', () => {
    const refusal = acceptanceRefusalFor(card({ status: 'integrated' }), null, false, concerns);
    // AGT-3830: a content refusal replaces Accept with its next step.
    expect(refusal).toMatchObject({
      reason: 'The requirement-fit review returned concerns: The lane hides the reason.',
      action: 'review-again', actionLabel: 'Review again', source: 'readiness', replacesAccept: true,
      secondaryAction: { action: 'override', actionLabel: 'Accept with a reason…' },
      headline: 'Review concerns', nextStep: 'Send a round to close the gap the review raised.', secondary: [],
    });
    expect(acceptanceRefusalTooltip(refusal!)).toBe('The requirement-fit review returned '
      + 'concerns: The lane hides the reason. Next step: Review again.');
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
      .toMatchObject({ reason: 'The requirement-fit review returned concerns: The lane hides the reason.',
        source: 'server', action: 'review-again', replacesAccept: true });
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false, null))
      .toMatchObject({ reason: 'Acceptance refused: requirement-fit verdict was concerns.', source: 'server', action: 'review-again' });
  });
});

describe('content refusal next steps after the merge of AGT-3867 (AGT-3830)', () => {
  const concerns = { ...readinessItem('AGT-1', 'review-concerns'), taskKey: 'wp::AGT-1' };

  it('offers the round beside Review again when an override cannot admit the card', () => {
    const report = { ...concerns, refusal: 'Completion refused: the required report is missing (results/report.md).' };
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), null, false, report)?.secondaryAction)
      .toEqual({ action: 'send-round', actionLabel: 'Send a round…' });
  });

  it('keeps a recorded server refusal ahead of a stale ready dry run', () => {
    const server = { taskKey: 'wp::AGT-1', code: 'completion-content-gap', message: 'Acceptance refused: concerns.' };
    const ready = { ...concerns, readiness: 'ready' as const };
    expect(acceptanceRefusalFor(card({ status: 'integrated' }), server, false, ready))
      .toMatchObject({ reason: 'Acceptance refused: concerns.', action: 'review-again', replacesAccept: true });
  });
});

describe('content refusal next steps (AGT-3873)', () => {
  const SUMMARY = 'The 830-card first-paint target remains unmet and lacks idle-host confirmation';
  const concerns = card({ status: 'integrated' }, { reviewProjection: { attempts: [{ aspects: [
    { aspect: 'requirement-fit', status: 'concerns', classification: 'product', summary: SUMMARY },
  ] }] } } as unknown as Partial<TaskInfo>);
  const refused = (error: string) => serverAcceptanceRefusal(concerns, { status: 409,
    error: { code: 'completion-content-gap', error } });

  it('names the concern of a content refusal from the dry run, else from the content verdict', () => {
    expect(acceptanceRefusalConcern(concerns)).toBe(SUMMARY);
    expect(acceptanceRefusalConcern(concerns, readinessItem('AGT-1', 'review-concerns', { concern: 'Dry run concern' })))
      .toBe('Dry run concern');
    expect(acceptanceRefusalConcern(card({ status: 'integrated' }))).toBeNull();
    const passed = card({ status: 'integrated' }, { reviewProjection: { attempts: [{ aspects: [
      { aspect: 'requirement-fit', status: 'pass', summary: 'Fine.' }] }] } } as unknown as Partial<TaskInfo>);
    expect(acceptanceRefusalConcern(passed)).toBeNull();
  });

  it('carries the concern on the server refusal the toolbar shows', () => {
    const refusal = acceptanceRefusalFor(concerns, refused('Completion refused: requirement-fit verdict was concerns.'), false);
    expect(refusal).toMatchObject({ source: 'server', action: 'review-again', concern: SUMMARY,
      secondaryAction: { action: 'override' } });
    const integration = acceptanceRefusalFor(concerns, { taskKey: concerns.taskKey, code: 'integration-dead-end',
      message: 'Not in develop.' }, false);
    expect(integration?.concern).toBeNull();
  });

  it('lets an override admit a content verdict, never a missing report or an integration refusal', () => {
    expect(acceptanceRefusalOverridable(refused('Completion refused: requirement-fit verdict was concerns.'))).toBe(true);
    expect(acceptanceRefusalOverridable(refused('Completion refused: the required report is missing (results/report.md).')))
      .toBe(false);
    expect(acceptanceRefusalOverridable({ code: 'integration-dead-end', message: 'Not in develop.' })).toBe(false);
  });

  it('keeps one pending override request per card until that card takes it', () => {
    const store = new AcceptanceRefusalStore();
    store.requestOverride('wp::AGT-1');
    expect(store.takeOverrideRequest('wp::AGT-2')).toBe(false);
    expect(store.overrideRequest()).toBe('wp::AGT-1');
    expect(store.takeOverrideRequest('wp::AGT-1')).toBe(true);
    expect(store.overrideRequest()).toBeNull();
    store.requestOverride('wp::AGT-1');
    store.clear('wp::AGT-1');
    expect(store.overrideRequest()).toBeNull();
  });
});
