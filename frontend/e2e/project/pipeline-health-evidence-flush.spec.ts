import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3000 evidence: a workspace evidence flush that keeps failing (the
 * 2026-09-27 stale `index.lock` night) raises the `evidence-flush-stalled`
 * pipeline health alarm, and the project Pipeline rail shows it with the
 * repository and the git error.
 *
 * Fully MOCKED (no backend): `/api/**` and `/hubs/**` are stubbed, the same
 * way as `hub-project-wiki-switch.spec.ts`, so the real compiled component
 * renders deterministically and the shots are labelled `--mocked`.
 */

const PROJECT = 'Agent Taskboard';
const STORAGE = 'C:/repos/agent-taskboard';
const REPOSITORY = 'C:\\Projects\\agent-taskboard-workspace';

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-health-evidence-flush');
})();

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

async function installMocks(page: Page): Promise<void> {
  // Broad fallbacks first; Playwright runs later-registered routes first.
  await page.route('**/api/**', (route: Route) =>
    route.fulfill(json(route.request().method() === 'GET' ? [] : {})));
  await page.route('**/hubs/**', (route: Route) => route.abort());
  await page.route('**/api/crash-recovery/pending', r => r.fulfill(json({ pending: [] })));
  await page.route('**/api/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/tasks/grouped**', r => r.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  })));
  await page.route('**/api/tasks/archive**', r => r.fulfill(json({ items: [], total: 0, offset: 0, limit: 50 })));
  await page.route('**/api/runner/status', r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/cli/quota', r => r.fulfill(json({ at: '2026-09-27T16:00:00Z', snapshots: [], ttlSeconds: 600 })));
  await page.route('**/api/cli/usage', r => r.fulfill(json({ snapshots: [], ttlSeconds: 600 })));
  await page.route('**/api/watch-paths', r => r.fulfill(json([{ name: PROJECT, path: STORAGE, rootPath: STORAGE }])));
  await page.route('**/api/workspaces**', r => r.fulfill(json([{
    id: 'ws1', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-09-01T00:00:00Z',
    projects: [{
      id: 'proj1', displayName: PROJECT, shortCode: 'AGT', workspaceId: 'ws1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: STORAGE, urls: [], archived: false, createdAt: '2026-09-01T00:00:00Z',
    }],
  }])));
  // The Deck opens on Overview before the Pipeline rail is chosen; these are
  // the minimum shapes its panels dereference (see hub-project-wiki-switch.spec.ts).
  await page.route('**/api/projects/*/snapshot', r => r.fulfill(json({
    project: PROJECT,
    capturedAt: '2026-09-27T15:50:00Z',
    paths: { path: STORAGE, rootPath: STORAGE, repositoryPath: STORAGE },
    settings: {
      autoCommit: false, crashRecoveryEnabled: false, autoPushStrategy: 'never',
      runnerMode: null, orchestratorModel: null, laneSortStrategies: {},
    },
    runnerStatus: null,
    orchestratorLogTail: [],
    orchestratorSession: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
  })));
  await page.route('**/api/projects/*/visual-evidence', r => r.fulfill(json({
    project: PROJECT, capturedAt: '2026-09-27T15:50:00Z', unseenCount: 0, items: [],
  })));
  await page.route('**/api/projects/*/token-usage/summary', r => r.fulfill(json({
    project: PROJECT, hasData: false,
    lifetimeTotalTokens: 0, lifetimeJobTokens: 0, lifetimeSupportingTokens: 0, lifetimeOrchestratorTokens: 0, lifetimeCalls: 0,
    last24hTotalTokens: 0, last24hJobTokens: 0, last24hSupportingTokens: 0, last24hOrchestratorTokens: 0, last24hCalls: 0,
    last7dTotalTokens: 0, last7dJobTokens: 0, last7dSupportingTokens: 0, last7dOrchestratorTokens: 0, last7dCalls: 0,
    firstActivity: null, lastActivity: null, fetchedAt: '2026-09-27T15:50:00Z', disclaimer: '',
  })));
  await page.route('**/api/projects/*/deployment/summary', r => r.fulfill(json({
    project: PROJECT, available: false, reason: 'No history.', source: 'logs/stable-restarts.jsonl',
    lastDeployment: null, pendingCount: null, pendingCommits: [],
  })));
  await page.route('**/api/projects/*/wiki/pulse**', r => r.fulfill(json({
    projectName: PROJECT, baseDir: STORAGE, exists: true, generatedAtUtc: '2026-09-27T15:50:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true, reason: null, overallGrade: 'Fresh', areas: [],
      counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
    },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  })));
  await page.route('**/api/git/inventory**', r => r.fulfill(json({
    projectName: PROJECT, repositoryPath: STORAGE, isRepo: true, currentBranch: 'develop',
    worktrees: [], recentCommits: [], branches: [], error: null,
  })));
  await page.route('**/api/projects/*/workbenches**', r => r.fulfill(json({ items: [] })));
  await page.route('**/api/workbenches**', r => r.fulfill(json({ items: [] })));
  await page.route('**/api/projects/pipeline-catalogue**', r => r.fulfill(json({ pipelineId: 'default', steps: [] })));
  await page.route('**/api/projects/settings', r => r.fulfill(json({ [PROJECT]: { pipelineSteps: {}, pipelineStepOrder: [] } })));
  await page.route('**/api/projects/*/pipeline-health', r => r.fulfill(json({
    project: PROJECT,
    capturedAtUtc: '2026-09-27T15:50:00Z',
    status: 'alarm',
    activeGate: null,
    fingerprint: null,
    lanes: [
      { lane: '2-ready', queueCount: 2, completedPerHour: 1, isStalled: false },
      { lane: '3-progress', queueCount: 1, completedPerHour: 1, isStalled: false },
      { lane: '4-auto-review', queueCount: 1, completedPerHour: 2, isStalled: false },
      { lane: '5-human-review', queueCount: 3, completedPerHour: 2, isStalled: false },
    ],
    alerts: [{
      kind: 'evidence-flush-stalled',
      severity: 'high',
      summary: 'Workspace evidence flush failing for 17 min',
      detail: `Repository ${REPOSITORY}: 9 consecutive evidence flushes failed since 2026-09-27T15:32:04Z. `
        + 'No evidence commit reaches the workspace repository until this clears. '
        + `Last error: git-add: fatal: Unable to create '${REPOSITORY}\\.git\\index.lock': File exists.`,
      detectedAtUtc: '2026-09-27T15:49:12Z',
      repository: REPOSITORY,
    }],
  })));
}

test('pipeline health shows a stalled workspace evidence flush with repository and error', async ({ page }) => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await installMocks(page);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.tabs.v1'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload({ waitUntil: 'domcontentloaded', timeout: 30_000 });
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });

  const hubRow = page.getByTestId(`studio-explorer-project-hub-${PROJECT}`);
  if (!(await hubRow.count()) || !(await hubRow.isVisible())) {
    await page.getByTestId(`studio-explorer-project-${PROJECT}`).first().click();
  }
  await hubRow.click();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
  await page.getByTestId('project-shell-rail-pipeline').click();

  const health = page.getByTestId('pipeline-health');
  await expect(health).toHaveAttribute('data-status', 'alarm');
  const stall = page.getByTestId('pipeline-health-evidence-flush');
  await expect(stall).toHaveCount(1);
  await expect(stall).toHaveAttribute('data-repository', REPOSITORY);
  await expect(stall).toContainText('Workspace evidence flush failing for 17 min');
  await expect(stall).toContainText('index.lock');
  await expect(stall).toContainText(REPOSITORY);

  await setTheme(page, 'light');
  await health.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-health-evidence-flush-light.png') });
  await setTheme(page, 'dark');
  await health.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-health-evidence-flush-dark.png') });
});
