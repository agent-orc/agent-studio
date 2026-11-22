import { test, expect, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const resultsDir = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
interface ShellProject { id: string; displayName: string; shortCode: string; workspaceId: string; storageLocation: string }
const projects: ShellProject[] = [
  { id: 'site', displayName: 'Website', shortCode: 'WEB', workspaceId: 'web', storageLocation: 'C:/fixtures/website' },
  { id: 'api', displayName: 'API', shortCode: 'API', workspaceId: 'services', storageLocation: 'C:/fixtures/api' },
];
const matrixStatus = (projectId: string, kind: 'staging' | 'production', tenant: string | null) => ({
  environment: { id: kind, projectId, name: kind, order: kind === 'staging' ? 0 : 1, kind },
  currentRelease: { id: `${projectId}-release`, projectId, version: '2026.10.16.1', commitSha: 'a'.repeat(40),
    artifactDigests: [], processVersion: '1', variablesVersion: '1', cardKeys: [], createdAtUtc: '2026-10-16T10:00:00Z', channel: 'default' },
  currentDeployment: { id: `${projectId}-${kind}-${tenant}`, releaseId: `${projectId}-release`, environmentId: kind,
    state: tenant === 'acme' && kind === 'production' ? 'awaiting-approval' : 'succeeded',
    startedAtUtc: '2026-10-16T11:00:00Z', finishedAtUtc: '2026-10-16T11:30:00Z', approvedBy: null, steps: [], tenant },
  newerRelease: null, cardsSinceLastDeploy: [], health: 'healthy', tenant,
});

/**
 * Answers the shell's own reads (projects, workspaces, board, usage) so a
 * dashboard test only describes its deploy data. Returns false for any other
 * request.
 */
function fulfillShell(route: Route, registered: readonly ShellProject[], watched: readonly ShellProject[] = registered): boolean {
  const url = route.request().url();
  const json = (body: unknown) => { void route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }); return true; };
  if (url.includes('/api/v1/studio/auth/status')) return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
  if (url.includes('/api/v1/management/credential-inventory')) return json({ generatedAt: '2026-10-20T12:00:00Z', items: [] });
  if (url.includes('/api/watch-paths')) return json(watched.map(project => ({ name: project.displayName, path: project.storageLocation })));
  if (new URL(url).pathname === '/api/v1/projects') return json(registered);
  if (url.includes('/quota/admission')) return json({
    workspaceId: new URL(url).pathname.split('/')[3],
    workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false,
      codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  });
  if (url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) {
    const ids = [...new Set(registered.map(project => project.workspaceId))];
    return json(ids.map((id, index) => ({
      id, displayName: id.charAt(0).toUpperCase() + id.slice(1), sortOrder: index, isDefault: index === 0, color: null,
      createdAt: '2026-10-16T00:00:00Z', projects: registered.filter(project => project.workspaceId === id),
    })));
  }
  if (url.includes('/api/v1/studio/board')) return json({
    backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], review: [], completed: [], archive: [],
    ready: [], progress: [], humanReview: [], escalated: [],
  });
  if (url.includes('/api/tasks/archive')) return json({ items: [], total: 0, offset: 0, limit: 50 });
  if (/\/api\/projects\/[^/]+\/workbenches/.test(new URL(url).pathname)) return json({
    projectName: decodeURIComponent(new URL(url).pathname.split('/')[3]), includesHistory: true, count: 0, items: [],
  });
  if (new URL(url).pathname === '/api/workbenches') return json({
    projectName: null, count: 0, currentCount: 0, historyCount: 0, items: [],
  });
  if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json([]);
  if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
  if (url.includes('/api/environment')) return json({ isDev: false, devTools: {} });
  if (url.includes('/api/usage/cockpit')) return json({ workspaceId: registered[0]?.workspaceId ?? 'web', clis: [], cost: null });
  if (url.includes('/api/cli/usage')) return json({ at: '2026-10-16T00:00:00Z', sessions: [] });
  if (url.includes('/api/cli/quota')) return json({ at: '2026-10-16T00:00:00Z', ttlSeconds: 600, snapshots: [] });
  return false;
}

