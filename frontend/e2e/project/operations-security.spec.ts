import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';
import { SECURITY_POSTURE_FIXTURE_NOW, securityPostureFixture } from '../../src/app/features/project-detail/testing/security-posture-fixtures';

/**
 * Security section of the Operations page on a fully mocked backend
 * (AGT-3575). GET /api/security/posture answers the shared fixture; every
 * other API answers an empty list. Screenshots in both themes at 1280 and
 * 390 px go to JOB_RESULTS_DIR when it is set.
 */

const PROJECT = 'Studio';
const STORAGE = '/srv/tasks/studio';
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'operations-security');
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function openOperations(page: Page, posture: { status: number; body: unknown }): Promise<void> {
  await page.clock.setFixedTime(new Date(SECURITY_POSTURE_FIXTURE_NOW));
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
    project: PROJECT, since: '2026-10-10T00:00:00Z', until: SECURITY_POSTURE_FIXTURE_NOW, environments: [],
    dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null, cardsMeasured: 0,
      changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
  })));
  await page.route('**/api/security/posture', route => route.fulfill(json(posture.body, posture.status)));
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
    .forEach(element => ((element as HTMLElement).style.display = 'none')));
  await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 60_000 });
}

test.describe('Operations security section', () => {
  test('ranks entries, highlights what needs action soon, and renders in both themes at 1280 and 390 px', async ({ page }) => {
    fs.mkdirSync(results, { recursive: true });
    await page.setViewportSize({ width: 1280, height: 900 });
    await openOperations(page, { status: 200, body: securityPostureFixture() });
    const section = page.getByTestId('operations-security');
    await expect(section).toBeVisible({ timeout: 30_000 });

    const ranks = section.getByTestId('operations-security-findings').locator('.operations-security__rank');
    await expect(ranks).toHaveText(['#1', '#2', '#3', '#4', '#5', '#6']);
    const soon = section.getByTestId('operations-security-action-soon').locator('li');
    await expect(soon).toHaveCount(3);
    await expect(soon.first()).toContainText('Owner has no second factor');
    await expect(section.getByTestId('operations-security-total')).toHaveText('6');
    await expect(section.getByTestId('security-user-u1')).toContainText('No second factor');
    await expect(section.getByTestId('security-host-r1')).toContainText('Below required');

    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    for (const [width, height] of [[1280, 900], [390, 844]] as const) {
      await page.setViewportSize({ width, height });
      // The section must fit the viewport width; wide tables scroll inside their own wrapper.
      const overflow = await section.evaluate(element => element.scrollWidth - element.clientWidth);
      expect(overflow, `${width}px horizontal overflow`).toBeLessThanOrEqual(1);
      const box = await section.boundingBox();
      expect(box!.x + box!.width, `${width}px right edge`).toBeLessThanOrEqual(width + 1);
      // The Operations area scrolls inside the shell; a viewport as tall as the
      // area lets the element screenshot show the whole section unclipped.
      const tall = await page.getByTestId('project-operations-area').evaluate(element => element.scrollHeight);
      await page.setViewportSize({ width, height: Math.min(Math.max(height, tall + 200), 12_000) });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await section.scrollIntoViewIfNeeded();
        await section.screenshot({ path: path.join(results, `operations-security-${theme}-${width}--mocked.png`), animations: 'disabled' });
      }
    }
  });

  test('stays hidden for a caller without security.posture.read', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await openOperations(page, { status: 403, body: { error: 'permission-denied', message: "'security.posture.read' is required." } });
    const answered = page.waitForResponse(response => response.url().endsWith('/api/security/posture'));
    await page.reload({ waitUntil: 'domcontentloaded' });
    expect((await answered).status()).toBe(403);
    await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('operations-security')).toHaveCount(0);
  });
});
