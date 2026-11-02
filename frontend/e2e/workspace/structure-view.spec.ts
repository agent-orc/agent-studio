import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const makeNode = (id: string, kind: string, label: string, parentId: string | null, role?: string) => ({
  id, kind, label, parentId, unitId: null, environmentId: null, project: null,
  provenance: 'declared', sourceRef: 'role fixture', role,
});
const nodes = [
  makeNode('b', 'boundary', 'AGT installation', null),
  makeNode('cloud', 'location', 'Cloud provider', 'b'),
  makeNode('site', 'location', 'Operator site', 'b'),
  makeNode('home', 'location', 'Home lab', 'b'),
  makeNode('studio', 'physical', 'Studio machine', 'site'),
  makeNode('runner', 'cloud-server', 'Linux runner host', 'cloud'),
  makeNode('lab', 'physical', 'Home-lab physical machine', 'home'),
  makeNode('vm', 'vm', 'Home-lab VM', 'lab'),
  makeNode('api', 'process', 'Studio backend', 'studio', 'control'),
  makeNode('coding', 'process', 'Coding host', 'runner', 'execution'),
  makeNode('engine', 'container', 'Orchestrator Engine', 'runner', 'control'),
  ...[1, 2, 3].map(i => makeNode(`review-${i}`, 'container', `Review host ${i}`, 'vm', 'execution')),
  makeNode('gate', 'container', 'Build and test gate', 'vm', 'verification'),
  makeNode('github', 'external', 'GitHub', null),
  makeNode('registry', 'external', 'Container registry', null),
  makeNode('models', 'external', 'Model providers', null),
];
const links = [
  { from: 'api', to: 'coding', kind: 'tunnel', label: 'Runner route', sourceRef: 'role fixture' },
  { from: 'coding', to: 'github', kind: 'call', label: 'Git', sourceRef: 'role fixture' },
];

test('declared structure is accessible in both projections, widths and themes', async ({ page }) => {
  fs.mkdirSync(results, { recursive: true });
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
  })));
  await page.context().route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/v1/operations/structure')) return json({ nodes, links });
    if (url.includes('/api/v1/studio/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/v1/management/credential-inventory')) return json({ generatedAt: '2026-11-02T00:00:00Z', items: [] });
    if (url.includes('/api/usage/cockpit')) return json({ workspaceId: 'workspace', clis: [], cost: null });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-11-02T00:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json({ at: '2026-11-02T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/watch-paths') || url.includes('/api/v1/projects') || url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) return json([]);
    if (url.includes('/api/v1/studio/board')) return json({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], autoReview: [], review: [], humanReview: [], escalated: [], completed: [], archive: [] });
    if (url.includes('/api/tasks/archive')) return json({ items: [], total: 0, offset: 0, limit: 50 });
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (url.includes('/api/environment')) return json({ isDev: false, devTools: {} });
    if (url.includes('/api/workbenches')) return json({ projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [] });
    return json([]);
  });
  await page.goto('/#/workspace/operations/structure');
  const view = page.getByTestId('structure-view');
  await expect(view).toBeVisible();
  await expect(view.locator('[data-node-id]')).toHaveCount(14);
  await expect(view.locator('[data-outline-id]')).toHaveCount(14);
  await view.locator('[data-node-id="vm"]').focus();
  await page.keyboard.press('Enter');
  await expect(view.locator('[data-node-id="gate"]')).toBeVisible();
  await page.keyboard.press('1');
  await page.keyboard.press('3');
  for (const projection of ['host', 'container'] as const) {
    await view.getByRole('button', { name: projection === 'host' ? 'Host' : 'Container', exact: true }).click();
    if (projection === 'container') await expect(view.locator('[data-node-id="cloud"]')).toHaveCount(0);
    for (const width of [1440, 1920]) {
      await page.setViewportSize({ width, height: 900 });
      for (const theme of ['light', 'dark'] as const) {
        await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
        await view.screenshot({ path: path.join(results, `structure-${projection}-${width}-${theme}.png`) });
      }
    }
  }
  await view.getByRole('button', { name: 'Host', exact: true }).click();
  await page.keyboard.press('3');
  await expect(view.locator('[data-node-id="studio"]')).toBeVisible();
  await expect(view.locator('[data-node-id="function:control"]')).toHaveCount(0);
  const ids = await view.locator('[data-node-id]').evaluateAll(items => items.map(item => item.getAttribute('data-node-id')));
  const visited = new Set<string>();
  await view.locator('[data-node-id]').first().focus();
  for (let i = 0; i < ids.length; i++) {
    const id = await page.evaluate(() => document.activeElement?.getAttribute('data-node-id'));
    if (id) visited.add(id);
    await page.keyboard.press('Tab');
  }
  expect(visited).toEqual(new Set(ids));
  await expect(view.locator('[data-outline-id]')).toHaveCount(ids.length);
  expect(errors).toEqual([]);
});