test('workspace deployment matrix filters and opens project deployment in both themes', async ({ page }) => {
  fs.mkdirSync(resultsDir, { recursive: true });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'deployment-dashboard' }], activeKey: 'deployment-dashboard',
  })));
  await page.context().route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/deploy/delivery-lanes')) {
      const website = decodeURIComponent(url).includes('/Website/');
      const projectId = website ? 'Website' : 'API';
      return json({ projectId, lanes: [
        { environmentId: null, name: 'Waiting for release', order: -1, cards: [] },
        { environmentId: 'dev', name: 'Dev', order: 0, cards: website ? [{ key: 'WEB-1', title: 'Ship landing page', releaseVersion: '2026.10.20.1', deployedAtUtc: '2026-10-20T12:00:00Z' }] : [] },
        { environmentId: 'stage', name: 'Stage', order: 1, cards: [] },
        { environmentId: 'production', name: 'Production', order: 2, cards: [] },
      ] });
    }
    if (url.includes('/api/deploy/matrix')) return json({ environmentKinds: ['staging', 'production'], projects: [
      { projectId: 'Website', error: null, statuses: ['staging', 'production'].map(kind => matrixStatus('Website', kind as 'staging' | 'production', null)),
        tenantStatuses: ['staging', 'production'].map(kind => matrixStatus('Website', kind as 'staging' | 'production', 'acme')) },
      { projectId: 'API', error: null, statuses: ['staging', 'production'].map(kind => matrixStatus('API', kind as 'staging' | 'production', null)), tenantStatuses: [] },
    ] });
    if (fulfillShell(route, projects)) return;
    return json([]);
  });
  await page.setViewportSize({ width: 1500, height: 900 });
  await page.goto('/');
  const dashboard = page.getByTestId('deployment-dashboard');
  await expect(dashboard).toBeVisible();
  await expect(dashboard.getByRole('columnheader')).toHaveCount(3);
  await expect(dashboard.getByRole('row')).toHaveCount(4);
  await expect(dashboard.getByRole('row', { name: /Website.*acme/ })).toBeVisible();
  await expect(dashboard.getByText(/Release .* · default/).first()).toBeVisible();
  await expect(dashboard.getByTestId('delivery-card-WEB-1')).toBeVisible();
  await expect(dashboard.getByRole('button', { name: 'Delivery to Stage 0' })).toHaveCount(2);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await dashboard.getByTestId('delivery-card-WEB-1').scrollIntoViewIfNeeded();
    const shot = path.join(resultsDir, `agt-3277-delivery-dashboard-${width}.png`);
    await page.screenshot({ path: shot, fullPage: true });
  }
  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    const shot = path.join(resultsDir, `agt-3261-deployment-dashboard-${theme}.png`);
    await dashboard.screenshot({ path: shot });
    await test.info().attach(`deployment-dashboard-${theme}`, { path: shot, contentType: 'image/png' });
  }
  await dashboard.getByTestId('deployment-group-filter').selectOption('web');
  await dashboard.getByTestId('deployment-environment-filter').selectOption('production');
  await dashboard.getByTestId('deployment-tenant-filter').selectOption('acme');
  await expect(dashboard.getByRole('row')).toHaveCount(2);
  await expect(dashboard.getByRole('columnheader')).toHaveCount(2);
  await expect(dashboard.getByText('Awaiting approval')).toBeVisible();
  await dashboard.getByRole('button', { name: 'Website acme Production deployment' }).click();
  await expect(page.getByTestId('studio-tab-hub:Website')).toBeVisible();
});

