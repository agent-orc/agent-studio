import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const results = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const chatVersion = JSON.parse(fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8'))
  .dependencies['coding-agent-chat'] as string;
const makeNode = (id: string, kind: string, label: string, parentId: string | null, role?: string) => ({
  id, kind, label, parentId, unitId: null, environmentId: null, project: null,
  provenance: 'declared', sourceRef: 'role fixture', role,
});
const nodes = [
  makeNode('b', 'boundary', 'AGT installation', null),
  makeNode('cloud', 'location', 'Cloud provider', 'b'),
  makeNode('site', 'location', 'Operator site', 'b'),
  makeNode('home', 'location', 'Home lab', 'b'),
  makeNode('studio', 'physical', 'Studio placement', 'site'),
  makeNode('runner', 'cloud-server', 'Runner placement', 'cloud'),
  makeNode('lab', 'physical', 'Gate placement', 'home'),
  makeNode('vm', 'vm', 'Verification VM role', 'lab'),
  makeNode('api', 'process', 'Studio backend', 'studio', 'control'),
  makeNode('coding', 'process', 'Coding host', 'runner', 'execution'),
  makeNode('engine', 'container', 'Orchestrator Engine', 'runner', 'control'),
  ...[1, 2, 3].map(i => makeNode(`review-${i}`, 'container', `Review host ${i}`, 'vm', 'execution')),
  makeNode('gate', 'container', 'Build and test gate', 'vm', 'verification'),
  makeNode('github', 'external', 'GitHub', null),
  makeNode('registry', 'external', 'Container registry', null),
  makeNode('models', 'external', 'Model providers', null),
  makeNode('product', 'system', 'Product', 'b'),
  makeNode('web-placement', 'vm', 'Web placement', 'b'),
  { ...makeNode('website', 'process', 'Website process', 'web-placement'), project: 'PROJ-002', ownerLabel: 'Website', systemBoundaryId: 'product' },
  { ...makeNode('agent', 'process', 'Run-target agent', 'web-placement'), project: 'PROJ-001', ownerLabel: 'AGT', systemBoundaryId: 'product' },
  { ...makeNode('channel', 'registry', 'npm channel', 'runner'), project: 'PROJ-003', ownerLabel: 'Library' },
];
const links = [
  { from: 'api', to: 'coding', kind: 'tunnel', label: 'Runner route', sourceRef: 'role fixture' },
  { from: 'coding', to: 'github', kind: 'call', label: 'Git', sourceRef: 'role fixture' },
  { from: 'website', to: 'channel', kind: 'artifact', label: 'Package', sourceRef: 'role fixture' },
  { from: 'website', to: 'github', kind: 'call', label: 'Shared external', sourceRef: 'role fixture' },
];
const partialNodes = [
  { ...makeNode('boundary', 'boundary', 'Product', null), environmentId: 'production' },
  makeNode('web-placement', 'vm', 'Web placement', 'boundary'),
  makeNode('runner-placement', 'physical', 'Runner placement', 'boundary'),
  { ...makeNode('website', 'process', 'Website process', 'web-placement'), project: 'PROJ-002', ownerLabel: 'Website' },
  { ...makeNode('agent', 'process', 'Run-target agent', 'web-placement'), project: 'PROJ-001', ownerLabel: 'AGT', otherProject: true },
  { ...makeNode('channel', 'registry', 'npm channel', 'runner-placement'), project: 'PROJ-003', ownerLabel: 'Library', otherProject: true },
  makeNode('external', 'external', 'Shared external', null),
];
const partialLinks = [
  { from: 'website', to: 'channel', kind: 'artifact', label: 'Package', sourceRef: 'role fixture' },
  { from: 'website', to: 'external', kind: 'call', label: 'Service', sourceRef: 'role fixture' },
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
    if (url.includes('/api/v1/operations/structure/library')) return json({
      registries: [
        { projectId: 'library', artifact: 'chat-package', provider: 'jfrog', package: 'coding-agent-chat', repository: 'npm-internal', private: true,
          state: 'current', checkedAt: '2026-11-02T00:00:00Z', lastPublishAt: '2026-11-01T00:00:00Z',
          channels: [{ name: 'latest', version: '0.4.1', publishedAt: null }, { name: 'next', version: '0.5.0-rc.2', publishedAt: null }] },
        { projectId: 'library', artifact: 'chat-package', provider: 'npm', package: 'coding-agent-chat', repository: null, private: false,
          state: 'current', checkedAt: '2026-11-02T00:00:00Z', lastPublishAt: '2026-10-15T00:00:00Z',
          channels: [{ name: 'latest', version: '0.4.1', publishedAt: null }, { name: 'next', version: '0.5.0-rc.1', publishedAt: null }] },
        { projectId: 'library', artifact: 'demo-image', provider: 'docker-hub', package: 'chat-demo', repository: null, private: false,
          state: 'unavailable', checkedAt: '2026-11-01T21:00:00Z', lastPublishAt: '2026-10-15T00:00:00Z',
          channels: [{ name: 'latest', version: '0.4.1', publishedAt: null }] },
      ],
      consumers: [
        { projectId: 'studio', project: 'Agent Studio frontend', package: 'coding-agent-chat', version: chatVersion,
          source: 'frontend/package-lock.json', latestStable: '0.4.1', releasesBehind: 0, ownershipMapped: true },
        { projectId: 'website', project: 'Website', package: 'coding-agent-chat', version: '0.3.2',
          source: 'package-lock.json', latestStable: '0.4.1', releasesBehind: 2, ownershipMapped: true },
      ],
    });
    if (url.includes('/api/v1/operations/structure')) return json(new URL(url).searchParams.get('scope') === 'project:Website'
      ? { nodes: partialNodes, links: partialLinks } : { nodes, links });
    if (url.includes('/api/v1/studio/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/v1/management/credential-inventory')) return json({ generatedAt: '2026-11-02T00:00:00Z', items: [] });
    if (url.includes('/api/usage/cockpit')) return json({ workspaceId: 'workspace', clis: [], cost: null });
    if (url.includes('/api/cli/usage')) return json({ at: '2026-11-02T00:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json({ at: '2026-11-02T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/watch-paths')) return json([{ name: 'Website', path: 'role fixture', rootPath: 'role fixture' }]);
    if (url.includes('/api/v1/projects') || url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) return json([]);
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
  const library = page.getByTestId('structure-library');
  await expect(library.getByTestId('structure-registry')).toHaveCount(3);
  await expect(library.getByText('0.5.0-rc.2')).toBeVisible();
  await expect(library.getByText('0.5.0-rc.1')).toBeVisible();
  await expect(library.getByText('Lookup unavailable')).toBeVisible();
  await expect(library.getByText('2 releases behind')).toBeVisible();
  await expect(library.getByText(`coding-agent-chat ${chatVersion}`)).toBeVisible();
  await expect(view.locator('[data-node-id]')).toHaveCount(17);
  await expect(view.locator('[data-outline-id]')).toHaveCount(17);
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
        if (projection === 'host' && width === 1440)
          await library.screenshot({ path: path.join(results, `library-${theme}.png`) });
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
  await page.goto('/#/projects/website/operations/structure');
  await expect(view.getByText('Website partial picture')).toBeVisible();
  await page.keyboard.press('3');
  await expect(view.locator('[data-other-project="true"] [data-node-id]')).toHaveCount(2);
  await expect(view.locator('[data-outline-id][data-other-project="true"]')).toHaveCount(2);
  await expect(view.locator('[data-node-id="agent"]')).toHaveAttribute('aria-label', /other project, AGT/);
  await expect(view.locator('[data-node-id="channel"]')).toHaveAttribute('aria-label', /other project, Library/);
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await view.screenshot({ path: path.join(results, `structure-website-partial-${theme}.png`) });
  }
  expect(errors).toEqual([]);
});
