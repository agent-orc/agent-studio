import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/task-detail-navigation';
const TASK_ID = 'agt-2577-heavy';
const TASK_KEY = `${WATCH_PATH}::${TASK_ID}`;
const PEER_ID = 'agt-2578-peer';
const PEER_KEY = `${WATCH_PATH}::${PEER_ID}`;

// Held API responses make the page load event unsuitable for these tests.
// Give the dev bundle room to load on a shared CI host, then assert the UI.
async function gotoApp(page: Page, url: string): Promise<void> {
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60_000 });
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function task() {
  return {
    id: TASK_ID,
    taskKey: TASK_KEY,
    key: 'AGT-2577',
    displayKey: 'AGT-2577',
    title: 'Heavy task with many runs and artifacts',
    state: '5-human-review',
    kind: 'task',
    mode: 'coding',
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.2-codex',
    order: 1,
    createdAt: '2026-08-11T08:00:00Z',
    lastActivity: '2026-08-11T10:00:00Z',
    watchPath: WATCH_PATH,
    projectName: 'fixture',
    folderPath: `${WATCH_PATH}/${TASK_ID}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    estimatedTokens: 0,
  };
}

function peerTask() {
  return { ...task(), id: PEER_ID, taskKey: PEER_KEY, key: 'AGT-2578',
    displayKey: 'AGT-2578', title: 'Peer task', order: 2 };
}

function core() {
  return {
    state: 'ready', projectId: 'fixture', projectName: 'fixture',
    id: TASK_ID, taskKey: TASK_KEY, key: 'AGT-2577',
    watchPath: WATCH_PATH, folderPath: `${WATCH_PATH}/${TASK_ID}`,
    title: 'Heavy task with many runs and artifacts', kind: 'task',
    taskType: 'chore', lane: '5-human-review', archiveState: null,
    enteredLaneAt: '2026-08-11T10:00:00Z', order: 1, mode: 'coding',
    released: false, pendingIntent: false, coreVersion: '7',
    pins: { model: 'gpt-5.2-codex', modelExplicit: true,
      thinkingLevel: null, thinkingLevelExplicit: false, cliType: 'codex',
      contextMode: null, useOwnSession: null, allowWebAccess: false,
      noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
      blockerDescription: null, needsInput: null, dependencyBlocked: false,
      dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: 'attempt-1', runnerId: null, runnerName: null,
      hostname: null, executionStatus: null, location: 'none',
      heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: 'Ready for review.', originalBytes: 17,
      hash: 'status', cursor: null },
    prompt: { state: 'ready', text: '# Heavy task\n\nBounded core prompt.',
      originalBytes: 800_000, hash: 'prompt', cursor: '2048',
      continuationUrl: `/api/tasks/${TASK_ID}/files/prompt.md?project=fixture` },
    timeline: { state: 'ready', events: [{ sequence: 1, ts: '2026-08-11T10:00:00Z',
      kind: 'note', actor: 'system', runId: 'attempt-1', summary: 'Recent work' }],
      cursor: '1', continuationUrl: `/api/tasks/${TASK_ID}/timeline?project=fixture` },
  };
}

function resource(name: string, data: unknown) {
  return { id: TASK_ID, taskKey: TASK_KEY, projectId: 'fixture', attemptId: 'attempt-1',
    coreVersion: '7', resource: name, version: 'v1', computedAt: '2026-08-11T10:00:00Z',
    state: 'ready', data, reason: null };
}

function grouped(includePeer = false) {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: includePeer ? [task(), peerTask()] : [task()],
    escalated: [], completed: [], archive: [],
  };
}

async function mockApplication(page: Page, includePeer = false): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/projects', route => json(route, [{
    id: 'fixture', displayName: 'fixture', shortCode: 'FIX', archived: false,
    sourceType: 'local-folder', workspaceId: 'workspace', storageLocation: WATCH_PATH,
    repositoryPath: WATCH_PATH, rootPath: WATCH_PATH,
  }]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { id: 'fixture', name: 'fixture', shortCode: 'FIX', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-08-11T08:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'fixture', displayName: 'fixture', shortCode: 'FIX',
      workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
      sortOrder: 0, storageLocation: WATCH_PATH, repositoryPath: WATCH_PATH,
      rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-08-11T08:00:00Z',
    }],
  }]));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  // The usage cockpit is not under test; a failed read keeps it inert
  // instead of feeding it the catch-all array.
  await page.route('**/api/usage/cockpit**', route => json(route, {}, 503));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-08-11T10:00:00Z', snapshots: [] }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, includePeer ? [task(), peerTask()] : [task()]));
  await page.route('**/api/v1/studio/board**', route => json(route, grouped(includePeer)));
  await page.route(`**/api/tasks/${TASK_ID}/runs**`, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null,
    hasActiveRun: false, runs: [], promptEntries: [], refinements: [], runnerEvents: [],
  }));
  await page.route(`**/api/tasks/${TASK_ID}/session-events**`, route =>
    json(route, { events: [], sessionChain: [] }));
  await page.route(`**/api/tasks/${TASK_ID}/plan**`, route =>
    json(route, { hasPlan: false, source: null, snapshotCount: 0,
      activeItemId: null, items: [], unassignedSubActions: [] }));
  await page.route(`**/api/tasks/${TASK_ID}/pipeline**`, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null,
    executions: [],
    config: {},
    cost: null,
  }));
}

async function clickCard(page: Page): Promise<void> {
  const boardTab = page.getByRole('tab', { name: /All projects.*Board/ });
  if (await boardTab.count()) await boardTab.click({ force: true });
  const card = page.locator('[data-testid="task-card"], [data-testid="job-card"]')
    .filter({ hasText: 'Heavy task with many runs and artifacts' });
  await expect(card).toBeVisible();
  const heading = card.getByRole('heading', { name: 'Heavy task with many runs and artifacts' });
  for (let attempt = 0; attempt < 3; attempt++) {
    await heading.click({ force: true });
    const opened = await page.waitForURL(/#\/tasks\/AGT-2577/, { timeout: 3000 })
      .then(() => true, () => false);
    if (opened) return;
  }
  throw new Error('Card click did not open the task route');
}

async function selectPromptTab(page: Page, testId: string, label: string): Promise<void> {
  const tabId = testId.replace('prompt-tab-', '');
  const body = page.getByTestId('pane-prompt-body');
  const inline = page.getByTestId(testId);
  for (let attempt = 0; attempt < 4; attempt++) {
    if (await body.getAttribute('data-active-tab') === tabId) {
      const backdrop = page.getByTestId('app-menu-backdrop');
      if (await backdrop.count()) await page.keyboard.press('Escape');
      await expect(backdrop).toHaveCount(0);
      return;
    }
    try {
      if (await inline.isVisible()) await inline.click({ force: true, timeout: 2000 });
      else {
        await page.getByTestId('pane-tabs-overflow').click({ force: true, timeout: 2000 });
        await page.getByRole('menuitem', { name: label, exact: true }).click({ force: true, timeout: 2000 });
      }
    } catch { /* Tab widths can change while a response paints; try the current layout. */ }
  }
  await expect(body).toHaveAttribute('data-active-tab', tabId);
}

async function firstHeadPaintMs(page: Page): Promise<number> {
  await page.waitForFunction(() => performance.getEntriesByName('task-core-ready').length > 0);
  return page.evaluate(() => {
    const click = performance.getEntriesByName('task-card-click').at(-1);
    const paint = performance.getEntriesByName('task-core-ready').at(-1);
    if (!click || !paint) throw new Error('Task navigation performance marks are missing');
    return Math.round(paint.startTime - click.startTime);
  });
}

test('loads hidden tab data on selection, reuses it, and shows tab failures', async ({ page }) => {
  await mockApplication(page);
  const seen: string[] = [];
  let timelineFailures = 1;
  let historyFailures = 1;
  let evidenceFailures = 1;
  let qualityFailures = 1;
  let fileFailures = 1;
  let codeReviewFailures = 1;
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith(`/api/tasks/${TASK_ID}/`)) seen.push(url.pathname + url.search);
  });
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource('documents', { name, markdown: name === 'prompt' ? '# Task' : 'Ready' }));
  });
  await page.route(`**/api/tasks/${TASK_ID}/details/overview**`, route => json(route, resource('overview', {})));
  await page.route(`**/api/tasks/${TASK_ID}/artifacts**`, route => json(route, {
    files: [
      { name: 'review.md', kind: 'codeReview', sizeBytes: 14, mtime: '2026-11-05T00:00:00Z' },
      { name: 'notes.txt', kind: 'other', sizeBytes: 11, mtime: '2026-11-05T00:00:00Z' },
    ],
  }));
  await page.route(`**/api/tasks/${TASK_ID}/screenshots**`, route => json(route, { screenshots: [] }));
  await page.route(`**/api/tasks/${TASK_ID}/timeline**`, route =>
    timelineFailures-- > 0 ? json(route, { error: 'offline' }, 503) : json(route, []));
  await page.route(`**/api/tasks/${TASK_ID}/details/history**`, route =>
    historyFailures-- > 0 ? json(route, { error: 'offline' }, 503) : json(route,
      resource('history', { promptHistory: [], titleHistory: [], log: [] })));
  await page.route(`**/api/tasks/${TASK_ID}/details/review**`, route => {
    const evidence = new URL(route.request().url()).searchParams.get('evidence') === 'true';
    return evidence && evidenceFailures-- > 0
      ? json(route, { error: 'offline' }, 503)
      : json(route, resource('review', { reviewProjection: null, evidence: evidence ? [] : null }));
  });
  await page.route(`**/api/tasks/${TASK_ID}/quality-studio**`, route =>
    qualityFailures-- > 0 ? json(route, { error: 'offline' }, 503)
      : json(route, { state: 'not-registered', repositoryId: null, currentHead: null,
        rounds: [], landedSha: null, landedBaseSha: null, landedPatchEqualsReviewedPatch: null }));
  await page.route(`**/api/tasks/${TASK_ID}/code-review/list**`, async route => {
    const activeTab = await page.getByTestId('pane-prompt-body').getAttribute('data-active-tab').catch(() => null);
    return activeTab === 'code-review' && codeReviewFailures-- > 0
      ? json(route, { error: 'offline' }, 503)
      : json(route, { entries: [] });
  });
  await page.route(`**/api/tasks/${TASK_ID}/files/review.md**`, route =>
    fileFailures-- > 0
      ? json(route, { error: 'offline' }, 503)
      : route.fulfill({ status: 200, contentType: 'text/plain', body: '# Review ready' }));
  await page.route(`**/api/tasks/${TASK_ID}/files/notes.txt**`, route =>
    route.fulfill({ status: 200, contentType: 'text/plain', body: 'Notes ready' }));

  await gotoApp(page, '/');
  await dismissDevErrorDialog(page);
  await clickCard(page);
  await expect(page.getByTestId('pane-prompt-body')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('task-core')).toHaveCount(0);
  expect(seen.filter(url => /\/timeline\?|\/details\/history|\/files\/review.md|\/artifacts|\/screenshots/.test(url))).toEqual([]);
  expect(seen.some(url => url.includes('/details/review') && url.includes('evidence=true'))).toBe(false);

  await page.getByTestId('prompt-tab-timeline').click();
  await expect(page.getByTestId('timeline-load-error')).toBeVisible();
  await expect(page.getByTestId('timeline-history-error')).toBeVisible();
  if (process.env.JOB_RESULTS_DIR) {
    await page.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR, 'timeline-error-dark.png') });
    await setTheme(page, 'light');
    await page.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR, 'timeline-error-light.png') });
    await setTheme(page, 'dark');
  }
  await page.getByTestId('timeline-retry').click();
  await page.getByTestId('timeline-history-error').getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByTestId('timeline-load-error')).toHaveCount(0);
  await expect(page.getByTestId('timeline-history-error')).toHaveCount(0);
  const timelineReads = seen.filter(url => url.includes('/timeline?')).length;
  const historyReads = seen.filter(url => url.includes('/details/history')).length;
  await page.getByTestId('prompt-tab-overview').click();
  await page.getByTestId('prompt-tab-timeline').click();
  expect(seen.filter(url => url.includes('/timeline?')).length).toBe(timelineReads);
  expect(seen.filter(url => url.includes('/details/history')).length).toBe(historyReads);

  await page.getByTestId('prompt-tab-evidence').click();
  await expect.poll(() => seen.filter(url => url.includes('/screenshots')).length).toBe(1);
  await expect(page.getByTestId('evidence-load-error')).toBeVisible();
  await expect(page.getByTestId('quality-load-error')).toBeVisible();
  if (process.env.JOB_RESULTS_DIR) {
    await page.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR, 'evidence-error-dark.png') });
    await setTheme(page, 'light');
    await page.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR, 'evidence-error-light.png') });
    await setTheme(page, 'dark');
  }
  await page.getByTestId('evidence-load-error').getByRole('button', { name: 'Retry' }).click();
  await page.getByTestId('quality-retry').click();
  await expect(page.getByTestId('evidence-load-error')).toHaveCount(0);
  await expect(page.getByTestId('quality-load-error')).toHaveCount(0);
  const evidenceReads = seen.filter(url => url.includes('/details/review') && url.includes('evidence=true')).length;
  const qualityReads = seen.filter(url => url.includes('/quality-studio')).length;
  await page.getByTestId('prompt-tab-overview').click();
  await page.getByTestId('prompt-tab-evidence').click();
  expect(seen.filter(url => url.includes('/details/review') && url.includes('evidence=true')).length).toBe(evidenceReads);
  expect(seen.filter(url => url.includes('/quality-studio')).length).toBe(qualityReads);
  expect(seen.filter(url => url.includes('/screenshots')).length).toBe(1);

  await selectPromptTab(page, 'prompt-tab-code-review', 'Code Review');
  await expect(page.getByTestId('code-review-error')).toBeVisible();
  await page.getByTestId('code-review-retry').click();
  await expect(page.getByTestId('code-review-error')).toHaveCount(0);
  const codeReviewReads = seen.filter(url => url.includes('/code-review/list')).length;
  await page.getByTestId('prompt-tab-overview').click();
  await selectPromptTab(page, 'prompt-tab-code-review', 'Code Review');
  expect(seen.filter(url => url.includes('/code-review/list')).length).toBe(codeReviewReads);

  await selectPromptTab(page, 'prompt-tab-description', 'Docs');
  await expect.poll(() => seen.filter(url => url.includes('/artifacts')).length).toBe(1);
  expect(seen.some(url => url.includes('/files/notes.txt'))).toBe(false);
  await expect(page.getByTestId('file-card-retry-review.md')).toBeVisible();
  await page.getByTestId('file-card-retry-review.md').click();
  await expect(page.getByTestId('file-card-review.md')).toContainText('Review ready');
  await page.getByTestId('file-card-expand-notes.txt').click();
  await expect(page.getByTestId('file-card-notes.txt')).toContainText('Notes ready');
  const fileReads = seen.filter(url => url.includes('/files/review.md')).length;
  const notesReads = seen.filter(url => url.includes('/files/notes.txt')).length;
  await page.getByTestId('prompt-tab-overview').click();
  await selectPromptTab(page, 'prompt-tab-description', 'Docs');
  expect(seen.filter(url => url.includes('/files/review.md')).length).toBe(fileReads);
  expect(seen.filter(url => url.includes('/files/notes.txt')).length).toBe(notesReads);
  expect(seen.filter(url => url.includes('/artifacts')).length).toBe(1);
  if (process.env.JOB_RESULTS_DIR) {
    mkdirSync(process.env.JOB_RESULTS_DIR, { recursive: true });
    writeFileSync(path.join(process.env.JOB_RESULTS_DIR, 'tab-loading-after-requests.json'),
      `${JSON.stringify({ fixture: 'AGT-2577 mocked task', requests: seen }, null, 2)}\n`);
  }
});

test('paints complete bounded core before large documents resolve in both themes', async ({ page }) => {
  await mockApplication(page);
  let documentsRequested = 0;
  let releaseDocuments!: () => void;
  const documentGate = new Promise<void>(resolve => { releaseDocuments = resolve; });
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, async route => {
    documentsRequested++;
    await documentGate;
    const name = new URL(route.request().url()).searchParams.get('name');
    await json(route, resource('documents', { name,
      markdown: name === 'prompt' ? '# Heavy task\n\n' + 'Large markdown. '.repeat(40_000) : 'Ready for review.',
      summaryState: null }));
  });
  await page.route(`**/api/tasks/${TASK_ID}/details/usage**`, route => json(route, { error: 'usage offline' }, 503));

  try {
    await gotoApp(page, '/');
    await dismissDevErrorDialog(page);
    await setTheme(page, 'light');
    await page.evaluate(() => {
      document.addEventListener('click', () => performance.mark('task-card-click'), {
        capture: true,
        once: true,
      });
    });

    await clickCard(page);
    const firstTaskHeadPaintMs = await firstHeadPaintMs(page);
    const head = page.getByTestId('task-detail-head');
    await expect(head).toBeVisible();
    await expect(head).toContainText('AGT-2577');
    await expect(head).toContainText('Heavy task with many runs and artifacts');

    // This first board click has no cached core, so the sample includes the
    // intercepted `/core` round trip. The Dossier scopes the 100 ms budget to
    // a core already on the workstation; the thirty-switch cached cohort below
    // asserts it. The uncached sample is recorded as evidence, not gated.
    const completeCore = page.getByTestId('task-core');
    await expect(completeCore).toBeVisible();
    for (const id of ['identity', 'state', 'pins', 'execution', 'status', 'prompt', 'timeline'])
      await expect(page.getByTestId(`task-core-${id}`)).toBeVisible();
    await expect(page.getByTestId('task-core-status')).toContainText('Ready for review.');
    await expect(page.getByTestId('task-core-prompt')).toContainText('Bounded core prompt.');
    await expect(page.getByTestId('task-core-timeline')).toContainText('Recent work');
    await expect.poll(() => documentsRequested).toBe(2);
    await expect(page.getByTestId('studio-board')).toHaveCount(0);

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      mkdirSync(resultsDir, { recursive: true });
      writeFileSync(
        path.join(resultsDir, 'task-detail-navigation-after.json'),
        `${JSON.stringify({ uncachedFirstCoreReadyMs: firstTaskHeadPaintMs, gated: false,
          documentsPendingAtPaint: documentsRequested }, null, 2)}\n`,
      );
      await page.screenshot({
        path: path.join(resultsDir, 'task-detail-navigation-after-loading-light--mocked.png'),
        fullPage: true,
      });
      await setTheme(page, 'dark');
      await page.screenshot({
        path: path.join(resultsDir, 'task-detail-navigation-after-loading-dark--mocked.png'),
        fullPage: true,
      });
    }

    releaseDocuments();
    await expect(page.getByTestId('task-core')).toHaveCount(0);
    await expect(page.getByTestId('task-resource-status')).toContainText('Usage information unavailable');
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  } finally {
    releaseDocuments();
  }
});

test('card and chat link the ready delivery preview with its screenshot', async ({ page }) => {
  await mockApplication(page);
  const preview = {
    address: 'https://agt-2577.fixture.preview.internal', status: 'ready',
    deliverySha: 'a'.repeat(40), screenshotUrl: '/api/projects/fixture/previews/agt-2577/screenshot',
    updatedAtUtc: '2026-08-11T10:00:00Z', expiresAtUtc: '2026-08-18T10:00:00Z', jobId: 'preview-job',
  };
  const previewTask = { ...task(), preview };
  await page.route('**/api/v1/management/credential-inventory', route =>
    json(route, { generatedAt: '2026-08-11T10:00:00Z', items: [] }));
  await page.route('**/api/tasks', route => json(route, [previewTask]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    ...grouped(), humanReview: [previewTask],
  }));
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/overview**`, route =>
    json(route, resource('overview', {})));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource('documents', { name, markdown: 'Ready', summaryState: null }));
  });
  await page.route('**/api/projects/fixture/previews/agt-2577/screenshot**', route =>
    route.fulfill({ status: 200, contentType: 'image/png', body: Buffer.from(
      'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC',
      'base64') }));
  await gotoApp(page, '/');
  await dismissDevErrorDialog(page);
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  const card = page.getByTestId('task-card-preview');
  await expect(card).toContainText('ready');
  await expect(card.getByRole('link', { name: 'Open preview for' })).toHaveAttribute('href', preview.address);
  await expect(card.getByRole('img')).toBeVisible();
  await dismissDevErrorDialog(page);
  await page.locator('app-job-card').filter({ hasText: 'Heavy task with many runs and artifacts' })
    .dispatchEvent('click');
  const detailPreview = page.getByTestId('task-detail-preview');
  await expect(detailPreview).toContainText(preview.deliverySha.substring(0, 12));
  await expect(detailPreview.getByRole('link')).toHaveAttribute('href', preview.address);
  await expect(detailPreview.getByRole('img')).toBeVisible();
  await page.getByTestId('inspector-tab-activity').click();
  await expect(page.getByTestId('activity-chat-preview').getByRole('link')).toHaveAttribute('href', preview.address);
  await dismissDevErrorDialog(page);
  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    mkdirSync(resultsDir, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(resultsDir, `agt-3180-card-preview-${theme}.png`) });
    }
  }
});

