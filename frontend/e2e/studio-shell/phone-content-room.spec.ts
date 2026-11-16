import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3836 (playthrough AGT-3796, finding F9): at 390 px the 48 px activity
 * rail stayed, Area and Tag stacked above the lane, the first card started at
 * about y=500, the board tab read "All" because the tab actions took the row,
 * earlier tabs scrolled off the left edge, and "Code Review" clipped.
 *
 * The board test is fully mocked (`--mocked` screenshots); the pane-tab test
 * opens a real fixture card. Both themes.
 */

const PROJECT = 'phone-room';
const WATCH_PATH = '/fixtures/phone-room';
const PHONE = { width: 390, height: 844 };
const DESKTOP = { width: 1440, height: 900 };
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'phone-content-room');

function job(id: string, state: string, title: string, order = 1) {
  return {
    id, jobKey: `${WATCH_PATH}::${id}`, taskKey: `${WATCH_PATH}::${id}`, title, state, order,
    agent: 'codex', cliType: 'codex', createdAt: '2026-11-15T07:00:00Z', watchPath: WATCH_PATH,
    projectName: PROJECT, folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-11-15T07:30:00Z', sessionName: null, model: 'gpt-5-codex', useOwnSession: null,
    lastUsage: null, execution: null, commit: null, commits: [], ownerClientId: 'local-default', tags: [],
  };
}

