import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3096: the "acceptance needs an override" notice in the core-first task
 * detail. AGT-3076 projected the gap only on the legacy full-detail read; the
 * Studio opens a card from `/core` plus the `/details/*` resources. These
 * tests open the card through the canonical `#/tasks/<key>` URL and fail if
 * the legacy detail route is read, so the notice must come from
 * `/details/overview`.
 *
 * Fully mocked (no backend), on the harness of
 * task-detail-instant-navigation.spec.ts.
 */

const WATCH_PATH = 'C:/fixtures/acceptance-attempt-gap';
const TASK_ID = 'agt-3042-lost-attempt';
const TASK_REF = 'AGT-3042';
const TASK_KEY = `${WATCH_PATH}::${TASK_ID}`;
const DELIVERY_SHA = 'abcdef0123456789abcdef0123456789abcdef01';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const REFUSAL = `Acceptance refused: no run attempt in the current authority epoch (2) for '${TASK_REF}'. `
  + "The review subject names run attempt 'run-agt-3042', which the attempt authority store no longer holds. "
  + 'Accept with an operator override and a written reason to record the acceptance against the delivery evidence.';
const GAP = { authorityEpoch: 2, subjectRunAttemptId: 'run-agt-3042', resultSha: DELIVERY_SHA, reason: REFUSAL };

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function task() {
  return {
    id: TASK_ID, taskKey: TASK_KEY, key: TASK_REF, displayKey: TASK_REF,
    title: 'Lost run attempt fixture', state: '5-human-review', kind: 'task', mode: 'coding',
    agent: 'codex', cliType: 'codex', model: 'gpt-5.2-codex', order: 1,
    createdAt: '2026-10-10T08:00:00Z', lastActivity: '2026-10-10T10:00:00Z',
    watchPath: WATCH_PATH, projectName: 'fixture', folderPath: `${WATCH_PATH}/${TASK_ID}`,
    sessionName: null, useOwnSession: null, lastUsage: null, execution: null,
    commit: { sha: DELIVERY_SHA, shortSha: 'abcdef0', message: 'feat: delivery',
      filesChanged: 1, files: ['src/delivery.ts'], at: '2026-10-10T09:00:00Z' },
    commits: [],
    integration: { status: 'integrated', integrationBranch: 'develop', sha: DELIVERY_SHA,
      deliveryRef: 'runner/agent-runner-01/AGT-3042' },
    estimatedTokens: 0,
  };
}