test('pending crash recovery stays reviewable without blocking task navigation', async ({ page }) => {
  await mockApplication(page);
  let releaseDocuments!: () => void;
  const documentGate = new Promise<void>(resolve => { releaseDocuments = resolve; });
  await page.route('**/api/crash-recovery/pending', route => json(route, { pending: [{
    id: 'recovery-1', projectName: 'fixture', jobId: TASK_ID,
    reason: 'Uncommitted changes after restart', repoRoot: WATCH_PATH,
    message: 'Recovery', files: ['src/example.ts'], createdAt: '2026-08-11T10:00:00Z',
    classification: 'review',
  }] }));
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, async route => {
    await documentGate;
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource('documents', { name,
      markdown: name === 'prompt' ? 'Prompt' : 'Status', summaryState: null }));
  });

  try {
    await gotoApp(page, '/');
    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('crash-recovery-entry')).toBeVisible();
    await expect(page.getByTestId('crash-recovery-prompt')).toHaveCount(0);
    await clickCard(page);
    await expect(page.getByTestId('task-core')).toBeVisible();
    await expect(page.getByTestId('crash-recovery-entry')).toBeVisible();
    await page.getByTestId('crash-recovery-entry').click();
    await expect(page.getByTestId('crash-recovery-prompt')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('crash-recovery-prompt')).toHaveCount(0);
    await expect(page.getByTestId('crash-recovery-entry')).toBeVisible();
  } finally {
    releaseDocuments();
  }
});

