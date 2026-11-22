import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * PUB-1: the Project Hub overview shows read-only publish badges derived from
 * repo facts - "NuGet 0.3.1 -> 4 commits pending", a website delta, and the
 * "first publish pending" special state. Zero-pending targets render no badge
 * (Ruhe). This spec mounts the Hub on the overview rail with the project
 * snapshot mocked (publishTargets folded in), so no live backend is required;
 * the screenshot is labelled `--mocked`.
 */

const PROJECT = 'coding-agent-runner';
const REPO_PATH = 'C:/repo/coding-agent-runner';
const HUB_TAB_KEY = `hub:${PROJECT}`;
const BOOT_TIMEOUT = 60_000;

// `kind` is the camelCase enum value the backend emits (JsonStringEnumConverter).
const PUBLISH_TARGETS = [
  {
    id: 'package:nuget', kind: 'package', ecosystem: 'nuget', label: 'NuGet',
    packageName: 'Coding.Agent.Runner', currentVersion: '0.3.1',
    firstPublishPending: false, pendingCount: 4, referenceKind: 'tag', reference: 'v0.3.1',
  },
  {
    id: 'website', kind: 'website', ecosystem: null, label: 'Website',
    packageName: null, currentVersion: null,
    firstPublishPending: false, pendingCount: 2, referenceKind: 'release-tag', reference: 'v0.3.1',
  },
  {
    // A package that has never been released -> the first-publish special state.
    id: 'package:npm', kind: 'package', ecosystem: 'npm', label: 'npm',
    packageName: 'coding-agent-chat', currentVersion: null,
    firstPublishPending: true, pendingCount: null, referenceKind: 'none', reference: null,
  },
  {
    // Quiet target: nothing pending -> MUST render no badge.
    id: 'package:quiet', kind: 'package', ecosystem: 'nuget', label: 'NuGet',
    packageName: 'Quiet.Pkg', currentVersion: '1.0.0',
    firstPublishPending: false, pendingCount: 0, referenceKind: 'tag', reference: 'v1.0.0',
  },
];

