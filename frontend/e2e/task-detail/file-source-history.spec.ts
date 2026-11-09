import { test, expect, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { setTheme } from '../helpers/theme';

const EVIDENCE_DIR = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'review-file-history')
  : resolve('test-results/review-file-history');

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/file-source-history';
const JOB_ID = 'file-source-history-test';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function text(route: Route, body: string): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'text/plain',
    body,
  });
}

async function selectDocsTab(page: Page): Promise<void> {
  const body = page.getByTestId('pane-prompt-body');
  const inline = page.getByTestId('prompt-tab-description');
  for (let attempt = 0; attempt < 4; attempt++) {
    if (await body.getAttribute('data-active-tab') === 'description') break;
    try {
      if (await inline.isVisible()) await inline.click({ force: true, timeout: 2000 });
      else {
        await page.getByTestId('pane-tabs-overflow').click({ force: true, timeout: 2000 });
        await page.getByRole('menuitem', { name: 'Docs', exact: true }).click({ force: true, timeout: 2000 });
      }
    } catch { /* Tab widths can change while the detail paints; use the current layout. */ }
  }
  await expect(body).toHaveAttribute('data-active-tab', 'description');
  const backdrop = page.getByTestId('app-menu-backdrop');
  if (await backdrop.isVisible()) await backdrop.click();
}

