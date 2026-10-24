import { expect, test, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Route Project';
const PROJECT_SLUG = 'route-project';
const PROJECT_ID = 'PROJ-ROUTE';
const TASK_REFERENCE = 'AGT-2291';
const TASK_ID = 'route-restoration-task';
const WATCH_PATH = '/tmp/route-project';
const TASK_KEY = `${WATCH_PATH}::${TASK_ID}`;
const WORKBENCH_KEY = 'ROU-W4';
const PROJECT_RECORD = {
  id: PROJECT_ID, displayName: PROJECT, shortCode: 'ROU', workspaceId: 'WS-ROUTE',
  storageLocation: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  sortOrder: 0, archived: false, urls: [],
};
const WORKBENCH = {
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
};

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

const TASK_CORE = {
  state: 'ready', projectId: PROJECT_ID, projectName: PROJECT,
  id: TASK_ID, taskKey: TASK_KEY, key: TASK_REFERENCE,
  watchPath: WATCH_PATH, folderPath: TASK_DETAIL.info.folderPath,
  title: TASK_DETAIL.info.title, kind: 'task', taskType: 'chore',
  lane: TASK_DETAIL.info.state, archiveState: null,
  enteredLaneAt: TASK_DETAIL.info.lastActivity, order: 1, mode: 'coding',
  released: false, pendingIntent: false, coreVersion: '1', runtimeVersion: '1',
  pins: { model: null, modelExplicit: false, thinkingLevel: null,
    thinkingLevelExplicit: false, cliType: null, contextMode: null,
    useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
  actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
  blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
    blockerDescription: null, needsInput: null, dependencyBlocked: false,
    dependencyState: 'ready', dependencies: [] },
  runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
    executionStatus: null, location: 'none', heartbeatAt: null,
    leaseState: 'none', leaseId: null },
  statusSummary: { state: 'ready', text: '', originalBytes: 0, hash: null, cursor: null },
  prompt: { state: 'ready', text: TASK_DETAIL.promptMarkdown,
    originalBytes: TASK_DETAIL.promptMarkdown.length, hash: null, cursor: null },
  timeline: { state: 'ready', events: [], cursor: null },
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

    if (url.pathname === '/api/v1/studio/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/v1/workspaces') return json([{
      id: 'WS-ROUTE', displayName: 'Route workspace', sortOrder: 0, isDefault: true,
      projects: [PROJECT_RECORD],
    }]);
    if (url.pathname === '/api/v1/projects') return json([PROJECT_RECORD]);
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (url.pathname === '/api/usage/cockpit') return json({
      snapshotVersion: 1, workspaceId: 'WS-ROUTE', timeZone: 'Europe/Berlin', weekStart: 1,
      generatedAt: '2026-07-24T10:00:00Z',
      calendar: { timeZone: 'Europe/Berlin', weekStart: 1,
        dayStartUtc: '', dayEndUtc: '', weekStartUtc: '', weekEndUtc: '' },
      clis: [], cost: null, runs: [], slots: [], sources: {},
    });
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
    if (url.pathname === '/api/v1/management/remote-hosts') return json([]);
    if (url.pathname === '/api/v1/management/links') return json([]);
    if (url.pathname === '/api/v1/management/credential-inventory') {
      return json({ generatedAt: '2026-07-24T10:00:00Z', items: [] });
    }
    if (/\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    if (url.pathname === '/api/watch-paths') {
      return json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/v1/studio/board') return json(EMPTY_GROUPED);
    if (url.pathname === '/api/v1/studio/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/pipeline/accepted-integration-alert') {
      return json({ active: false, items: [], generatedAtUtc: '2026-07-24T10:00:00Z' });
    }
    if (url.pathname === '/api/runner/queue-starvation') return json({ active: false, items: [] });
    const coreTaskId = /^\/api\/tasks\/([^/]+)\/core$/.exec(url.pathname)?.[1];
    if (coreTaskId === TASK_REFERENCE || coreTaskId === TASK_ID) return json(TASK_CORE);
    const detailResource = /^\/api\/tasks\/([^/]+)\/details\/([^/]+)$/.exec(url.pathname);
    if (detailResource && [TASK_REFERENCE, TASK_ID].includes(detailResource[1])) {
      const resource = detailResource[2];
      const name = url.searchParams.get('name');
      const data = resource === 'documents'
        ? { name, markdown: name === 'prompt' ? TASK_DETAIL.promptMarkdown : '', summaryState: null }
        : resource === 'overview'
          ? { references: TASK_DETAIL.info.references, relatedWikiPages: [], waitsOn: null }
          : resource === 'history'
            ? { promptHistory: [], titleHistory: [], log: [] }
            : resource === 'usage'
              ? { tokenSummary: null, lastUsage: null, contextUsage: null }
              : {};
      return json({ id: TASK_ID, taskKey: TASK_KEY, projectId: PROJECT_ID,
        attemptId: null, coreVersion: '1', resource, version: '1',
        computedAt: null, state: 'ready', data, reason: null });
    }
    const detailTaskId = /^\/api\/v1\/projects\/[^/]+\/tasks\/([^/]+)$/.exec(url.pathname)?.[1];
    if (detailTaskId === TASK_REFERENCE || detailTaskId === TASK_ID) {
      return json(TASK_DETAIL);
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
          }, {
            name: 'history.md',
            title: 'History',
            relPath: 'concepts/history.md',
            type: 'md',
            children: [],
          }, {
            name: 'gestures.md',
            title: 'Gestures',
            relPath: 'concepts/gestures.md',
            type: 'md',
            children: [],
          }],
        }],
      });
    }
    if (url.pathname.includes('/wiki/files/concepts/')) {
      const relPath = decodeURIComponent(url.pathname.split('/wiki/files/')[1] ?? 'concepts/routing.md');
      return json({ relPath, content: `# ${relPath}\n\nRestored Wiki page.` });
    }
    if (url.pathname.includes('/wiki/history/concepts/')) {
      const relPath = decodeURIComponent(url.pathname.split('/wiki/history/')[1] ?? 'concepts/routing.md');
      return json({
        relPath,
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
    if (url.pathname === '/api/workbenches') {
      return json({
        projectName: url.searchParams.get('project'),
        count: 1,
        currentCount: 1,
        historyCount: 0,
        items: [{ projectName: PROJECT, workbench: WORKBENCH }],
      });
    }
    if (url.pathname.endsWith('/workbenches')) {
      return json({
        projectName: PROJECT,
        includesHistory: false,
        count: 1,
        items: [WORKBENCH],
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

  for (const legacy of [
    { path: '/epics', canonical: '/workbenches', tab: 'workbenches:__all__' },
    { path: `/projects/${PROJECT_SLUG}/epics`, canonical: `/projects/${PROJECT_SLUG}/workbenches`, tab: `workbenches:${PROJECT}` },
  ]) {
    test(`retired overview ${legacy.path} restores the Dossiers tab and canonical URL`, async ({ page }, testInfo) => {
      await page.goto(`/#${legacy.path}`, { waitUntil: 'commit' });
      await expect(page.getByTestId(`studio-tab-${legacy.tab}`)).toBeVisible();
      await expect.poll(() => new URL(page.url()).hash).toBe(`#${legacy.canonical}`);
      await expect(page.getByTestId('error-dialog')).toHaveCount(0);
      await page.screenshot({
        path: evidencePath(testInfo, legacy.tab.endsWith('__all__')
          ? 'retired-overview-global--mocked.png' : 'retired-overview-project--mocked.png'),
      });
    });
  }

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
      .toContain(`#/projects/${PROJECT_ID}/wiki?page=concepts%2Frouting.md`);
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);

    await page.goto(`/#/projects/${PROJECT_SLUG}/wiki`, { waitUntil: 'commit' });
    await page.getByTestId('project-wiki-file-concepts/routing.md').click();
    await expect.poll(() => new URL(page.url()).hash)
      .toContain(`#/projects/${PROJECT_ID}/wiki?page=concepts%2Frouting.md`);
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
        'ws:WS-ROUTE': true,
      }));
      window.localStorage.setItem('atp.studio.explorer.workbenches.expanded.v1', '[]');
    });
    await page.goto(`/#/projects/${PROJECT_SLUG}/workbenches/route-lab`, { waitUntil: 'commit' });

    await expect(page.frameLocator('iframe')
      .getByRole('heading', { name: 'Route Lab' })).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toBe(`#/projects/${PROJECT_ID}/workbenches/route-lab`);
    await expect(page.getByTestId('error-dialog')).toHaveCount(0);

    const projectRow = page.getByTestId(`studio-explorer-project-${PROJECT}`);
    await expect(projectRow).toBeVisible();
    await expect(projectRow).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('studio-explorer-workspace-head')).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('studio-explorer-ws-group-WS-ROUTE')).toHaveAttribute('aria-expanded', 'true');

    const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${PROJECT}`);
    const activeWorkbench = page.getByTestId(`studio-explorer-workbench-${PROJECT}-route-lab`);
    await expect(workbenchesRow).toHaveAttribute('aria-expanded', 'true');
    await expect(workbenchesRow).not.toHaveAttribute('aria-current', 'page');
    await expect(activeWorkbench).toHaveAttribute('aria-current', 'page');
    await expect(activeWorkbench).toBeInViewport();
    await expect(activeWorkbench).toHaveAttribute('aria-label', /testing/);
    await expect(activeWorkbench).not.toContainText(/updated|today|\d+d/);
    await activeWorkbench.hover();
    await expect(page.getByTestId(`studio-explorer-workbench-tooltip-${PROJECT}-route-lab`))
      .toContainText(WORKBENCH_KEY);
    await page.mouse.move(900, 500);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({
        path: evidencePath(testInfo, `workbench-tree-after--mocked-${theme}.png`),
      });
    }
    await workbenchesRow.getByLabel('Collapse').click();
    await expect(workbenchesRow).toHaveAttribute('aria-expanded', 'false');
    await workbenchesRow.getByLabel('Expand').click();
    await expect(activeWorkbench).toBeVisible();

    await page.reload({ waitUntil: 'commit' });
    await expect(page.frameLocator('iframe')
      .getByRole('heading', { name: 'Route Lab' })).toBeVisible();
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
      .toContainText('Delivery record');

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
      .toContain(`#/projects/${PROJECT_ID}`);
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

  test('Project Settings route restores the active Hub rail and survives reload', async ({ page }) => {
    await page.goto(`/#/projects/${PROJECT_SLUG}/settings`, { waitUntil: 'commit' });

    await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible();
    await expect.poll(() => new URL(page.url()).hash)
      .toContain(`#/projects/${PROJECT_ID}/settings`);

    await page.reload({ waitUntil: 'commit' });
    await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible();
  });

  test('Wiki links reuse the current tab, keep per-tab history, and middle-click opens a new tab', async ({ page }, testInfo) => {
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [], activeKey: null }));
    });
    await page.goto(`/#/projects/${PROJECT_SLUG}/wiki`, { waitUntil: 'commit' });
    await expect(page.getByTestId('project-wiki-section')).toBeVisible();
    const tabList = page.getByTestId('studio-tab-list');
    await expect(tabList.getByRole('tab')).toHaveCount(1);
    await tabList.screenshot({ path: evidencePath(testInfo, 'wiki-tab-strip-before-links.png') });
    await page.getByTestId('project-wiki-chevron-concepts').click();

    for (const relPath of ['concepts/routing.md', 'concepts/history.md', 'concepts/gestures.md']) {
      await page.getByTestId(`project-wiki-file-${relPath}`).click();
      await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(relPath);
      await expect(tabList.getByRole('tab')).toHaveCount(1);
    }

    await expect(page.getByTestId('studio-document-history')).toHaveAttribute('data-history-length', '4');
    await expect(page.getByTestId('studio-document-back')).toBeEnabled();
    await tabList.screenshot({ path: evidencePath(testInfo, 'wiki-tab-strip-after-three-links.png') });

    await page.getByTestId('studio-document-back').click();
    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText('concepts/history.md');
    await expect.poll(() => new URL(page.url()).hash)
      .toContain('wiki?page=concepts%2Fhistory.md');

    await page.getByTestId('project-wiki-file-concepts/routing.md').click({ button: 'middle' });
    await expect(tabList.getByRole('tab')).toHaveCount(2);
    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText('concepts/routing.md');
    await tabList.screenshot({ path: evidencePath(testInfo, 'wiki-tab-strip-after-middle-click.png') });
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