test('keeps keyboard pager and browser history identity while documents and Git hang', async ({ page }) => {
  await mockApplication(page, true);
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route('**/api/tasks/AGT-2577/core**', route => json(route, core()));
  await page.route(`**/api/tasks/${PEER_ID}/core**`, route => json(route, {
    ...core(), id: PEER_ID, taskKey: PEER_KEY, key: 'AGT-2578', title: 'Peer task', order: 2,
    folderPath: `${WATCH_PATH}/${PEER_ID}`,
  }));
  await page.route('**/api/tasks/AGT-2578/core**', route => json(route, {
    ...core(), id: PEER_ID, taskKey: PEER_KEY, key: 'AGT-2578', title: 'Peer task', order: 2,
    folderPath: `${WATCH_PATH}/${PEER_ID}`,
  }));
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/tasks/*/details/documents**', async route => {
    await gate;
    const id = route.request().url().includes(PEER_ID) ? PEER_ID : TASK_ID;
    const key = id === PEER_ID ? PEER_KEY : TASK_KEY;
    const name = new URL(route.request().url()).searchParams.get('name');
    await json(route, { ...resource('documents', { name, markdown: 'Ready', summaryState: null }), id, taskKey: key });
  });
  try {
    await gotoApp(page, '/');
    await dismissDevErrorDialog(page);
    await clickCard(page);
    await expect(page.getByTestId('task-core')).toContainText('Bounded core prompt.');
    await expect(page.getByTestId('task-core-resources')).toContainText('Git: idle');
    await expect(page.getByTestId('task-core-status')).toContainText('Ready for review.');
    await expect(page.getByTestId('task-core-next')).toBeEnabled();
    await page.keyboard.press('j');
    await expect(page.getByTestId('task-detail-head')).toContainText('Peer task');
    await expect(page.getByTestId('task-core')).toHaveAttribute('data-core-version', '7');
    await page.keyboard.press('k');
    await expect(page.getByTestId('task-detail-head')).toContainText('Heavy task with many runs and artifacts');
    await page.goBack();
    await expect(page.getByTestId('task-detail-head')).toContainText('Peer task');
    await page.goForward();
    await expect(page.getByTestId('task-detail-head')).toContainText('Heavy task with many runs and artifacts');
  } finally {
    release();
  }
});

test('opens a public task URL before any board response arrives', async ({ page }) => {
  await mockApplication(page);
  let releaseBoard!: () => void;
  const boardGate = new Promise<void>(resolve => { releaseBoard = resolve; });
  await page.route('**/api/v1/studio/board**', async route => {
    await boardGate;
    await json(route, grouped());
  });
  await page.route('**/api/tasks/AGT-2577/core**', route => json(route, core()));
  let releaseDocuments!: () => void;
  const documentGate = new Promise<void>(resolve => { releaseDocuments = resolve; });
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, async route => {
    await documentGate;
    await json(route, resource('documents', { name: 'prompt', markdown: 'Ready', summaryState: null }));
  });
  try {
    await gotoApp(page, '/#/tasks/AGT-2577');
    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('task-core')).toBeVisible();
    await expect(page.getByTestId('task-core-prompt')).toContainText('Bounded core prompt.');
    await expect(page.getByTestId('studio-board')).toHaveCount(0);
  } finally {
    releaseBoard();
    releaseDocuments();
    // Drain the released handlers before Playwright tears down the context.
    await page.unrouteAll({ behavior: 'wait' });
    await page.close();
  }
});