function core() {
  return {
    state: 'ready', projectId: 'fixture', projectName: 'fixture',
    id: TASK_ID, taskKey: TASK_KEY, key: TASK_REF,
    watchPath: WATCH_PATH, folderPath: `${WATCH_PATH}/${TASK_ID}`,
    title: 'Lost run attempt fixture', kind: 'task', taskType: 'feature',
    lane: '5-human-review', archiveState: null, enteredLaneAt: '2026-10-10T10:00:00Z',
    order: 1, mode: 'coding', released: false, pendingIntent: false, coreVersion: '3',
    pins: { model: 'gpt-5.2-codex', modelExplicit: true, thinkingLevel: null,
      thinkingLevelExplicit: false, cliType: 'codex', contextMode: null,
      useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
      blockerDescription: null, needsInput: null, dependencyBlocked: false,
      dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: 'Delivered and integrated.', originalBytes: 25,
      hash: 'status', cursor: null },
    prompt: { state: 'ready', text: '# Lost run attempt\n\nIntegrated before the store rebuild.',
      originalBytes: 60, hash: 'prompt', cursor: null, continuationUrl: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  };
}

function resource(name: string, data: unknown) {
  return { id: TASK_ID, taskKey: TASK_KEY, projectId: 'fixture', attemptId: null,
    coreVersion: '3', resource: name, version: 'v1', computedAt: '2026-10-10T10:00:00Z',
    state: 'ready', data, reason: null };
}

function overview(gap: typeof GAP | null) {
  return resource('overview', {
    decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null,
    relatedWikiPages: [], creationSource: 'operator', completionClaim: null,
    parkedBlocker: null, needsInput: null,
    references: { dependsOn: [], relatesTo: [], supersedes: [] }, waitsOn: null,
    acceptanceAttemptGap: gap,
  });
}

interface Traffic { legacyDetailReads: string[]; overviewReads: number; moves: Record<string, unknown>[] }

async function mockApplication(page: Page, move: (route: Route) => Promise<void>): Promise<Traffic> {
  const traffic: Traffic = { legacyDetailReads: [], overviewReads: 0, moves: [] };
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/hubs/v1/studio/negotiate**', route => json(route, {
    connectionId: 'mock', connectionToken: 'mock', negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (String(message).includes('"protocol"')) socket.send('{}\x1e');
    });
  });
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { id: 'fixture', name: 'fixture', shortCode: 'AGT', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-10-10T08:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'fixture', displayName: 'fixture', shortCode: 'AGT',
      workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
      sortOrder: 0, storageLocation: WATCH_PATH, repositoryPath: WATCH_PATH,
      rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-10-10T08:00:00Z',
    }],
  }]));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/usage/cockpit**', route => json(route, {}, 503));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-10-10T10:00:00Z', snapshots: [] }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, [task()]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [task()],
    escalated: [], completed: [], archive: [],
  }));
  await page.route(`**/api/tasks/${TASK_ID}/runs**`, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null,
    hasActiveRun: false, runs: [], promptEntries: [], refinements: [], runnerEvents: [],
  }));
  await page.route(`**/api/tasks/${TASK_ID}/session-events**`, route =>
    json(route, { events: [], sessionChain: [] }));
  await page.route(`**/api/tasks/${TASK_ID}/plan**`, route => json(route, { hasPlan: false,
    source: null, snapshotCount: 0, activeItemId: null, items: [], unassignedSubActions: [] }));
  await page.route(`**/api/tasks/${TASK_ID}/pipeline**`, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, executions: [], config: {}, cost: null,
  }));
  await page.route(`**/api/tasks/${TASK_ID}/provenance**`, route => json(route, {
    branch: 'runner/agent-runner-01/AGT-3042', base: 'base0000', transitions: [], merge: null,
    landedState: 'merged-to-develop',
    ladder: { branch: 'runner/agent-runner-01/AGT-3042', branchTip: DELIVERY_SHA,
      integrationBranch: 'develop', integrationHead: DELIVERY_SHA, mergedToIntegration: true,
      releaseBranch: 'main', releaseHead: 'mainhead', releasedToRelease: false },
    commits: [],
  }));
  await page.route(new RegExp(`/api/tasks/${TASK_ID}/delivery-claim(\\?|$)`), route => json(route, {
    taskKey: TASK_KEY, jobId: TASK_ID, lane: '5-human-review', deliveryRef: 'runner/agent-runner-01/AGT-3042',
    integrationBranch: 'develop', releaseBranch: 'main', containmentStatus: 'integrated',
    integrated: true, released: false, integratedSha: DELIVERY_SHA, mergeCommit: DELIVERY_SHA,
    mergeSubject: 'merge(AGT-3042): integrate reviewed delivery', mergedAtUtc: '2026-10-10T12:00:00Z',
    hasIntegrationRecord: true, class: 'integrated-delivery', findings: [], completionClaim: null,
    commits: [], detail: null,
  }));
  await page.route(new RegExp(`/api/tasks/(?:${TASK_REF}|${TASK_ID})/core(\\?|$)`), route => json(route, core()));
  await page.route(`**/api/tasks/${TASK_ID}/details/documents**`, route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource('documents', { name,
      markdown: name === 'prompt' ? '# Lost run attempt\n\nIntegrated before the store rebuild.' : 'Delivered.',
      summaryState: null }));
  });
  await page.route(`**/api/tasks/${TASK_ID}/details/usage**`, route => json(route, { error: 'usage offline' }, 503));
  await page.route(`**/api/tasks/${TASK_ID}/details/review**`, route =>
    json(route, resource('review', { reviewProjection: null, evidence: null })));
  await page.route(`**/api/tasks/${TASK_ID}/details/overview**`, route => {
    traffic.overviewReads++;
    return json(route, overview(GAP));
  });
  // The legacy full-detail read is the path AGT-3076 covered. Record any
  // read so a fall-back to it fails the test instead of passing silently.
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/(?:${TASK_REF}|${TASK_ID})(\\?|$)`), route => {
    traffic.legacyDetailReads.push(route.request().url());
    return json(route, { error: 'legacy detail read' }, 500);
  });
  await page.route(new RegExp(`/tasks/${TASK_ID}/move(\\?|$)`), route => {
    traffic.moves.push(route.request().postDataJSON() as Record<string, unknown>);
    return move(route);
  });
  return traffic;
}

async function openCoreFirst(page: Page): Promise<void> {
  page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text()); });
  page.on('pageerror', e => console.log('PAGEERROR', e.message));
  page.on('response', r => { if (r.status() >= 400) console.log('HTTP', r.status(), r.url()); });
  page.on('request', r => { if (r.url().includes('/api/tasks') || r.url().includes('/tasks/')) console.log('REQ', r.url()); });
  await page.goto(`/#/tasks/${TASK_REF}`, { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await dismissDevErrorDialog(page);
  await page.waitForTimeout(8000);
  await page.screenshot({ path: '/tmp/agt3096-dbg.png' });
  console.log('BODY', (await page.locator('body').innerText()).slice(0, 1500));
  await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 30_000 });
}

