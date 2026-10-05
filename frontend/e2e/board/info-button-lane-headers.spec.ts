import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';

/**
 * Lane-info on lane headers.
 *
 * Every lane carries an `<app-info-button>`: each maps to a committed
 * concept doc (`docs/app/help/lane-guides/lane-*.md`, the single source of
 * truth) fetched from `GET /api/concept-docs/{topic}` and shown in the
 * centered lane-info modal (the app-wide `<app-dialog>` surface, so it
 * flips light/dark from studio tokens).
 *
 * This spec uses `page.route` intercepts so it doesn't require a real
 * job pipeline; the live backend is still reached for /api/concept-docs
 * to prove the endpoint serves the committed markdown.
 */

interface JobInfoStub {
  id: string;
  jobKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: string | null;
  cliType: string | null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  ownerClientId: string;
  tokenSummary: null;
  tags: string[];
}

function jobStub(over: Partial<JobInfoStub>): JobInfoStub {
  const id = over.id ?? 'stub-job';
  return {
    id,
    jobKey: `stub::${id}`,
    title: over.title ?? id,
    state: over.state ?? '4-auto-review',
    order: over.order ?? 1,
    agent: 'claude',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: 'C:/stub',
    projectName: 'stub-project',
    folderPath: 'C:/stub/' + id,
    lastActivity: '2026-05-05T08:00:00Z',
    sessionName: null,
    model: 'claude-sonnet-4-6',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ownerClientId: 'local-default',
    tokenSummary: null,
    tags: over.tags ?? []
  };
}

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.INFO_BUTTON_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'info-button');
})();

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

async function dismissErrorDialogIfPresent(page: Page): Promise<void> {
  const overlay = page.locator('app-error-dialog .overlay--error');
  if (await overlay.isVisible().catch(() => false)) {
    const close = page.locator('app-error-dialog button').first();
    await close.click({ trial: false }).catch(() => { /* best-effort */ });
  }
}