test('resolves a public task URL on the server when no project owns its key prefix', async ({ page }) => {
  await mockApplication(page);
  const project = (id: string, shortCode: string, storage: string) => ({
    sourceType: 'local-folder', id, displayName: id, shortCode, workspaceId: 'workspace',
    color: null, cliDefault: 'codex', modelDefault: null, sortOrder: 0,
    storageLocation: storage, repositoryPath: storage, rootPath: storage,
    repositoryUrl: null, urls: [], archived: false, createdAt: '2026-08-11T08:00:00Z',
  });
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-08-11T08:00:00Z',
    projects: [project('fixture', 'FIX', WATCH_PATH), project('other', 'OTH', 'C:/fixtures/other')],
  }]));
  let coreRequests = 0;
  await page.route('**/api/tasks/*/core**', route => { coreRequests++; return json(route, core()); });
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/AGT-2577(\?|$)/, route => json(route, {
    info: task(), promptMarkdown: '# Heavy task\n\nResolved by the server.', promptHistory: [],
    titleHistory: [], statusMarkdown: 'Ready for review.', contextUsage: null, log: [],
    summaryState: null, reviewEvidence: [],
  }));

  await gotoApp(page, '/#/tasks/AGT-2577');
  await dismissDevErrorDialog(page);

  await expect(page.getByTestId('studio-task')).toBeVisible();
  await expect(page.getByRole('heading', { name: /Heavy task with many runs and artifacts/ }).first()).toBeVisible();
  await expect(page.locator('app-detail-load-error')).toHaveCount(0);
  expect(coreRequests).toBe(0);
});