// AGT-3949: the owner's report of 17 November 2026. The instance deploys
// itself to Production, one watched project declares no environments, one
// registered project is not watched by this instance, and one column is long.
test('dashboard shows the Studio instance, undeclared projects, watched lanes only and a bounded long column', async ({ page }) => {
  fs.mkdirSync(resultsDir, { recursive: true });
  const registered: ShellProject[] = [
    { id: 'PROJ-002', displayName: 'Agent Studio', shortCode: 'AGT', workspaceId: 'studio', storageLocation: '/srv/projects/agent-studio' },
    { id: 'PROJ-001', displayName: 'Runbook', shortCode: 'RUN', workspaceId: 'studio', storageLocation: '/srv/projects/runbook' },
    { id: 'PROJ-003', displayName: 'Handbook', shortCode: 'HBK', workspaceId: 'studio', storageLocation: '/srv/projects/handbook' },
    { id: 'PROJ-004', displayName: 'Website', shortCode: 'WEB', workspaceId: 'studio', storageLocation: '/srv/projects/website' },
  ];
  const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();
  const commit = '5faca674a'.padEnd(40, '0');
  const instance = {
    environment: { id: 'studio-instance', projectId: 'Agent Studio', name: 'Production', order: 0, kind: 'production', protected: true, automatic: false },
    currentRelease: { id: 'instance-1', projectId: 'Agent Studio', version: '0.9.5-5faca674a', commitSha: commit,
      artifactDigests: ['server@0.9.5-5faca674a'], processVersion: 'instance', variablesVersion: 'instance', cardKeys: [],
      createdAtUtc: hoursAgo(3), channel: 'default' },
    currentDeployment: { id: 'deploy-20261117', releaseId: 'instance-1', environmentId: 'studio-instance', state: 'succeeded',
      startedAtUtc: hoursAgo(3.2), finishedAtUtc: hoursAgo(3), approvedBy: null, steps: [], tenant: null },
    newerRelease: null, cardsSinceLastDeploy: ['AGT-3949'], health: 'healthy', releaseUncertain: false, tenant: null,
    instance: { liveSinceUtc: hoursAgo(3), lastRun: { id: 'deploy-20261117', state: 'succeeded', version: '0.9.5-5faca674a',
      commitSha: commit, startedAtUtc: hoursAgo(3.2), atUtc: hoursAgo(3), step: 'host:runner-1' } },
  };
  // What the lanes route answers for the source project: the cards the live commit
  // contains in the instance lane, the cards integrated after it as waiting.
  const liveCard = (index: number) => ({ key: `AGT-${3948 - index}`, title: `Delivered card ${index + 1}`,
    releaseVersion: '0.9.5-5faca674a', deployedAtUtc: '2026-11-17T03:35:00Z' });
  const lanes: Record<string, unknown> = {
    'Agent Studio': { projectId: 'Agent Studio', lanes: [
      { environmentId: null, name: 'Waiting for release', order: -1, cards: [
        { key: 'AGT-3949', title: 'Workspace deployment dashboard data', releaseVersion: null, deployedAtUtc: null },
        { key: 'AGT-3954', title: 'Structure from observed hosts', releaseVersion: null, deployedAtUtc: null },
      ] },
      { environmentId: 'studio-instance', name: 'Production', order: 0, cards: Array.from({ length: 144 }, (_, index) => liveCard(index)) },
    ] },
    Handbook: { projectId: 'Handbook', lanes: [
      { environmentId: null, name: 'Waiting for release', order: -1, cards: [{ key: 'HBK-7', title: 'Glossary page', releaseVersion: null, deployedAtUtc: null }] },
    ] },
    Website: { projectId: 'Website', lanes: [
      { environmentId: null, name: 'Waiting for release', order: -1, cards: [] },
      { environmentId: 'stage', name: 'Stage', order: 0, cards: [{ key: 'WEB-1', title: 'Ship landing page', releaseVersion: '2026.10.20.1', deployedAtUtc: '2026-10-20T12:00:00Z' }] },
      { environmentId: 'production', name: 'Production', order: 1, cards: [] },
    ] },
  };
  const laneRequests: string[] = [];
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'deployment-dashboard' }], activeKey: 'deployment-dashboard',
  })));
  await page.context().route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/deploy/delivery-lanes')) {
      const name = decodeURIComponent(new URL(url).pathname.split('/')[3]);
      laneRequests.push(name);
      // The backend answers 404 for a project this instance does not watch.
      return lanes[name] ? json(lanes[name]) : json({ error: `Unknown project '${name}'` }, 404);
    }
    if (url.includes('/api/deploy/matrix')) return json({ environmentKinds: ['staging', 'production'], projects: [
      { projectId: 'Agent Studio', error: null, environmentsDeclared: false, statuses: [instance], tenantStatuses: [] },
      { projectId: 'Handbook', error: null, environmentsDeclared: false, statuses: [], tenantStatuses: [] },
      { projectId: 'Website', error: null, environmentsDeclared: true, tenantStatuses: [],
        statuses: ['staging', 'production'].map(kind => matrixStatus('Website', kind as 'staging' | 'production', null)) },
    ] });
    // The shell lists all four projects; Runbook is registered but not in this instance's matrix.
    if (fulfillShell(route, registered)) return;
    return json([]);
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/');
  const dashboard = page.getByTestId('deployment-dashboard');
  await expect(dashboard).toBeVisible();

  // 1. The instance is the Production cell of its source project; it has no staging.
  const studio = dashboard.getByTestId('deployment-project-PROJ-002');
  const production = studio.getByRole('button', { name: 'Agent Studio Production deployment' });
  await expect(production).toContainText('Release 0.9.5-5faca674a');
  await expect(production).toContainText('Live 3h ago');
  await expect(production.getByTestId('deployment-dashboard-state')).toContainText('Succeeded');
  await expect(production.getByTestId('deployment-dashboard-health')).toContainText('Healthy');
  await expect(production.getByTestId('deployment-dashboard-instance')).toHaveText('This Studio instance · commit 5faca674a');
  await expect(studio).toContainText('No staging environment');

  // 2. A watched project without declarations is listed with the way to declare them.
  const undeclared = dashboard.getByTestId('deployment-undeclared-PROJ-003');
  await expect(undeclared).toContainText('No environments declared');
  await expect(undeclared.getByRole('link', { name: 'How to declare environments' }))
    .toHaveAttribute('href', /docs\/concepts\/deployment-first-class\.md/);
  await expect(dashboard.getByTestId('deployment-project-PROJ-004')).toContainText('Release 2026.10.16.1');

  // 3. The registered but unwatched project has no row, no lane request and no error section.
  await expect(dashboard.getByTestId('delivery-lanes')).toHaveCount(3);
  await expect(dashboard.getByTestId('delivery-card-HBK-7')).toBeVisible();
  await expect(dashboard.getByTestId('deployment-project-PROJ-001')).toHaveCount(0);
  await expect(dashboard.getByText('Runbook')).toHaveCount(0);
  await expect(dashboard.getByText('could not be loaded')).toHaveCount(0);
  expect(new Set(laneRequests)).toEqual(new Set(['Agent Studio', 'Handbook', 'Website']));

  // 4. The long column shows its count, twelve cards and the control.
  const long = dashboard.getByTestId('delivery-lane-Agent Studio-studio-instance');
  const toggle = dashboard.getByTestId('delivery-lane-toggle-Agent Studio-studio-instance');
  await expect(long.getByRole('button', { name: 'Delivery to Production 144' })).toBeVisible();
  await expect(long.getByTestId(/^delivery-card-/)).toHaveCount(12);
  await expect(long).toContainText('12 of 144 shown');
  await expect(toggle).toHaveText('Show all 144');
  await toggle.click();
  await expect(long.getByTestId(/^delivery-card-/)).toHaveCount(144);
  await expect(toggle).toHaveText('Show first 12');
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await toggle.click();
  await expect(long.getByTestId(/^delivery-card-/)).toHaveCount(12);
  await expect(dashboard.getByTestId('delivery-lane-toggle-Agent Studio-waiting')).toHaveCount(0);

  // Tall viewports, so one screenshot per theme and width holds the table and the lanes.
  // On the phone the table scrolls sideways; a second shot shows its Production column.
  const shoot = async (name: string) => {
    const shot = path.join(resultsDir, `agt-3949-deployment-dashboard-${name}.png`);
    await dashboard.screenshot({ path: shot });
    await test.info().attach(`agt-3949-deployment-dashboard-${name}`, { path: shot, contentType: 'image/png' });
  };
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    for (const [width, height] of [[1280, 2600], [390, 3000]] as const) {
      await page.setViewportSize({ width, height });
      await dashboard.evaluate(element => { element.scrollTop = 0; });
      await expect(long.getByTestId(/^delivery-card-/)).toHaveCount(12);
      await shoot(`${theme}-${width}`);
      if (width === 390) {
        await production.scrollIntoViewIfNeeded();
        await expect(production).toBeInViewport();
        await dashboard.evaluate(element => { element.scrollTop = 0; });
        await shoot(`${theme}-${width}-production`);
        await studio.getByRole('rowheader').scrollIntoViewIfNeeded();
      }
    }
  }
});
