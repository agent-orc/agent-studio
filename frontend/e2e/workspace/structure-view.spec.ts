import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const makeNode = (id: string, kind: string, label: string, parentId: string | null, role?: string) => ({
  id, kind, label, parentId, unitId: null, environmentId: null, project: 'fixture-project',
  provenance: 'declared', sourceRef: 'role fixture', role,
});
const nodes = [
  makeNode('b', 'boundary', 'AGT installation', null),
  makeNode('cloud', 'location', 'Cloud provider', 'b'),
  makeNode('site', 'location', 'Operator site', 'b'),
  makeNode('home', 'location', 'Home lab', 'b'),
  makeNode('studio', 'physical', 'Studio host', 'site'),
  makeNode('runner', 'cloud-server', 'Runner host', 'cloud'),
  makeNode('lab', 'physical', 'Lab host', 'home'),
  makeNode('vm', 'vm', 'Build VM', 'lab'),
  makeNode('api', 'process', 'Studio backend', 'studio', 'control'),
  makeNode('coding', 'process', 'Coding host', 'runner', 'execution'),
  makeNode('engine', 'container', 'Orchestrator Engine', 'runner', 'control'),
  ...[1, 2, 3].map(i => makeNode(`review-${i}`, 'container', `Review host ${i}`, 'vm', 'execution')),
  makeNode('gate', 'container', 'Build and test gate', 'vm', 'verification'),
  makeNode('github', 'external', 'GitHub', null),
  makeNode('registry', 'external', 'Container registry', null),
  makeNode('models', 'external', 'Model providers', null),
  makeNode('managed', 'managed', 'State service', 'b'),
  makeNode('registry-node', 'registry', 'Package registry', null),
  makeNode('package-node', 'package', 'Release package', 'registry-node'),
  { ...makeNode('environment-node', 'environment', 'Production', 'b'), environmentId: 'production' },
  makeNode('unit-node', 'unit', 'Service unit', 'b'),
];
const links = [
  { from: 'api', to: 'coding', kind: 'tunnel', label: 'Runner route', sourceRef: 'role fixture' },
  { from: 'coding', to: 'github', kind: 'call', label: 'Git', sourceRef: 'role fixture' },
];