function detail(statusMarkdown = '') {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'File source history fixture',
      state: '2-ready',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
      createdAt: '2026-06-09T12:00:00Z',
      sessionChain: [],
    },
    promptMarkdown: '# Current prompt\n\nThe current task prompt.',
    statusMarkdown,
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: statusMarkdown
      ? { status: 'ready', startedAt: '2026-06-09T12:00:00Z', finishedAt: '2026-06-09T12:01:00Z', errorMessage: null }
      : { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/api/**', (route) => json(route, []));

  await page.route(new RegExp(`/api/tasks/${idEsc}/core(\\?|$)`), (route) => json(route, {
    state: 'ready', projectId: PROJECT, projectName: PROJECT,
    id: JOB_ID, taskKey: `${WATCH_PATH}::${JOB_ID}`, key: JOB_ID,
    watchPath: WATCH_PATH, folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/${JOB_ID}`,
    title: 'File source history fixture', kind: 'task', taskType: 'chore',
    lane: '2-ready', archiveState: null, enteredLaneAt: '2026-06-09T12:00:00Z',
    order: 1, mode: 'coding', released: false, pendingIntent: false,
    coreVersion: '1', runtimeVersion: '1',
    pins: { model: 'claude-haiku-4-5', modelExplicit: false, thinkingLevel: null,
      thinkingLevelExplicit: false, cliType: 'claude', contextMode: null,
      useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
      blockerDescription: null, needsInput: null, dependencyBlocked: false,
      dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null,
      hostname: null, executionStatus: null, location: 'none',
      heartbeatAt: null, leaseState: 'none', leaseId: null },
    statusSummary: { state: 'ready', text: null, originalBytes: 0, hash: null, cursor: null },
    prompt: { state: 'ready', text: '# Current prompt\n\nThe current task prompt.',
      originalBytes: 47, hash: 'prompt', cursor: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/details/documents(\\?|$)`), (route) => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, {
      id: JOB_ID, taskKey: `${WATCH_PATH}::${JOB_ID}`, projectId: PROJECT,
      attemptId: null, coreVersion: '1', resource: 'documents', version: '1',
      computedAt: '2026-06-09T12:00:00Z', state: 'ready', reason: null,
      data: { name, markdown: name === 'prompt'
        ? '# Current prompt\n\nThe current task prompt.' : '' },
    });
  });

  await page.route('**/api/v1/studio/auth/status', (route) => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks', (route) => json(route, []));
  await page.route('**/api/v1/studio/board**', (route) => json(route, {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: [],
    escalated: [],
    completed: [],
    archive: [],
  }));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/clients', (route) => json(route, []));
  await page.route('**/api/agent-rules**', (route) => json(route, []));
  await page.route('**/api/projects/*/workbenches**', (route) => json(route, { items: [] }));
  await page.route('**/api/cli/usage**', (route) => json(route, { items: [] }));
  // The usage header expects an object; the catch-all's [] crashes it.
  await page.route('**/api/usage/cockpit**', (route) => route.fulfill({ status: 503 }));
  await page.route('**/api/cli/quota**', (route) => json(route, { at: '2026-06-09T00:00:00Z', snapshots: [] }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: [],
      },
    },
  }));

  await page.route(new RegExp(`/api/tasks/${idEsc}/artifacts(\\?|$)`), (route) => json(route, {
    jobId: JOB_ID,
    files: [
      {
        name: 'prompt.md',
        sizeBytes: 46,
        mtime: '2026-06-09T12:10:00Z',
        kind: 'prompt',
      },
    ],
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/prompt\\.md/history(\\?|$)`), (route) => json(route, [
    {
      sha: '2222222',
      at: '2026-06-09T12:10:00Z',
      runIndex: 2,
      verdict: 'pass',
      message: 'update prompt',
      author: 'Agent <agent@example.com>',
      provenance: { source: 'workspace', path: 'prompt.md' },
    },
    {
      sha: '1111111',
      at: '2026-06-09T12:00:00Z',
      runIndex: 1,
      verdict: 'concerns',
      message: 'create prompt',
      author: 'Agent <agent@example.com>',
      provenance: { source: 'workspace', path: 'prompt.md' },
    },
  ]));
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/prompt\\.md(\\?|$)`), (route) => {
    const at = new URL(route.request().url()).searchParams.get('at');
    return text(route, at === '1111111'
      ? '# Historical prompt\n\nRun one prompt body.'
      : '# Historical prompt\n\nRun two prompt body.');
  });
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) => json(route, {
    pipeline: { id: 'p', displayName: 'Pipeline', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null,
    cost: null,
    tokensByModel: null,
    config: {},
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) => json(route, []));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) => json(route, {
    runCount: 0,
    firstStartedAt: null,
    lastActivityAt: null,
    hasActiveRun: false,
    runs: [],
    promptEntries: [],
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/timeline(\\?|$)`), (route) => json(route, []));
  await page.route(new RegExp(`/api/tasks/${idEsc}/screenshots(\\?|$)`), (route) => json(route, { jobId: JOB_ID, screenshots: [] }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) => json(route, { events: [], sessionChain: [] }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) => json(route, {
    isRepo: true,
    branch: 'main',
    filesChanged: 0,
    totalAdded: 0,
    totalRemoved: 0,
    files: [],
    error: null,
  }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) => json(route, detail()));
}

/**
 * Extra routes for the protocol `status.md` surface. Registered after
 * {@link installRoutes} so the status-bearing detail and the `status.md`
 * file-source endpoints win over the generic handlers (Playwright matches the
 * most recently added route first).
 */
async function installStatusRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route(new RegExp(`/api/tasks/${idEsc}/details/documents(\\?|$)`), (route) => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, {
      id: JOB_ID, taskKey: `${WATCH_PATH}::${JOB_ID}`, projectId: PROJECT,
      attemptId: null, coreVersion: '1', resource: 'documents', version: '1',
      computedAt: '2026-06-09T12:00:00Z', state: 'ready', reason: null,
      data: { name, markdown: name === 'prompt'
        ? '# Current prompt\n\nThe current task prompt.'
        : '# Status\n\n- Result: Success\n- Case: feature\n\n## What Was Done\n\n- The current protocol summary.' },
    });
  });

  await page.route(new RegExp(`/api/tasks/${idEsc}/files/status\\.md/history(\\?|$)`), (route) => json(route, [
    {
      sha: '4444444',
      at: '2026-06-09T12:10:00Z',
      runIndex: 2,
      verdict: 'pass',
      message: 'update status',
      author: 'Agent <agent@example.com>',
      provenance: { source: 'workspace', path: 'status.md' },
    },
    {
      sha: '3333333',
      at: '2026-06-09T12:00:00Z',
      runIndex: 1,
      verdict: 'concerns',
      message: 'create status',
      author: 'Agent <agent@example.com>',
      provenance: { source: 'workspace', path: 'status.md' },
    },
  ]));
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/status\\.md(\\?|$)`), (route) => {
    const at = new URL(route.request().url()).searchParams.get('at');
    return text(route, at === '3333333'
      ? '# Status\n\nResult: concerns\n\n## Summary\n\nHistorical status body for run one.'
      : '# Status\n\nResult: pass\n\n## Summary\n\nHistorical status body for run two.');
  });
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    json(route, detail(
      '# Status\n\n- Result: Success\n- Case: feature\n\n## What Was Done\n\n- The current protocol summary.')));
}

