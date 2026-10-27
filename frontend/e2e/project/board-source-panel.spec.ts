import { expect, test } from '../fixtures/dev-backend';
import { setTheme, type Theme } from '../helpers/theme';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const resultsDir = process.env.JOB_RESULTS_DIR ?? 'test-results';

/**
 * Board source metrics card (pipeline delivery target M1 and M2): the real
 * backend answers the default workspace source, and a shadow state with one
 * recorded difference renders the card fields, the cutover window and the
 * inline explanation in both themes.
 */
test('board source card shows the source, the shadow differences and records an explanation', async ({ page }) => {
  test.setTimeout(120_000);
  mkdirSync(resultsDir, { recursive: true });
  await page.setViewportSize({ width: 1600, height: 1100 });

  await page.goto('/#/workspace/settings/prompts', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  const card = page.getByTestId('board-source-panel');
  await expect(card).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('board-source-mode')).toHaveText('workspace');
  await expect(page.getByTestId('board-source-readiness')).toContainText('Shadow off');

  const difference = {
    fingerprint: 'Agent Studio|AGT-3317|title', project: 'Agent Studio', taskKey: 'AGT-3317', field: 'title',
    workspaceValue: 'Board read model served by the Task Server', taskServerValue: 'Board read model',
    firstSeenAt: '2026-10-26T08:00:00Z', lastSeenAt: '2026-10-27T09:00:00Z', occurrences: 4, present: true,
    explainedAt: null, explanation: null, explainedBy: null,
  };
  const shadow = {
    source: 'task-server-shadow', rejectedSetting: null, mirroring: true,
    sync: { syncedGeneration: 412, syncedAt: '2026-10-27T09:00:00Z', lastError: null, lastErrorAt: null, missingProjects: [], rejectedCards: 0 },
    comparisonRequests: 1840, comparisons: 1802, skippedComparisons: 3,
    lastComparisonAt: '2026-10-27T09:00:00Z', lastComparedGeneration: 412,
    unexplainedDifferences: 1, explainedDifferences: 0, differences: [difference],
    readiness: { state: 'clean-window-open', ready: false, cleanSince: '2026-10-24T09:00:00Z', cleanDays: 3, requiredDays: 7 },
    boardRead: { sampleCount: 1840, p95Ms: 86, spawnsPerRequest: 0, targetP95Ms: 200, targetSpawnsPerRequest: 0, met: true },
  };
  await page.route('**/api/admin/board-source', route => route.fulfill({ json: shadow }));
  await page.route('**/api/admin/board-source/differences/explain', route => route.fulfill({
    json: {
      ...shadow, unexplainedDifferences: 0, explainedDifferences: 1,
      differences: [{ ...difference, explainedAt: '2026-10-27T09:05:00Z', explanation: 'Title shortened on the Task Server by hand', explainedBy: 'local-default' }],
    },
  }));

  await page.getByTestId('board-source-refresh').click();
  await expect(page.getByTestId('board-source-mode')).toHaveText('task-server-shadow');
  await expect(page.getByTestId('board-source-comparisons')).toHaveText('1,802 of 1,840 reads, 3 skipped');
  await expect(page.getByTestId('board-source-board-read')).toContainText('p95 86 ms (target under 200 ms)');
  await expect(page.getByTestId('board-source-readiness')).toContainText('3.0 of 7 days clean');
  await expect(card.locator('[data-fingerprint="Agent Studio|AGT-3317|title"]')).toContainText('Board read model');
  await card.scrollIntoViewIfNeeded();

  for (const theme of ['dark', 'light'] as Theme[]) {
    await setTheme(page, theme);
    await card.screenshot({ path: join(resultsDir, `board-source-card-${theme}.png`) });
  }

  await page.getByTestId('board-source-explain').click();
  await page.getByTestId('board-source-explanation').fill('Title shortened on the Task Server by hand');
  const explained = page.waitForRequest('**/api/admin/board-source/differences/explain');
  await page.getByTestId('board-source-explain-save').click();
  expect((await explained).postDataJSON()).toEqual({
    fingerprint: 'Agent Studio|AGT-3317|title', explanation: 'Title shortened on the Task Server by hand',
  });
  await expect(page.getByTestId('board-source-differences-count')).toHaveText('0 unexplained, 1 explained');
  await expect(page.getByTestId('board-source-differences')).toContainText('explained by local-default');
  await setTheme(page, 'dark');
  await card.screenshot({ path: join(resultsDir, 'board-source-card-explained-dark.png') });
});
