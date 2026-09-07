import { expect, test, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Route Project';
const PROJECT_SLUG = 'route-project';
const TASK_REFERENCE = 'AGT-2291';
const TASK_ID = 'route-restoration-task';
const EPIC_REFERENCE = 'AGT-2200';
const EPIC_ID = 'route-restoration-epic';
const WATCH_PATH = '/tmp/route-project';
const TASK_KEY = `${WATCH_PATH}::${TASK_ID}`;
const WORKBENCH_KEY = 'ROU-W4';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], escalated: [],
  completed: [], archive: [],
};

const TASK_DETAIL = {
  info: {
    id: TASK_ID,
    key: TASK_REFERENCE,
    displayKey: TASK_REFERENCE,
    taskKey: TASK_KEY,
    title: 'Route restoration task',
    state: '3-progress',
    order: 1,
    agent: 'route-agent',
    createdAt: '2026-07-24T10:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/${TASK_ID}`,
    lastActivity: '2026-07-24T10:00:00Z',
    sessionName: null,
    model: null,
    cliType: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    references: {
      dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: [WORKBENCH_KEY],
    },
  },
  promptMarkdown: '# Route restoration',
  promptHistory: [],
  titleHistory: [],
  statusMarkdown: null,
  contextUsage: null,
  log: [],
  summaryState: null,
  reviewEvidence: [],
};

const ALL_PROJECTS_GROUPED = {
  ...EMPTY_GROUPED,
  progress: [TASK_DETAIL.info],
};

const EPIC_DETAIL = {
  ...TASK_DETAIL,
  info: {
    ...TASK_DETAIL.info,
    id: EPIC_ID,
    key: EPIC_REFERENCE,
    displayKey: EPIC_REFERENCE,
    taskKey: `${WATCH_PATH}::${EPIC_ID}`,
    title: 'Route restoration epic',
    kind: 'epic',
    state: '1-backlog',
    folderPath: `${WATCH_PATH}/1-backlog/${EPIC_ID}`,
  },
};

function evidencePath(testInfo: TestInfo, name: string): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? path.resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  fs.mkdirSync(root, { recursive: true });
  return path.join(root, name);
}

