import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { api, BACKEND, DEFAULT_CLIENT_ID } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3624 (playthrough AGT-3560, finding H8): the card context menu offered
 * lane moves the server's lane guard refused, each refusal opened the generic
 * HTTP error dialog with raw JSON, and Delete dropped unintegrated work after
 * a plain confirm.
 *
 *  1. Real backend: a move the guard refuses is disabled in the menu, its
 *     tooltip names the reason, and the server indeed refuses that move.
 *  2. Mocked API: a refusal the board cannot predict reads as one plain
 *     sentence and a next action; the payload sits behind Technical details.
 *  3. Mocked API: deleting a card with unintegrated work names what is lost
 *     and needs a written reason, which travels with the DELETE.
 */

const EVIDENCE_DIR = resolve(process.env.JOB_RESULTS_DIR ?? 'test-results/AGT-3624');
mkdirSync(EVIDENCE_DIR, { recursive: true });

async function evidence(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: resolve(EVIDENCE_DIR, `${name}.png`) });
}

async function captureThemes(page: Page, name: string, setup: () => Promise<void>): Promise<void> {
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await setup();
    await evidence(page, `${name}-${theme}`);
  }
}

test.describe('Card menu lane guard against the real backend', () => {
  const PREFIX = 'e2e-agt3624-guard-';
  let jobId = '';
  let watchPath = '';

  test.beforeAll(async () => {
    const paths = await api<Array<{ path: string }>>('/api/watch-paths');
    watchPath = paths[0].path;
    jobId = PREFIX + Date.now();
    await createJob({ id: jobId, title: 'AGT-3624 unrun coding card', watchPath, targetState: '2-ready', fixture: false });
  });

  test.afterAll(async () => {
    await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE',
      headers: { 'x-client-id': DEFAULT_CLIENT_ID },
    }).catch(() => undefined);
  });

  test('a refused move is disabled with the reason as a tooltip, and the server agrees', async ({ page }) => {
    // The server-side truth the menu mirrors: an unrun card whose work must
    // integrate cannot be archived.
    const refused = await fetch(
      `${BACKEND}/api/tasks/${encodeURIComponent(jobId)}/state?watchPath=${encodeURIComponent(watchPath)}`,
      {
        method: 'PUT',
        headers: { 'content-type': 'application/json', 'x-client-id': DEFAULT_CLIENT_ID },
        body: JSON.stringify({ targetState: '7-archive' }),
      },
    );
    expect(refused.status).toBe(409);

    await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
    const card = page.locator('[data-testid="task-card"]', { hasText: 'AGT-3624 unrun coding card' });
    await expect(card).toBeVisible({ timeout: 20_000 });

    const openMenu = async () => {
      await page.keyboard.press('Escape');
      await card.click({ button: 'right' });
      const archive = page.getByTestId('card-ctx-item-move-card:7-archive');
      await expect(archive).toBeVisible();
      await archive.hover({ force: true });
    };
    await openMenu();
    const archive = page.getByTestId('card-ctx-item-move-card:7-archive');
    await expect(archive).toBeDisabled();
    await expect(page.getByTestId('card-ctx-item-move-card:0-backlog')).toBeEnabled();
    await expect(page.getByTestId('card-ctx-item-delete-task')).toBeEnabled();
    const tooltip = page.locator('.cac-tooltip', { hasText: 'This card has not delivered its work yet.' });
    await expect(tooltip).toBeVisible();
    await expect(tooltip).toContainText('Run it first, or delete the card if it is no longer needed.');
    // The tooltip paints above the menu: same z-index as the overlay root, later in DOM order.
    expect(await page.evaluate(() => {
      const tip = document.querySelector('body > .cac-tooltip');
      const root = document.querySelector('body > .studio-overlay-root');
      return !!tip && !!root && getComputedStyle(tip).zIndex === getComputedStyle(root).zIndex
        && !!(root.compareDocumentPosition(tip) & Node.DOCUMENT_POSITION_FOLLOWING);
    })).toBe(true);
    await captureThemes(page, 'agt3624-menu-disabled-archive', openMenu);

    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await openMenu();
    await expect(page.getByTestId('card-ctx-item-move-card:7-archive')).toBeDisabled();
    await evidence(page, 'agt3624-menu-disabled-archive-390');
  });
});

