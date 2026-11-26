import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

// AGT-3965: real Project settings and Operations components with isolated HTTP
// fixtures. No live project is mutated.
const project = 'strategy-demo';
const strategies = [
  { id: 'fast', isDefault: true, sentence: 'Each card runs its build and affected tests, integrates right after the review passed, and develop gets affected tests per merge window, the full routine suite nightly and the full suite before a Production release.' },
  { id: 'balanced', isDefault: false, sentence: 'Each card runs its build and affected tests, integrates right after the review passed, and every routine suite runs on develop after each merge window.' },
  { id: 'strict', isDefault: false, sentence: 'Each card runs its build in review and the full suite in the batch gate, and integrates only after a green batch.' },
];
const missingMessage = 'No post-merge run was ever reported for strategy-demo, while develop moved to 4f1c2a9b7d3e. '
  + 'Testing strategy fast integrates before the tests run, so these merges are untested. Start the post-merge window and '
  + 'nightly timers for this project, or choose strict on the project settings page.';

function coverage(strategy: string) {
  const missing = strategy !== 'strict';
  return {
    project, strategy, state: missing ? 'missing' : 'not-expected', developTipSha: '4f1c2a9b7d3e'.padEnd(40, '0'),
    lastReportedTipSha: null, lastReportedAtUtc: null, missing,
    message: missing ? missingMessage : `${project} tests before integration (strict); no post-merge run is expected.`,
  };
}

async function mockStudio(page: Page, state: { strategy: string; writes: string[] }, section: 'settings' | 'operations') {
  const settings = () => ({
    autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'never',
    pickupMode: 'manual', executionLocation: 'agent-runner-01', integrationBranch: 'develop',
    testingStrategy: state.strategy, testingStrategies: strategies, postMergeCoverage: coverage(state.strategy),
  });
  const testing = () => ({
    strategy: state.strategy, stored: state.writes.length > 0, sentence: '', perCard: 'affected', mode: 'post-merge',
    effectiveMode: 'post-merge', postMergeCoverage: coverage(state.strategy), strategies,
  });
  const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill(json({
    negotiateVersion: 1, connectionId: 'strategy-mock', connectionToken: 'strategy-mock',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  })));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'networked', bootstrapRequired: false, authenticated: true,
    user: { id: 'project-admin', username: 'project.admin', displayName: 'Project Admin', role: 'operator',
      projects: [project], disabled: false, mustChangePassword: false, hasFactor: true },
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: project, path: '/tasks', rootPath: '/repo', repositoryPath: '/repo' },
  ])));
  await page.route('**/api/v1/management/retention/policy**', route => route.fulfill(json({
    scope: route.request().url().includes('/projects/') ? 'project' : 'workspace',
    version: 1, updatedAt: '2026-11-23T00:00:00Z', updatedBy: 'fixture', rules: [], fullBackups: null,
  })));
  await page.route('**/api/environment', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({}, 503)));
  // The settings page also mounts the quota panel; it shows its own error line for a missing snapshot.
  await page.route(/\/api\/workspaces\/[^/]+(\/projects\/[^/]+)?\/quota$/, route => route.fulfill(json({}, 404)));
  const admission = (scope: string | null) => ({ project: scope, status: 'ok', message: null, overrideActive: false,
    codingAllowed: true, hostingAllowed: true, limited: false });
  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill(json({
    workspaceId: 'ws', workspace: admission(null), projects: [admission(project)] })));
  const record = {
    sourceType: 'local-folder', id: 'PROJ-STR', displayName: project, shortCode: 'STR', workspaceId: 'ws',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: '/tasks',
    repositoryPath: '/repo', rootPath: '/repo', urls: [], ownershipMappings: [],
    archived: false, createdAt: '2026-10-01T00:00:00Z',
  };
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws', displayName: 'Workspace', projects: [record] }])));
  await page.route('**/api/v1/projects', route => route.fulfill(json([record])));
  await page.route(url => url.pathname === '/api/v1/operations/structure', route => route.fulfill(json({ nodes: [], links: [] })));
  await page.route('**/api/v1/operations/structure/library', route => route.fulfill(json({ registries: [], consumers: [] })));
  await page.route('**/api/diagnostics/api-stalls**', route => route.fulfill(json({
    status: 'ok', intervalMs: 1000, maxDelayMs: 0, alarmThresholdMs: 1000, alarmWindowMinutes: 5, recent: [] })));
  await page.route(`**/api/projects/${project}/environments`, route => route.fulfill(json({
    project, environments: [], promotions: [], failureCards: [] })));
  await page.route(`**/api/projects/${project}/workbenches**`, route => route.fulfill(json({ items: [] })));
  await page.route(`**/api/projects/${project}/execution/proposal-preview`, route => route.fulfill({ json: {
    definition: 'schemaVersion: 1', prepareScript: '#!/bin/sh', detectedStacks: ['dotnet'],
    reasoning: 'Fixture build profile.', reviewCommands: { build: [], test: [], lint: [] },
  } }));
  await page.route(`**/api/projects/${project}/execution`, route => route.fulfill({ json: { valid: true, issues: [], source: 'subject-commit' } }));
  await page.route('**/api/projects/settings', route => route.fulfill({ json: { [project]: settings() } }));
  await page.route(`**/api/projects/${project}/snapshot`, route => route.fulfill({ json: { settings: settings() } }));
  await page.route(`**/api/projects/${project}/deploy/operations`, route => route.fulfill({ json: {
    project, since: '2026-10-23T00:00:00Z', until: '2026-11-22T00:00:00Z', environments: [],
    dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null,
      cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
  } }));
  await page.route(`**/api/projects/${project}/testing`, async route => {
    if (route.request().method() === 'PUT') {
      state.strategy = route.request().postDataJSON().strategy;
      state.writes.push(state.strategy);
    }
    await route.fulfill({ json: testing() });
  });
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.setViewportSize({ width: 1440, height: 1100 });
  await page.addInitScript(([projectName, hubSection]) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName, section: hubSection }],
      activeKey: `hub:${projectName}`,
    }));
  }, [project, section] as const);
}