test('measures thirty cached-core browser switches without waiting for documents', async ({ page }) => {
  test.setTimeout(120_000);
  await mockApplication(page, true);
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route(`**/api/tasks/${PEER_ID}/core**`, route => json(route, {
    ...core(), id: PEER_ID, taskKey: PEER_KEY, key: 'AGT-2578', title: 'Peer task', order: 2,
    folderPath: `${WATCH_PATH}/${PEER_ID}`,
  }));
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/tasks/*/details/documents**', async route => {
    await gate;
    await json(route, { error: 'cancelled' }, 503);
  });
  try {
    await gotoApp(page, '/');
    await dismissDevErrorDialog(page);
    await clickCard(page);
    await expect(page.getByTestId('task-core')).toBeVisible();
    const samples: number[] = [];
    for (let index = 0; index < 30; index++) {
      const before = await page.evaluate(() => performance.getEntriesByName('task-core-ready').length);
      await page.evaluate(() => document.addEventListener('keydown', () =>
        performance.mark('cached-core-switch-start'), { capture: true, once: true }));
      await page.keyboard.press(index % 2 === 0 ? 'j' : 'k');
      await page.waitForFunction(count => performance.getEntriesByName('task-core-ready').length > count, before);
      await expect(page.getByTestId('task-detail-head'))
        .toContainText(index % 2 === 0 ? 'Peer task' : 'Heavy task with many runs and artifacts');
      samples.push(await page.evaluate(() => {
        const start = performance.getEntriesByName('cached-core-switch-start').at(-1)!;
        const ready = performance.getEntriesByName('task-core-ready').at(-1)!;
        return ready.startTime - start.startTime;
      }));
    }
    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.ceil(sorted.length * 0.95) - 1];
    const output = process.env.JOB_RESULTS_DIR;
    if (output) writeFileSync(path.join(output, 'task-core-cached-switches.json'),
      JSON.stringify({ environment: 'shared Linux runner Chromium, mocked core transport', samples: samples.length,
        p50Ms: sorted[14], p95Ms: p95, maxMs: sorted.at(-1), budgetMs: 100,
        budgetMetOnThisHost: p95 <= 100 }, null, 2) + '\n');
    if (process.env.PERF_WORKSTATION_GATE === '1') expect(p95).toBeLessThanOrEqual(100);
  } finally {
    release();
  }
});

test('waits for the Git pane and keeps the task usable when Git times out', async ({ page }) => {
  await mockApplication(page);
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource('documents', { name,
      markdown: name === 'prompt' ? '# Heavy task\n\nLoaded detail content.' : 'Ready for review.',
      summaryState: null }));
  });
  await page.route(`**/api/tasks/${TASK_ID}/details/usage**`, route =>
    json(route, resource('usage', { contextUsage: null })));
  let gitRequested = 0;
  let releaseGit!: () => void;
  const gitGate = new Promise<void>(resolve => { releaseGit = resolve; });
  await page.route(`**/api/tasks/${TASK_ID}/details/git**`, async route => {
    gitRequested++;
    await gitGate;
    await json(route, { error: 'refresh timed out' }, 503);
  });
  try {
    await gotoApp(page, '/');
    await dismissDevErrorDialog(page);
    await clickCard(page);
    await expect(page.getByTestId('studio-pane-toggle-git')).toBeVisible();
    expect(gitRequested).toBe(0);
    await page.getByTestId('studio-pane-toggle-git').click();
    await expect.poll(() => gitRequested).toBe(1);
    await expect(page.getByTestId('studio-task')).toBeVisible();
    releaseGit();
    await expect(page.getByTestId('task-resource-status')).toContainText('Git information unavailable');
    await expect(page.getByTestId('studio-pane-toggle-git')).toBeVisible();
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  } finally {
    releaseGit();
  }
});

test('keeps the task head and gives every failed section a retry', async ({ page }) => {
  await mockApplication(page);
  let attempt = 0;
  await page.route(`**/api/tasks/${TASK_ID}/core**`, route => {
    attempt++;
    return attempt === 1
      ? json(route, { title: 'Temporary failure' }, 503)
      : json(route, core());
  });
  // Hold documents so the retried core stays on screen long enough to assert.
  let releaseDocuments!: () => void;
  const documentGate = new Promise<void>(resolve => { releaseDocuments = resolve; });
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, async route => {
    await documentGate;
    await json(route, { error: 'released' }, 503);
  });

  await gotoApp(page, '/');
  await dismissDevErrorDialog(page);
  await setTheme(page, 'light');
  await clickCard(page);

  await expect(page.getByTestId('task-detail-head')).toContainText('Heavy task with many runs and artifacts');
  const sectionErrors = page.getByTestId('task-detail-load-sections').locator('[role="alert"]');
  await expect(sectionErrors).toHaveCount(3);
  await expect(sectionErrors.first()).toContainText('The detail request failed');
  await expect(page.locator('app-detail-load-error')).toHaveCount(0);

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    await page.screenshot({
      path: path.join(resultsDir, 'task-detail-navigation-section-error-light--mocked.png'),
      fullPage: true,
    });
  }

  await page.getByTestId('task-detail-section-retry-activity').click();
  await expect(page.getByTestId('task-core')).toBeVisible();
  expect(attempt).toBe(2);
  releaseDocuments();
  await expect(page.getByTestId('task-resource-retry-documents')).toBeVisible();
});
