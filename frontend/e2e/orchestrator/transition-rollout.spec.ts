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
        { profile: 'CodingCompletion', state: 'shadow', streak: 12, threshold: 50,
          mismatchCount: 0, lastMismatch: null },
        { profile: 'ReviewReport', state: 'ready', streak: 50, threshold: 50,
          mismatchCount: 1, lastMismatch: { monolith: {
            occurrence: 'review-one', inputsDigest: 'abc', action: 'FindingRound', reason: 'Fix finding.' },
          engine: { occurrence: 'review-one', inputsDigest: 'abc', action: 'Escalate', reason: 'Test block.' } } },
        { profile: 'GateResult', state: 'engine', streak: 50, threshold: 50,
          mismatchCount: 0, lastMismatch: null },
      ] });
    } else await route.continue();
  });
  await page.goto('/#/workspace/settings/orchestrator');
  const panel = page.getByTestId('transition-rollout');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('transition-profile-ReviewReport')).toContainText('ready');
  await expect(panel.getByTestId('transition-profile-ReviewReport')).toContainText('50 / 50');
  await expect(panel.getByRole('button', { name: 'Switch to engine' })).toBeDisabled();
  await panel.getByTestId('transition-rollout-reason').fill('Shadow receipts matched');
  await expect(panel.getByRole('button', { name: 'Switch to engine' })).toBeEnabled();
  await panel.screenshot({ path: path.join(shots, 'transition-rollout-light.png') });
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await panel.screenshot({ path: path.join(shots, 'transition-rollout-dark.png') });
});
