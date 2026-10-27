import { expect, test, type Locator, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3276 D5 L-B and D10 A: the board's Delivery group follows "Done &
 * Decide", one column per environment from the `deploy/lanes` read model,
 * landed cards first, then a Waiting divider. The 6-completed lane reads
 * "Accepted". Fixture releases cover landed, waiting, approval pending and
 * rolled back; the deploy poll moves a card from waiting to landed.
 */
const PROJECT = 'Demo';
const card = (key: string, title: string) => ({
  id: key.toLowerCase(), key, displayKey: key, taskKey: `demo::${key.toLowerCase()}`, title, projectName: PROJECT,
  state: '6-completed', watchPath: '/fixtures/demo', folderPath: `/fixtures/demo/6-completed/${key.toLowerCase()}`,
  order: 1, agent: 'codex', cliType: 'codex', ownerClientId: 'local-default', tags: [],
  createdAt: '2026-10-20T09:00:00Z', lastActivity: '2026-10-21T09:00:00Z',
});
const cards = [
  card('AGT-3301', 'Release notes per card'), card('AGT-3304', 'Faster board load'),
  card('AGT-3288', 'Deploy approvals in the header'), card('AGT-3290', 'Retry deploy health probe'),
  card('AGT-3310', 'Post-merge checks on develop'),
];
const entry = (key: string, releaseVersion: string | null, rolledBack = false) =>
  ({ key, releaseId: releaseVersion && `release-${releaseVersion}`, releaseVersion, rolledBack });
const release = (version: string, createdAtUtc: string) =>
  ({ id: `release-${version}`, version, channel: 'default', createdAtUtc });
const run = (version: string, state: string, trigger: string, atUtc: string) =>
  ({ id: `run-${version}-${state}`, releaseId: `release-${version}`, state, trigger, atUtc });
const column = (id: string, name: string, order: number, parts: Record<string, unknown>) => ({
  id, environmentId: id === 'integrated' ? null : id, name, kind: id === 'dev' ? 'other' : id, order,
  currentRelease: null, lastRun: null, health: null, pendingApproval: null,
  landed: [], waiting: [], rolledBack: [], ...parts,
});

function lanes(productionLive: boolean) {
  return {
    projectId: PROJECT, releaseDepth: 5, columns: [
      column('integrated', 'Integrated', -1, { landed: [entry('AGT-3310', null)] }),
      column('dev', 'Dev', 0, {
        currentRelease: release('2026.10.21.1', '2026-10-21T08:30:00Z'),
        lastRun: run('2026.10.21.1', 'succeeded', 'deploy', '2026-10-21T09:12:00Z'), health: 'healthy',
        landed: [entry('AGT-3301', '2026.10.21.1'), entry('AGT-3304', '2026.10.21.1'), entry('AGT-3288', '2026.10.19.1')],
      }),
      column('staging', 'Stage', 1, {
        currentRelease: release('2026.10.19.1', '2026-10-19T08:30:00Z'),
        lastRun: run('2026.10.20.3', 'succeeded', 'rollback', '2026-10-20T16:40:00Z'), health: 'healthy',
        landed: [entry('AGT-3288', '2026.10.19.1')],
        waiting: [entry('AGT-3301', '2026.10.21.1'), entry('AGT-3304', '2026.10.21.1'), entry('AGT-3290', '2026.10.20.3', true)],
        rolledBack: [entry('AGT-3290', '2026.10.20.3', true)],
      }),
      column('production', 'Production', 2, productionLive ? {
        currentRelease: release('2026.10.19.1', '2026-10-19T08:30:00Z'),
        lastRun: run('2026.10.19.1', 'succeeded', 'deploy', '2026-10-21T10:05:00Z'), health: 'healthy',
        landed: [entry('AGT-3288', '2026.10.19.1')],
      } : {
        lastRun: run('2026.10.19.1', 'awaiting-approval', 'deploy', '2026-10-21T10:00:00Z'), health: 'unknown',
        pendingApproval: { deploymentId: 'run-approval', releaseId: 'release-2026.10.19.1',
          releaseVersion: '2026.10.19.1', requestedAtUtc: '2026-10-21T10:00:00Z' },
        waiting: [entry('AGT-3288', '2026.10.19.1')],
      }),
    ],
  };
}

async function tileKeys(scope: Locator, part: 'landed' | 'waiting'): Promise<(string | null)[]> {
  return scope.getByTestId(`delivery-${part}-card`).evaluateAll((els) => els.map((el) => el.getAttribute('data-card-key')));
}

async function noLeftAccent(page: Page): Promise<void> {
  // The host's left hairline is the studio board's neutral divider between
  // super-columns, shared by every group; only the section's own chrome is probed.
  const offenders = await page.locator('[data-testid="lane-group-delivery"] [class*="delivery"]')
    .evaluateAll((els) => els.filter((el) => {
      const style = getComputedStyle(el);
      const left = parseFloat(style.borderLeftWidth);
      const accentBorder = left > 0 && (left !== parseFloat(style.borderRightWidth)
        || style.borderLeftColor !== style.borderRightColor);
      const insetBar = style.boxShadow.includes('inset') && /\s[1-9]\d*px 0px 0px/.test(style.boxShadow);
      return accentBorder || insetBar;
    }).map((el) => el.getAttribute('class') ?? el.tagName));
  expect(offenders, 'no coloured left accent bar inside the Delivery group').toEqual([]);
}

test('Delivery group places accepted cards by environment in both themes', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-21T12:00:00Z') });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  let productionLive = false;
  const grouped = { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], humanReview: [], escalated: [], review: [], completed: cards, archive: [] };
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
  fs.mkdirSync(results, { recursive: true });
  await page.addInitScript(() => {
    localStorage.setItem('activeProjects', '[]');
    localStorage.removeItem('deliveryFurthestOnly');
  });
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json(grouped)));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json(grouped)));
  await page.route('**/api/tasks', route => route.fulfill(json(cards)));
  await page.route('**/api/deploy/cards', route => route.fulfill(json([])));
  await page.route('**/api/watch-paths', route => route.fulfill(json([
    { name: PROJECT, path: '/fixtures/demo', rootPath: '/fixtures/demo' },
  ])));
  await page.route('**/api/projects/*/deploy/lanes**', route => route.fulfill(json(lanes(productionLive))));
  await page.setViewportSize({ width: 2000, height: 1900 });
  await page.goto('/');
  await expect(page.getByTestId('studio-project-picker-trigger')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('studio-project-picker-trigger').click();
  await page.getByTestId('studio-project-picker-item-__all__').click();

  const board = page.getByTestId('studio-board');
  const delivery = board.getByTestId('lane-group-delivery');
  await expect(delivery).toBeVisible();
  // D5 L-B: the Delivery group directly follows Done & Decide.
  const groupOrder = await board.locator('[data-testid^="lane-group-"]:not([data-testid^="lane-group-lanes"])')
    .evaluateAll((els) => els.map((el) => el.getAttribute('data-testid')));
  expect(groupOrder.slice(-2)).toEqual(['lane-group-decide', 'lane-group-delivery']);
  // D10 A: the lane keeps its key and reads Accepted.
  await expect(board.getByTestId('lane-6-completed')).toContainText('Accepted');

  const columnTitles = await delivery.locator('.delivery-column__title').allTextContents();
  expect(columnTitles.map((t) => t.trim())).toEqual(['Integrated', 'Delivery to Dev', 'Delivery to Stage', 'Delivery to Production']);

  const integrated = delivery.getByTestId('delivery-column-integrated');
  const dev = delivery.getByTestId('delivery-column-dev');
  const stage = delivery.getByTestId('delivery-column-staging');
  const production = delivery.getByTestId('delivery-column-production');
  expect(await tileKeys(integrated, 'landed')).toEqual(['AGT-3310']);
  // Landed: the cards of the current release and earlier ones, as the standard task card.
  expect(await tileKeys(dev, 'landed')).toEqual(['AGT-3301', 'AGT-3304', 'AGT-3288']);
  await expect(dev.getByTestId('task-card')).toHaveCount(3);
  await expect(dev.getByTestId('delivery-release-version')).toHaveText('2026.10.21.1');
  await expect(dev.getByTestId('delivery-run-state')).toHaveText('Succeeded');
  await expect(dev.getByTestId('delivery-health')).toContainText('Healthy');
  await expect(dev.getByTestId('delivery-waiting-divider')).toContainText('Waiting · 0');
  // Waiting below the divider; a rolled-back card waits again with its flag.
  expect(await tileKeys(stage, 'landed')).toEqual(['AGT-3288']);
  expect(await tileKeys(stage, 'waiting')).toEqual(['AGT-3301', 'AGT-3304', 'AGT-3290']);
  await expect(stage.getByTestId('delivery-run-state')).toHaveText('Rolled back');
  await expect(stage.getByTestId('delivery-rolled-back')).toHaveCount(1);
  await expect(stage.getByTestId('delivery-rolled-back')).toContainText('Rolled back · 2026.10.20.3');
  const stageOrder = await stage.locator('[data-testid="delivery-landed-card"], [data-testid="delivery-waiting-divider"], [data-testid="delivery-waiting-card"]')
    .evaluateAll((els) => els.map((el) => el.getAttribute('data-testid')));
  expect(stageOrder.indexOf('delivery-waiting-divider')).toBe(1);
  // Approval pending: pill plus a tinted column, not a side stripe.
  await expect(production.getByTestId('delivery-approval-pill')).toContainText('Approval pending · 2026.10.19.1');
  await expect(production).toHaveAttribute('data-delivery-tone', 'attention');
  expect(await tileKeys(production, 'waiting')).toEqual(['AGT-3288']);

  // R3: the group count equals the sum of the visible tiles and of the column counts.
  const tiles = await delivery.locator('[data-testid="delivery-landed-card"], [data-testid="delivery-waiting-card"]').count();
  const columnCounts = (await delivery.locator('[data-testid^="delivery-column-count-"]').allTextContents())
    .reduce((sum, n) => sum + Number(n), 0);
  await expect(delivery.getByTestId('delivery-group-count')).toHaveText(String(tiles));
  expect(columnCounts).toBe(tiles);

  // Reduced motion: the column toggle has no transition.
  const toggleTransition = await delivery.getByTestId('delivery-column-toggle-dev')
    .evaluate((el) => getComputedStyle(el).transitionDuration);
  expect(toggleTransition.split(',').every((d) => parseFloat(d) === 0)).toBe(true);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await noLeftAccent(page);
    await delivery.screenshot({ path: path.join(results, `agt-3316-delivery-section-${theme}.png`) });
    await page.screenshot({ path: path.join(results, `agt-3316-board-with-delivery-${theme}.png`) });
    await stage.screenshot({ path: path.join(results, `agt-3316-delivery-stage-rolled-back-${theme}.png`) });
    await production.screenshot({ path: path.join(results, `agt-3316-delivery-production-approval-${theme}.png`) });
  }

  // Display option: each card once, in its furthest environment.
  await delivery.getByTestId('delivery-furthest-only').check();
  expect(await tileKeys(dev, 'landed')).toEqual(['AGT-3301', 'AGT-3304']);
  expect(await tileKeys(stage, 'landed')).toEqual(['AGT-3288']);
  expect(await tileKeys(stage, 'waiting')).toEqual(['AGT-3290']);
  expect(await tileKeys(production, 'waiting')).toEqual([]);
  await delivery.screenshot({ path: path.join(results, 'agt-3316-delivery-section-furthest-only-dark.png') });
  await delivery.getByTestId('delivery-furthest-only').uncheck();

  // Collapsible like the board lanes.
  await delivery.getByTestId('delivery-column-toggle-integrated').click();
  await expect(delivery.getByTestId('delivery-column-toggle-integrated')).toHaveAttribute('aria-expanded', 'false');
  await expect(integrated.getByTestId('delivery-landed-card')).toHaveCount(0);
  await delivery.getByTestId('delivery-column-toggle-integrated').click();

  // Live update on the deploy poll: approval granted, the card lands in Production.
  productionLive = true;
  await page.clock.fastForward(30_000);
  await expect(production.getByTestId('delivery-approval-pill')).toHaveCount(0);
  await expect.poll(() => tileKeys(production, 'landed')).toEqual(['AGT-3288']);
  await expect(production.getByTestId('delivery-run-state')).toHaveText('Succeeded');
  await setTheme(page, 'light');
  await delivery.screenshot({ path: path.join(results, 'agt-3316-delivery-section-after-approval-light.png') });
});
