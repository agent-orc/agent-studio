import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3587 (walkthrough AGT-3561, finding F12): one truthful Ready order.
 *
 * The All-projects board showed a Ready order that matched neither the stored
 * order nor the pickup order, every Ready card offered a "Next" control, and
 * the lane tooltip and the lane help described different rules. This spec
 * pins what the operator sees now:
 *
 * - the lane says it is grouped by project when it mixes projects;
 * - only the head of each project's pickup queue (runner status
 *   `queuedJobIds[0]`) carries "Next";
 * - every other Ready card offers "Move to top", and after it the other
 *   cards keep their order.
 *
 * Backend-mocked: the pickup order itself is covered by
 * `LaneSortStrategyTests.PickupOrder_*` and the promote rewrite by
 * `TaskLaneChangeLedgerTests.PromoteToReadyTop_MovesOnlyThePromotedCard`.
 */
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');
const NOW = '2026-11-09T09:00:00Z';

interface Card { id: string; key: string; project: string; order: number; title: string }

const APP = 'Demo App';
const PLATFORM = 'Demo Platform';
const SEED: Card[] = [
  { id: 'demo-3', key: 'DEMO-3', project: APP, order: 10, title: 'Seed the onboarding checklist' },
  { id: 'demo-11', key: 'DEMO-11', project: APP, order: 20, title: 'Retry webhook deliveries' },
  { id: 'demo-55', key: 'DEMO-55', project: APP, order: 30, title: 'Export invoices as CSV' },
  { id: 'plat-2', key: 'PLAT-2', project: PLATFORM, order: 10, title: 'Rotate service tokens' },
  { id: 'plat-16', key: 'PLAT-16', project: PLATFORM, order: 20, title: 'Cache tenant lookups' },
];

