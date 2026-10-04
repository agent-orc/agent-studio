import { expect, test } from '../fixtures/dev-backend';
import type { Page, Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/** Project Overview operator dashboard (AGT-2105).
 *
 * The mocked case pins exact real-shaped data, interactions, both themes, and
 * the existing URL start endpoint. The real case proxies this worktree's
 * fixture-owned backend and persists a second both-theme pair without claiming
 * deterministic values.
 */

const RESULTS_DIR = process.env.PROJECT_OVERVIEW_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-overview-dashboard');
const PROJECT_NAME = 'Operator Demo';
const PROJECT_ID = 'PROJ-900';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

const urls = [
  { id: 'frontend', label: 'Operator UI', url: 'http://127.0.0.1:4310', sortOrder: 0,
    startRule: { command: 'npm run start', cwd: null, port: 4310, source: 'manual' } },
  { id: 'storybook', label: 'Component preview', url: 'http://127.0.0.1:4311', sortOrder: 1,
    startRule: { command: 'npm run storybook', cwd: null, port: 4311, source: 'manual' } },
];

const project = {
  id: PROJECT_ID, displayName: PROJECT_NAME, shortCode: 'OPD', workspaceId: 'ws-product',
  storageLocation: '/mock/tasks/operator-demo', rootPath: '/mock/repos/operator-demo',
  repositoryPath: '/mock/repos/operator-demo', sortOrder: 0, archived: false, urls,
};
const workspaces = [{ id: 'ws-product', displayName: 'Product Engineering', color: '#6c8cff', projects: [project] }];

const tokenSummary = {
  project: PROJECT_NAME, hasData: true,
  lifetimeTotalTokens: 12_409_120, lifetimeJobTokens: 9_200_000,
  lifetimeSupportingTokens: 2_109_120, lifetimeOrchestratorTokens: 1_100_000, lifetimeCalls: 441,
  last24hTotalTokens: 1_248_320, last24hJobTokens: 910_000,
  last24hSupportingTokens: 221_000, last24hOrchestratorTokens: 117_320, last24hCalls: 37,
  last7dTotalTokens: 8_341_080, last7dJobTokens: 6_140_000,
  last7dSupportingTokens: 1_401_080, last7dOrchestratorTokens: 800_000, last7dCalls: 229,
  firstActivity: '2026-06-20T08:00:00Z', lastActivity: '2026-07-11T11:45:00Z',
  fetchedAt: '2026-07-11T12:00:00Z', disclaimer: 'Measured from token-usage events.',
};

const commits = [
  ['8c21d4f', 'Operator-first project overview'],
  ['0a71c22', 'Reuse URL start-in-place'],
  ['4f928ab', 'Add deployment summary contract'],
  ['9d30e60', 'Link Wiki Pulse and planning work'],
  ['ba812f1', 'Add both-theme acceptance proof'],
].map(([shortSha, subject], index) => ({
  sha: `${shortSha}${'0'.repeat(33)}`, shortSha, subject,
  authorDateUtc: `2026-07-11T${String(11 - index).padStart(2, '0')}:00:00Z`,
}));

const deployment = {
  project: PROJECT_NAME, available: true, reason: null, source: 'logs/stable-restarts.jsonl',
  defaultEvidenceRun: {
    id: 'TR-202607110930-green', commit: '4f928ab000000000000000000000000000000000', branch: 'develop',
    scope: { level: 'project', testSet: 'regression' }, completedAt: '2026-07-11T09:30:00Z',
    distanceToHead: 2, headDirection: 'head-ahead',
  },
  lastDeployment: {
    at: '2026-07-10T09:42:11Z', status: 'ok', headBefore: '2bec67c', headAfter: 'a1f4b29',
    durationSeconds: 47, jobsSinceLastRestart: 6, reviewCountAfter: 14, commits: commits.slice(0, 3),
  },
  history: [
    {
      at: '2026-07-10T09:42:11Z', status: 'ok', headBefore: '2bec67c', headAfter: 'a1f4b29',
      durationSeconds: 47, jobsSinceLastRestart: 6, reviewCountAfter: 14, commits: commits.slice(0, 3),
    },
    {
      at: '2026-07-08T17:12:00Z', status: 'ok', headBefore: '441bc21', headAfter: '2bec67c',
      durationSeconds: 38, jobsSinceLastRestart: 4, reviewCountAfter: 8, commits: [],
    },
    {
      at: '2026-07-07T14:03:00Z', status: 'failed', headBefore: '310aa91', headAfter: '441bc21',
      durationSeconds: 12, jobsSinceLastRestart: 2, reviewCountAfter: 4, commits: [],
    },
  ],
  pendingCount: commits.length,
  pendingCommits: commits,
  targets: [{
    id: 'deploy-stable', title: 'deploy-stable', kind: 'derived', template: 'deploy-stable',
    summary: 'Update the stable seat after confirming it is idle.', runnable: true,
    source: 'repository-fact', command: 'bash scripts/supervisor/restart-stable-after-batch.sh', targetHostId: null,
    parameters: [{ name: 'stableIdle', type: 'boolean', required: true, default: false, options: [] }],
  }, {
    id: 'docs-site', title: 'Docs site', kind: 'template', template: 'caddy-site',
    summary: 'Deploy docs to the Caddy host.', runnable: true,
    source: 'docs/deployments/docs-site/deployment.json', command: 'bash scripts/deploy-docs.sh --branch {{branch}}',
    targetHostId: 'agent-orchestrator-web',
    parameters: [{ name: 'branch', type: 'branch', required: true, default: 'develop', options: [] }],
  }],
};

const testRuns = {
  project: PROJECT_NAME,
  headCommit: '8c21d4f000000000000000000000000000000000',
  runs: [
    {
      run: {
        id: 'TR-202607111230-next', projectId: PROJECT_ID, trigger: 'pipeline',
        commit: '8c21d4f000000000000000000000000000000000', branch: 'develop',
        scope: { level: 'project', testSet: 'full-regression' }, state: 'planned', result: null,
        durationSeconds: null, host: null, plannedOrder: 1, createdAt: '2026-07-11T12:30:00Z',
        startedAt: null, completedAt: null,
      },
      attachedTasks: [{ taskKey: 'OPD-221', title: 'Plan deployment history detail' }],
    },
    {
      run: {
        id: 'TR-202607111200-active', projectId: PROJECT_ID, trigger: 'merge',
        commit: '0a71c22000000000000000000000000000000000', branch: 'develop',
        scope: { level: 'integration', testSet: 'smoke-and-contracts' }, state: 'running', result: null,
        durationSeconds: 82, host: 'runner-02', plannedOrder: 0, createdAt: '2026-07-11T12:00:00Z',
        startedAt: '2026-07-11T12:01:00Z', completedAt: null,
      },
      attachedTasks: [],
    },
    {
      run: {
        id: 'TR-202607110930-green', projectId: PROJECT_ID, trigger: 'scheduled',
        commit: '4f928ab000000000000000000000000000000000', branch: 'develop',
        scope: { level: 'project', testSet: 'regression' }, state: 'completed', result: 'passed',
        durationSeconds: 194.2, host: 'runner-01', plannedOrder: 0, createdAt: '2026-07-11T09:25:00Z',
        startedAt: '2026-07-11T09:26:00Z', completedAt: '2026-07-11T09:30:00Z',
      },
      attachedTasks: [{ taskKey: 'OPD-220', title: 'Visual overview delivery' }],
    },
  ],
};

const wikiPulse = {
  projectName: PROJECT_NAME, baseDir: '/mock/repos/operator-demo/docs', exists: true,
  generatedAtUtc: '2026-07-11T12:00:00Z',
  feed: { available: true, reason: null, items: [
    { relPath: 'concepts/deployment-first-class.md', title: 'Deployment as a first-class citizen', author: 'Robert',
      authorDateUtc: '2026-07-11T10:30:00Z', sha: 'a', shortSha: 'a', subject: 'AGT-2097 concept',
      areaSlug: 'concepts', areaTitle: 'Concepts', taskKey: 'AGT-2097' },
    { relPath: 'concepts/operator-dashboard.md', title: 'Operator dashboard decisions', author: 'Codex',
      authorDateUtc: '2026-07-11T09:00:00Z', sha: 'b', shortSha: 'b', subject: 'AGT-2105 overview',
      areaSlug: 'current-state', areaTitle: 'Current State', taskKey: 'AGT-2105' },
  ] },
  inbox: { available: true, reason: null, count: 0, items: [] },
  drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [],
    counts: { fresh: 4, aging: 0, stale: 0, graded: 4 } },
  critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
};