async function openStudio(page: Page) {
  await page.goto('/', { waitUntil: 'domcontentloaded' });
}

function resultsDir(): string {
  const results = process.env['JOB_RESULTS_DIR'];
  expect(results, 'durable evidence directory').toBeTruthy();
  mkdirSync(results!, { recursive: true });
  return results!;
}

test('a project Admin chooses the testing strategy and fast warns while post-merge runs are missing', async ({ page }) => {
  const state = { strategy: 'fast', writes: [] as string[] };
  await mockStudio(page, state, 'settings');
  await openStudio(page);
  const section = page.getByTestId('project-detail-testing-strategy');
  await expect(section).toBeVisible();
  for (const option of strategies)
    await expect(section.getByTestId(`project-detail-testing-strategy-sentence-${option.id}`)).toContainText(option.sentence);
  await expect(section.getByTestId('project-detail-testing-strategy-fast')).toHaveAttribute('aria-pressed', 'true');
  const warning = section.getByTestId('project-detail-post-merge-warning');
  await expect(warning).toContainText('No post-merge run was ever reported for strategy-demo');
  const results = resultsDir();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await section.scrollIntoViewIfNeeded();
    await page.mouse.move(0, 0);
    await expect(page.getByRole('tooltip')).toHaveCount(0);
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
    await section.screenshot({ path: join(results, `testing-strategy-admin-fast-warning-${theme}--mocked.png`) });
  }

  await section.getByTestId('project-detail-testing-strategy-strict').click();
  await expect.poll(() => state.writes.at(-1)).toBe('strict');
  await expect(section.getByTestId('project-detail-testing-strategy-strict')).toHaveAttribute('aria-pressed', 'true');
  await expect(warning).toHaveCount(0);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await section.scrollIntoViewIfNeeded();
    await page.mouse.move(0, 0);
    await expect(page.getByRole('tooltip')).toHaveCount(0);
    await section.screenshot({ path: join(results, `testing-strategy-admin-strict-${theme}--mocked.png`) });
  }
});

test('the Operations view names missing post-merge runs and links to the strategy', async ({ page }) => {
  await mockStudio(page, { strategy: 'fast', writes: [] }, 'operations');
  await openStudio(page);
  const warning = page.getByTestId('operations-post-merge-warning');
  await expect(warning).toContainText('No post-merge run was ever reported for strategy-demo');
  await expect(warning.getByRole('link', { name: 'Testing strategy in project settings' })).toHaveAttribute(
    'href', `#/projects/${project}/settings`);
  const results = resultsDir();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
    await page.getByTestId('project-operations').screenshot({ path: join(results, `operations-post-merge-warning-${theme}--mocked.png`) });
  }
});