async function stubRouteData(page: Page): Promise<void> {
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
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (url.pathname.startsWith('/api/runner/token-summary-aggregate')) {
      return json({
        projects: 0,
        orchestratorEntries: 0,
        orchestratorLlmCalls: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCacheReadTokens: 0,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: true,
        byModel: [],
        byProject: [],
        fetchedAt: '2026-07-24T10:00:00Z',
        disclaimer: '',
      });
    }
    if (url.pathname.startsWith('/api/workspace/tokens/timeline')) {
      return json({
        windowStart: '2026-07-23T10:00:00Z',
        windowEnd: '2026-07-24T10:00:00Z',
        windowHours: Number(url.searchParams.get('windowHours') ?? 24),
        bucketMinutes: Number(url.searchParams.get('bucketMinutes') ?? 60),
        bucketCount: 0,
        cells: [],
        projects: [],
        fetchedAt: '2026-07-24T10:00:00Z',
        disclaimer: '',
      });
    }
    if (url.pathname.startsWith('/api/workspace/tokens/expensive-jobs')) return json({ jobs: [] });
    if (url.pathname.startsWith('/api/adhoc-usage')) {
      return json({
        calls: 0,
        inputTokens: 0,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: true,
        bySource: [],
        byDay: [],
        byModel: [],
        logPath: '',
        logSizeBytes: 0,
        logModifiedAt: null,
        disclaimer: '',
      });
    }
    if (url.pathname === '/api/tags' || url.pathname === '/api/clients' || url.pathname === '/api/clients/') {
      return json([]);
    }
    if (url.pathname === '/api/git/summary') return json([]);
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    if (/\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    if (url.pathname === '/api/watch-paths') {
      return json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/tasks/grouped') return json(EMPTY_GROUPED);
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === `/api/epics/${EPIC_ID}`) {
      return json({
        id: EPIC_ID,
        title: 'Route restoration epic',
        projectName: PROJECT,
        watchPath: WATCH_PATH,
        state: '1-backlog',
        subTaskTotal: 0,
        completed: 0,
        inProgress: 0,
        open: 0,
        subTasks: [],
      });
    }
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === `/api/tasks/${TASK_REFERENCE}` || url.pathname === `/api/tasks/${TASK_ID}`) {
      return json(TASK_DETAIL);
    }
    if (url.pathname === `/api/tasks/${EPIC_REFERENCE}` || url.pathname === `/api/tasks/${EPIC_ID}`) {
      return json(EPIC_DETAIL);
    }
    if (url.pathname === '/api/tasks') return json([]);
    if (url.pathname.endsWith('/wiki/tree')) {
      return json({
        projectName: PROJECT,
        baseDir: `${WATCH_PATH}/docs`,
        exists: true,
        root: [{
          name: 'concepts',
          title: 'Concepts',
          relPath: 'concepts',
          type: 'folder',
          children: [{
            name: 'routing.md',
            title: 'Routing',
            relPath: 'concepts/routing.md',
            type: 'md',
            children: [],
          }],
        }],
      });
    }
    if (url.pathname.endsWith('/wiki/files/concepts/routing.md')) {
      return json({ relPath: 'concepts/routing.md', content: '# Routing\n\nRestored Wiki page.' });
    }
    if (url.pathname.endsWith('/wiki/history/concepts/routing.md')) {
      return json({
        relPath: 'concepts/routing.md',
        model: null,
        metadata: {
          model: null, updatedAt: null, reason: null, taskKey: null,
          status: null, runCount: null, hasFrontmatter: false,
        },
        commits: [],
      });
    }
    if (url.pathname.endsWith('/wiki/pulse')) {
      return json({
        projectName: PROJECT,
        baseDir: `${WATCH_PATH}/docs`,
        exists: true,
        generatedAtUtc: '2026-07-24T10:00:00Z',
        feed: { available: true, reason: null, items: [] },
        inbox: { available: true, reason: null, count: 0, items: [] },
        drift: {
          available: true, reason: null, overallGrade: 'Fresh', areas: [],
          counts: { fresh: 1, aging: 0, stale: 0, graded: 1 },
        },
        critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
      });
    }
    if (url.pathname.endsWith('/wiki/grading/status')) return json({ status: null });
    if (url.pathname === '/api/cli/maintenance-model') {
      return json({ cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null });
    }
    if (url.pathname.endsWith('/style-guides')) {
      return json({
        projectKey: 'ROUTE', projectDisplayName: PROJECT, technologies: [],
        guides: [], warnings: [], snapshotId: 'route', capturedAtUtc: null, refreshAfterUtc: null,
      });
    }
    if (url.pathname.endsWith('/wiki/home')) return json({ sections: [] });
    if (url.pathname.endsWith('/workbenches')) {
      return json({
        projectName: PROJECT,
        includesHistory: false,
        count: 1,
        items: [{
          id: 'route-lab',
          key: WORKBENCH_KEY,
          title: 'Route Lab',
          summary: 'Deep-link restoration proof.',
          status: 'active',
          phase: 'testing',
          updatedAtUtc: '2026-07-24T10:00:00Z',
          entryPath: 'docs/concepts/route-lab.html',
          valid: true,
          error: null,
          sourceTaskKeys: [TASK_REFERENCE],
          relatedTaskKeys: [],
        }],
      });
    }
    if (url.pathname.endsWith('/workbenches/route-lab')) {
      return json({
        workbench: {
          id: 'route-lab',
          key: WORKBENCH_KEY,
          title: 'Route Lab',
          summary: 'Deep-link restoration proof.',
          status: 'active',
          phase: 'testing',
          updatedAtUtc: '2026-07-24T10:00:00Z',
          entryPath: 'docs/concepts/route-lab.html',
          valid: true,
          error: null,
          sourceTaskKeys: [TASK_REFERENCE],
          relatedTaskKeys: [],
        },
        html: '<h1>Route Lab</h1><p>Restored Dossier.</p>',
        branch: 'task/route',
        revision: '1234567890abcdef',
        workingTreeModified: false,
        fingerprint: 'a'.repeat(64),
      });
    }
    if (/\/api\/cli\/[^/]+\/models$/.test(url.pathname)) {
      return json({ models: [], source: 'route-e2e' });
    }
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (url.pathname.includes('/screenshots')) return json({ screenshots: [] });
    if (url.pathname.includes('/timeline')) return json([]);
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
    if (url.pathname.includes('/pipeline')) return json(null);
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
    if (url.pathname.includes('/output')) return json([]);
    if (request.method() === 'GET') return json({});
    return json({});
  });
}

