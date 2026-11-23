import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Studio';
const STORAGE = '/srv/tasks/studio';
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'post-merge-window');
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function openOperations(page: Page): Promise<void> {
  await page.clock.setFixedTime(new Date('2026-11-22T04:00:00Z'));
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'operations' }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT });
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill(json({
    negotiateVersion: 1, connectionId: 'security-mock', connectionToken: 'security-mock',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  })));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([{ name: PROJECT, path: STORAGE, rootPath: '/srv/repo/studio' }])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    review: [], autoReview: [], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/environment', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route(/\/workbenches(\?|$)/, route => route.fulfill(json({ items: [] })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({}, 503)));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  const project = {
    sourceType: 'local-folder', id: 'PROJ-STU', displayName: PROJECT, shortCode: 'STU', workspaceId: 'ws',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
    repositoryPath: '/srv/repo/studio', rootPath: '/srv/repo/studio', urls: [], ownershipMappings: [],
    archived: false, createdAt: '2026-10-01T00:00:00Z',
  };
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws', displayName: 'Workspace', projects: [project] }])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([project])));
  // The other panels of the Operations area expect their object shapes, not the catch-all empty list.
  await page.route(url => url.pathname === '/api/v1/operations/structure', route => route.fulfill(json({ nodes: [], links: [] })));
  await page.route('**/api/v1/operations/structure/library', route => route.fulfill(json({ registries: [], consumers: [] })));
  await page.route('**/api/diagnostics/api-stalls**', route => route.fulfill(json({
    status: 'ok', intervalMs: 1000, maxDelayMs: 0, alarmThresholdMs: 1000, alarmWindowMinutes: 5, recent: [] })));
  await page.route(`**/api/projects/${PROJECT}/environments`, route => route.fulfill(json({
    project: PROJECT, environments: [], promotions: [], failureCards: [] })));
  await page.route(`**/api/projects/${PROJECT}/deploy/operations`, route => route.fulfill(json({
    project: PROJECT, since: '2026-10-10T00:00:00Z', until: '2026-11-22T04:00:00Z', environments: [],
    dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null, cardsMeasured: 0,
      changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
  })));
  await page.route('**/api/develop-states', route => route.fulfill(json([{ project: PROJECT, developState: 'green',
    pendingWindow: { starved: true, openedAtUtc: '2026-11-22T02:00:00Z', admissionReason: '2 occupied slots, load 0.10' },
  }])));
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
    .forEach(element => ((element as HTMLElement).style.display = 'none')));
  await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 60_000 });
}

test('Operations exposes overdue post-merge admission and clears it after refresh', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await openOperations(page);
  const alert = page.getByTestId('operations-starved-window');
  await expect(alert).toContainText('Post-merge tests overdue');
  await expect(alert).toContainText('2 occupied slots');
  fs.mkdirSync(results, { recursive: true });
  for (const [width, height] of [[1280, 900], [390, 844]] as const) {
    await page.setViewportSize({ width, height });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await alert.scrollIntoViewIfNeeded();
      await expect(alert).toBeVisible();
      await page.screenshot({ path: path.join(results, `post-merge-${theme}-${width}.png`) });
    }
  }
  await page.route('**/api/develop-states', route => route.fulfill(json([{ project: PROJECT, pendingWindow: null }])));
  await page.getByTestId('project-operations').getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(alert).toHaveCount(0);
});
