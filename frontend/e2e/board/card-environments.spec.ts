import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

test('card environments and Staging-only board filter show the same deployment state', async ({ page }) => {
  await page.clock.install();
  let productionLive = false;
  let stagingRolledBack = false;
  const selected = {
    id: 'demo-deploy-card', key: 'DEMO-1', title: 'Faster page loading', projectName: 'Demo', state: '2-ready',
    watchPath: '/fixtures/demo', folderPath: '/fixtures/demo/2-ready/demo-deploy-card',
    order: 1, agent: 'codex', cliType: 'codex', ownerClientId: 'local-default', tags: [],
    createdAt: '2026-10-16T09:00:00Z',
  };
  const incident = { ...selected, id: 'incident', key: 'DEMO-2', title: 'Investigate release failure', state: '0-backlog' };
  const coding = { ...selected, id: 'coding', key: 'DEMO-3', title: 'Work in progress', state: '3-progress' };
  const grouped = { backlog: [incident], preparation: [], orchestratorPrep: [], ready: [selected], progress: [coding],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [] };
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
  fs.mkdirSync(results, { recursive: true });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.addInitScript(() => localStorage.setItem('activeProjects', '[]'));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json(grouped)));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json(grouped)));
  await page.route('**/api/tasks', route => route.fulfill(json([selected, incident, coding])));
  await page.route('**/api/watch-paths', route => route.fulfill(json([
    { name: 'Demo', path: '/fixtures/demo', rootPath: '/fixtures/demo' },
  ])));
  await page.route('**/api/deploy/cards', route => route.fulfill({ status: 200, contentType: 'application/json',
    body: JSON.stringify([{ projectId: selected.projectName, cardKey: selected.key,
      environments: [
        { environmentId: 'stage', environmentName: 'Staging', kind: 'staging',
          state: stagingRolledBack ? 'failed-rolled-back' : 'succeeded',
          releaseId: 'release-1', releaseVersion: '2026.11.16.1', atUtc: '2026-11-16T10:00:00Z' },
        { environmentId: 'production', environmentName: 'Production', kind: 'production',
          state: productionLive ? 'succeeded' : 'not-deployed',
          releaseId: productionLive ? 'release-2' : null,
          releaseVersion: productionLive ? '2026.11.16.2' : null,
          atUtc: productionLive ? '2026-11-16T11:00:00Z' : null },
      ] }]),
  }));
  await page.goto('/');
  await page.getByTestId('studio-project-picker-trigger').click();
  await page.getByTestId('studio-project-picker-item-__all__').click();
  const board = page.getByTestId('studio-board');
  await expect(board).toBeVisible();
  const selectedCard = board.getByTestId('task-card').filter({ hasText: selected.title }).first();
  await expect(selectedCard.getByTestId('card-environment-staging')).toContainText('Staging · Live · 2026.11.16.1 · 2026-11-16');
  await expect(selectedCard.getByTestId('card-environment-production')).toContainText('Not yet in Production');
  const incidentCard = board.getByTestId('task-card').filter({ hasText: incident.title });
  const codingCard = board.getByTestId('task-card').filter({ hasText: coding.title });
  await expect(incidentCard).toBeVisible();
  await expect(codingCard).toBeVisible();
  await expect(incidentCard.getByTestId('card-environments')).toHaveCount(0);
  await expect(codingCard.getByTestId('card-environments')).toHaveCount(0);
  await expect(selectedCard.getByTestId('card-environment-staging').locator('.status-indicator__label'))
    .toHaveCSS('text-overflow', 'clip');
  expect(await selectedCard.getByTestId('card-environment-staging').locator('.status-indicator__label')
    .evaluate(label => label.scrollWidth <= label.clientWidth)).toBe(true);

  await page.getByTestId('studio-ab-filters').click();
  await page.getByTestId('board-environment-filter').selectOption('staging-only');
  await expect(page.getByTestId('studio-ab-badge-filters')).toHaveText('1');
  await expect(board.getByTestId('task-card')).toHaveCount(1);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await board.screenshot({ path: path.join(results, `agt-3900-card-environments-${theme}.png`) });
  }
  await page.getByTestId('board-environment-filter').selectOption('all');
  productionLive = true;
  await page.clock.fastForward(30_000);
  await expect(selectedCard.getByTestId('card-environment-production')).toContainText('Production · Live · 2026.11.16.2');
  stagingRolledBack = true;
  await page.clock.fastForward(30_000);
  const failedStage = selectedCard.getByTestId('card-environment-staging');
  await expect(failedStage).toContainText('Staging · Failed, rolled back · 2026.11.16.1 · 2026-11-16');
  await expect(failedStage).toHaveAttribute('data-tone', 'danger');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await board.screenshot({ path: path.join(results, `agt-3900-card-staging-failed-rolled-back-${theme}.png`) });
  }
});