function boardCard(card: Card): Record<string, unknown> {
  const watchPath = `/fixtures/${card.project.toLowerCase().replace(/\s+/g, '-')}`;
  return {
    id: card.id,
    key: card.key,
    displayKey: card.key,
    taskKey: `${watchPath}::${card.id}`,
    title: card.title,
    state: '2-ready',
    order: card.order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-11-01T09:00:00Z',
    lastActivity: '2026-11-01T09:00:00Z',
    enteredLaneAt: '2026-11-01T09:00:00Z',
    watchPath,
    projectName: card.project,
    folderPath: `${watchPath}/2-ready/${card.id}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
  };
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** Server-side lane order: projects alphabetically, each by its order. */
function laneOrder(cards: Card[]): Card[] {
  return [...cards].sort((a, b) => a.project.localeCompare(b.project) || a.order - b.order);
}

async function installRoutes(page: Page, cards: Card[]): Promise<void> {
  const queueHead = (project: string) => laneOrder(cards).find((card) => card.project === project)?.id;
  const runner = (project: string) => ({
    projectName: project,
    mode: 'auto-continuous',
    activeJobId: null,
    activeExecution: null,
    queuedJobIds: laneOrder(cards).filter((card) => card.project === project).map((card) => card.id),
  });
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/v1/studio/auth/status', (route) => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  // A registered project keeps the first-run checklist closed; the usage
  // footer needs its object shape rather than the catch-all array.
  await page.route(/\/api\/v1\/projects(\?|$)/, (route) => json(route, [APP, PLATFORM].map((id) => ({ id, archived: false }))));
  await page.route(/\/api\/usage\/cockpit(\?|$)/, (route) => json(route, { workspaceId: 'fixture', clis: [], cost: null }));
  await page.route('**/api/tasks/archive**', (route) => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, (route) => json(route, laneOrder(cards).map(boardCard)));
  await page.route('**/api/v1/studio/board**', (route) => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: laneOrder(cards).map(boardCard),
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', (route) => json(route, [APP, PLATFORM].map((name) => {
    const path = `/fixtures/${name.toLowerCase().replace(/\s+/g, '-')}`;
    return { name, path, rootPath: path, repositoryPath: path };
  })));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/projects/settings', (route) => json(route, Object.fromEntries(
    [APP, PLATFORM].map((name) => [name, { laneSortStrategies: { '2-ready': 'lane-entry' } }]),
  )));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, {
    projects: { [APP]: runner(APP), [PLATFORM]: runner(PLATFORM) },
  }));
  // Move to top: the promoted card goes first in its project; the others
  // keep their relative order (the backend rewrite this spec mirrors).
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/[^/]+\/move-to-top/, (route) => {
    const id = decodeURIComponent(route.request().url().split('/tasks/')[1].split('/')[0]);
    const target = cards.find((card) => card.id === id)!;
    const head = queueHead(target.project);
    const minOrder = Math.min(...cards.filter((card) => card.project === target.project).map((card) => card.order));
    if (head !== id) target.order = minOrder - 10;
    return json(route, { position: 1 });
  });
}

async function openBoard(page: Page, cards: Card[]): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 960 });
  await page.clock.setFixedTime(new Date(NOW));
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page, cards);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
}

async function readyKeys(page: Page): Promise<string[]> {
  return page.getByTestId('lane-2-ready').getByTestId('task-card-key')
    .evaluateAll((nodes) => nodes.map((node) => node.textContent?.trim() ?? ''));
}

test.describe('Ready shows one truthful order per project', () => {
  test('only each project queue head says Next, and the lane names the per-project order', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page, SEED.map((card) => ({ ...card })));

    const lane = page.getByTestId('lane-2-ready');
    await expect(lane.getByTestId('task-card')).toHaveCount(5, { timeout: 15_000 });
    await expect(lane.getByTestId('lane-per-project-note')).toHaveText('Grouped by project. Each project keeps its own order.');
    expect(await readyKeys(page)).toEqual(['DEMO-3', 'DEMO-11', 'DEMO-55', 'PLAT-2', 'PLAT-16']);

    const next = lane.getByTestId('task-card-pickup-next');
    await expect(next).toHaveCount(2);
    for (const key of ['DEMO-3', 'PLAT-2']) {
      const card = lane.getByTestId('task-card').filter({ hasText: key });
      await expect(card.getByTestId('task-card-pickup-next')).toHaveText('Next');
      await expect(card.getByTestId('task-card-pick-next')).toHaveCount(0);
    }
    for (const key of ['DEMO-11', 'DEMO-55', 'PLAT-16']) {
      const card = lane.getByTestId('task-card').filter({ hasText: key });
      await expect(card.getByTestId('task-card-pickup-next')).toHaveCount(0);
      await expect(card.getByRole('button', { name: 'Move to top' })).toBeAttached();
    }

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('lane-scroll-2-ready').evaluate((el) => { el.scrollTop = 0; });
      await page.screenshot({ path: join(RESULTS, `ready-order-next-marker--${theme}--1600.png`) });
    }
  });

  test('Move to top moves only that card and hands it the Next marker', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page, SEED.map((card) => ({ ...card })));

    const lane = page.getByTestId('lane-2-ready');
    const target = lane.getByTestId('task-card').filter({ hasText: 'DEMO-55' });
    await target.hover();
    await target.getByRole('button', { name: 'Move to top' }).click();

    await expect.poll(() => readyKeys(page), { timeout: 15_000 })
      .toEqual(['DEMO-55', 'DEMO-3', 'DEMO-11', 'PLAT-2', 'PLAT-16']);
    await expect(target.getByTestId('task-card-pickup-next')).toHaveText('Next');
    await expect(lane.getByTestId('task-card-pickup-next')).toHaveCount(2);
    await expect(lane.getByTestId('task-card').filter({ hasText: 'DEMO-3' }).getByTestId('task-card-pickup-next'))
      .toHaveCount(0);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('lane-scroll-2-ready').evaluate((el) => { el.scrollTop = 0; });
      await page.screenshot({ path: join(RESULTS, `ready-order-after-move-to-top--${theme}--1600.png`) });
    }
  });
});
