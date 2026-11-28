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
  await dashboard.getByText('1 project has no deployment target').click();
  await expect(undeclared).toHaveText('Handbook');
  await expect(dashboard.getByRole('link', { name: 'How to declare environments' }))
    .toHaveAttribute('href', /docs\/concepts\/deployment-first-class\.md/);
  await expect(dashboard.getByTestId('deployment-project-PROJ-004')).toContainText('Release 2026.10.16.1');

  // 3. The registered but unwatched project has no row, no lane request and no error section.
  await expect(dashboard.getByTestId('delivery-lanes')).toHaveCount(2);
  await expect(dashboard.getByTestId('delivery-card-HBK-7')).toHaveCount(0);
  await expect(dashboard.getByTestId('deployment-project-PROJ-001')).toHaveCount(0);
  await expect(dashboard.getByText('Runbook')).toHaveCount(0);
  await expect(dashboard.getByText('could not be loaded')).toHaveCount(0);
  expect(new Set(laneRequests)).toEqual(new Set(['Agent Studio', 'Website']));

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

// The matrix, readiness and journal use their wire contracts. Every environment in this fixture is observed.
test('production, waiting deliveries and history are visible without declarations', async ({ page }) => {
  const live = '497d40ebb'.padEnd(40, 'a');
  const prior = '772fc4c0d'.padEnd(40, 'b');
  const since = '2026-11-27T17:47:00Z';
  const shell = Array.from({ length: 16 }, (_, i) => ({ id: 'PROJ-' + String(i + 1).padStart(3, '0'),
    displayName: ['Agent Studio', 'Handbook'][i] ?? 'Project ' + (i + 1), workspaceId: 'studio', shortCode: 'P' + i,
    storageLocation: '/workspace/project-' + i }));
  const components = [
    ['api', 'Studio API', 'studio-server'], ['web', 'Web frontend', 'studio-server'],
    ['engine', 'Orchestrator engine', 'studio-server'], ['coding', 'Coding host', 'studio-server'],
    ['review', 'Review host', 'workstation-vm'], ['gate', 'Gate worker', 'workstation-vm'],
  ].map(([id, name, machine]) => ({ id, name, machine, version: '0.9.5', commit: live, health: 'Reporting', seenAtUtc: since }));
  const installation = { version: '0.9.5', commit: live, sinceUtc: since, timeBasis: 'deploy', components,
    branch: 'develop', tip: 'e7c812356'.padEnd(40, 'd'), verification: { state: 'green', source: 'Post-merge run 81 (green)', atUtc: since },
    waiting: [
      { key: 'AGT-4251', title: 'Show the host that runs each component', area: 'Observation', flags: ['Interface only'], integratedAtUtc: since },
      { key: 'AGT-4254', title: 'Record the outcome of each deployment', area: 'Delivery chain', flags: ['Changes stored data'], integratedAtUtc: since },
    ], detail: null, history: [] };
  const records = [
    { id: 'switch', startedAtUtc: '2026-11-27T17:31:00Z', endedAtUtc: since,
      fromCommit: prior, toCommit: live, outcome: 'succeeded', cardsShipped: ['AGT-4244'],
      steps: [{ name: 'Server switch', startedAtUtc: '2026-11-27T17:45:00Z', endedAtUtc: since, outcome: 'succeeded' }] },
    { id: 'rollback', startedAtUtc: '2026-11-27T13:48:31Z', endedAtUtc: '2026-11-27T14:06:47Z',
      fromCommit: prior, toCommit: 'a4a9f1c54'.padEnd(40, 'c'), outcome: 'rolled-back', cardsShipped: [], steps: [] },
  ];
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.clock.setFixedTime(new Date('2026-11-28T00:00:00Z'));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'deployments', connectionToken: 'deployments',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => socket.onMessage(message => {
    if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
  }));
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'deployment-dashboard' }], activeKey: 'deployment-dashboard',
  })));
  await page.context().route('**/api/**', route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({ json: body });
    if (url.pathname.endsWith('/deploy/matrix')) return json({ hasDeployRecords: true, environmentKinds: [],
      projects: shell.map((project, i) => ({ projectId: project.displayName, environmentsDeclared: false, error: null,
        statuses: [], tenantStatuses: [], installation: i === 0 ? installation : null,
        urls: i === 1 ? [{ projectId: project.id, id: 'docs', name: 'Public handbook', url: 'https://handbook.example' }] : [] })) });
    if (url.pathname === '/api/operations/deploy-records') return json({ items: records, total: 2 });
    if (url.pathname.endsWith('/urls/docs/readiness')) return json({ kind: 'healthy', statusCode: 200, commit: 'ab88dce88' });
    if (fulfillShell(route, shell)) return;
    return json([]);
  });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/#/workspace/deployments');
  const dashboard = page.getByTestId('deployment-dashboard');
  await expect(dashboard.getByRole('heading', { name: 'Production v0.9.5' })).toBeVisible();
  await expect(dashboard.getByText('6 components on 2 machines · All on the same commit')).toBeVisible();
  await expect(dashboard.getByText('AGT-4254', { exact: true })).toBeVisible();
  await expect(dashboard.getByText('Post-merge run 81 (green) · 6 h ago')).toBeVisible();
  await expect(dashboard.getByRole('heading', { name: 'Delivery chain' })).toBeVisible();
  await expect(dashboard.getByText('Changes stored data', { exact: true })).toBeVisible();
  await expect(dashboard.getByText('Commit ab88dce88')).toBeVisible();
  const disclosure = dashboard.getByText('14 projects have no deployment target', { exact: true });
  await expect(dashboard.getByText('Project 16', { exact: true })).toBeHidden();
  await disclosure.focus();
  await page.keyboard.press('Enter');
  await expect(dashboard.getByText('Project 16', { exact: true })).toBeVisible();
  await page.keyboard.press('Enter');
  const attempt = dashboard.getByTestId('deploy-attempt-switch');
  await attempt.locator('summary').focus();
  await page.keyboard.press('Enter');
  await expect(attempt.getByText('Server switch', { exact: false })).toBeVisible();
  await expect(dashboard.getByText('Rolled back', { exact: true })).toBeVisible();
  await attempt.locator('summary').blur();
  fs.mkdirSync(resultsDir, { recursive: true });
  for (const width of [1440, 390]) {
    for (const theme of ['light', 'dark']) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 1310 });
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await dashboard.evaluate(el => { el.scrollTop = 0; });
      await expect(page.getByTestId('offline-banner-state')).toBeHidden();
      if (width === 1440) await expect(disclosure).toBeInViewport();
      expect(await dashboard.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
      await dashboard.screenshot({ path: path.join(resultsDir, `deployments-${width}-${theme}.png`) });
      if (width === 390) {
        await dashboard.getByRole('heading', { name: 'History', exact: true }).evaluate(el => el.scrollIntoView({ block: 'start' }));
        await dashboard.screenshot({ path: path.join(resultsDir, `deployments-history-${width}-${theme}.png`) });
      }
    }
  }
  await dashboard.getByTestId('deployment-environment-filter').selectOption('url:public handbook');
  await expect(dashboard.getByRole('heading', { name: 'This installation' })).toHaveCount(0);
  await expect(disclosure).toHaveCount(0);
  await expect(dashboard.getByText('HTTP 200 · Reachable')).toBeVisible();
  expect(errors).toEqual([]);
});
