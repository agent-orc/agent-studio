import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3590 (walkthrough AGT-3561, finding F15): at 390 px the studio board
 * squeezed its three containers to about 110 px each and clipped card titles
 * and lane counts. Below tablet width the board now shows one container at a
 * time, picked in a switcher that carries every container's card count.
 *
 * Fully mocked (`--mocked` screenshots), both themes.
 */

const PROJECT = 'narrow-board';
const WATCH_PATH = '/fixtures/narrow-board';
const PHONE = { width: 390, height: 844 };
const DESKTOP = { width: 1440, height: 900 };
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'board-narrow-group-switcher');

function job(id: string, state: string, title: string, order = 1) {
  return {
    id, jobKey: `${WATCH_PATH}::${id}`, taskKey: `${WATCH_PATH}::${id}`, title, state, order,
    agent: 'codex', cliType: 'codex', createdAt: '2026-11-09T07:00:00Z', watchPath: WATCH_PATH,
    projectName: PROJECT, folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-11-09T07:30:00Z', sessionName: null, model: 'gpt-5-codex', useOwnSession: null,
    lastUsage: null, execution: null, commit: null, commits: [], ownerClientId: 'local-default', tags: [],
  };
}

const READY = [
  job('nb-ready-1', '2-ready', 'Add saved views to the board so operators can return to a filter set', 1),
  job('nb-ready-2', '2-ready', 'Show the runner capacity next to the queue', 2),
];
const BACKLOG = [job('nb-backlog-1', '0-backlog', 'Triage the walkthrough findings into cards')];
const PROGRESS = [job('nb-progress-1', '3-progress', 'Fix large avatar uploads timing out on slow links')];
const HUMAN_REVIEW = [
  job('nb-review-1', '5-human-review', 'Approve the release notes for the November train', 1),
  job('nb-review-2', '5-human-review', 'Review the board search ranking change', 2),
];
const COMPLETED = [job('nb-done-1', '6-completed', 'Document the narrow board layout')];
const ALL = [...READY, ...BACKLOG, ...PROGRESS, ...HUMAN_REVIEW, ...COMPLETED];