function snapshot(targets: unknown[] = PUBLISH_TARGETS) {
  return {
    project: PROJECT,
    capturedAt: '2026-07-10T12:00:00Z',
    paths: { path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH },
    settings: {
      autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'on-completed',
      runnerMode: 'manual', orchestratorModel: null, orchestratorThinkingLevel: null,
      laneSortStrategies: {},
    },
    runnerStatus: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
    orchestratorLogTail: [],
    orchestratorSession: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    publishTargets: targets,
    queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
  };
}

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page, targets?: unknown[]): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route('**/api/v1/studio/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route(/\/api\/v1\/studio\/board/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));
  // The snapshot under test - publishTargets folded in.
  await page.route('**/api/projects/*/snapshot**', r => r.fulfill(json(snapshot(targets))));
  await page.route('**/api/projects/*/throughput', r => r.fulfill(json({
    project: PROJECT, capturedAt: '2026-07-10T12:00:00Z', completedLast24h: 0,
    completedLast7d: 0, recentCompletions: [],
  })));
  await page.route('**/api/projects/*/token-usage/summary', r => r.fulfill(json({
    project: PROJECT, hasData: false,
    lifetimeTotalTokens: 0, lifetimeJobTokens: 0, lifetimeSupportingTokens: 0,
    lifetimeOrchestratorTokens: 0, lifetimeCalls: 0,
    last24hTotalTokens: 0, last24hJobTokens: 0, last24hSupportingTokens: 0,
    last24hOrchestratorTokens: 0, last24hCalls: 0,
    last7dTotalTokens: 0, last7dJobTokens: 0, last7dSupportingTokens: 0,
    last7dOrchestratorTokens: 0, last7dCalls: 0,
    firstActivity: null, lastActivity: null, fetchedAt: '2026-07-10T12:00:00Z', disclaimer: '',
  })));
  await page.route('**/api/projects/*/deployment/summary', r => r.fulfill(json({
    project: PROJECT, available: false, reason: 'No history.', source: 'logs/stable-restarts.jsonl',
    lastDeployment: null, pendingCount: null, pendingCommits: [],
  })));
  await page.route('**/api/projects/*/wiki/pulse**', r => r.fulfill(json({
    projectName: PROJECT, baseDir: REPO_PATH, exists: true, generatedAtUtc: '2026-07-10T12:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: { available: true, reason: null, overallGrade: 'Fresh', areas: [], counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  })));
  await page.route('**/api/projects/*/wiki/recent**', r => r.fulfill(json({
    projectName: PROJECT, baseDir: REPO_PATH, exists: true, edits: [],
  })));
  await page.route('**/api/projects/*/visual-evidence**', r => r.fulfill(json({
    project: PROJECT, capturedAt: '2026-07-10T12:00:00Z', unseenCount: 0, items: [],
  })));
  await page.route('**/api/workspaces/*/quota/admission', r => r.fulfill(json({
    workspaceId: 'ws', projects: [], workspace: {
      projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false,
    },
  })));
  await page.route('**/api/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/git/inventory**', r => r.fulfill(json({
    projectName: PROJECT, repositoryPath: REPO_PATH, isRepo: true, currentBranch: 'develop',
    branches: [], worktrees: [], recentCommits: [], error: null,
  })));
  const calendar = {
    timeZone: 'UTC', weekStart: 1, dayStartUtc: '2026-07-10T00:00:00Z', dayEndUtc: '2026-07-11T00:00:00Z',
    weekStartUtc: '2026-07-06T00:00:00Z', weekEndUtc: '2026-07-13T00:00:00Z',
  };
  await page.route('**/api/usage/cockpit**', r => r.fulfill(json({
    snapshotVersion: 1, workspaceId: 'ws', timeZone: 'UTC', weekStart: 1,
    generatedAt: '2026-07-10T12:00:00Z', calendar, clis: [], runs: [], slots: [], sources: {},
    cost: { currency: 'USD', calendar, todayUsd: null, weekUsd: null, projects: [],
      coverage: { status: 'unavailable', observedAt: null, ttlSeconds: null },
      pricingVersion: 'fixture', normalizationVersion: 'fixture', ledgerEndpointTemplate: '' },
  })));
  const metric = { median: null, p90: null, samples: 0, last7d: [], last30d: [] };
  await page.route('**/api/projects/*/token-usage/reviews**', r => r.fulfill(json({
    deliveryTokens: metric, deliveryUsd: metric, roundsUntilAcceptance: metric,
    tokensPerAcceptedCard: metric, usdPerAcceptedCard: metric, reviewSharePercent: metric,
    byStepTokens: {}, byStepUsd: {}, byModelLevelTokens: {}, byModelLevelUsd: {},
    acceptedCards: [], reviews: [], warning: null,
  })));
  await page.route('**/api/projects/*/workbenches**', r => r.fulfill(json({
    projectName: PROJECT, includesHistory: true, count: 0, items: [],
  })));
  await page.route(/\/api\/(?:v1\/)?workspaces(?:\?|$)/, r => r.fulfill(json([{
    id: 'ws', displayName: 'Workspace', projects: [{
      id: 'PROJ-1', displayName: PROJECT, workspaceId: 'ws', storageLocation: REPO_PATH,
      sortOrder: 0, archived: false, urls: [],
    }],
  }])));
  await page.route(/\/api\/projects\/[^/]+\/publish\/package(?::|%3A)nuget\/panel/i, r => r.fulfill(json({
    project: PROJECT,
    target: PUBLISH_TARGETS[0],
    automationMode: 'suggest',
    pendingTasks: [
      { taskId: 'restore-retry', taskKey: 'CAR-41', title: 'Fix restore retry', taskType: 'bug' },
      { taskId: 'remote-lease-view', taskKey: 'CAR-42', title: 'Add remote lease view', taskType: 'feature' },
    ],
    suggestedVersion: '0.4.0',
    notice: null,
    lastRun: null,
  })));
  await page.route(/\/api\/projects\/[^/]+\/publish\/package$/i, r => r.fulfill(json({
    project: PROJECT, targetId: 'package:nuget', workflow: 'release.yml', runId: null,
    status: 'queued', conclusion: null, version: '0.4.0', url: null,
    triggeredAt: '2026-07-10T12:05:00Z', error: null,
  })));
}

/**
 * Seed a persisted Project Hub tab after the app has loaded, then reload into
 * the overview rail. Select the restored Deck tab after startup, which can
 * activate Workspace settings while the registry is loading.
 */
async function openHubOnOverview(page: Page, targets?: unknown[]): Promise<void> {
  await installRoutes(page, targets);
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
  await page.addInitScript(({ tabKey, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__' },
        { kind: 'hub', projectName: project, section: 'overview' },
      ],
      activeKey: tabKey,
    }));
    history.replaceState(null, '', '/');
  }, { tabKey: HUB_TAB_KEY, project: PROJECT });
  await page.reload({ waitUntil: 'domcontentloaded', timeout: BOOT_TIMEOUT });
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
  await page.getByRole('tab', { name: `${PROJECT} · Deck`, exact: true }).click({ timeout: BOOT_TIMEOUT });
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

function resultsDir(): string {
  const fromEnv = process.env.JOB_RESULTS_DIR ?? process.env.PUB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pub-1');
}

async function assertNoHarnessError(page: Page): Promise<void> {
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
}

async function openPublishingActions(page: Page): Promise<void> {
  const publishing = page.getByTestId('project-overview-publishing-actions');
  await expect(publishing).toBeVisible({ timeout: 15_000 });
  if (await publishing.getAttribute('open') === null) await publishing.locator('summary').click();
}

test.describe('PUB-1 · Project Hub publish badges (mocked)', () => {
  test.setTimeout(180_000);

  test('renders package/website/first-publish badges and stays quiet on zero-pending', async ({ page }, testInfo) => {
    await openHubOnOverview(page);
    await assertNoHarnessError(page);
    await openPublishingActions(page);

    const badges = page.getByTestId('project-publish-badges');
    await expect(badges).toBeVisible({ timeout: 15_000 });

    // Package delta badge with version + pending count.
    const nuget = page.getByTestId('publish-badge-package:nuget');
    await expect(nuget).toBeVisible();
    await expect(nuget).toContainText('NuGet 0.3.1');
    await expect(nuget).toContainText('4 commits pending');

    // Website delta badge - and the website styling modifier is applied, which
    // depends on the camelCase `kind` value the backend actually emits.
    const website = page.getByTestId('publish-badge-website');
    await expect(website).toBeVisible();
    await expect(website).toContainText('Website');
    await expect(website).toContainText('2 commits pending');
    await expect(website).toHaveClass(/publish__badge--website/);
    await expect(nuget).not.toHaveClass(/publish__badge--website/);

    // First-publish special state.
    const npm = page.getByTestId('publish-badge-package:npm');
    await expect(npm).toBeVisible();
    await expect(npm).toContainText('first publish pending');
    await expect(npm).toContainText('manual, operator');

    // Quiet target renders NO badge (Ruhe).
    await expect(page.getByTestId('publish-badge-package:quiet')).toHaveCount(0);

    await assertNoHarnessError(page);
    await badges.scrollIntoViewIfNeeded();
    await page.waitForTimeout(150);

    // Evidence screenshot (mocked API).
    fs.mkdirSync(resultsDir(), { recursive: true });
    const shotPath = path.join(resultsDir(), 'project-hub-publish-badges--mocked.png');
    await page.screenshot({ path: shotPath, fullPage: true });
    await testInfo.attach('project-hub-publish-badges--mocked.png', { path: shotPath, contentType: 'image/png' });
  });

  test('opens the guided package release with task mix, editable SemVer and workflow tracking', async ({ page }, testInfo) => {
    await openHubOnOverview(page);
    await assertNoHarnessError(page);
    await openPublishingActions(page);
    const panelResponse = page.waitForResponse(response => response.url().includes('/publish/') && response.url().endsWith('/panel'));
    await page.getByTestId('publish-badge-package:nuget').evaluate((element: HTMLButtonElement) => element.click());
    await expect.poll(async () => (await panelResponse).status()).toBe(200);

    const panel = page.getByTestId('publish-action-panel');
    await expect(panel).toBeVisible();
    await expect(panel).toContainText('NuGet · 4 pending commits');
    await expect(page.getByTestId('publish-pending-tasks')).toContainText('CAR-41');
    await expect(page.getByTestId('publish-pending-tasks')).toContainText('Add remote lease view');
    await expect(page.getByTestId('publish-version')).toHaveValue('0.4.0');
    await expect(page.getByTestId('publish-automation-mode')).toHaveValue('suggest');

    const publishResponse = page.waitForResponse(response => response.url().endsWith('/publish/package'));
    await page.getByTestId('publish-confirm').evaluate((element: HTMLButtonElement) => element.click());
    await expect.poll(async () => (await publishResponse).status()).toBe(200);
    await expect(page.getByTestId('publish-workflow-status')).toContainText('queued');
    await expect(page.getByTestId('publish-workflow-status')).toContainText('release.yml');
    await expect(page.getByTestId('publish-workflow-status')).toContainText('v0.4.0');

    await assertNoHarnessError(page);
    fs.mkdirSync(resultsDir(), { recursive: true });
    const shotPath = path.join(resultsDir(), 'package-publish-guided-flow--mocked.png');
    await panel.screenshot({ path: shotPath });
    await testInfo.attach('package-publish-guided-flow--mocked.png', { path: shotPath, contentType: 'image/png' });

    await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
    const darkShotPath = path.join(resultsDir(), 'package-publish-guided-flow-dark--mocked.png');
    await panel.screenshot({ path: darkShotPath });
    await testInfo.attach('package-publish-guided-flow-dark--mocked.png', { path: darkShotPath, contentType: 'image/png' });
  });
});

for (const theme of ['light', 'dark'] as const) {
  test(`missing library release definition links setup (${theme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: theme });
    await openHubOnOverview(page, []);
    await page.evaluate(value => document.documentElement.setAttribute('data-studio-theme', value), theme);
    await openPublishingActions(page);
    await expect(page.getByTestId('publish-not-configured')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Set up library releases' })).toHaveAttribute('href', /library-releases.md/);
    await page.getByTestId('publish-not-configured').scrollIntoViewIfNeeded();
    await assertNoHarnessError(page);
    fs.mkdirSync(resultsDir(), { recursive: true });
    await page.screenshot({ path: path.join(resultsDir(), `library-release-empty-${theme}--mocked.png`), fullPage: true });
  });
}

test('declared release previews its branch and command before execution', async ({ page }) => {
  const target = { ...PUBLISH_TARGETS[2], releaseCommand: 'bash scripts/release.sh', releaseBranch: 'main' };
  await openHubOnOverview(page, [target]);
  await page.route('**/api/projects/*/publish/*/panel', r => r.fulfill({ json: {
    project: PROJECT, target, pendingTasks: [], suggestedVersion: '0.1.0', automationMode: 'manual', notice: null, lastRun: null,
  } }));
  let body: Record<string, unknown> | undefined;
  await page.route('**/api/projects/*/publish/package', async r => {
    body = r.request().postDataJSON();
    await r.fulfill({ json: { status: 'preview', workflow: 'Branch: main\nRelease: bash scripts/release.sh', version: '0.1.0' } });
  });
  await openPublishingActions(page);
  await page.getByTestId('publish-badge-package:npm').click();
  await expect(page.getByTestId('publish-action-panel')).toContainText('Release branch: main');
  await page.getByTestId('publish-dry-run').click();
  await expect(page.getByTestId('publish-workflow-status')).toContainText('Branch: main');
  expect(body).toEqual({ targetId: 'package:npm', version: '0.1.0', dryRun: true });
  fs.mkdirSync(resultsDir(), { recursive: true });
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => document.documentElement.setAttribute('data-studio-theme', value), theme);
    await page.getByTestId('publish-action-panel').screenshot({ path: path.join(resultsDir(), `library-release-preview-${theme}--mocked.png`) });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByTestId('publish-action-panel').scrollIntoViewIfNeeded();
  await assertNoHarnessError(page);
  await page.screenshot({ path: path.join(resultsDir(), 'library-release-phone-dark--mocked.png'), fullPage: true });
});