const READY = [
  job('pr-ready-1', '2-ready', 'Give the content more of the phone screen', 1),
  job('pr-ready-2', '2-ready', 'Show the runner capacity next to the queue', 2),
];
const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: READY, progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};
const OPEN_TABS = {
  v: 1,
  tabs: [{ kind: 'feed' }, { kind: 'workbenches', projectName: null }, { kind: 'board', projectName: '__all__' }],
  activeKey: 'board:__all__',
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function bootBoard(page: Page, viewport: { width: number; height: number }): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.stack ?? error.message));
  await page.setViewportSize(viewport);
  await page.addInitScript((tabs) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(tabs)), OPEN_TABS);
  // No backend runs: answer the Studio hub handshake so the shell stays online.
  await page.route('**/update/status', (route) => json(route, { phase: 'idle', isRunning: false, behindBy: 0 }));
  await page.route('**/hubs/v1/studio/negotiate**', (route) => json(route, {
    connectionId: 'phone-room-e2e', connectionToken: 'phone-room-e2e', negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', (socket) => {
    socket.onMessage((message) => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  // Specific routes after the catch-all: Playwright matches the newest first.
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    json(route, { profile: 'local', bootstrapRequired: false, authenticated: false, user: null }));
  await page.route('**/api/crash-recovery/pending', (route) => json(route, { pending: [] }));
  await page.route('**/api/v1/studio/board**', (route) => json(route, GROUPED));
  await page.route(/\/api\/tasks(\?|$)/, (route) => json(route, READY));
  await page.route('**/api/watch-paths**', (route) =>
    json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]));
  await page.route('**/api/environment**', (route) => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', (route) => json(route, {}));
  const project = {
    sourceType: 'local', id: 'PROJ-3836', displayName: PROJECT, shortCode: 'PR', workspaceId: 'ws-default',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: WATCH_PATH,
    repositoryPath: WATCH_PATH, rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
  };
  await page.route(/\/api\/v1\/workspaces(\?|$)/, (route) => json(route, [{
    id: 'ws-default', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-09-01T00:00:00Z', projects: [project],
  }]));
  // A registered project keeps the first-run checklist from replacing the board.
  await page.route(/\/api\/v1\/projects(\?|$)/, (route) => json(route, [project]));
  await page.route(/\/api\/tasks\/archive(\?|$)/, (route) => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/clients\/[^/]+\/telemetry/, (route) => json(route, { points: [], findings: [], window: '14d' }));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(\?|$)/, (route) => json(route, {
    projectName: PROJECT, includesHistory: false, count: 0, items: [],
  }));
  await page.route('**/api/clients', (route) => json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]));
  await page.route('**/api/cli/quota**', (route) => json(route, { at: '2026-11-15T09:30:00.000Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/runner/orchestrator-feed**', (route) => json(route, { entries: [], generatedAtUtc: '2026-11-15T09:30:00.000Z' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, { projects: {} }));
  await page.goto('/?includeFixtures=true');
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('task-card').first()).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  // The thin route mocks leave an empty error dialog behind; hide it so it does not take the clicks.
  await page.addStyleTag({
    content: 'app-error-dialog, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
  return errors;
}

/** Every visible control of a strip lies inside the viewport. */
async function expectControlsOnScreen(strip: Locator, width: number, label: string): Promise<void> {
  const boxes = await strip.locator('button, select, [role="tab"]').evaluateAll((els) => els
    .map((el) => ({ el, r: el.getBoundingClientRect() }))
    .filter(({ el, r }) => r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden'
      && !el.closest('[aria-hidden="true"]'))
    .map(({ el, r }) => ({ text: (el.getAttribute('aria-label') ?? el.textContent ?? '').trim().slice(0, 40), left: r.left, right: r.right })));
  expect(boxes.length, `${label}: controls render`).toBeGreaterThan(0);
  for (const box of boxes) {
    expect(box.left, `${label}: "${box.text}" starts on screen`).toBeGreaterThanOrEqual(-0.5);
    expect(box.right, `${label}: "${box.text}" ends on screen`).toBeLessThanOrEqual(width + 0.5);
  }
}

/** A label is clipped when its text is wider than its box. */
async function clippedLabels(labels: Locator): Promise<string[]> {
  return labels.evaluateAll((els) => els
    .filter((el) => el.getBoundingClientRect().width > 0 && el.scrollWidth - el.clientWidth > 1)
    .map((el) => (el.textContent ?? '').trim()));
}

test.describe('Phone shell gives the content the screen (AGT-3836)', () => {
  test.beforeAll(() => mkdirSync(RESULTS_DIR, { recursive: true }));

  for (const theme of ['light', 'dark'] as const) {
    test(`390 px board shows the first card without scrolling (${theme})`, async ({ page }) => {
      const errors = await bootBoard(page, PHONE);
      await setTheme(page, theme);

      // The rail gives way: the editor starts at the left edge.
      await expect(page.getByTestId('studio-activity-bar')).toBeHidden();
      const editor = await page.getByTestId('studio-editor').boundingBox();
      expect(editor!.x).toBe(0);
      expect(editor!.width).toBe(PHONE.width);

      // The first card is on screen above the status bar, without scrolling.
      const card = await page.getByTestId('task-card').first().boundingBox();
      const statusbar = await page.getByTestId('studio-statusbar').boundingBox();
      expect(card!.y, 'first card top').toBeGreaterThan(0);
      expect(card!.y + Math.min(card!.height, 48), 'first card sits above the status bar').toBeLessThanOrEqual(statusbar!.y);

      // Area and Tag share one row.
      const [area, tag] = await Promise.all([
        page.getByTestId('shared-area-filter').first().boundingBox(),
        page.getByTestId('shared-facet-filter').first().boundingBox(),
      ]);
      expect(Math.abs(area!.y - tag!.y), 'Area and Tag on one row').toBeLessThanOrEqual(1);

      // Only the active tab shows, with its full label; the rest sit in the overflow menu.
      const tabbar = page.getByTestId('studio-tabbar');
      await expect(page.locator('.studio-tab:visible')).toHaveCount(1);
      await expect(page.getByTestId('studio-tab-board:__all__')).toBeVisible();
      expect(await clippedLabels(tabbar.locator('.studio-tab__title'))).toEqual([]);
      await expectControlsOnScreen(tabbar, PHONE.width, `${theme} tab bar`);
      // The board actions take their own row inside the tab bar instead of the tab's room.
      const [bar, addTask] = await Promise.all([
        tabbar.boundingBox(), page.getByTestId('studio-board-add-task').boundingBox(),
      ]);
      expect(addTask!.y + addTask!.height, 'Add task inside the tab bar').toBeLessThanOrEqual(bar!.y + bar!.height + 0.5);
      await page.screenshot({ path: path.join(RESULTS_DIR, `phone-board-390-${theme}--mocked.png`) });

      const overflow = page.getByTestId('studio-tab-overflow');
      await expect(overflow).toHaveAttribute('aria-label', 'Open tabs: 3');
      await overflow.click();
      const rows = page.locator('[data-testid^="studio-tab-overflow-menu-item-"]');
      await expect(rows).toHaveCount(3);
      await page.screenshot({ path: path.join(RESULTS_DIR, `phone-tab-overflow-390-${theme}--mocked.png`) });
      await rows.filter({ hasText: 'Dossiers' }).click();
      await expect(page.getByTestId('studio-tab-workbenches:__all__')).toBeVisible();
      await expect(page.getByTestId('studio-tab-board:__all__')).toBeHidden();
      await expectControlsOnScreen(tabbar, PHONE.width, `${theme} tab bar after switch`);

      // The rail's Activity destination moved into the header More menu.
      await page.getByTestId('cockpit-header-more').click();
      await expect(page.getByTestId('cockpit-header-more-menu-item-dossiers')).toBeVisible();
      await page.getByTestId('cockpit-header-more-menu-item-activity').click();
      await expect(page.getByTestId('studio-tab-feed')).toBeVisible();
      expect(errors, 'no page errors').toEqual([]);
    });
  }

  test('desktop keeps the rail, every tab, and no overflow control', async ({ page }) => {
    const errors = await bootBoard(page, DESKTOP);
    await expect(page.getByTestId('studio-activity-bar')).toBeVisible();
    await expect(page.locator('.studio-tab:visible')).toHaveCount(3);
    await expect(page.getByTestId('studio-tab-overflow')).toBeHidden();
    await page.getByTestId('cockpit-header-more').click();
    await expect(page.getByTestId('cockpit-header-more-menu-item-activity')).toHaveCount(0);
    await expect(page.getByTestId('cockpit-header-more-menu-item-dossiers')).toHaveCount(0);
    expect(errors, 'no page errors').toEqual([]);
  });
});

interface WatchPath { path: string; }

test('card pane tabs fit or overflow at phone widths in both themes (AGT-3836)', async ({ page }) => {
  const watchPaths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!watchPaths.length) throw new Error('No watch path configured for task detail.');
  const watchPath = watchPaths[0].path;
  const created = await createJob({
    id: `e2e-phone-room-${Date.now()}`, title: 'Phone pane tabs', watchPath, targetState: '2-ready',
    mode: 'concept', requiresIntegration: false, promptMarkdown: '# Phone pane tabs\n\nCheck the pane tabs.',
  });
  try {
    for (let attempt = 0; attempt < 25; attempt++) {
      try { await getJob(created.id, watchPath); break; }
      catch { await new Promise(resolve => setTimeout(resolve, 200)); }
    }
    await page.setViewportSize(PHONE);
    await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`);
    await expect(page.getByTestId('studio-task').locator('app-job-detail')).toBeVisible({ timeout: 30_000 });
    await dismissDevErrorDialog(page);

    const strip = page.locator('app-pane-tabs.pane-tabs-host--header').first();
    await expect(strip).toBeVisible();
    for (const width of [390, 320]) {
      await page.setViewportSize({ width, height: 844 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.waitForTimeout(200);
        const live = strip.locator('.pane-tabs--header:not(.pane-tabs__measurement) .pane-tab__label');
        await expect.poll(() => clippedLabels(live), { message: `${width} ${theme}: no clipped pane tab` }).toEqual([]);
        await expectControlsOnScreen(strip, width, `${width} ${theme} pane tabs`);
        await page.screenshot({ path: path.join(RESULTS_DIR, `phone-pane-tabs-${width}-${theme}.png`) });
      }
    }
    // At 320 px the five card tabs cannot all fit, so some move into the overflow menu.
    await expect(strip.getByTestId('pane-tabs-overflow')).toBeVisible();
  } finally {
    await moveJob(created.id, watchPath, '7-archive').catch(() => undefined);
  }
});
