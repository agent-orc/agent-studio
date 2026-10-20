import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const resultsDir = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const projects = [
  { id: 'site', displayName: 'Website', shortCode: 'WEB', workspaceId: 'web', storageLocation: 'C:/fixtures/website' },
  { id: 'api', displayName: 'API', shortCode: 'API', workspaceId: 'services', storageLocation: 'C:/fixtures/api' },
];

test('workspace deployment matrix filters and opens project deployment in both themes', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-20T09:00:00Z') });
  const consoleErrors: string[] = [];
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
  page.on('pageerror', error => consoleErrors.push(error.message));
  page.on('requestfailed', request => consoleErrors.push(`request failed: ${request.url()}`));
  page.on('response', response => { if (response.status() >= 400) consoleErrors.push(`HTTP ${response.status()}: ${response.url()}`); });
  fs.mkdirSync(resultsDir, { recursive: true });
  await page.route('**/favicon.ico', route => route.fulfill({ status: 204, body: '' }));
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'ok' }));
  await page.route('**/update/status**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ available: false }) }));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      negotiateVersion: 1, connectionId: 'deployment-dashboard-e2e', connectionToken: 'deployment-dashboard-e2e',
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    }),
  }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'deployment-dashboard' }], activeKey: 'deployment-dashboard',
  })));
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/v1/studio/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/watch-paths')) return json(projects.map(project => ({ name: project.displayName, path: project.storageLocation })));
    if (new URL(url).pathname === '/api/v1/projects') return json(projects);
    if (url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) return json([
      { id: 'web', displayName: 'Web', sortOrder: 0, isDefault: true, color: null, createdAt: '2026-10-16T00:00:00Z', projects: [projects[0]] },
      { id: 'services', displayName: 'Services', sortOrder: 1, isDefault: false, color: null, createdAt: '2026-10-16T00:00:00Z', projects: [projects[1]] },
    ]);
    if (url.includes('/api/v1/studio/board')) return json({
      backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [],
      codeNotComplete: [], autoReview: [], review: [], completed: [], archive: [],
      ready: [], progress: [], humanReview: [], escalated: [],
    });
    if (url.includes('/api/tasks/archive')) return json({ items: [], total: 0, offset: 0, limit: 50 });
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (url.includes('/api/usage/cockpit')) return json({ workspaceId: 'web', clis: [], cost: null });
    if (url.includes('/api/v1/management/credential-inventory')) return json({ generatedAt: '2026-10-20T09:00:00Z', items: [] });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-10-20T09:00:00Z', sessions: [] });
    if (url.includes('/api/crash-recovery/pending')) return json({ pending: [] });
    if (url.includes('/api/runner/queue-starvation')) return json({ active: false, waitingTaskCount: 0, availableSlots: 0,
      thresholdMinutes: 15, oldestEnteredLaneAt: null, observedAt: '2026-10-20T09:00:00Z', items: [] });
    if (url.includes('/api/pipeline/accepted-integration-alert')) return json({ active: false, stalledTaskCount: 0,
      thresholdMinutes: 30, oldestAcceptedAt: null, observedAt: '2026-10-20T09:00:00Z', items: [] });
    if (url.includes('/api/environment')) return json({ isDev: false, devTools: {} });
    if (url.includes('/api/usage/cockpit')) return json({ workspaceId: 'web', clis: [], cost: null });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-10-16T00:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json({ at: '2026-10-16T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/workbenches')) return json({ projectName: 'Website', items: [] });
    return json([]);
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const dashboard = page.getByTestId('deployment-dashboard');
  await expect(dashboard).toBeVisible();
  await expect(dashboard.getByRole('columnheader')).toHaveCount(3);
  await expect(dashboard.getByRole('row')).toHaveCount(3);
  await expect(dashboard.getByText('Sample data until deployment API is available')).toBeVisible();
  await dashboard.getByTestId('deployment-group-filter').selectOption('unassigned');
  await expect(dashboard.getByText('No projects in this group.')).toBeVisible();
  await dashboard.getByTestId('deployment-group-filter').selectOption('all');
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    if (width === 390) {
      await expect.poll(() => dashboard.locator('table').evaluate(table =>
        table.parentElement!.scrollWidth <= table.parentElement!.clientWidth)).toBe(true);
    }
    const shot = path.join(resultsDir, `agt-3200-deployment-dashboard-${width}.png`);
    await dashboard.screenshot({ path: shot });
    await test.info().attach(`deployment-dashboard-${width}`, { path: shot, contentType: 'image/png' });
  }
  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    const shot = path.join(resultsDir, `agt-3200-deployment-dashboard-${theme}.png`);
    await dashboard.screenshot({ path: shot });
    await test.info().attach(`deployment-dashboard-${theme}`, { path: shot, contentType: 'image/png' });
  }
  await dashboard.getByTestId('deployment-group-filter').selectOption('web');
  await dashboard.getByTestId('deployment-environment-filter').selectOption('production');
  await expect(dashboard.getByRole('row')).toHaveCount(2);
  await expect(dashboard.getByRole('columnheader')).toHaveCount(2);
  await dashboard.getByRole('button', { name: 'Website Production deployment' }).click();
  await expect(page.getByTestId('studio-tab-hub:Website')).toBeVisible();
  expect(consoleErrors).toEqual([]);
});
