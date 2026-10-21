import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * The Explorer lane dots match the cards visible in the board lanes, and the
 * human-review dot uses the Review hue in both themes.
 */

const PROJECT = 'Token Economy';
const WATCH_PATH = 'C:/fixtures/token-economy';
const resultsDir = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');

function card(id: string, state: string, order: number) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, title: id,
    state, order, projectName: PROJECT, watchPath: WATCH_PATH,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${id}`,
    createdAt: '2026-08-23T08:00:00Z', lastActivity: '2026-08-23T09:00:00Z',
    agent: 'codex', cliType: 'codex',
    sessionName: null, model: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default',
    tags: [], pendingIntent: null, autoLoop: null, summaryState: null,
  };
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], review: [], completed: [], archive: [],
    ready: [card('TE-1', '2-ready', 1)],
    progress: [card('TE-2', '3-progress', 1)],
    humanReview: [card('TE-3', '5-human-review', 2)],
    escalated: [card('TE-4', '5e-escalated', 1)],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const project = {
    id: 'PROJ-TE', displayName: PROJECT, shortCode: 'TE', workspaceId: 'ws-te',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
    storageLocation: WATCH_PATH, archived: false, createdAt: '2026-08-23T08:00:00Z', urls: [],
  };
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify(body),
    });
    if (url.includes('/api/v1/studio/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/v1/studio/board')) return json(grouped());
    if (url.includes('/api/tasks/archive')) return json({ items: [], total: 0, offset: 0, limit: 50 });
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(Object.values(grouped()).flat());
    if (url.includes('/api/watch-paths')) {
      return json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    }
    if (new URL(url).pathname === '/api/v1/projects') return json([project]);
    if (url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) {
      return json([{
        id: 'ws-te', displayName: 'Economy', sortOrder: 0, isDefault: true, color: null,
        createdAt: '2026-08-23T08:00:00Z', projects: [project],
      }]);
    }
    if (url.includes('/api/environment')) return json({ isDev: false, devTools: {} });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-08-23T08:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json({ at: '2026-08-23T08:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    return json([]);
  });
}

async function boot(page: Page, metricView: 'numbers' | 'dots'): Promise<void> {
  await page.addInitScript(([name, view]) => {
    localStorage.setItem('atp.studio.explorer.expanded', JSON.stringify([name]));
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: name }], activeKey: `board:${name}`,
    }));
    localStorage.setItem('atp.studio.explorer.metrics', view);
  }, [PROJECT, metricView] as const);
  await installRoutes(page);
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
}

function rgbToHex(value: string): string {
  const parts = value.match(/\d+(\.\d+)?/g)?.slice(0, 3).map(Number) ?? [];
  return parts.map(n => Math.round(n).toString(16).padStart(2, '0')).join('');
}

test('the human-review dot reads in the Review hue, never Delivered green, in both themes', async ({ page }) => {
  fs.mkdirSync(resultsDir, { recursive: true });
  await boot(page, 'dots');

  const sidebar = page.getByTestId('studio-sidebar');
  const dots = page.getByTestId(`studio-explorer-project-board-dots-${PROJECT}`);
  const reviewDot = dots.locator('[data-lane="humanReview"]').first();
  await expect(reviewDot).toBeVisible();

  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);

    const probe = await page.evaluate(() => {
      const root = getComputedStyle(document.documentElement);
      const resolve = (token: string) => {
        // Resolve the token through a throwaway element so color-mix / var
        // chains collapse to a concrete rgb() the same way the dot does.
        const el = document.createElement('span');
        el.style.color = root.getPropertyValue(token).trim();
        document.body.appendChild(el);
        const out = getComputedStyle(el).color;
        el.remove();
        return out;
      };
      return { review: resolve('--lane-human-review'), completed: resolve('--lane-completed') };
    });
    const dotColor = await reviewDot.evaluate(el => getComputedStyle(el).color);

    // The dot IS the Review lane hue.
    expect(rgbToHex(dotColor), `${theme}: dot must be the Review lane hue`)
      .toBe(rgbToHex(probe.review));
    // ...and is NOT the Delivered green it used to borrow.
    expect(rgbToHex(dotColor), `${theme}: green stays reserved for Delivered`)
      .not.toBe(rgbToHex(probe.completed));

    const shot = path.join(resultsDir, `agt-2676--explorer-lane-dots--${theme}--mocked.png`);
    await sidebar.screenshot({ path: shot });
    await test.info().attach(`explorer-lane-dots-${theme}`, { path: shot, contentType: 'image/png' });
  }
});

test('the numbers view mirrors the same board-truthful counts in both themes', async ({ page }) => {
  fs.mkdirSync(resultsDir, { recursive: true });
  await boot(page, 'numbers');

  const sidebar = page.getByTestId('studio-sidebar');
  await expect(page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`)).toHaveText('1');
  await expect(page.getByTestId(`studio-explorer-project-board-count-progress-${PROJECT}`)).toHaveText('1');
  // One human review card and one escalated card share the counter.
  await expect(page.getByTestId(`studio-explorer-project-board-count-human-review-${PROJECT}`)).toHaveText('2');

  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    const shot = path.join(resultsDir, `agt-2676--explorer-lane-numbers--${theme}--mocked.png`);
    await sidebar.screenshot({ path: shot });
    await test.info().attach(`explorer-lane-numbers-${theme}`, { path: shot, contentType: 'image/png' });
  }
});