test('declared structure is accessible in both projections, widths and themes', async ({ page }) => {
  test.setTimeout(180_000);
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
    if (url.includes('/api/diagnostics/api-stalls')) return json({ status: 'ok', intervalMs: 1000, maxDelayMs: 0, alarmThresholdMs: 1000, alarmWindowMinutes: 5, recent: [] });
    if (url.includes('/api/system/execution-infrastructure')) return json({ gate: { host: null, busy: 0, total: 0, reachability: 'unknown', lastReachedAt: null, lastFailureAt: null }, taskServer: 'local monolith', engine: { state: 'not-required', required: false, lastContact: null, lastEngineId: null }, workstation: 'local' });
    if (/\/api\/projects\/[^/]+\/environments(?:\?|$)/.test(url)) return json({ project: 'Fixture project', environments: [], promotions: [], failureCards: [] });
    if (url.includes('/deploy/operations')) return json({ project: 'Fixture project', since: '2026-10-01T00:00:00Z', until: '2026-11-01T00:00:00Z', environments: [], dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null, cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 } });
    if (url.includes('/api/projects/') && url.includes('/workbenches')) return json({ projectName: 'Fixture project', count: 0, currentCount: 0, historyCount: 0, items: [] });
    if (url.includes('/quota/admission')) return json({ workspaceId: 'fixture-workspace', workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false }, projects: [] });
    if (url.includes('/api/v1/studio/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/v1/management/credential-inventory')) return json({ generatedAt: '2026-11-02T00:00:00Z', items: [] });
    if (url.includes('/api/usage/cockpit')) return json({ workspaceId: 'workspace', clis: [], cost: null });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-11-02T00:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json({ at: '2026-11-02T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/watch-paths')) return json([{ name: 'Fixture project', path: '/tmp/fixture-project', rootPath: '/tmp/fixture-project' }]);
    if (url.includes('/api/v1/workspaces')) return json([{ id: 'fixture-workspace', displayName: 'Workspace', projects: [{ id: 'fixture-project', displayName: 'Fixture project', storageLocation: '/tmp/fixture-project' }] }]);
    if (url.includes('/api/v1/projects') || url.includes('/api/workspaces')) return json([]);
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
  await expect(view.locator('[data-node-id]')).toHaveCount(19);
  await expect(view.locator('[data-outline-id]')).toHaveCount(19);
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

  for (const [id, target] of [
    ['studio', 'Host row'], ['runner', 'Host row'], ['vm', 'Host telemetry'], ['api', 'Deploy runs'],
    ['engine', 'Logs'], ['b', 'Deployment dashboard'], ['models', 'Health checks'],
    ['managed', 'Declaration'], ['registry-node', 'Publish panel'], ['package-node', 'Release page'],
    ['environment-node', 'Deployment dashboard'], ['unit-node', 'Publish panel'], ['cloud', 'Deployment dashboard'],
  ] as const) {
    const destinationPage = await page.context().newPage();
    await destinationPage.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'structure' }], activeKey: 'structure',
    })));
    await destinationPage.goto('/#/workspace/operations/structure');
    const diagram = destinationPage.getByTestId('structure-view');
    await expect(diagram).toBeVisible();
    await diagram.locator(`[data-outline-id="${id}"]`).click();
    const destination = diagram.getByTestId('structure-targets').getByRole('link', { name: target });
    await expect(destination).toBeVisible();
    const href = await destination.getAttribute('href');
    expect(href).toBeTruthy();
    await destination.click();
    await expect.poll(() => new URL(destinationPage.url()).hash.toLowerCase())
      .toBe(new URL(href!, destinationPage.url()).hash.toLowerCase());
    await destinationPage.close();
  }
  await expect(view.getByTestId('structure-link-0')).toHaveAttribute('href', /host=studio&link=tunnel/);
  await view.getByTestId('structure-link-0').click();
  await expect.poll(() => new URL(page.url()).hash).toContain('/workspace/settings/environment?host=studio&link=tunnel');

  await page.goto('/#/workspace/operations/structure?view=container&level=3&overlay=drift&env=production&focus=engine');
  await page.reload();
  await expect(view.getByRole('button', { name: 'Container', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(view.getByRole('button', { name: '3', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(view.getByTestId('structure-overlay')).toHaveValue('drift');
  await expect(view.getByTestId('structure-environment')).toHaveValue('production');
  await expect(view.getByTestId('structure-targets')).toContainText('Orchestrator Engine');
  await page.route('**/api/watch-paths**', route => route.fulfill({ json: [{ name: 'Fixture project', path: '/tmp/fixture-project', rootPath: '/tmp/fixture-project' }] }));
  await page.route('**/api/v1/workspaces**', route => route.fulfill({ json: [{ id: 'fixture-workspace', displayName: 'Workspace', projects: [{ id: 'fixture-project', displayName: 'Fixture project', storageLocation: '/tmp/fixture-project' }] }] }));
  await page.goto('/#/projects/fixture-project/operations/structure?view=host&level=2&overlay=health&focus=studio');
  await expect(page.getByTestId('structure-view')).toBeVisible();
  await expect(page.getByTestId('structure-targets')).toContainText('Studio host');
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await page.getByTestId('structure-view').screenshot({ path: path.join(results, `project-structure-${theme}.png`) });
  }
  await page.goto('/#/projects/fixture-project/operations');
  const projectSection = page.getByTestId('project-operations-area').getByTestId('structure-view');
  await expect(projectSection.locator('[data-node-id="studio"]')).toBeVisible();
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await projectSection.screenshot({ path: path.join(results, `project-operations-structure-${theme}.png`) });
  }
  const settingsPage = await page.context().newPage();
  await settingsPage.setViewportSize({ width: 1920, height: 1400 });
  await settingsPage.goto('/#/workspace/settings/environment');
  await settingsPage.getByRole('button', { name: 'Map', exact: true }).click();
  await expect(settingsPage.getByTestId('structure-view').locator('[data-node-id="studio"]')).toBeVisible();
  await expect(settingsPage.locator('app-environment-map')).toHaveCount(0);
  for (const theme of ['light', 'dark'] as const) {
    await settingsPage.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await settingsPage.getByTestId('structure-view').screenshot({ path: path.join(results, `settings-structure-${theme}.png`) });
  }
  await settingsPage.close();
});
