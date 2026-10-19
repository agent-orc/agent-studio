import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const shots = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots') : 'test-results';
mkdirSync(shots, { recursive: true });

test('transition profiles show comparison state and require an operator reason', async ({ page, devBackend }) => {
  expect(devBackend.port).toBeGreaterThan(0);
  await page.route('**/api/orchestration/rollout/profiles', async route => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ json: [
        { profile: 'CodingCompletion', state: 'canary', streak: 50, threshold: 50,
          mismatchCount: 0, lastMismatch: null,
          applierContract: { status: 'covered', actions: ['CreateReview', 'ReturnToReady', 'SalvageContinuation',
            'Escalate', 'HumanReview', 'Continue'], uncovered: [] },
          canary: { status: 'watching', required: 3, verified: 1, instanceId: 'studio/abc123',
            passedOnThisInstance: false },
          lastEffectCheck: { occurrence: 'orch_coding', action: 'CreateReview', verified: true, missing: [],
            evidence: 'AGT-4242 is in 4-auto-review.', checkedAt: '2026-10-19T08:00:00Z' } },
        { profile: 'ReviewReport', state: 'ready', streak: 50, threshold: 50,
          mismatchCount: 1, lastMismatch: { monolith: {
            occurrence: 'review-one', inputsDigest: 'abc', action: 'FindingRound', reason: 'Fix finding.' },
          engine: { occurrence: 'review-one', inputsDigest: 'abc', action: 'Escalate', reason: 'Test block.' } },
          applierContract: { status: 'covered', actions: ['FindingRound', 'DeferToBatch'], uncovered: [] },
          canary: { status: 'failed', required: 3, verified: 0, instanceId: 'studio/abc123',
            passedOnThisInstance: false },
          lastEffectCheck: { occurrence: 'orch_defer', action: 'DeferToBatch', verified: false,
            missing: ['BatchAdmission'], evidence: 'DOC-1 is in 4-auto-review; missing BatchAdmission.',
            checkedAt: '2026-10-16T09:12:00Z' } },
        { profile: 'GateResult', state: 'engine', streak: 50, threshold: 50,
          mismatchCount: 0, lastMismatch: null,
          applierContract: { status: 'covered', actions: ['Continue', 'ReturnToReady', 'Escalate'], uncovered: [] },
          canary: { status: 'passed', required: 3, verified: 3, instanceId: 'studio/abc123',
            passedOnThisInstance: true },
          lastEffectCheck: null },
      ] });
    } else await route.continue();
  });
  const receipt = 'odr_0123456789abcdef0123456789abcdef';
  await page.route(/\/api\/orchestration\/decision-receipts\?limit=5$/, route => route.fulfill({ json: [
    { receiptId: receipt, occurrenceId: 'orch_sample', taskId: 'AGT-4242', profile: 'CodingCompletion',
      mode: 'shadow', chosenAction: 'prepare(review-subject)', deterministicAction: 'prepare(review-subject)',
      matchesDeterministic: true, callStatus: 'completed', model: 'gpt-6-sol', thinkingLevel: 'medium',
      custody: 'runner-host-job', totalTokens: 6412, priceStatus: 'priced', estimatedUsd: 0.0412,
      evidenceRef: `/api/orchestration/decision-receipts/${receipt}`, endedAt: '2026-10-15T08:00:00Z' },
    { receiptId: 'odr_fedcba9876543210fedcba9876543210', occurrenceId: 'orch_other', taskId: 'AGT-4243',
      profile: 'CodingCompletion', mode: 'shadow', chosenAction: 'ask(decision-unavailable)',
      deterministicAction: 'prepare(review-subject)', matchesDeterministic: false, callStatus: 'unavailable',
      model: 'gpt-6-sol', thinkingLevel: 'medium', custody: 'runner-host-job', totalTokens: 0,
      priceStatus: 'no-usage', estimatedUsd: null,
      evidenceRef: '/api/orchestration/decision-receipts/odr_fedcba9876543210fedcba9876543210',
      endedAt: '2026-10-15T08:05:00Z' },
  ] }));
  await page.context().route(`**/api/orchestration/decision-receipts/${receipt}`, route => route.fulfill({ json: {
    receiptId: receipt, occurrenceId: 'orch_sample', groundsDigest: 'sha256:abc', grounds: { facts: [] },
  } }));
  await page.goto('/#/workspace/settings/orchestrator');
  const panel = page.getByTestId('transition-rollout');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('decision-workspace-spend')).toContainText('no usage data');
  await expect(panel.getByTestId('transition-profile-ReviewReport')).toContainText('ready');
  await expect(panel.getByTestId('transition-profile-ReviewReport')).toContainText('50 / 50');
  // AGT-3218: Ready proves no applied effect, so the engine waits for an applier canary.
  const review = panel.getByTestId('transition-profile-ReviewReport');
  await expect(review.getByRole('button', { name: 'Switch to engine' })).toHaveCount(0);
  await expect(review.getByRole('button', { name: 'Start applier canary' })).toBeDisabled();
  await expect(panel.getByTestId('applier-canary-ReviewReport')).toContainText('failed · 0 / 3');
  await expect(panel.getByTestId('effect-check-ReviewReport')).toContainText('missing BatchAdmission');
  await expect(panel.getByTestId('applier-contract-CodingCompletion')).toContainText('covered · 6 actions');
  await expect(panel.getByTestId('applier-canary-CodingCompletion')).toContainText('watching · 1 / 3');
  await expect(panel.getByTestId('effect-check-CodingCompletion')).toContainText('CreateReview · applied');
  await expect(panel.getByTestId('applier-canary-GateResult')).toContainText('passed on this instance');
  await panel.getByTestId('transition-rollout-reason').fill('Shadow receipts matched');
  await expect(review.getByRole('button', { name: 'Start applier canary' })).toBeEnabled();
  const decision = panel.getByTestId(`shadow-decision-${receipt}`);
  await expect(decision).toContainText('AGT-4242');
  await expect(decision).toContainText('match');
  await expect(decision).toContainText('$0.0412');
  await expect(panel.getByTestId('shadow-decision-odr_fedcba9876543210fedcba9876543210'))
    .toContainText('no usage data');
  const evidence = decision.getByRole('link', { name: 'Evidence' });
  await expect(evidence).toHaveAttribute('href', `/api/orchestration/decision-receipts/${receipt}`);
  const [opened] = await Promise.all([page.waitForEvent('popup'), evidence.click()]);
  await expect(opened.locator('body')).toContainText('sha256:abc');
  await opened.close();
  await panel.screenshot({ path: path.join(shots, 'transition-rollout-light.png') });
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await panel.screenshot({ path: path.join(shots, 'transition-rollout-dark.png') });
});