async function installPreviousResultRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const entries = [
    {
      id: 'local-0002',
      timestamp: '2026-09-11T08:21:00Z',
      producer: 'review attempt #2',
      producerKind: 'review-attempt',
      lane: '4-auto-review',
      result: 'Success',
      case: 'blocked',
      source: 'task-folder',
      version: 2,
    },
    {
      id: 'git-71c5299b0f',
      timestamp: '2026-09-07T06:00:00Z',
      producer: 'run attempt #1',
      producerKind: 'run-attempt',
      lane: '5e-escalated',
      result: 'Success',
      case: 'feature',
      source: 'workspace-history',
      version: null,
    },
  ];
  await page.route(new RegExp(`/api/tasks/${idEsc}/result-history/git-71c5299b0f(\\?|$)`), route => json(route, {
    entry: entries[1],
    markdown: '# Status\n\n- Result: Success\n- Case: feature\n\n## What Was Done\n\n- Previous run result remains openable.\n',
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/result-history(\\?|$)`), route => json(route, entries));
}

test.describe('File source history viewer', () => {
  test('Files tab defaults to the current result and keeps version choice in History', async ({ page }) => {
    await installRoutes(page);

    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-prompt-body')).toBeVisible({ timeout: 10_000 });
    await selectDocsTab(page);

    const promptCard = page.getByTestId('file-card-prompt.md');
    await expect(promptCard).toBeVisible();
    await promptCard.getByTestId('file-card-expand-prompt.md').click();
    await expect(promptCard.getByTestId('file-card-details-prompt.md')).toBeVisible();
    await expect(promptCard).toContainText('The current task prompt.');
    await expect(promptCard.getByTestId('file-source-history-timeline')).toHaveCount(0);
    await expect(promptCard.getByTestId('file-source-version-select')).toHaveCount(0);
    await expect(promptCard.getByTestId('file-source-diff-panel')).toHaveCount(0);

    mkdirSync(EVIDENCE_DIR, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await promptCard.screenshot({ path: join(EVIDENCE_DIR, `after-default-current-${theme}--mocked.png`) });
    }
    await setTheme(page, 'light');
    await promptCard.getByTestId('file-card-details-prompt.md').click();
    await promptCard.getByTestId('file-card-history-prompt.md').click();
    await promptCard.getByTestId('file-card-details-prompt.md').click();

    await expect(promptCard.getByTestId('file-source-history-timeline')).toContainText('Run #2');
    await expect(promptCard.getByTestId('file-source-history-timeline')).toContainText('pass');
    await expect(promptCard.getByTestId('file-source-version')).toContainText('Historical prompt');
    await expect(promptCard.getByTestId('file-source-version-select')).toHaveCount(0);
    await expect(promptCard.getByTestId('file-source-diff-panel')).toHaveCount(0);

    await promptCard.getByTestId('file-source-history-run-1').click();
    await expect(promptCard.getByTestId('file-source-version')).toContainText('Run one prompt body.');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await promptCard.screenshot({ path: join(EVIDENCE_DIR, `after-history-list-${theme}--mocked.png`) });
    }
  });

  test('Protocol "View version history" exposes the status.md run timeline', async ({ page }) => {
    await installRoutes(page);
    await installStatusRoutes(page);

    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    // The Result inspector carries the view menu.
    await page.getByTestId('inspector-tab-protocol').click();
    await expect(page.getByTestId('protocol-beautiful-results')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('protocol-more-actions').click();
    await page.getByTestId('protocol-context-menu-item-view-history').click();

    const history = page.getByTestId('protocol-file-history');
    await expect(history).toBeVisible();
    // The same file-source-history mechanic, opened straight onto the timeline.
    await expect(history.getByTestId('file-source-history-timeline')).toContainText('Run #2');
    await expect(history.getByTestId('file-source-history-timeline')).toContainText('pass');
    await expect(history.getByTestId('file-source-version')).toContainText('Historical status body');
    await expect(history.getByTestId('file-source-version-select')).toHaveCount(0);
    await expect(history.getByTestId('file-source-diff-panel')).toHaveCount(0);

    await history.getByTestId('file-source-history-run-1').click();
    await expect(history.getByTestId('file-source-version')).toContainText('Historical status body for run one');
  });

  test('Result tab lists and opens previous results in the same viewer', async ({ page }) => {
    await installRoutes(page);
    await installStatusRoutes(page);
    await installPreviousResultRoutes(page);
    let historyReads = 0;
    page.on('request', request => {
      if (new URL(request.url()).pathname === `/api/tasks/${JOB_ID}/result-history`) historyReads++;
    });

    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    const result = page.getByTestId('protocol-beautiful-results');
    await expect(result).toBeVisible({ timeout: 10_000 });
    expect(historyReads).toBe(0);

    await page.getByTestId('previous-results-toggle').click();
    await expect(page.getByTestId('previous-results-toggle')).toContainText('2');
    expect(historyReads).toBe(1);
    const list = page.getByTestId('previous-results-list');
    await expect(list).toContainText('review attempt #2');
    await expect(list).toContainText('Result: Success');
    await expect(list).toContainText('Case: blocked');
    mkdirSync(EVIDENCE_DIR, { recursive: true });
    await page.getByTestId('pane-protocol').screenshot({
      path: join(EVIDENCE_DIR, 'previous-results-list--mocked.png'),
    });
    await page.getByTestId('previous-result-git-71c5299b0f').click();

    await expect(page.getByTestId('previous-result-banner')).toContainText('Previous result from');
    await expect(page.getByTestId('previous-result-banner')).toContainText('run attempt #1');
    await expect(result).toContainText('Previous run result remains openable.');
    await page.getByTestId('pane-protocol').screenshot({
      path: join(EVIDENCE_DIR, 'previous-results-open--mocked.png'),
    });

    await page.getByTestId('previous-results-current').click();
    await expect(page.getByTestId('previous-result-banner')).toHaveCount(0);
    await expect(result).toContainText('The current protocol summary.');
    await page.getByTestId('previous-results-toggle').click();
    expect(historyReads).toBe(1);
  });

  test('Result tab says older results are loading or failed instead of claiming there are none', async ({ page }) => {
    await installRoutes(page);
    await installStatusRoutes(page);
    const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    // AGT-3086: the legacy git backfill runs in the background. The first
    // selected read is pending, and reopening the menu looks once more.
    let reads = 0;
    let release!: () => void;
    const refetchHeld = new Promise<void>(resolve => { release = resolve; });
    await page.route(new RegExp(`/api/tasks/${idEsc}/result-history(\\?|$)`), async route => {
      const legacy = ++reads > 1 ? 'failed' : 'pending';
      if (legacy === 'failed') await refetchHeld;
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        headers: { 'X-Result-History-Legacy': legacy },
        body: '[]',
      });
    });

    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('protocol-beautiful-results')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('previous-results-toggle')).toBeEnabled();

    mkdirSync(EVIDENCE_DIR, { recursive: true });
    await page.getByTestId('previous-results-toggle').click();
    const list = page.getByTestId('previous-results-list');
    await expect(list.getByTestId('previous-results-legacy-hint')).toHaveText('Older results are still loading.');
    await expect(list).not.toContainText('No previous results found.');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pane-protocol').screenshot({
        path: join(EVIDENCE_DIR, `previous-results-legacy-pending-${theme}--mocked.png`),
      });
    }

    await page.getByTestId('previous-results-toggle').click();
    await page.getByTestId('previous-results-toggle').click();
    release();
    await expect(list.getByTestId('previous-results-legacy-hint'))
      .toHaveText('Older results could not be loaded. Studio retries later.');
    await expect(list).not.toContainText('No previous results found.');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pane-protocol').screenshot({
        path: join(EVIDENCE_DIR, `previous-results-legacy-failed-${theme}--mocked.png`),
      });
    }
  });

  test('Result tab loads previous results on first disclosure and retries a failed list', async ({ page }) => {
    await installRoutes(page);
    await installStatusRoutes(page);
    const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    let reads = 0;
    await page.route(new RegExp(`/api/tasks/${idEsc}/result-history(\\?|$)`), route => {
      reads++;
      return reads === 1
        ? route.fulfill({ status: 503, contentType: 'application/json',
          body: JSON.stringify({ error: 'Previous results offline' }) })
        : json(route, []);
    });

    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('protocol-beautiful-results')).toBeVisible({ timeout: 10_000 });
    expect(reads).toBe(0);
    await page.getByTestId('previous-results-toggle').click();
    await expect(page.getByTestId('previous-results-list').getByRole('alert'))
      .toContainText('Previous results offline');
    mkdirSync(EVIDENCE_DIR, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pane-protocol').screenshot({
        path: join(EVIDENCE_DIR, `previous-results-error-${theme}--mocked.png`),
      });
    }
    await page.getByTestId('previous-results-retry').click();
    await expect(page.getByTestId('previous-results-list')).toContainText('No previous results found.');
    await page.getByTestId('previous-results-toggle').click();
    await page.getByTestId('previous-results-toggle').click();
    expect(reads).toBe(2);
  });
});
