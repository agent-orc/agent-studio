import { expect, test, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-2692 — operator sighting (2026-08-29).
 *
 * From the "All projects" board, opening a task switched the active
 * workspace into that task's single project: the picker stopped saying
 * "All projects", the board scope narrowed, and closing the task left the
 * operator stranded in one project.
 *
 * The contract pinned here: opening a task from the cross-project board is a
 * navigation INSIDE the All-projects context. The detail still loads its own
 * project's data (that is a data scope for the detail view), but the app-wide
 * scope stays "All projects" while the task is open and after it is closed.
 * The last test pins the other half — a task opened from a project board
 * still scopes to that project, so the fix is a separation, not a removal.
 */

const ALPHA = 'Alpha Project';
const ALPHA_PATH = '/tmp/alpha-project';
const ALPHA_SLUG = 'alpha-project';
const BETA = 'Beta Project';
const BETA_PATH = '/tmp/beta-project';

const ALPHA_TASK_ID = 'alpha-scope-task';
const ALPHA_TASK_KEY = `${ALPHA_PATH}::${ALPHA_TASK_ID}`;
const BETA_TASK_ID = 'beta-scope-task';
const BETA_TASK_KEY = `${BETA_PATH}::${BETA_TASK_ID}`;

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], escalated: [],
  completed: [], archive: [],
};

function taskInfo(over: Record<string, unknown>) {
  return {
    id: ALPHA_TASK_ID,
    key: 'AGT-9001',
    displayKey: 'AGT-9001',
    taskKey: ALPHA_TASK_KEY,
    title: 'Alpha scope task',
    state: '2-ready',
    order: 1,
    agent: 'scope-agent',
    createdAt: '2026-08-29T10:00:00Z',
    watchPath: ALPHA_PATH,
    projectName: ALPHA,
    folderPath: `${ALPHA_PATH}/2-ready/${ALPHA_TASK_ID}`,
    lastActivity: '2026-08-29T10:00:00Z',
    sessionName: null,
    model: null,
    cliType: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    kind: 'task',
    ...over,
  };
}

const ALPHA_TASK = taskInfo({});
const BETA_TASK = taskInfo({
  id: BETA_TASK_ID,
  key: 'AGT-9002',
  displayKey: 'AGT-9002',
  taskKey: BETA_TASK_KEY,
  title: 'Beta scope task',
  order: 2,
  watchPath: BETA_PATH,
  projectName: BETA,
  folderPath: `${BETA_PATH}/2-ready/${BETA_TASK_ID}`,
});

function detailFor(info: Record<string, unknown>) {
  return {
    info,
    promptMarkdown: '# Scope probe',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

function evidencePath(testInfo: TestInfo, name: string): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? path.resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  fs.mkdirSync(root, { recursive: true });
  return path.join(root, name);
}

async function stubTwoProjectWorkspace(page: Page): Promise<void> {
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });

    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/workspaces') return json([]);
    if (url.pathname === '/api/projects') return json([]);
    if (url.pathname === '/api/watch-paths') {
      return json([
        { name: ALPHA, path: ALPHA_PATH, rootPath: ALPHA_PATH },
        { name: BETA, path: BETA_PATH, rootPath: BETA_PATH },
      ]);
    }
    if (url.pathname === '/api/tasks/grouped') {
      return json({ ...EMPTY_GROUPED, ready: [ALPHA_TASK, BETA_TASK] });
    }
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/tasks') return json([ALPHA_TASK, BETA_TASK]);
    if (url.pathname === `/api/tasks/${ALPHA_TASK_ID}` || url.pathname === '/api/tasks/AGT-9001') {
      return json(detailFor(ALPHA_TASK));
    }
    if (url.pathname === `/api/tasks/${BETA_TASK_ID}` || url.pathname === '/api/tasks/AGT-9002') {
      return json(detailFor(BETA_TASK));
    }
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === '/api/tags' || url.pathname.startsWith('/api/clients')) return json([]);
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    if (url.pathname === '/api/v1/management/remote-hosts/link-health') return json([]);
    if (url.pathname === '/api/runner/orchestrator-feed') return json({ entries: [] });
    if (/\/api\/cli\/[^/]+\/models$/.test(url.pathname)) return json({ models: [], source: 'scope-e2e' });
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (/\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    if (url.pathname === '/api/pipeline/accepted-integration-alert') {
      return json({ active: false, items: [] });
    }
    if (url.pathname === '/api/git/summary') return json([]);
    if (url.pathname.endsWith('/workbenches')) {
      return json({ projectName: ALPHA, includesHistory: false, count: 0, items: [] });
    }
    if (url.pathname.endsWith('/dependents')) return json([]);
    if (url.pathname.endsWith('/code-review/list')) return json({ entries: [] });
    if (url.pathname.includes('/screenshots')) return json({ screenshots: [] });
    if (url.pathname.includes('/timeline')) return json([]);
    if (url.pathname.includes('/pipeline')) return json(null);
    if (url.pathname.includes('/output')) return json([]);
    // Task-detail child sections: the detail pane fans out to these the
    // moment a task opens, and each one has a shape its consumer indexes into.
    if (url.pathname.includes('/plan')) {
      return json({
        hasPlan: false,
        source: null,
        snapshotCount: 0,
        activeItemId: null,
        softEstimateMedian: null,
        items: [],
        unassignedSubActions: [],
      });
    }
    if (url.pathname.includes('/runs')) {
      return json({
        runCount: 0,
        firstStartedAt: null,
        lastActivityAt: null,
        hasActiveRun: false,
        runs: [],
      });
    }
    if (url.pathname.includes('/session-events')) return json({ events: [], sessionChain: [] });
    if (url.pathname.includes('/claude-session')) return json(null);
    if (url.pathname.includes('/agent-work-summary')) {
      return json({
        calls: 0,
        recovered: false,
        toolCalls: 0,
        toolCounts: [],
        startedAt: null,
        lastTouchAt: null,
        currentSessionId: null,
      });
    }
    return json({});
  });
}