const GROUPED = {
  backlog: BACKLOG, preparation: [], orchestratorPrep: [], ready: READY, progress: PROGRESS,
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: HUMAN_REVIEW,
  escalated: [], completed: COMPLETED, archive: [],
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function bootBoard(page: Page, viewport: { width: number; height: number }): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.stack ?? error.message));
  await page.setViewportSize(viewport);
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  // No backend runs: answer the Studio hub handshake so the shell stays online.
  await page.route('**/update/status', (route) => json(route, { phase: 'idle', isRunning: false, behindBy: 0 }));
  await page.route('**/hubs/v1/studio/negotiate**', (route) => json(route, {
    connectionId: 'narrow-board-e2e', connectionToken: 'narrow-board-e2e', negotiateVersion: 1,
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
  await page.route(/\/api\/tasks(\?|$)/, (route) => json(route, ALL));
  await page.route('**/api/watch-paths**', (route) =>
    json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]));
  await page.route('**/api/environment**', (route) => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', (route) => json(route, {}));
  const project = {
    sourceType: 'local', id: 'PROJ-0390', displayName: PROJECT, shortCode: 'NB', workspaceId: 'ws-default',
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
  await page.route('**/api/cli/quota**', (route) => json(route, { at: '2026-11-09T09:30:00.000Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/runner/orchestrator-feed**', (route) => json(route, { entries: [], generatedAtUtc: '2026-11-09T09:30:00.000Z' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, { projects: {} }));
  await page.goto('/?includeFixtures=true');
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('task-card').first()).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  return errors;
}

/** Every rendered lane count in a container, read from the lane headers. */
async function laneCountSum(group: Locator): Promise<number> {
  const counts = await group.locator('[data-testid^="lane-count-"]:not([data-testid="lane-count-7-archive"])')
    .evaluateAll((els) => els.map((el) => Number(el.firstElementChild?.textContent?.trim() ?? NaN)));
  return counts.reduce((sum, n) => sum + n, 0);
}

/** A title or count is clipped when it overflows its box or leaves the board. */
async function expectNothingClipped(page: Page, group: Locator, label: string): Promise<void> {
  const board = await page.getByTestId('studio-board').boundingBox();
  expect(board, `${label}: board box`).not.toBeNull();
  const targets = group.locator('[data-testid="task-card-title"], [data-testid^="lane-count-"], [data-testid^="lane-title-"]');
  const boxes = await targets.evaluateAll((els) => els.map((el) => {
    const r = el.getBoundingClientRect();
    return {
      text: (el.textContent ?? '').trim().slice(0, 40), left: r.left, right: r.right, width: r.width,
      overflow: el.scrollWidth - el.clientWidth,
    };
  }));
  expect(boxes.length, `${label}: titles and counts render`).toBeGreaterThan(0);
  for (const box of boxes) {
    expect(box.width, `${label}: "${box.text}" has room`).toBeGreaterThan(0);
    expect(box.left, `${label}: "${box.text}" starts inside the board`).toBeGreaterThanOrEqual(board!.x - 0.5);
    expect(box.right, `${label}: "${box.text}" ends inside the board`).toBeLessThanOrEqual(board!.x + board!.width + 0.5);
    expect(box.overflow, `${label}: "${box.text}" does not overflow its box`).toBeLessThanOrEqual(1);
  }
}

test.describe('Narrow board: one container at a time (AGT-3590)', () => {
  test.beforeAll(() => mkdirSync(RESULTS_DIR, { recursive: true }));

  for (const theme of ['light', 'dark'] as const) {
    test(`390 px shows one container with a counted switcher (${theme})`, async ({ page }) => {
      const errors = await bootBoard(page, PHONE);
      await setTheme(page, theme);

      const switcher = page.getByTestId('board-group-switcher');
      await expect(switcher).toBeVisible();
      const groups = [
        { id: 'backlog', label: 'Backlog', count: READY.length + BACKLOG.length },
        { id: 'active', label: 'Active', count: PROGRESS.length },
        { id: 'decide', label: 'Done & Decide', count: HUMAN_REVIEW.length + COMPLETED.length },
      ];

      for (const group of groups) {
        const option = page.getByTestId(`board-group-switch-${group.id}`);
        await option.click();
        await expect(option).toHaveAttribute('aria-pressed', 'true');
        await expect(option).toHaveText(`${group.label} ${group.count}`);

        // Exactly the picked container shows, at the full board width.
        const shown = page.getByTestId(`lane-group-${group.id}`);
        await expect(shown).toBeVisible();
        for (const other of groups.filter((g) => g.id !== group.id)) {
          await expect(page.getByTestId(`lane-group-${other.id}`)).toBeHidden();
        }
        const [board, column] = await Promise.all([
          page.getByTestId('studio-board').boundingBox(),
          shown.boundingBox(),
        ]);
        expect(column!.width).toBeCloseTo(board!.width, 0);

        // R3: the switcher count is the sum of the lane counts it stands for.
        expect(await laneCountSum(shown)).toBe(group.count);
        await expectNothingClipped(page, shown, `${theme} ${group.id}`);

        // The switcher keeps every label and count on screen.
        const box = await option.boundingBox();
        expect(box!.x + box!.width).toBeLessThanOrEqual(PHONE.width);
        expect(await option.evaluate((el) => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(1);

        await page.screenshot({ path: path.join(RESULTS_DIR, `board-390-${group.id}-${theme}--mocked.png`) });
      }
      expect(errors, 'no page errors').toEqual([]);
    });
  }

  test('desktop keeps the three containers side by side without the switcher', async ({ page }) => {
    const errors = await bootBoard(page, DESKTOP);
    await expect(page.getByTestId('board-group-switcher')).toHaveCount(0);
    for (const id of ['backlog', 'active', 'decide']) {
      await expect(page.getByTestId(`lane-group-${id}`)).toBeVisible();
    }
    // A phone pick does not leave the wide board focused.
    await page.setViewportSize(PHONE);
    await page.getByTestId('board-group-switch-active').click();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();
    await page.setViewportSize(DESKTOP);
    await expect(page.getByTestId('board-group-switcher')).toHaveCount(0);
    for (const id of ['backlog', 'active', 'decide']) {
      await expect(page.getByTestId(`lane-group-${id}`)).toBeVisible();
    }
    expect(errors, 'no page errors').toEqual([]);
  });
});