async function installMocks(page: Page, jobs: JobInfoStub[]): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;

    // Reach the real backend for the concept-docs endpoint so the spec
    // exercises the full pipe (committed markdown -> service -> drawer).
    if (p.startsWith('/api/concept-docs/')) {
      const upstream = await fetch(`${BACKEND}${p}`);
      const body = await upstream.text();
      return route.fulfill({
        status: upstream.status,
        contentType: upstream.headers.get('content-type') ?? 'application/json',
        body
      });
    }

    if (p === '/api/v1/studio/board') {
      const body = {
        backlog: jobs.filter(j => j.state === '0-backlog'),
        preparation: jobs.filter(j => j.state === '1-preparation'),
        orchestratorPrep: [],
        ready: jobs.filter(j => j.state === '2-ready'),
        progress: jobs.filter(j => j.state === '3-progress'),
        failedPickup: [],
        autoReview: jobs.filter(j => j.state === '4-auto-review'),
        humanReview: jobs.filter(j => j.state === '5-human-review'),
        review: jobs.filter(j => j.state === '4-auto-review'),
        completed: jobs.filter(j => j.state === '6-completed'),
        archive: jobs.filter(j => j.state === '7-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          lastTickAt: new Date(Date.now() - 5_000).toISOString(),
          accept: 0, reissue: 0, escalate: 0, aspectsRun: 0,
          currentJob: null, currentProject: null
        })
      });
    }
    if (p === '/api/tags') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) });
    }
    if (p.startsWith('/api/clients')) {
      const list = [{
        id: 'local-default', displayName: 'Local Default', emoji: '🤖', colour: '#64748b', kind: 'human',
        registeredAt: '2026-01-01T00:00:00Z', lastSeenAt: null, tokenBudgetMonthly: null, notes: null
      }];
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(list) });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([{ name: 'stub-project', path: 'C:/stub', rootPath: 'C:/stub' }])
      });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }) });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sections: [] }) });
    }
    if (p.match(/^\/api\/cli\/[^/]+\/models$/)) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ models: [], defaultModel: null }) });
    }
    if (p === '/api/settings/cli/models') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ models: [] }) });
    }
    if (p.startsWith('/api/runner') || p.startsWith('/api/v1/studio/runner')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

test.describe('Info button on lane headers (selective placement)', () => {
  test('backend serves the committed concept-docs', async () => {
    const a = await api<{ topic: string; title: string; body: string }>('/api/concept-docs/lane-4-auto-review');
    expect(a.topic).toBe('lane-4-auto-review');
    expect(a.title).toBe('Post Processing');
    expect(a.body.length).toBeGreaterThan(200);

    const b = await api<{ topic: string; title: string; body: string }>('/api/concept-docs/lane-3-progress');
    expect(b.topic).toBe('lane-3-progress');
    expect(b.body.length).toBeGreaterThan(200);
  });

  test('endpoint returns 404 for unknown topics', async () => {
    const res = await fetch(`${BACKEND}/api/concept-docs/nonexistent`);
    expect(res.status).toBe(404);
  });

  test('4-auto-review lane header carries an info button that opens the modal', async ({ page }) => {
    await installMocks(page, [
      jobStub({ id: 'fix-a', title: 'A', state: '4-auto-review' }),
      jobStub({ id: 'fix-b', title: 'B', state: '3-progress' })
    ]);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    const trigger = page.getByTestId('info-button-lane-4-auto-review');
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();

    const modal = page.getByTestId('info-button-modal-lane-4-auto-review');
    await expect(modal).toBeVisible();
    await expect(modal.locator('.dialog__title')).toHaveText('Post Processing');
    const body = page.getByTestId('info-button-body-lane-4-auto-review');
    await expect(body).toContainText(/multi-aspect/i);

    // Regression guard: the lane `.column` carries `contain: layout paint`,
    // which makes it the containing block for the overlay's `position:
    // fixed`. Before the portal-to-body fix the modal landed thousands of
    // px down the scrolled lane and was clipped off-screen. Assert the
    // centered panel sits inside the viewport.
    const box = await modal.boundingBox();
    expect(box).not.toBeNull();
    const vp = page.viewportSize();
    expect(vp).not.toBeNull();
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.y + box!.height).toBeLessThanOrEqual(vp!.height + 1);
    expect(box!.x + box!.width).toBeLessThanOrEqual(vp!.width + 1);

    await page.setViewportSize({ width: 1400, height: 900 });
    await page.screenshot({ path: `${SCREENSHOT_DIR}/01-auto-review-modal.png`, fullPage: false });

    // ESC closes the modal.
    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0);
  });

  test('3-progress lane header carries its own info button', async ({ page }) => {
    await installMocks(page, [
      jobStub({ id: 'fix-c', title: 'C', state: '3-progress' })
    ]);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    const trigger = page.getByTestId('info-button-lane-3-progress');
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();

    const modal = page.getByTestId('info-button-modal-lane-3-progress');
    await expect(modal).toBeVisible();
    await expect(modal.locator('.dialog__title')).toContainText(/progress/i);
    const body = page.getByTestId('info-button-body-lane-3-progress');
    await expect(body).toContainText(/orchestrator/i);

    await page.setViewportSize({ width: 1400, height: 900 });
    await page.screenshot({ path: `${SCREENSHOT_DIR}/02-progress-modal.png`, fullPage: false });

    await page.getByTestId('info-button-modal-lane-3-progress-close').click();
    await expect(modal).toHaveCount(0);
  });

  test('every lane header now carries its own info button', async ({ page }) => {
    await installMocks(page, [
      jobStub({ id: 'fix-d', title: 'D', state: '2-ready' }),
      jobStub({ id: 'fix-e', title: 'E', state: '6-completed' }),
      jobStub({ id: 'fix-f', title: 'F', state: '7-archive' }),
      jobStub({ id: 'fix-g', title: 'G', state: '0-backlog' }),
      jobStub({ id: 'fix-h', title: 'H', state: '1-preparation' }),
      jobStub({ id: 'fix-i', title: 'I', state: '5-human-review' })
    ]);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissErrorDialogIfPresent(page);

    // The kanban renders eagerly; once a lane is visible we can assert
    // across siblings without polling. Lanes that used to deliberately
    // have nothing now each expose a lane-info trigger.
    await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 10_000 });
    for (const state of ['0-backlog', '1-preparation', '2-ready', '5-human-review', '6-completed', '7-archive']) {
      await expect(page.getByTestId(`info-button-lane-${state}`)).toBeVisible();
    }

    // Opening any of the newly-covered lanes resolves real prose.
    await page.getByTestId('info-button-lane-2-ready').click();
    const modal = page.getByTestId('info-button-modal-lane-2-ready');
    await expect(modal).toBeVisible();
    await expect(modal.locator('.dialog__title')).toHaveText('Ready');
    await expect(page.getByTestId('info-button-body-lane-2-ready')).toContainText(/oldest-first/i);
  });
});