async function screenshot(page: Page, fileName: string): Promise<void> {
  if (!RESULTS_DIR) return;
  mkdirSync(RESULTS_DIR, { recursive: true });
  await page.getByTestId('delivery-claim-panel').locator('..').screenshot({ path: path.join(RESULTS_DIR, fileName) });
}

test.describe('Acceptance attempt gap in the core-first detail (AGT-3096)', () => {
  test.use({ serviceWorkers: 'block' });

  test('shows the notice from the overview resource and accepts with a written override reason', async ({ page }) => {
    const traffic = await mockApplication(page, route => json(route, {}));
    await openCoreFirst(page);

    const notice = page.getByTestId('acceptance-attempt-gap');
    await expect(notice).toContainText('Acceptance needs an operator override', { timeout: 15_000 });
    await expect(page.getByTestId('acceptance-attempt-gap-epoch')).toHaveText('2');
    await expect(page.getByTestId('acceptance-attempt-gap-reason'))
      .toContainText('no run attempt in the current authority epoch (2)');
    await expect(notice).toContainText('run-agt-3042');
    await expect(notice).toContainText('written reason');
    expect(traffic.overviewReads).toBeGreaterThan(0);

    await page.getByTestId('acceptance-attempt-gap-override').click();
    const submit = page.getByTestId('acceptance-attempt-gap-submit');
    await expect(submit).toBeDisabled();
    await page.getByTestId('acceptance-attempt-gap-override-reason')
      .fill('Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.');
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await screenshot(page, `acceptance-attempt-gap-core-first-${theme}.png`);
    }
    await expect(submit).toBeEnabled();
    await submit.click();

    await expect.poll(() => traffic.moves.length).toBe(1);
    expect(traffic.moves[0]).toMatchObject({
      targetState: '6-completed',
      operatorOverride: true,
      reason: 'Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.',
    });
    expect(traffic.legacyDetailReads).toEqual([]);
  });

  test('Accept answered 409 run-attempt-missing reopens the card on the notice, never a silent refresh', async ({ page }) => {
    const traffic = await mockApplication(page, route =>
      json(route, { error: REFUSAL, code: 'run-attempt-missing' }, 409));
    await openCoreFirst(page);
    await expect(page.getByTestId('acceptance-attempt-gap')).toBeVisible({ timeout: 15_000 });
    const readsBeforeAccept = traffic.overviewReads;

    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toHaveText(/Accept/);
    await expect(accept).toBeEnabled();
    await accept.click();

    await expect.poll(() => traffic.moves.length).toBe(1);
    expect(traffic.moves[0]).toMatchObject({ targetState: '6-completed' });
    expect(traffic.moves[0]['operatorOverride']).toBeUndefined();
    // The refusal is named, and the reopened card reads its overview again.
    await expect(page.getByText('Acceptance needs an operator override').first()).toBeVisible();
    await expect(page.getByText(/no run attempt in the current authority epoch \(2\)/).first()).toBeVisible();
    await expect.poll(() => traffic.overviewReads).toBeGreaterThan(readsBeforeAccept);
    await expect(page.getByTestId('acceptance-attempt-gap')).toBeVisible();
    await expect(page.getByTestId('acceptance-attempt-gap-override')).toBeVisible();
    await screenshot(page, 'acceptance-attempt-gap-core-first-after-409.png');
    expect(traffic.legacyDetailReads).toEqual([]);
  });
});
