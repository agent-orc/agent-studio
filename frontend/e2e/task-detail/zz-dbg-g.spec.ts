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
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
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
  const card = page.locator('[data-testid="task-card"], [data-testid="job-card"]')
    .filter({ hasText: 'Heavy task with many runs and artifacts' });
  await expect(card).toBeVisible();
  const box = await card.boundingBox();
  if (!box) throw new Error('Task card has no layout box');
  await card.click({ position: { x: box.width / 2, y: box.height - 4 }, force: true });
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

test('DBG g public URL rich', async ({ page }) => {
  await mockApplication(page);
  await page.route('**/api/tasks/AGT-2577/core**', route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, async route => {
    await json(route, resource('documents', { name: 'prompt', markdown: 'Ready', summaryState: null }));
  });
  await gotoApp(page, '/#/tasks/AGT-2577');
  await dismissDevErrorDialog(page);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Debugger.enable');
  await page.waitForTimeout(6000);
  const paused = new Promise<any>(resolve => cdp.once('Debugger.paused', resolve));
  await cdp.send('Debugger.pause');
  const event = await Promise.race([paused, new Promise(r => setTimeout(() => r(null), 5000))]) as any;
  if (!event) console.log('STACK none (not hung?)');
  else {
    const frames = event.callFrames;
    console.log('STACKDEPTH', frames.length);
    const scripts = new Map<string, string>();
    for (const frame of [...frames.slice(0, 6), ...frames.slice(-25)]) {
      const id = frame.location.scriptId;
      if (!scripts.has(id)) scripts.set(id, (await cdp.send('Debugger.getScriptSource', { scriptId: id })).scriptSource);
      const src = scripts.get(id)!.split('\n')[frame.location.lineNumber] ?? '';
      const col = frame.location.columnNumber;
      console.log('STACK', JSON.stringify(frame.functionName), frame.url, frame.location.lineNumber, col, '::', src.slice(Math.max(0, col - 120), col + 120));
    }
  }
  await cdp.send('Debugger.resume').catch(() => undefined);
});
