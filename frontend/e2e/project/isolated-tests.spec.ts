import { setThemeThroughControl } from '../helpers/theme';
import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const PROJECT = 'Studio';
const STORAGE = '/srv/tasks/studio';
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'isolated-tests');
const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

async function openOperations(page: Page, mode: 'entries' | 'empty' | 'error' | 'loading' = 'entries'): Promise<void> {
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
  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill(json({ workspaceId: 'ws',
    workspace: { projectId: null, project: null, status: 'ok', message: null, overrideActive: false,
      codingAllowed: true, hostingAllowed: true, limited: false }, projects: [], cardRefusals: {} })));
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
  await page.route('**/api/projects/*/test-quarantines', async route => {
    if (mode === 'loading') return;
    await route.fulfill(json(mode === 'error' ? { error: 'Unavailable' } : mode === 'empty' ? [] : entries, mode === 'error' ? 503 : 200));
  });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('error-dialog-overlay')).toBeHidden();
}

const entries = [
  { id: 'held', test: 'FixtureTests.ConcurrentWrites', card: 'AGT-3980', kind: 'held',
    coveringFolders: ['backend/Features/Pipeline', 'backend/Features/Runner'], expiresAtUtc: '2026-11-23T04:00:00Z' },
  { id: 'report', test: 'EngineReportReviewTests.MixedResults', card: 'AGT-3981', kind: 'report-only',
    coveringFolders: ['runner'], expiresAtUtc: '2026-11-28T04:00:00Z' },
  { id: 'expired', test: 'ExpiredTests.Hidden', card: 'AGT-3900', kind: 'held', coveringFolders: [], expiresAtUtc: '2026-11-21T04:00:00Z' },
  { id: 'lifted', test: 'LiftedTests.Hidden', card: 'AGT-3901', kind: 'held', coveringFolders: [],
    expiresAtUtc: '2026-11-23T04:00:00Z', liftedAtUtc: '2026-11-22T03:00:00Z' },
];

test('isolated tests show held areas, report-only entries and card links in both themes and widths', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await openOperations(page);
  const panel = page.getByTestId('isolated-tests');
  await expect(panel.getByTestId('isolated-test-row')).toHaveCount(2);
  await expect(panel).toContainText('backend/Features/Pipeline, backend/Features/Runner');
  await expect(panel).toContainText('Report-only');
  await expect(panel).toContainText('None');
  await expect(panel).toContainText('1 d');
  await expect(panel.getByRole('link', { name: 'AGT-3980' })).toHaveAttribute('href', '#/tasks/AGT-3980');
  fs.mkdirSync(results, { recursive: true });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 844 });
    for (const theme of ['light', 'dark'] as const) {
      await setThemeThroughControl(page, theme);
      await panel.scrollIntoViewIfNeeded();
      expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      await panel.screenshot({ path: path.join(results, `isolated-tests-${width}-${theme}.png`) });
    }
  }
  expect(errors).toEqual([]);
});

for (const mode of ['empty', 'error', 'loading'] as const) {
  test(`isolated tests ${mode} state`, async ({ page }) => {
    await openOperations(page, mode);
    const panel = page.getByTestId('isolated-tests');
    if (mode === 'empty') await expect(panel).toContainText('No isolated tests.');
    if (mode === 'loading') {
      await expect(panel).toHaveAttribute('aria-busy', 'true');
      await expect(panel).toContainText('Loading isolated tests');
    }
    if (mode === 'error') {
      await expect(panel.getByRole('alert')).toContainText('could not be loaded');
      await page.route('**/api/projects/*/test-quarantines', route => route.fulfill(json(entries)));
      await panel.getByRole('button', { name: 'Try again' }).click();
      await expect(panel.getByTestId('isolated-test-row')).toHaveCount(2);
    }
  });
}

test('RT-1 and RT-4 owner questions list options and mark recommendations in Needs you', async ({ page }) => {
  await openOperations(page);
  await page.route('**/api/operator/attention', route => route.fulfill(json({ count: 2, items: [
    { id: 'legacy', type: 'post-merge.red-incident', project: PROJECT, cardKey: 'AGT-3980', cards: ['AGT-3980'],
      reason: 'The repair is still in review.', observedAt: '2026-11-22T03:00:00Z', suggestedOperations: ['card.inspect'],
      ownerOptions: [{ id: 'fix', label: 'keep fixing forward', recommended: true }, { id: 'strict', label: 'switch the project to strict', recommended: false }] },
    { id: 'expired', type: 'post-merge.red-incident', project: PROJECT, cardKey: 'AGT-3981', cards: ['AGT-3981'],
      title: 'Quarantine reached 48 hours', reason: 'Owner decides the next step.', observedAt: '2026-11-22T03:00:00Z', suggestedOperations: ['card.inspect'],
      ownerOptions: [{ id: 'extend', label: 'extend 24 hours', recommended: false }, { id: 'report', label: 'report-only with a card', recommended: true }] },
  ] })));
  await page.reload();
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 844 });
    for (const theme of ['light', 'dark'] as const) {
      await setThemeThroughControl(page, theme);
      await page.getByTestId('studio-attention-trigger').click();
      const options = page.getByTestId('attention-owner-options');
      await expect(options).toHaveCount(2);
      await expect(options.first()).toContainText('keep fixing forward Recommended');
      await expect(options.nth(1)).toContainText('report-only with a card Recommended');
      await page.getByTestId('studio-attention-list').screenshot({ path: path.join(results, `isolation-owner-${width}-${theme}.png`) });
      await page.getByTestId('studio-attention-list').getByRole('button', { name: 'Close', exact: true }).click();
    }
  }
});
