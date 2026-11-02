import { expect, test, type Page, type Request, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';
import {
  FLOW_FIXTURE_NOW,
  batchSettingsPayload,
  executionInfrastructurePayload,
  flowHealthFixture,
  type FlowFixtureKind,
} from '../../src/app/features/flow-health/testing/flow-health.fixtures';
import {
  DEPLOY_FIXTURE_NOW,
  deployEnvironmentsFixture,
  deployStatusFixture,
  pipelineDefinitionFixture,
} from '../../src/app/features/project-detail/testing/deploy-fixtures';

/**
 * Project flow health (AGT-3399) on a mocked backend. The routes serve the
 * shared wire fixtures of the derivation specs from the real contracts the
 * page joins: gate economics (both windows), cycle time (both windows),
 * pipeline health, the batch report and settings, and the execution
 * infrastructure. Screenshots of the review and gate jams land in the task
 * results folder in both themes.
 */

const PROJECT = 'Website';
const STORAGE = '/srv/tasks/website';
const BASE = `/api/projects/${PROJECT}`;
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'flow-health');
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function mockApp(page: Page, kind: FlowFixtureKind): Promise<void> {
  const data = flowHealthFixture(kind, PROJECT, FLOW_FIXTURE_NOW);
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill(json({
    negotiateVersion: 1, connectionId: 'flow-health-mock', connectionToken: 'flow-health-mock',
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
  await page.route('**/api/watch-paths**', route => route.fulfill(json([{ name: PROJECT, path: STORAGE, rootPath: '/srv/repo/website' }])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    review: [], autoReview: [], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/environment', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route(/\/workbenches(\?|$)/, route => route.fulfill(json({ items: [] })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({}, 503)));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ sessions: [] })));
  const project = {
    sourceType: 'local-folder', id: 'PROJ-WEB', displayName: PROJECT, shortCode: 'WEB', workspaceId: 'ws',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
    repositoryPath: '/srv/repo/website', rootPath: '/srv/repo/website', urls: [], ownershipMappings: [],
    archived: false, createdAt: '2026-10-01T00:00:00Z',
  };
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws', displayName: 'Workspace', projects: [project] }])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([project])));

  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill(json({
    workspaceId: 'ws',
    workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false,
      codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  })));
  // Run steps' sweep block expects an object; this spec does not exercise it.
  await page.route(url => url.pathname === `${BASE}/operator-sweeps`, route => route.fulfill(json({ error: 'not mocked' }, 404)));
  // The Deploy area reads its own sources whichever view is open.
  const emptyPage = { items: [], offset: 0, limit: 25, total: 0 };
  await page.route(url => url.pathname === `${BASE}/environments`, route => route.fulfill(json(deployEnvironmentsFixture())));
  await page.route(url => url.pathname === `${BASE}/environments/health`, route => route.fulfill(json({
    capturedAt: new Date(DEPLOY_FIXTURE_NOW).toISOString(), probeOrigin: 'mock', environments: [],
  })));
  await page.route(url => url.pathname === `${BASE}/deploy/status`, route => route.fulfill(json(deployStatusFixture())));
  await page.route(url => url.pathname.startsWith(`${BASE}/deploy/`) && url.pathname.endsWith('/page'),
    route => route.fulfill(json(emptyPage)));
  await page.route(url => url.pathname === `${BASE}/pipeline-definition`, route => route.fulfill(json(pipelineDefinitionFixture())));

  const window = (route: Route) => new URL(route.request().url()).searchParams.get('window') === '30d' ? '30d' : '7d';
  await page.route(url => url.pathname === '/api/gate-economics', route => route.fulfill(json(data.economics[window(route)])));
  await page.route(url => url.pathname === `${BASE}/cycle-time`, route => route.fulfill(json(data.cycleTime[window(route)])));
  await page.route(url => url.pathname === `${BASE}/pipeline-health`, route => route.fulfill(json(data.pipelineHealth)));
  await page.route(url => url.pathname === `${BASE}/batch-gate/report`, route => route.fulfill(json(data.batchReport)));
  await page.route(url => url.pathname === `${BASE}/batch-gate`, route => route.fulfill(json(batchSettingsPayload(data.batchingEnabled ?? false))));
  await page.route(url => url.pathname === '/api/system/execution-infrastructure',
    route => route.fulfill(json(executionInfrastructurePayload(data))));
}

async function openDeploy(page: Page, kind: FlowFixtureKind): Promise<void> {
  page.on('dialog', dialog => dialog.accept());
  await page.clock.setFixedTime(new Date(FLOW_FIXTURE_NOW));
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    // Run steps, not the Deploy overview: the overview reads environment and
    // release routes this spec does not mock.
    tabs: [{ kind: 'hub', projectName: project, section: 'pipeline' }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT });
  await mockApp(page, kind);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
    .forEach(element => ((element as HTMLElement).style.display = 'none')));
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible({ timeout: 30_000 });
}

async function capture(page: Page, name: string): Promise<void> {
  fs.mkdirSync(results, { recursive: true });
  await page.getByTestId('flow-health').screenshot({ path: path.join(results, `${name}.png`) });
}

test.describe('Project flow health (mocked)', () => {
  test('names a review jam as the main brake and states every target, in both themes', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1400 });
    await openDeploy(page, 'reviewJam');
    const reads: string[] = [];
    const record = (request: Request) => {
      const url = new URL(request.url());
      if (url.pathname.startsWith('/api/')) reads.push(url.pathname + url.search);
    };
    page.on('request', record);

    await page.getByTestId('deploy-tab-flow').click();
    const brake = page.getByTestId('flow-health-brake');
    await expect(brake).toHaveAttribute('data-tone', 'brake', { timeout: 30_000 });
    await expect(brake).toHaveAttribute('data-stage', 'review');
    await expect(brake).toContainText('Review waits: 14 cards, oldest 6 h');
    await expect(page.locator('[data-testid="flow-health-stage"][data-stage="review"]')).toHaveAttribute('data-status', 'missed');
    for (const block of ['now', 'grouping', 'gate', 'review']) {
      await expect(page.getByTestId(`flow-health-${block}`)).toBeVisible();
    }
    const gateMinutes = page.locator('[data-figure="gateMinutesPerCard"]');
    await expect(gateMinutes.getByTestId('flow-health-target')).toHaveText('≤ 10 min');
    // Every figure states its target in the page.
    const targets = await page.getByTestId('flow-health-target').allTextContents();
    expect(targets.length).toBeGreaterThan(10);
    expect(targets.every(text => /^[≤≥] /.test(text.trim())), targets.join(' | ')).toBe(true);
    page.off('request', record);
    // Opening the page reads projections only; the batch state read diffs pending cards with Git.
    expect(reads.filter(url => url.includes('/batch-gate/state'))).toEqual([]);
    expect(reads.filter(url => url.includes('/cycle-time')).every(url => !url.includes('detail='))).toBe(true);

    await setTheme(page, 'light');
    await capture(page, 'flow-health-review-jam-light');
    await setTheme(page, 'dark');
    await capture(page, 'flow-health-review-jam-dark');
  });

  test('names a gate jam with its slot use, in both themes', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1400 });
    await openDeploy(page, 'gateJam');
    await page.getByTestId('deploy-tab-flow').click();
    await expect(page.getByTestId('flow-health-brake'))
      .toContainText('Gate waits: 9 cards, oldest 3 h, 2 of 2 slots busy', { timeout: 30_000 });
    await expect(page.locator('[data-figure="gateMinutesPerCard"]')).toHaveAttribute('data-status', 'missed');

    await setTheme(page, 'light');
    await capture(page, 'flow-health-gate-jam-light');
    await setTheme(page, 'dark');
    await capture(page, 'flow-health-gate-jam-dark');
  });

  test('opens from Pipeline health and links a block back to Run steps', async ({ page }) => {
    await openDeploy(page, 'healthyWeek');
    await page.getByTestId('pipeline-health-open-flow').click({ timeout: 30_000 });
    await expect(page.getByTestId('flow-health-brake')).toHaveAttribute('data-tone', 'calm', { timeout: 30_000 });
    await page.getByTestId('flow-health-detail-gate').click();
    await expect(page.getByTestId('pipeline-health')).toBeVisible({ timeout: 30_000 });
  });
});