/**
 * The real backend serves the shell; the two demo cards are injected into its
 * board payload, and their move and delete calls are answered here.
 */
interface WatchPathInfo { name: string; path: string }

function card(project: WatchPathInfo, overrides: Record<string, unknown>) {
  const id = String(overrides['id']);
  const state = String(overrides['state']);
  return {
    taskKey: `${project.path}::${id}`,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-11-09T08:00:00Z',
    watchPath: project.path,
    projectName: project.name,
    folderPath: `${project.path}/${state}/${id}`,
    lastActivity: '2026-11-09T09:00:00Z',
    sessionName: null,
    model: 'gpt-5.6-codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

const INTEGRATED_TITLE = 'AGT-3624 integrated delivery awaiting acceptance';
const PENDING_TITLE = 'AGT-3624 delivery waiting for integration';

function demoCards(project: WatchPathInfo) {
  return [
    card(project, {
      id: 'agt3624-demo-integrated',
      key: 'DEMO-1',
      title: INTEGRATED_TITLE,
      state: '5-human-review',
      commit: { sha: 'b'.repeat(40), shortSha: 'bbbbbbb', message: 'feat: demo', filesChanged: 1, files: ['a.txt'], at: '2026-11-09T08:30:00Z' },
      integration: { status: 'integrated', deliveryRef: 'runner/host/DEMO-1', sha: 'bbbbbbb', integrationBranch: 'develop', detail: null },
    }),
    card(project, {
      id: 'agt3624-demo-pending',
      key: 'DEMO-2',
      title: PENDING_TITLE,
      state: '5-human-review',
      commit: { sha: 'c'.repeat(40), shortSha: 'ccccccc', message: 'feat: pending', filesChanged: 1, files: ['b.txt'], at: '2026-11-09T08:40:00Z' },
      integration: { status: 'pending', deliveryRef: 'runner/host/DEMO-2', sha: null, integrationBranch: 'develop', detail: 'Not in develop yet.' },
    }),
  ];
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

/** Fetch the real response unconditionally (no 304), then add the demo cards. */
async function injectInto<T>(route: Route, add: (body: T) => T): Promise<void> {
  const headers = { ...route.request().headers() };
  delete headers['if-none-match'];
  delete headers['if-modified-since'];
  // A board poll can still be in flight when the test ends; a failed fetch
  // hands the request back instead of failing the test.
  try {
    const response = await route.fetch({ headers });
    const text = await response.text();
    if (!response.ok() || !text.trim()) return await route.fulfill({ response });
    return await route.fulfill({ response, json: add(JSON.parse(text) as T) });
  } catch {
    await route.continue().catch(() => undefined);
  }
}

async function installMockedBoard(page: Page, onDelete: (url: URL) => void): Promise<void> {
  const paths = await api<WatchPathInfo[]>('/api/watch-paths');
  const cards = demoCards(paths[0]);
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === 'DELETE' && url.pathname.endsWith('/tasks/agt3624-demo-pending')) {
      onDelete(url);
      return route.fulfill({ status: 200, body: '' });
    }
    if ((request.method() === 'PUT' || request.method() === 'POST')
      && url.pathname.includes('/tasks/agt3624-demo-integrated/')) {
      return json(route, {
        error: 'Completion refused: delivery brief version is missing; no content verdict (requirement-fit); only no aspects ran.',
        code: 'completion-content-gap',
      }, 409);
    }
    if (request.method() === 'GET' && url.pathname.endsWith('/api/v1/studio/board')) {
      return injectInto(route, (board: Record<string, unknown[]>) =>
        ({ ...board, humanReview: [...cards, ...(board['humanReview'] ?? [])] }));
    }
    if (request.method() === 'GET' && /\/api\/tasks\/?$/.test(url.pathname)) {
      return injectInto(route, (list: unknown[]) => [...cards, ...list]);
    }
    return route.fallback();
  });
}

