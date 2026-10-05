import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Token-popover open/close + viewport regression (ASS-1700).
 *
 * Bug: the portal migration dropped the native `popover` attribute that kept
 * the token-usage panel collapsed by default. Every card then painted its
 * `position: fixed` popover at its static position (off the right edge of the
 * viewport), clipped, and hung permanently open across multiple cards.
 *
 * This spec mocks the whole API surface so it runs against any served frontend
 * (no live backend). It asserts:
 *   1. a 32-card Ready lane paints no panels before interaction;
 *   2. opening two cards sequentially leaves only the second panel open;
 *   3. a board-data refresh closes the active panel;
 *   4. once open it is portaled into the body overlay root and sits fully
 *      inside the viewport (no right-edge cutoff);
 *   5. outside click, Escape, and lane scroll dismiss it.
 */

const PROJECT = 'fixture-token-viewport';
const WATCH_PATH = 'C:/fixtures/token-viewport';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? 'test-results';

function makeJob(index: number) {
  const suffix = String(index).padStart(2, '0');
  return {
    id: `token-viewport-job-${suffix}`,
    taskKey: `${WATCH_PATH}::token-viewport-job-${suffix}`,
    title: `Token viewport fixture ${suffix}`,
    state: '2-ready',
    order: index,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/token-viewport-job-${suffix}`,
    lastActivity: '2026-06-09T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    tokenSummary: {
      calls: 3,
      inputTokens: 120_000,
      outputTokens: 18_000,
      cacheReadTokens: 250_000,
      cacheCreationTokens: 12_000,
      totalTokens: 400_000,
      lastModel: 'claude-opus-4-7',
      lastUpdate: '2026-06-09T08:30:00Z',
      entries: [
        { ts: '2026-06-09T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 50_000, outputTokens: 6_000, cacheReadTokens: 100_000, cacheCreationTokens: 4_000 },
        { ts: '2026-06-09T08:15:00Z', model: 'claude-opus-4-7', inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 80_000, cacheCreationTokens: 4_000 },
        { ts: '2026-06-09T08:30:00Z', model: 'claude-opus-4-7', inputTokens: 30_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000 },
      ],
    },
  };
}

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: Array.from({ length: 32 }, (_, index) => makeJob(index + 1)),
  progress: [],
  failedPickup: [],
  autoReview: [],
  humanReview: [],
  escalated: [],
  review: [],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page): Promise<() => number> {
  let groupedRequests = 0;
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
  await page.route('**/api/v1/studio/board**', (route) => {
    groupedRequests += 1;
    const snapshot = {
      ...GROUPED,
      ready: GROUPED.ready.map(job => ({
        ...job,
        title: groupedRequests > 1 ? `${job.title} refreshed` : job.title,
      })),
    };
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(snapshot) });
  });
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-09T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-09T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  return () => groupedRequests;
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const testWindow = window as typeof window & { __agt2675EnableRefresh?: boolean };
    const nativeSetInterval = window.setInterval.bind(window);
    testWindow.__agt2675EnableRefresh = false;
    window.setInterval = ((handler: TimerHandler, timeout?: number) => {
      if (timeout === 30_000 && typeof handler === 'function') {
        return nativeSetInterval(() => {
          if (testWindow.__agt2675EnableRefresh) handler();
        }, 250);
      }
      return nativeSetInterval(handler, timeout);
    }) as typeof window.setInterval;

    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

test.describe('Token popover open/close + viewport (ASS-1700)', () => {
  test('keeps one explicit popover and clears it at every board lifecycle boundary', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await seedBoardTab(page);
    const groupedRequestCount = await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const readyLane = page.getByTestId('lane-2-ready');
    const cards = readyLane.getByTestId('task-card');
    await expect(cards).toHaveCount(32, { timeout: 10_000 });
    await dismissDevErrorDialog(page);
    // ng serve can raise its known NG0919 dialog again during mocked polling.
    // Keep that development-only artifact out of pointer checks and evidence.
    await page.addStyleTag({ content: 'app-error-dialog, app-offline-banner { display: none !important; }' });

    const firstBubble = cards.nth(0).getByTestId('task-card-token-bubble');
    const secondBubble = cards.nth(1).getByTestId('task-card-token-bubble');
    await expect(firstBubble).toBeVisible({ timeout: 5_000 });
    await expect(secondBubble).toBeVisible({ timeout: 5_000 });

    const popovers = page.getByTestId('task-card-token-popover');
    const visiblePopovers = page.locator('[data-testid="task-card-token-popover"]:visible');
    await expect(popovers).toHaveCount(32);
    await expect(visiblePopovers).toHaveCount(0);

    mkdirSync(RESULTS_DIR, { recursive: true });
    await setTheme(page, 'light');

    // Review-only before evidence: recreate the reported unconditional-render
    // state in the mocked 32-card lane. The actual product assertion below is
    // made only after every panel is restored to its default-hidden state.
    await popovers.evaluateAll(elements => {
      for (const element of elements) (element as HTMLElement).hidden = false;
    });
    await page.screenshot({
      path: path.join(RESULTS_DIR, 'token-popover-before-stacked--mocked.png'),
      fullPage: false,
    });
    await popovers.evaluateAll(elements => {
      for (const element of elements) (element as HTMLElement).hidden = true;
    });
    await expect(visiblePopovers).toHaveCount(0);
    await page.screenshot({
      path: path.join(RESULTS_DIR, 'token-popover-after-rest-light--mocked.png'),
      fullPage: false,
    });
    await setTheme(page, 'dark');
    await expect(visiblePopovers).toHaveCount(0);
    await page.screenshot({
      path: path.join(RESULTS_DIR, 'token-popover-after-rest-dark--mocked.png'),
      fullPage: false,
    });

    // Sequential opens are globally coordinated: the second anchor owns the
    // only visible panel and the first panel is restored under its card.
    await firstBubble.hover();
    await expect(visiblePopovers).toHaveCount(1, { timeout: 3_000 });
    await secondBubble.hover();
    await expect(visiblePopovers).toHaveCount(1, { timeout: 3_000 });
    const popover = visiblePopovers.first();
    await expect(popover).toHaveAttribute('data-token-anchor', `${WATCH_PATH}::token-viewport-job-02`);
    const overlayRoot = page.locator('[data-testid="studio-overlay-root"]');
    await expect(overlayRoot.locator('[data-testid="task-card-token-popover"]')).toBeVisible();

    // The active panel sits fully inside the viewport: no right-edge cutoff,
    // no left spill.
    const box = await popover.boundingBox();
    expect(box, 'popover should have a layout box when open').not.toBeNull();
    if (box) {
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(1280);
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.y + box.height).toBeLessThanOrEqual(800);
    }

    await page.screenshot({
      path: path.join(RESULTS_DIR, 'token-popover-after-single-open--mocked.png'),
      fullPage: false,
    });

    // Accelerate the real 30-second reconciliation heartbeat only after the
    // sequential-open assertion. The next grouped response replaces every
    // stable-key card input, matching the SignalR re-pull path.
    const requestCountBeforeRefresh = groupedRequestCount();
    await page.evaluate(() => {
      (window as typeof window & { __agt2675EnableRefresh?: boolean }).__agt2675EnableRefresh = true;
    });
    await expect.poll(groupedRequestCount, { timeout: 5_000 }).toBeGreaterThan(requestCountBeforeRefresh);
    await expect(visiblePopovers).toHaveCount(0);
    await page.evaluate(() => {
      (window as typeof window & { __agt2675EnableRefresh?: boolean }).__agt2675EnableRefresh = false;
    });

    // Direct dismissal paths remain deterministic after a data refresh.
    await firstBubble.hover();
    await expect(visiblePopovers).toHaveCount(1, { timeout: 3_000 });
    await page.mouse.click(10, 10);
    await expect(visiblePopovers).toHaveCount(0);

    await firstBubble.focus();
    await expect(visiblePopovers).toHaveCount(1);
    await page.keyboard.press('Escape');
    await expect(visiblePopovers).toHaveCount(0);

    await firstBubble.hover();
    await expect(visiblePopovers).toHaveCount(1, { timeout: 3_000 });
    await page.getByTestId('lane-scroll-2-ready').evaluate(element => {
      element.scrollTop += 300;
      element.dispatchEvent(new Event('scroll'));
    });
    await expect(visiblePopovers).toHaveCount(0);
  });
});
