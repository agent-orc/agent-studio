import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * PUB-1: the Project Hub overview shows read-only publish badges derived from
 * repo facts - "NuGet 0.3.1 -> 4 tasks pending", a website delta, and the
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

function snapshot() {
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
    publishTargets: PUBLISH_TARGETS,
    queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
  };
}

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route(/\/api\/v1\/studio\/board/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));
  // The snapshot under test - publishTargets folded in.
  await page.route('**/api/projects/*/snapshot**', r => r.fulfill(json(snapshot())));
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
  await page.route('**/api/v1/workspaces', r => r.fulfill(json([{
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
 * Seed a persisted Project Hub tab pinned to the overview rail BEFORE the app
 * boots (via addInitScript, like the passing board specs) so the Hub opens
 * straight on the overview rail with all routes already mocked - no reload race
 * that lets an early /api/watch-paths escape to the network.
 */
async function openHubOnOverview(page: Page): Promise<void> {
  await page.addInitScript(({ tabKey, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__' },
        { kind: 'hub', projectName: project, section: 'overview' },
      ],
      activeKey: tabKey,
    }));
  }, { tabKey: HUB_TAB_KEY, project: PROJECT });
  await installRoutes(page);
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

function resultsDir(): string {
  const fromEnv = process.env.PUB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pub-1');
}

async function dismissHarnessOverlay(page: Page): Promise<void> {
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
    document.querySelectorAll('.overlay--error, app-error-dialog, [data-testid="error-dialog"], [data-testid="error-dialog-overlay"]')
      .forEach(n => ((n as HTMLElement).style.display = 'none'));
  });
}

async function openPublishingActions(page: Page): Promise<void> {
  const publishing = page.getByTestId('project-overview-publishing-actions');
  await expect(publishing).toBeVisible({ timeout: 15_000 });
  await publishing.locator('summary').click();
}

test.describe('PUB-1 · Project Hub publish badges (mocked)', () => {
  test.setTimeout(180_000);

  test('renders package/website/first-publish badges and stays quiet on zero-pending', async ({ page }, testInfo) => {
    await openHubOnOverview(page);
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    await dismissHarnessOverlay(page);
    await openPublishingActions(page);

    const badges = page.getByTestId('project-publish-badges');
    await expect(badges).toBeVisible({ timeout: 15_000 });

    // Package delta badge with version + pending count.
    const nuget = page.getByTestId('publish-badge-package:nuget');
    await expect(nuget).toBeVisible();
    await expect(nuget).toContainText('NuGet 0.3.1');
    await expect(nuget).toContainText('4 tasks pending');

    // Website delta badge - and the website styling modifier is applied, which
    // depends on the camelCase `kind` value the backend actually emits.
    const website = page.getByTestId('publish-badge-website');
    await expect(website).toBeVisible();
    await expect(website).toContainText('Website');
    await expect(website).toContainText('2 tasks pending');
    await expect(website).toHaveClass(/publish__badge--website/);
    await expect(nuget).not.toHaveClass(/publish__badge--website/);

    // First-publish special state.
    const npm = page.getByTestId('publish-badge-package:npm');
    await expect(npm).toBeVisible();
    await expect(npm).toContainText('first publish pending');
    await expect(npm).toContainText('manual, operator');

    // Quiet target renders NO badge (Ruhe).
    await expect(page.getByTestId('publish-badge-package:quiet')).toHaveCount(0);

    // Strip the global error dialog / dev overlays before the evidence frame.
    // Under full API mocking a sibling overview component (regression radar /
    // cli-environment) reacts to an empty mocked response and trips the global
    // ErrorHandler - harness noise, unrelated to the publish badges, which have
    // already asserted above. (Same treatment as card-merge-signal.spec.ts.)
    const errMsg = await page.locator('[data-testid="error-dialog-message"]').first().textContent().catch(() => null);
    if (errMsg && errMsg.trim()) console.log(`[publish-badges spec] global error-dialog present (harness noise): ${errMsg.trim().slice(0, 160)}`);
    await page.evaluate(() => {
      document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
      document.querySelectorAll('.overlay--error, app-error-dialog, [data-testid="error-dialog"]')
        .forEach(n => ((n as HTMLElement).style.display = 'none'));
    });
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
    await dismissHarnessOverlay(page);
    await openPublishingActions(page);
    const panelResponse = page.waitForResponse(response => response.url().includes('/publish/') && response.url().endsWith('/panel'));
    await page.getByTestId('publish-badge-package:nuget').evaluate((element: HTMLButtonElement) => element.click());
    await expect.poll(async () => (await panelResponse).status()).toBe(200);

    const panel = page.getByTestId('publish-action-panel');
    await expect(panel).toBeVisible();
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

    await dismissHarnessOverlay(page);
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