test.describe('Studio route restoration', () => {
  test.setTimeout(180_000);

  test.beforeEach(async ({ page }) => {
    await stubRouteData(page);
  });

  test('Task detail route restores its tab state and survives reload', async ({ page }, testInfo) => {
    await page.goto(`/#/tasks/${TASK_REFERENCE}?view=evidence%3Aprotocol`, { waitUntil: 'commit' });

    await expect(page.getByTestId('prompt-tab-evidence')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('inspector-tab-protocol')).toHaveAttribute('aria-selected', 'true');
    await expect.poll(() => new URL(page.url()).hash).toBe('#/tasks/AGT-2291?view=evidence%3Aprotocol');
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);

    await page.screenshot({ path: evidencePath(testInfo, 'route-restoration-task-tabs.png') });
    await page.getByTestId('prompt-tab-timeline').click();
    await expect.poll(() => new URL(page.url()).hash).toBe('#/tasks/AGT-2291?view=timeline%3Aprotocol');
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('prompt-tab-timeline')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('inspector-tab-protocol')).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('Task detail opens a linked document reference', async ({ page }, testInfo) => {
    await page.goto(`/#/tasks/${TASK_REFERENCE}`, { waitUntil: 'commit' });

    const reference = page.getByTestId(`reference-chip-${WORKBENCH_KEY}`);
    await expect(reference).toBeVisible({ timeout: 30_000 });
    await expect(reference).toContainText(WORKBENCH_KEY);
    await expect(reference).toContainText('Route Lab');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `task-document-reference--mocked-${theme}.png`),
      });
    }

    await reference.getByRole('button').click();
    await expect(page.getByTestId('workbench-viewer')).toContainText('Route Lab');
    await expect.poll(() => new URL(page.url()).hash)
      .toBe('#/projects/route-project/workbenches/route-lab');
  });

  test('Wiki page route restores, generates an honest route, and survives reload', async ({ page }, testInfo) => {
    const route = `/#/projects/${PROJECT_SLUG}/wiki?page=concepts%2Frouting.md`;
    await page.goto(route, { waitUntil: 'commit' });

    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText('concepts/routing.md');
    await expect(page.getByTestId('project-wiki-viewer')).toContainText('Restored Wiki page');
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('#/projects/route-project/wiki?page=concepts%2Frouting.md');
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);

    await page.goto(`/#/projects/${PROJECT_SLUG}/wiki`, { waitUntil: 'commit' });
    await page.getByTestId('project-wiki-file-concepts/routing.md').click();
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('#/projects/route-project/wiki?page=concepts%2Frouting.md');
    await page.screenshot({ path: evidencePath(testInfo, 'route-restoration-wiki-page.png') });
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText('concepts/routing.md');
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('Dossier route restores and survives reload', async ({ page }, testInfo) => {
    await page.addInitScript(() => {
      if (window.sessionStorage.getItem('workbench-reveal-test-initialized')) return;
      window.sessionStorage.setItem('workbench-reveal-test-initialized', 'true');
      window.localStorage.setItem('atp.studio.explorer.expanded', '[]');
      window.localStorage.setItem('atp.studio.explorerSections', JSON.stringify({
        workspace: true,
        'ws:__all__': true,
      }));
      window.localStorage.setItem('atp.studio.explorer.workbenches.expanded.v1', '[]');
    });
    await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches/route-lab`, { waitUntil: 'commit' });

    await expect(page.getByTestId('workbench-viewer')).toContainText('Route Lab');
    const referenceChip = page.getByTestId('workbench-key-chip');
    await expect(referenceChip).toHaveText(WORKBENCH_KEY);
    await expect(page.frameLocator('[data-testid="workbench-viewer-frame"]')
      .getByRole('heading', { name: 'Route Lab' })).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toBe('#/projects/route-project/workbenches/route-lab');
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);

    const projectRow = page.getByTestId(`studio-explorer-project-${PROJECT}`);
    await expect(projectRow).toBeVisible();
    await expect(projectRow).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('studio-explorer-workspace-head')).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('studio-explorer-ws-group-__all__')).toHaveAttribute('aria-expanded', 'true');

    const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${PROJECT}`);
    const activeWorkbench = page.getByTestId(`studio-explorer-workbench-${PROJECT}-route-lab`);
    await expect(workbenchesRow).toHaveAttribute('aria-expanded', 'true');
    await expect(workbenchesRow).not.toHaveAttribute('aria-current', 'page');
    await expect(activeWorkbench).toHaveAttribute('aria-current', 'page');
    await expect(activeWorkbench).toBeInViewport();
    await expect(activeWorkbench).toContainText('testing');
    await expect(activeWorkbench).not.toContainText(/updated|today|\d+d/);
    await activeWorkbench.hover();
    await expect(page.locator('.app-tooltip-overlay')).toContainText(WORKBENCH_KEY);
    await expect.poll(() => page.evaluate(() => JSON.parse(
      window.localStorage.getItem('atp.studio.explorer.workbenches.expanded.v1') ?? '[]',
    ))).toContain(PROJECT);

    await page.mouse.move(900, 500);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-tree-after--mocked-${theme}.png`),
      });
    }
    await referenceChip.click();
    await expect(referenceChip).toContainText('Copied');

    await workbenchesRow.click();
    await expect(workbenchesRow).toHaveAttribute('aria-expanded', 'false');
    await expect.poll(() => page.evaluate(() => JSON.parse(
      window.localStorage.getItem('atp.studio.explorer.workbenches.expanded.v1') ?? '[]',
    ))).not.toContain(PROJECT);
    await workbenchesRow.click();
    await expect(activeWorkbench).toBeVisible();

    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('workbench-viewer')).toContainText('Route Lab');
    await expect(page.getByTestId(`studio-explorer-project-workbenches-${PROJECT}`))
      .toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId(`studio-explorer-workbench-${PROJECT}-route-lab`))
      .toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('documented lifecycle suggestion stays visible in both themes', async ({ page }, testInfo) => {
    const readyItem = {
      id: 'route-lab',
      title: 'Delivery record',
      summary: 'Tracks implementation through its documented outcome.',
      status: 'decided',
      phase: 'decision-ready',
      updatedAtUtc: '2026-08-09T10:00:00Z',
      entryPath: 'docs/concepts/route-lab.html',
      valid: true,
      error: null,
      sourceTaskKeys: [],
      relatedTaskKeys: ['AGT-2291'],
      documentation: {
        eligible: true,
        totalCount: 1,
        terminalCount: 1,
        openCount: 0,
        missingCount: 0,
        references: [{ key: 'AGT-2291', exists: true, terminal: true, lane: '6-completed' }],
      },
    };
    await page.route('**/api/projects/*/workbenches**', async route => {
      const request = route.request();
      const url = new URL(request.url());
      const json = (body: unknown) => route.fulfill({
        status: 200, contentType: 'application/json', body: JSON.stringify(body),
      });
      if (url.pathname.endsWith('/workbenches/route-lab')) {
        return json({
          workbench: {
            ...readyItem,
          },
          html: '<h1>Delivery record</h1><p>Implementation evidence remains readable.</p>',
          branch: 'task/route',
          revision: 'a'.repeat(40),
          workingTreeModified: false,
          fingerprint: 'b'.repeat(64),
        });
      }
      if (url.pathname.endsWith('/workbenches')) {
        return json({ projectName: PROJECT, includesHistory: false, count: 1, items: [readyItem] });
      }
      return route.fallback();
    });

    await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches/route-lab`, { waitUntil: 'commit' });
    const notice = page.getByTestId('workbench-documentation-ready');
    await expect(notice).toContainText('All referenced cards are terminal');
    await expect(page.getByTestId(`studio-explorer-workbench-${PROJECT}-route-lab`))
      .toContainText('Ready to document');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `documented-lifecycle-ready--mocked-${theme}.png`),
      });
    }

    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('Board and Hub routes restore project scope and survive reload', async ({ page }) => {
    await page.goto(`/#/projects/${PROJECT_SLUG}/board`, { waitUntil: 'commit' });
    await expect(page.getByTestId('studio-board')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('#/projects/route-project/board');
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('studio-board')).toBeVisible();

    await page.goto(`/#/projects/${PROJECT_SLUG}`, { waitUntil: 'commit' });
    await expect(page.getByTestId('project-shell-panel-overview')).toBeVisible();
    await expect(page.getByTestId('project-overview-dashboard')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('#/projects/route-project');
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('project-shell-panel-overview')).toBeVisible();
  });

  test('All Projects Board owns its URL across navigation, reload, and browser history', async ({ page }, testInfo) => {
    await page.goto(`/#/projects/${PROJECT_SLUG}/board`, { waitUntil: 'commit' });
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(PROJECT);

    await page.getByTestId('studio-explorer-show-all-projects').click();
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText('All projects');
    await expect.poll(() => new URL(page.url()).hash).toMatch(/^#\/board(?:&|$)/);

    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText('All projects');
    await expect.poll(() => new URL(page.url()).hash).toMatch(/^#\/board(?:&|$)/);

    await page.evaluate(() => {
      const evidence = document.createElement('div');
      evidence.dataset['testid'] = 'route-evidence-url';
      evidence.textContent = `URL: ${window.location.href}`;
      Object.assign(evidence.style, {
        position: 'fixed',
        inset: '8px auto auto 50%',
        transform: 'translateX(-50%)',
        zIndex: '2147483647',
        padding: '8px 12px',
        border: '1px solid currentColor',
        borderRadius: '6px',
        background: 'Canvas',
        color: 'CanvasText',
        font: '13px monospace',
      });
      document.body.append(evidence);
    });
    await expect(page.getByTestId('route-evidence-url')).toContainText('/#/board');
    await page.screenshot({
      path: evidencePath(testInfo, 'all-projects-board-route.png'),
    });

    await page.goBack();
    await expect.poll(() => new URL(page.url()).hash)
      .toMatch(new RegExp(`^#/projects/${PROJECT_SLUG}/board(?:&|$)`));
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(PROJECT);

    await page.goForward();
    await expect.poll(() => new URL(page.url()).hash).toMatch(/^#\/board(?:&|$)/);
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText('All projects');
  });

  test('task opened from All projects keeps the workspace-wide scope and returns there', async ({ page }, testInfo) => {
    await page.route('**/api/tasks/grouped', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(ALL_PROJECTS_GROUPED),
    }));
    await page.route('**/api/pipeline/accepted-integration-alert', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ active: false, items: [] }),
    }));
    await page.addInitScript(() => {
      localStorage.removeItem('atp.studio.tabs.v1');
      localStorage.setItem('activeProjects', '[]');
    });
    await page.goto('/#/board', { waitUntil: 'commit' });

    const picker = page.getByTestId('studio-project-picker-trigger');
    const taskCard = page.getByTestId('task-card').filter({ hasText: 'Route restoration task' });
    await expect(picker).toContainText('All projects');
    await expect(taskCard).toBeVisible();
    await page.screenshot({
      path: evidencePath(testInfo, 'all-projects-task-open--before--mocked.png'),
    });

    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
    await taskCard.click();
    await expect(page.getByTestId('studio-task')).toBeVisible();
    await page.screenshot({
      path: evidencePath(testInfo, 'all-projects-task-open--after--mocked.png'),
    });
    await expect(picker).toContainText('All projects');
    await expect.poll(() => page.evaluate(() => localStorage.getItem('activeProjects'))).toBe('[]');

    await page.goBack();
    await expect(page.getByTestId('studio-board')).toBeVisible();
    await expect(picker).toContainText('All projects');

    await page.getByTestId('task-card').filter({ hasText: 'Route restoration task' }).click();
    const taskTab = page.getByTestId(`studio-tab-task:${TASK_KEY}`);
    await expect(taskTab).toHaveAttribute('aria-selected', 'true');
    await taskTab.getByRole('button', { name: 'Close tab' }).click();
    await expect(page.getByTestId('studio-board')).toBeVisible();
    await expect(picker).toContainText('All projects');
  });

  test('Project Settings route restores the active Hub rail and survives reload', async ({ page }) => {
    await page.goto(`/#/projects/${PROJECT_SLUG}/settings`, { waitUntil: 'commit' });

    await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('#/projects/route-project/settings');

    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible();
  });

  test('workspace and project Epics routes restore scope and survive reload', async ({ page }) => {
    await page.goto('/#/epics', { waitUntil: 'commit' });
    await expect(page.getByTestId('epic-overview-screen')).toBeVisible();
    await expect(page.getByTestId('epic-overview-scope')).toHaveCount(0);
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('epic-overview-screen')).toBeVisible();

    await page.goto(`/#/projects/${PROJECT_SLUG}/epics`, { waitUntil: 'commit' });
    await expect(page.getByTestId('epic-overview-screen')).toBeVisible();
    await expect(page.getByTestId('epic-overview-scope')).toHaveText(PROJECT);
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('#/projects/route-project/epics');
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('epic-overview-scope')).toHaveText(PROJECT);
  });

  test('Epic detail route restores the public reference and survives reload', async ({ page }) => {
    await page.goto(`/#/epics/${EPIC_REFERENCE}`, { waitUntil: 'commit' });

    await expect(page.getByTestId('epic-rollup-pane')).toBeVisible();
    await expect(page.getByTestId('epic-title-text')).toContainText('Route restoration epic');
    await expect.poll(() => new URL(page.url()).hash).toBe('#/epics/AGT-2200');

    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('epic-rollup-pane')).toBeVisible();
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

  test('Workspace Settings uses one canonical route, mirrors section changes, and survives reload', async ({ page }, testInfo) => {
    await page.goto('/#/workspace/settings/task-server', { waitUntil: 'commit' });

    await expect(page.getByTestId('workspace-settings-inline')).toBeVisible();
    await expect(page.getByTestId('workspace-task-server-overlay')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toBe('#/workspace/settings/task-server');

    await page.getByTestId('workspace-settings-rail-tokens').click();
    await expect(page.getByTestId('workspace-tokens-overlay')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toBe('#/workspace/settings/tokens');

    await page.screenshot({
      path: evidencePath(testInfo, 'route-restoration-workspace-settings.png'),
    });
    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('workspace-tokens-overlay')).toBeVisible();
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);
  });

});