const planningTask = {
  id: 'plan-deployment-history', taskKey: 'OPD-221', key: 'OPD-221',
  title: 'Plan deployment history detail', state: '2-ready', order: 1, agent: 'codex', cliType: 'codex',
  createdAt: '2026-07-11T08:00:00Z', lastActivity: '2026-07-11T10:00:00Z',
  watchPath: '/mock/tasks/operator-demo', projectName: PROJECT_NAME,
  folderPath: '/mock/tasks/operator-demo/2-ready/plan-deployment-history', mode: 'planning',
  model: null, sessionName: null, useOwnSession: null, lastUsage: null, execution: null, commit: null,
};

const snapshot = {
  project: PROJECT_NAME, capturedAt: '2026-07-11T12:00:00Z',
  paths: { path: '/mock/tasks/operator-demo', rootPath: '/mock/repos/operator-demo', repositoryPath: '/mock/repos/operator-demo' },
  settings: { autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'on-completed', runnerMode: 'manual', orchestratorModel: null },
  runnerStatus: null, orchestratorLogTail: [], orchestratorSession: null,
  reviewDecisionsPending: [], runnerPendingDecisions: [],
  publishTargets: [{
    id: 'package:npm', kind: 'package', ecosystem: 'npm', label: 'npm', packageName: 'operator-demo',
    currentVersion: '0.8.2', firstPublishPending: false, pendingCount: 2,
    referenceKind: 'tag', reference: 'v0.8.2',
  }],
  queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
};

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [planningTask], progress: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], humanReview: [], escalated: [], review: [], completed: [], archive: [],
  };
}