/** Reads the app-wide project scope straight out of its persisted home. */
async function activeProjectScope(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    try {
      const raw = window.localStorage.getItem('activeProjects');
      const parsed = raw ? JSON.parse(raw) : [];
      return Array.isArray(parsed) ? parsed.map(String) : [];
    } catch {
      return [];
    }
  });
}

const picker = (page: Page) => page.getByTestId('studio-project-picker-trigger');
const card = (page: Page, title: string) =>
  page.getByTestId('task-card').filter({ hasText: title }).first();

test.describe('AGT-2692 All-projects board keeps its scope when a task opens', () => {
  test.beforeEach(async ({ page }) => {
    await stubTwoProjectWorkspace(page);
  });

  test('opening a task from the All-projects board leaves the active scope on All projects', async ({ page }, testInfo) => {
    await page.goto('/#/board', { waitUntil: 'commit' });

    await expect(picker(page)).toContainText('All projects');
    await expect(card(page, 'Alpha scope task')).toBeVisible();
    await expect(card(page, 'Beta scope task')).toBeVisible();
    expect(await activeProjectScope(page)).toEqual([]);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: evidencePath(testInfo, `all-projects-board--${theme}.png`) });
    }

    // Open a task that belongs to Beta from the cross-project board.
    await card(page, 'Beta scope task').click();

    await expect(page.getByTestId(`studio-tab-task:${BETA_TASK_KEY}`))
      .toHaveAttribute('aria-selected', 'true');
    // The detail is open and loaded its own project's data, but the app-wide
    // scope did not move into Beta.
    await expect(picker(page)).toContainText('All projects');
    expect(await activeProjectScope(page)).toEqual([]);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: evidencePath(testInfo, `task-open-keeps-all-projects--${theme}.png`) });
    }

    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('closing the task returns to the All-projects board, not a single project', async ({ page }, testInfo) => {
    await page.goto('/#/board', { waitUntil: 'commit' });
    await expect(picker(page)).toContainText('All projects');

    await card(page, 'Beta scope task').click();
    const taskTab = page.getByTestId(`studio-tab-task:${BETA_TASK_KEY}`);
    await expect(taskTab).toHaveAttribute('aria-selected', 'true');

    await taskTab.getByRole('button', { name: 'Close tab' }).click();

    await expect(page.getByTestId('studio-tab-board:__all__'))
      .toHaveAttribute('aria-selected', 'true');
    await expect(picker(page)).toContainText('All projects');
    // Both projects are back on the board — the operator was not stranded.
    await expect(card(page, 'Alpha scope task')).toBeVisible();
    await expect(card(page, 'Beta scope task')).toBeVisible();
    expect(await activeProjectScope(page)).toEqual([]);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: evidencePath(testInfo, `task-close-returns-to-all-projects--${theme}.png`) });
    }

    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('a task opened from a project board still scopes the app to that project', async ({ page }, testInfo) => {
    await page.goto(`/#/projects/${ALPHA_SLUG}/board`, { waitUntil: 'commit' });

    await expect(picker(page)).toContainText(ALPHA);
    await expect.poll(() => activeProjectScope(page)).toEqual([ALPHA]);

    await card(page, 'Alpha scope task').click();

    await expect(page.getByTestId(`studio-tab-task:${ALPHA_TASK_KEY}`))
      .toHaveAttribute('aria-selected', 'true');
    await expect(picker(page)).toContainText(ALPHA);
    await expect.poll(() => activeProjectScope(page)).toEqual([ALPHA]);

    await page.screenshot({ path: evidencePath(testInfo, 'task-open-from-project-board-keeps-project.png') });
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });
});
