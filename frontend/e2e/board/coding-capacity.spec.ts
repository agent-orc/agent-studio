import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const shots = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('capacity header and Needs you explain blocked chains in both themes at three widths', async ({ page }) => {
  const reason = 'AGT-4011 is escalated and holds 2 cards; AGT-4015 is parked and holds 2 cards; resolve these chain heads.';
  const fact = { project: null, totalSlots: 12, usedSlots: 5, freeSlots: 7, ready: 19,
    claimableNow: 3, dependencyWidth: 3, maxConcurrentCards: 3, cards: [], heads: [], cause: reason };
  await page.route('**/api/operator/coding-capacity', route => route.fulfill({ json: { fleet: fact, projects: [], subscriptions: [] } }));
  await page.route('**/api/operator/attention', route => route.fulfill({ json: { count: 1, items: [{
    id: 'capacity.underused|fleet|2026-11-26', type: 'capacity.underused', project: null,
    cardKey: 'AGT-4011', cards: ['AGT-4011', 'AGT-4015'], reason, observedAt: '2026-11-26T12:00:00Z',
    suggestedOperations: ['capacity.read', 'card.inspect'], evidence: [],
  }] } }));
  await page.route('**/api/workbenches', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({ json: {
    generatedAt: '2026-11-26T12:00:00Z', items: [],
  } }));
  await page.route('**/api/operator/backups', route => route.fulfill({ json: {
    state: 'ok', stale: false, warnings: [], backups: [], offHost: {}, generatedAt: '2026-11-26T12:00:00Z',
  } }));
  await page.goto('/');
  await expect(page.getByTestId('board-capacity-line')).toHaveText('5 of 12 slots, 3 claimable, limited by 3 chains');
  await expect(page.getByTestId('studio-attention-count')).toHaveText('1');
  mkdirSync(shots, { recursive: true });
  for (const width of [1440, 1024, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.getByTestId('board-capacity-line')).toBeVisible();
      await page.getByTestId('studio-attention-trigger').click();
      const row = page.getByTestId('attention-group-capacity').getByTestId('attention-row');
      await row.scrollIntoViewIfNeeded();
      await expect(row).toBeVisible();
      await expect(row).toContainText('AGT-4011 is escalated and holds 2 cards');
      await expect(row).toContainText('AGT-4015 is parked and holds 2 cards');
      await expect(row.getByTestId('attention-row-action')).toHaveText('Open blocking card');
      const explanation = row.getByTestId('attention-row-reason');
      await expect(explanation).toHaveText(reason);
      expect(await explanation.evaluate(element => element.scrollHeight <= element.clientHeight + 1)).toBe(true);
      await page.screenshot({ path: join(shots, `coding-capacity-${width}-${theme}--mocked.png`), animations: 'disabled' });
      await page.getByTestId('studio-attention-list').getByRole('button', { name: 'Close' }).click();
      await page.getByTestId('board-capacity-line').hover();
      await expect(page.getByTestId('offline-banner')).toBeHidden();
      await page.screenshot({ path: join(shots, `coding-capacity-header-${width}-${theme}--mocked.png`), animations: 'disabled' });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    }
  }
});