async function openMockedBoard(page: Page): Promise<void> {
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
}

test.describe('Card menu refusals and delete with the API mocked', () => {
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  test('an unpredicted refusal reads plainly with the payload behind Technical details', async ({ page }) => {
    await installMockedBoard(page, () => undefined);
    await openMockedBoard(page);
    const integrated = page.locator('[data-testid="task-card"]', { hasText: INTEGRATED_TITLE });
    const pending = page.locator('[data-testid="task-card"]', { hasText: PENDING_TITLE });
    await expect(integrated).toBeVisible({ timeout: 20_000 });

    // The guard the board can predict: a pending delivery cannot be accepted.
    await pending.click({ button: 'right' });
    const pendingAccept = page.getByTestId('card-ctx-item-move-card:6-completed');
    await expect(pendingAccept).toBeDisabled();
    await pendingAccept.hover({ force: true });
    await expect(page.locator('.cac-tooltip', { hasText: "This card's work is not in develop yet." })).toBeVisible();
    await page.keyboard.press('Escape');

    // The guard it cannot predict: the content check refuses with 409.
    await integrated.click({ button: 'right' });
    const accept = page.getByTestId('card-ctx-item-move-card:6-completed');
    await expect(accept).toBeEnabled();
    await accept.click();

    const dialog = page.getByTestId('error-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Move refused');
    await expect(dialog).toContainText('Cannot move to Accepted');
    await expect(page.getByTestId('error-dialog-message'))
      .toHaveText("This card's review does not cover its current brief, so it cannot be accepted yet.");
    await expect(page.getByTestId('error-dialog-next-action')).toContainText('rerun its review');
    await expect(dialog).not.toContainText('requirement-fit');
    await expect(dialog).not.toContainText('Stack trace');
    await expect(page.getByTestId('error-dialog-technical')).toHaveCount(0);
    await captureThemes(page, 'agt3624-refusal-dialog', async () => undefined);

    const toggle = page.getByTestId('error-dialog-technical-toggle');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('error-dialog-server-message')).toContainText('delivery brief version');
    await expect(page.getByTestId('error-dialog-output')).toContainText('409');
    await captureThemes(page, 'agt3624-refusal-dialog-technical', async () => undefined);

    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await evidence(page, 'agt3624-refusal-dialog-technical-390');

    await page.getByTestId('error-dialog-dismiss').click();
    await expect(dialog).toHaveCount(0);
  });

  test('deleting a card with unintegrated work names the loss and needs a written reason', async ({ page }) => {
    let deleteUrl: URL | null = null;
    await installMockedBoard(page, (url) => { deleteUrl = url; });
    await openMockedBoard(page);
    const pending = page.locator('[data-testid="task-card"]', { hasText: PENDING_TITLE });
    await expect(pending).toBeVisible({ timeout: 20_000 });

    await pending.click({ button: 'right' });
    await page.getByTestId('card-ctx-item-delete-task').click();

    const dialog = page.getByTestId('confirm-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Delete this task and its unintegrated work?');
    await expect(page.getByTestId('confirm-dialog-message')).toContainText(
      "This card's delivered work (1 commit on runner/host/DEMO-2) is not integrated into develop.");
    const confirm = page.getByTestId('confirm-dialog-confirm');
    await expect(confirm).toBeDisabled();
    await page.getByTestId('confirm-dialog-reason-input').fill('short');
    await expect(confirm).toBeDisabled();
    await page.getByTestId('confirm-dialog-reason-input').fill('Superseded by DEMO-3');
    await expect(confirm).toBeEnabled();
    await captureThemes(page, 'agt3624-delete-unintegrated', async () => undefined);

    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await evidence(page, 'agt3624-delete-unintegrated-390');

    await confirm.click();
    await expect.poll(() => deleteUrl?.searchParams.get('reason') ?? null).toBe('Superseded by DEMO-3');
  });
});