async function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function proxyBackend(page: Page, backendBaseUrl: string): Promise<void> {
  await page.route('**/api/**', async route => {
    const requestUrl = new URL(route.request().url());
    try {
      const response = await route.fetch({ url: `${backendBaseUrl}${requestUrl.pathname}${requestUrl.search}` });
      await route.fulfill({ response });
    } catch {
      await route.abort('failed').catch(() => undefined);
    }
  });
}

async function resolveRealProject(backendBaseUrl: string): Promise<string | null> {
  const watchPathsResponse = await fetch(`${backendBaseUrl}/api/watch-paths`);
  expect(watchPathsResponse.ok).toBe(true);
  const watchPaths = await watchPathsResponse.json() as { name: string }[];
  if (watchPaths.length === 0) return null;
  return watchPaths.find(item => /agent.?task/i.test(item.name))?.name ?? watchPaths[0].name;
}

async function mockDashboard(page: Page): Promise<{
  startedUrl: () => boolean;
  taskRequested: () => boolean;
  validCompilePending: () => boolean;
  releaseValidCompile: () => void;
}> {
  let offlineStarted = false;
  let planningTaskRequested = false;
  let evidenceReviewed = false;
  let validCompilePending = false;
  let releaseValidCompile: (() => void) | null = null;
  await page.route('**/api/v1/studio/auth/status', route => fulfillJson(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('http://127.0.0.1:4310/**', route => route.fulfill({ status: 200, body: 'ok' }));
  await page.route('http://127.0.0.1:4311/**', route => offlineStarted
    ? route.fulfill({ status: 200, body: 'ok' })
    : route.abort('connectionrefused'));
  await page.route(`**/api/projects/${PROJECT_ID}/urls/frontend/readiness`, route => fulfillJson(route, {
    kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 12,
  }));
  await page.route(`**/api/projects/${PROJECT_ID}/urls/storybook/readiness`, route => fulfillJson(route, offlineStarted
    ? { kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 18 }
    : { kind: 'offline', statusCode: null, framePolicy: 'unknown', detail: 'Connection refused.', durationMs: 7 }));

  await page.route('**/api/watch-paths', route => fulfillJson(route, [{
    name: PROJECT_NAME, path: '/mock/tasks/operator-demo', rootPath: '/mock/repos/operator-demo',
    repositoryPath: '/mock/repos/operator-demo',
  }]));
  await page.route('**/api/v1/workspaces', route => fulfillJson(route, workspaces));
  await page.route('**/api/v1/projects', route => fulfillJson(route, [project]));
  await page.route('**/api/tasks', route => fulfillJson(route, [planningTask]));
  await page.route('**/api/v1/studio/board', route => fulfillJson(route, grouped()));
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/plan-deployment-history(?:\?|$)/, () => {
    planningTaskRequested = true;
    // Navigation writes the task deep link before detail loading. Leave the
    // heavy task pane pending so this dashboard spec verifies the handoff
    // without compiling an unrelated editor surface.
  });
  await page.route('**/api/crash-recovery/pending', route => fulfillJson(route, { pending: [] }));
  await page.route('**/api/projects/*/token-usage/summary', route => fulfillJson(route, tokenSummary));
  await page.route('**/api/projects/*/deployment/summary', route => fulfillJson(route, deployment));
  await page.route('**/api/projects/*/test-runs', route => fulfillJson(route, testRuns));
  await page.route('**/api/projects/*/deployment/compile', async route => {
    const request = route.request().postDataJSON() as { prompt?: string };
    if (request.prompt?.includes('Command: npm run deploy')) {
      return fulfillJson(route, {
        title: 'Deployment for Operator Demo', summary: 'Definition needs attention.',
        command: null, parameters: [], runnable: false,
        warnings: ['The command must be a repository-owned scripts/*.sh path with typed slots and no shell chaining or redirection.'],
      });
    }
    validCompilePending = true;
    await new Promise<void>(resolve => { releaseValidCompile = resolve; });
    validCompilePending = false;
    return fulfillJson(route, {
      title: 'Deployment for Operator Demo', summary: 'Definition valid.',
      command: 'bash scripts/deploy.sh --branch {{branch}}', runnable: true, warnings: [],
      parameters: [
        { name: 'branch', type: 'branch', required: true, default: null, options: [] },
        { name: 'confirm', type: 'boolean', required: true, default: false, options: [] },
      ],
    });
  });
  await page.route('**/api/projects/*/wiki/pulse**', route => fulfillJson(route, wikiPulse));
  await page.route('**/api/projects/*/snapshot', route => fulfillJson(route, snapshot));
  await page.route('**/api/git/inventory**', route => fulfillJson(route, {
    projectName: PROJECT_NAME, repositoryPath: '/mock/repos/operator-demo', isRepo: true,
    currentBranch: 'develop', worktrees: [], recentCommits: [], error: null,
    branches: [
      { name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: false,
        upstream: 'origin/main', ahead: 0, behind: 2, lastCommitSubject: 'release', lastCommitAtUtc: '2026-07-11T09:00:00Z', worktreePath: null },
      { name: 'develop', category: 'develop', tipSha: 'b'.repeat(40), tipShortSha: 'bbbbbbb', isCurrent: true,
        upstream: 'origin/develop', ahead: 4, behind: 0, lastCommitSubject: 'integrate', lastCommitAtUtc: '2026-07-11T11:00:00Z', worktreePath: '/mock/repos/operator-demo' },
      { name: 'task/OPD-221-plan-deployment-history', category: 'task', tipSha: 'c'.repeat(40), tipShortSha: 'ccccccc', isCurrent: false,
        upstream: null, ahead: 0, behind: 0, lastCommitSubject: 'plan', lastCommitAtUtc: '2026-07-11T10:00:00Z', worktreePath: null },
    ],
  }));
  await page.route('**/api/projects/*/visual-evidence', route => fulfillJson(route, {
    project: PROJECT_NAME, capturedAt: '2026-07-11T12:00:00Z', unseenCount: evidenceReviewed ? 0 : 1,
    items: [{
      id: 'visual-screenshot-overview', jobId: 'OPD-220', jobTitle: 'Visual overview delivery',
      watchPath: '/mock/tasks/operator-demo', fileName: 'overview--real.png',
      relativePath: 'results/overview--real.png', url: '/evidence-shot.svg', caption: 'Project overview in light theme',
      testStatus: 'passed', source: 'real', capturedAt: '2026-07-11T11:30:00Z',
      reviewStatus: evidenceReviewed ? 'reviewed' : 'unseen',
    }, {
      id: 'visual-screenshot-removed', jobId: 'OPD-204', jobTitle: 'Prior sweep',
      watchPath: '/mock/tasks/operator-demo', fileName: 'removed--mocked.png',
      relativePath: 'results/removed--mocked.png', url: null, caption: 'Prior settings sweep',
      testStatus: null, source: 'unavailable', capturedAt: '2026-07-10T09:00:00Z', reviewStatus: 'unavailable',
    }, ...Array.from({ length: 3 }, (_, index) => ({
      id: `visual-screenshot-history-${index + 3}`, jobId: `OPD-${203 - index}`, jobTitle: 'Earlier visual delivery',
      watchPath: '/mock/tasks/operator-demo', fileName: `history-${index + 3}--real.png`,
      relativePath: `results/history-${index + 3}--real.png`, url: '/evidence-shot.svg', caption: `Earlier evidence ${index + 3}`,
      testStatus: 'passed', source: 'real', capturedAt: `2026-07-0${9 - index}T09:00:00Z`, reviewStatus: 'reviewed',
    }))],
  }));
  await page.route('**/api/projects/*/visual-evidence/*/acknowledge', route => {
    evidenceReviewed = true;
    return fulfillJson(route, {
      id: 'visual-screenshot-overview', jobId: 'OPD-220', jobTitle: 'Visual overview delivery',
      watchPath: '/mock/tasks/operator-demo', fileName: 'overview--real.png', relativePath: 'results/overview--real.png',
      url: '/evidence-shot.svg', caption: 'Project overview in light theme', testStatus: 'passed', source: 'real',
      capturedAt: '2026-07-11T11:30:00Z', reviewStatus: 'reviewed',
    });
  });
  await page.route('**/evidence-shot.svg', route => route.fulfill({
    status: 200, contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="160" height="100"><rect width="160" height="100" fill="#6c8cff"/><rect x="14" y="14" width="132" height="18" rx="4" fill="#fff"/><rect x="14" y="42" width="60" height="44" rx="4" fill="#dce3ff"/><rect x="82" y="42" width="64" height="44" rx="4" fill="#fff"/></svg>',
  }));
  await page.route(`**/api/projects/${PROJECT_ID}/urls/storybook/start`, async route => {
    offlineStarted = true;
    await new Promise(resolve => setTimeout(resolve, 500));
    await fulfillJson(route, { started: true, processId: 4421 });
  });
  await page.route('**/api/projects/*/token-usage/heatmap**', route => fulfillJson(route, {
    project: PROJECT_NAME, days: [], jobs: [], hasData: false, fetchedAt: '2026-07-11T12:00:00Z',
  }));
  await page.route('**/api/projects/*/token-usage/expensive**', route => fulfillJson(route, { project: PROJECT_NAME, jobs: [] }));
  await page.route('**/api/projects/*/token-usage/pipeline-cost**', route => fulfillJson(route, {
    project: PROJECT_NAME, days: [], windowDays: 30, kinds: [], steps: [], totalTokens: 0,
    totalCostUsd: 0, anyModelUnknown: false, taskCount: 0, hasData: false, fetchedAt: '2026-07-11T12:00:00Z',
  }));
  await page.route('**/api/projects/*/wiki/tree', route => fulfillJson(route, {
    projectName: PROJECT_NAME, baseDir: '/mock/docs', exists: true, root: [],
  }));
  return {
    startedUrl: () => offlineStarted,
    taskRequested: () => planningTaskRequested,
    validCompilePending: () => validCompilePending,
    releaseValidCompile: () => releaseValidCompile?.(),
  };
}

async function openDashboard(page: Page, projectName = PROJECT_NAME, freshDocument = false): Promise<void> {
  await page.setViewportSize({ width: 1536, height: 1200 });
  const entry = freshDocument ? '/?source=real#' : '/#';
  await page.goto(`${entry}/projects/${slugFor(projectName)}/overview`);
  await expect(page.getByTestId('project-overview-dashboard')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  const recoveryDismiss = page.getByTestId('crash-recovery-dismiss').first();
  if (await recoveryDismiss.isVisible().catch(() => false)) await recoveryDismiss.click();
  const orchestratorClose = page.getByRole('region', { name: 'Orchestrator' }).getByRole('button', { name: 'Close' });
  if (await orchestratorClose.isVisible().catch(() => false)) {
    await orchestratorClose.evaluate((button: HTMLButtonElement) => button.click());
  }
}

test.describe('Project Overview · operator dashboard', () => {
  test.beforeAll(() => fs.mkdirSync(RESULTS_DIR, { recursive: true }));

  test('keeps the usage card intact and visual evidence bounded in both themes', async ({ page, devBackend }, testInfo) => {
    await proxyBackend(page, devBackend.baseUrl);
    await mockDashboard(page);
    await openDashboard(page);

    await expect(page.getByTestId('project-overview-throughput')).toHaveCount(0);
    await expect(page.getByTestId('project-overview-dashboard')).not.toContainText('Delivered tasks');
    await expect(page.getByTestId('project-overview-tokens-24h')).toHaveText('1.2M');
    await expect(page.getByTestId('project-overview-tokens-7d')).toHaveText('8.3M');
    await expect(page.getByTestId('project-overview-evidence').locator('li')).toHaveCount(4);
    await expect(page.getByTestId('project-overview-evidence-visual-screenshot-history-5')).toHaveCount(0);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: testInfo.outputPath(`project-overview-compact--${theme}.png`),
        fullPage: true,
      });
    }
  });

  test('shows the commit-bound run pipeline in Test Quality', async ({ page, devBackend }) => {
    await proxyBackend(page, devBackend.baseUrl);
    await mockDashboard(page);
    await page.setViewportSize({ width: 1536, height: 1200 });
    await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/overview`);
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    await dismissDevErrorDialog(page);

    await page.getByTestId('project-shell-rail-test-quality').click();
    const panel = page.getByTestId('project-test-runs-panel');
    await expect(panel).toBeVisible();
    await expect(page.getByTestId('test-run-pipeline-summary')).toContainText('1 planned');
    await expect(page.getByTestId('test-run-pipeline-summary')).toContainText('1 running');
    await expect(page.getByTestId('test-run-pipeline-summary')).toContainText('1 completed');
    await expect(page.getByTestId('test-run-lane-planned')).toContainText('OPD-221');
    await expect(page.getByTestId('test-run-lane-planned')).toContainText('Queue#1');
    await expect(page.getByTestId('test-run-lane-running')).toContainText('runner-02');
    await expect(page.getByTestId('test-run-lane-completed')).toContainText('passed');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await panel.screenshot({
        path: path.join(RESULTS_DIR, `test-run-pipeline--${theme}--mocked.png`),
      });
    }
  });

  test('defaults deployment to the last green run and shows its directed Head distance', async ({ page, devBackend }) => {
    await proxyBackend(page, devBackend.baseUrl);
    await mockDashboard(page);
    await openDashboard(page);

    await page.getByTestId('project-overview-open-deployment').click();
    const launcher = page.getByTestId('project-deployment-launcher');
    await expect(launcher).toBeVisible();
    await expect(launcher).toContainText('Run TR-202607110930-green');
    await expect(launcher).toContainText('Head is 2 commits ahead');
    await expect(page.locator('#deployment-tested-revision')).toBeChecked();

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await launcher.screenshot({
        path: path.join(RESULTS_DIR, `deployment-test-run-evidence--${theme}--mocked.png`),
      });
    }
  });

  test('shows exact operator signals, both themes, and reuses URL start-in-place', async ({ page, devBackend }) => {
    test.setTimeout(120_000);
    await proxyBackend(page, devBackend.baseUrl);
    const mocked = await mockDashboard(page);
    await openDashboard(page);

    await expect(page.getByTestId('project-overview-throughput')).toHaveCount(0);
    await expect(page.getByTestId('project-overview-dashboard')).not.toContainText('Delivered tasks');
    await expect(page.getByTestId('project-overview-tokens-24h')).toHaveText('1.2M');
    await expect(page.getByTestId('project-overview-tokens-7d')).toHaveText('8.3M');
    await expect(page.getByTestId('project-overview-deployment')).toContainText('5 changes ready to deploy');
    await expect(page.getByTestId('project-overview-wiki')).toContainText('Deployment as a first-class citizen');
    // Area badge comes from the stubbed areaTitle, not the 'Wiki' fallback.
    await expect(page.getByTestId('project-overview-wiki')).toContainText('Concepts');
    await expect(page.getByTestId('project-overview-planning-plan-deployment-history')).toBeVisible();
    await expect(page.getByTestId('project-overview-evidence-count')).toHaveText('1 unseen');
    await expect(page.getByTestId('project-overview-remote-truth')).toContainText('4 to push');
    await expect(page.getByTestId('project-overview-remote-truth')).toContainText('2 to pull');
    await expect(page.getByTestId('project-overview-remote-truth')).toContainText('No upstream · local-only');
    await expect(page.getByTestId('project-overview-branch-task-plan-deployment-history')).toBeVisible();
    await expect(page.getByTestId('project-overview-evidence-visual-screenshot-removed')).toContainText('No longer actionable');
    await page.getByTestId('project-overview-evidence-ack-visual-screenshot-overview').click();
    await expect(page.getByTestId('project-overview-evidence-count')).toHaveText('0 unseen');
    await expect(page.getByTestId('project-overview-evidence-visual-screenshot-overview')).toContainText('Reviewed');

    const legacyCopy = ['Watch path', 'Working directory', 'Repository', 'Clean context', 'Project sessions'];
    for (const copy of legacyCopy) await expect(page.getByTestId('project-overview-dashboard')).not.toContainText(copy);

    const numericVariant = await page.getByTestId('project-overview-tokens-24h')
      .evaluate(element => getComputedStyle(element).fontVariantNumeric);
    expect(numericVariant).toContain('tabular-nums');
    const overflow = await page.getByTestId('project-overview-dashboard')
      .evaluate(element => element.scrollWidth > element.clientWidth + 1);
    expect(overflow).toBe(false);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `project-overview-dashboard--${theme}--mocked.png`),
        fullPage: true,
      });
    }

    await expect(page.getByTestId('project-overview-url-status-frontend')).toHaveAttribute('data-status', 'running');
    await expect(page.getByTestId('project-overview-url-status-storybook')).toHaveAttribute('data-status', 'offline');
    await page.getByTestId('project-overview-url-start-storybook').click();
    await expect(page.getByTestId('project-overview-url-status-storybook')).toHaveAttribute('data-status', 'building');
    await expect.poll(mocked.startedUrl).toBe(true);
    await expect(page.getByTestId('project-overview-url-status-storybook')).toHaveAttribute('data-status', 'running');
    await expect(page.getByTestId('project-overview-urls-summary')).toContainText('2 / 2');

    await page.getByTestId('project-overview-deployment-details').locator('summary').click();
    await expect(page.getByTestId('project-overview-deployment-details')).toContainText('Operator-first project overview');
    await page.getByTestId('project-overview-last-deployment-details').locator('summary').click();
    await expect(page.getByTestId('project-overview-last-deployment-details')).toContainText('Operator-first project overview');

    await page.getByTestId('project-overview-open-deployment').click();
    await expect(page.getByTestId('project-deployment-panel')).toBeVisible();
    await expect(page.getByTestId('project-deployment-pending-count')).toHaveText('5');
    await expect(page.getByTestId('project-deployment-history').locator(':scope > li')).toHaveCount(3);
    await expect(page.getByTestId('project-deployment-targets').locator('button')).toHaveCount(2);
    await expect(page.getByTestId('project-deployment-panel')).toContainText('a1f4b29');
    await expect(page.getByTestId('project-deployment-panel')).not.toContainText('Run deployment');
    await expect(page.getByTestId('project-deployment-panel')).not.toContainText('Compile typed UI');
    await expect(page.getByTestId('project-deployment-launcher')).toContainText('Stable environment');
    await expect(page.getByTestId('project-deployment-launcher')).toContainText('Idle required');
    await expect(page.getByTestId('project-deployment-launcher')).toContainText('Head is 2 commits ahead');
    await expect(page.getByTestId('project-deployment-launcher')).toContainText('Require the stable environment to be idle before deployment');
    const visibleTask = page.getByTestId('deployment-visible-task');
    await expect(visibleTask).toBeChecked();
    const runDeployment = page.getByTestId('project-deployment-run');
    await expect(runDeployment).toHaveText('Start deployment task');
    await expect(runDeployment).toBeDisabled();
    await visibleTask.uncheck();
    await expect(page.getByTestId('project-deployment-launcher')).toContainText('Visible task execution is required for this workflow.');
    await expect(runDeployment).toHaveAttribute('aria-describedby', 'deployment-visible-task-required');
    await visibleTask.check();
    await expect(page.getByTestId('deployment-definition-result')).toContainText('Preview not generated');
    const stableIdle = page.getByRole('checkbox', { name: /Require the stable environment to be idle/ });
    await expect(stableIdle).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('project-deployment-launcher').screenshot({
        path: path.join(RESULTS_DIR, `deployment-selected-target-unconfirmed--${theme}--mocked.png`),
      });
    }
    await stableIdle.check();
    await expect(page.getByTestId('project-deployment-launcher')).toContainText('Idle confirmed');
    await expect(runDeployment).toBeEnabled();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('project-deployment-launcher').screenshot({
        path: path.join(RESULTS_DIR, `deployment-selected-target--${theme}--mocked.png`),
      });
    }

    const commandField = page.getByTestId('deployment-command');
    await commandField.focus();
    await page.keyboard.press('Tab');
    await expect(page.getByTestId('deployment-add-parameter')).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('deployment-parameter-name-0')).toHaveValue('branch');
    await expect(page.getByTestId('deployment-parameter-label-0')).toHaveValue('Branch to deploy');
    await commandField.fill('bash scripts/deploy.sh --branch {{branch}}');
    await page.getByTestId('deployment-preview-form').click();
    await expect.poll(mocked.validCompilePending).toBe(true);
    await expect(page.getByTestId('deployment-preview-form')).toHaveAttribute('data-pending-label', 'Validating definition...');
    await expect(page.getByTestId('deployment-preview-form')).toHaveAttribute('aria-busy', 'true');
    await expect(page.getByTestId('deployment-definition-result')).toContainText('Validating definition...');
    await expect(page.getByTestId('deployment-definition-result')).toHaveAttribute('aria-busy', 'true');
    mocked.releaseValidCompile();
    await expect(page.getByTestId('deployment-definition-result')).toContainText('Definition valid');
    await expect(page.getByTestId('deployment-form-preview')).toContainText('Branch to deploy *');
    await expect(page.getByTestId('deployment-form-preview').getByRole('textbox').first()).toHaveValue('develop');
    await expect(page.getByTestId('deployment-form-preview')).toContainText('Confirm deployment *');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `deployment-definition-valid--${theme}--mocked.png`),
        fullPage: true,
      });
    }

    await commandField.fill('npm run deploy -- {{branch}}');
    await page.getByTestId('deployment-preview-form').click();
    await expect(page.getByTestId('deployment-definition-result')).toContainText('Definition needs attention');
    await expect(page.getByTestId('deployment-command-error')).toContainText('repository-owned scripts/*.sh');
    await expect(commandField).toHaveAttribute('aria-invalid', 'true');
    await expect(commandField).toHaveAttribute('aria-describedby', /deployment-command-error/);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `deployment-definition-invalid--${theme}--mocked.png`),
        fullPage: true,
      });
    }
    await page.setViewportSize({ width: 1024, height: 900 });
    await setTheme(page, 'light');
    expect(await page.getByTestId('deployment-definition-editor').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    await page.getByTestId('deployment-definition-editor').screenshot({
      path: path.join(RESULTS_DIR, 'deployment-definition-compact--light--mocked.png'),
    });
    await page.setViewportSize({ width: 1536, height: 1200 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `project-deployment-history--${theme}--mocked.png`),
        fullPage: true,
      });
    }

    await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/overview`);
    await expect(page.getByTestId('project-overview-dashboard')).toBeVisible();

    await page.getByTestId('project-overview-open-token-usage').click();
    await expect(page.getByTestId('project-shell-panel-token-usage')).toBeVisible();

    await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/overview`);
    await expect(page.getByTestId('project-overview-dashboard')).toBeVisible();
    await page.getByTestId('project-overview-urls-details').click();
    await expect(page.getByTestId('project-shell-panel-project-urls')).toBeVisible();

    await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/overview`);
    await expect(page.getByTestId('project-overview-dashboard')).toBeVisible();
    await page.getByTestId('project-overview-open-wiki').click();
    await expect(page.getByTestId('project-shell-panel-wiki')).toBeVisible();

    await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/overview`);
    await expect(page.getByTestId('project-overview-dashboard')).toBeVisible();
    await page.getByTestId('project-overview-planning-plan-deployment-history').click();
    await expect.poll(() => new URL(page.url()).searchParams.get('job')).toBe('plan-deployment-history');
    await expect.poll(mocked.taskRequested).toBe(true);

    // Switch from deterministic routes to this worktree's fixture backend and
    // persist a separately labelled real-source pair.
    const realProjectName = await resolveRealProject(devBackend.baseUrl);
    if (!realProjectName) {
      test.info().annotations.push({
        type: 'real-source-evidence',
        description: 'Not captured because the fixture backend has no configured watch path.',
      });
      return;
    }
    await page.unrouteAll({ behavior: 'ignoreErrors' });
    await proxyBackend(page, devBackend.baseUrl);
    await openDashboard(page, realProjectName, true);
    await expect(page.getByTestId('project-overview-refresh')).toHaveText('Refresh', { timeout: 20_000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `project-overview-dashboard--${theme}--real.png`),
        fullPage: true,
      });
    }
    await page.getByTestId('project-overview-open-deployment').click();
    await expect(page.getByTestId('project-deployment-panel')).toBeVisible();
    await expect(page.getByTestId('project-deployment-refresh')).toHaveText('Refresh', { timeout: 20_000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: path.join(RESULTS_DIR, `project-deployment-history--${theme}--real.png`),
        fullPage: true,
      });
    }
  });
});
