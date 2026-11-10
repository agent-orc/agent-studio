import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3617 (newcomer playthrough AGT-3560, finding H1). With the cause
 * breaker open, held cards said "Waiting for review slot · position 1" while
 * the review host was idle, no UI reached /api/cause-breakers, and the probe
 * always took the oldest card, whose delivery still carried the broken test.
 * The breaker cleared only after that card was deleted.
 *
 * Scenario on a stateful breaker mock: three failed reviews open the breaker,
 * a fixed delivery arrives, the owner presses "Re-check now" on the
 * intervention card, the newest delivery is the probe, and its green review
 * releases every held card. Probe selection and release themselves are
 * covered against the real service in CauseBreakerRecheckTests.
 */
const PROJECT = 'Demo App';
const WATCH_PATH = '/fixtures/demo-app';
const RESULTS = process.env.JOB_RESULTS_DIR ?? 'test-results';
const NOW = '2026-11-09T09:00:00.000Z';
const HIDE_DEV_DIALOG = 'app-error-dialog { display: none !important; }';
const FINGERPRINT = '5a1e0c9d7b3f2e61';
const SUMMARY = 'review cannot read the test report';

interface BreakerState {
  failures: number;
  fixDelivered: boolean;
  probeKey: string | null;
  released: boolean;
  probeRequests: unknown[];
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function card(id: string, title: string, state: string, extra: Record<string, unknown> = {}) {
  return {
    id, key: id, taskKey: `${WATCH_PATH}::${id}`, title, state, order: 1,
    agent: 'codex', cliType: 'codex', model: 'gpt-5.6-codex',
    createdAt: '2026-11-09T07:00:00.000Z', lastActivity: '2026-11-09T08:55:00.000Z',
    enteredLaneAt: '2026-11-09T08:00:00.000Z',
    watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/tasks/000/${id}`,
    ownerClientId: 'local-default', commits: [], tags: [], taskType: 'feature',
    sessionName: null, useOwnSession: null, lastUsage: null, execution: null, commit: null,
    ...extra,
  };
}

function held(id: string, title: string, since: string, open: boolean) {
  return card(id, title, '4-auto-review', {
    liveStatus: {
      attempt: 2, activeStep: null,
      nextSteps: [{ stepId: 'build-tests', displayName: 'Build and tests' }],
      queue: { kind: 'review', position: 1 },
      latestEventAt: since,
    },
    causeWait: open ? {
      causeKey: 'DEMO-2', fingerprint: FINGERPRINT, failureClass: 'ReviewInfra/TestReportUnavailable',
      since, reason: 'waiting for DEMO-2: ReviewInfra/TestReportUnavailable on tool:dotnet', summary: SUMMARY,
    } : null,
  });
}

function cards(state: BreakerState) {
  const open = state.failures >= 3 && !state.released;
  const list = [held('DEMO-1', 'Add the calendar export test', '2026-11-09T08:10:00.000Z', open)];
  if (state.failures >= 3) {
    list.push(card('DEMO-2', 'Intervention: ReviewInfra/TestReportUnavailable', '1-preparation', {
      creationSource: 'orchestrator', createdBy: 'Orchestrator', taskType: 'bug',
      noBranchExpected: true, requiresIntegration: false,
      references: { followUpOf: ['DEMO-1'] },
    }));
  }
  if (state.fixDelivered) list.push(held('DEMO-4', 'Fix the calendar export test', '2026-11-09T08:45:00.000Z', open));
  return list;
}

/** The bounded core the Studio opens a card from (AGT-2953). */
function core(info: ReturnType<typeof card>) {
  return {
    state: 'ready', projectId: PROJECT, projectName: PROJECT,
    id: info.id, taskKey: info.taskKey, key: info.key, watchPath: WATCH_PATH, folderPath: info.folderPath,
    title: info.title, kind: 'task', taskType: info.taskType, lane: info.state, archiveState: null,
    enteredLaneAt: info.enteredLaneAt, order: 1, mode: 'coding', released: false, pendingIntent: false,
    coreVersion: '1',
    pins: { model: 'gpt-5.6-codex', modelExplicit: false, thinkingLevel: null, thinkingLevelExplicit: false,
      cliType: 'codex', contextMode: null, useOwnSession: null, allowWebAccess: false, noBranchExpected: true },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null, blockerDescription: null,
      needsInput: null, dependencyBlocked: false, dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null, executionStatus: null,
      location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: '', originalBytes: 0, hash: 'status', cursor: null },
    prompt: { state: 'ready', text: 'Diagnose and correct the shared failure.', originalBytes: 40,
      hash: 'prompt', cursor: null, continuationUrl: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  };
}

function resource(info: ReturnType<typeof card>, name: string, data: unknown) {
  return { id: info.id, taskKey: info.taskKey, projectId: PROJECT, attemptId: null, coreVersion: '1',
    resource: name, version: 'v1', computedAt: NOW, state: 'ready', data, reason: null };
}

function board(state: BreakerState) {
  const all = cards(state);
  const lane = (name: string) => all.filter(item => item.state === name);
  return {
    backlog: [], preparation: lane('1-preparation'), orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: lane('4-auto-review'), autoReview: lane('4-auto-review'),
    humanReview: [], escalated: [], completed: [], archive: [],
  };
}

function causeView(state: BreakerState) {
  if (state.failures < 3 || state.released) return null;
  const heldCards = [
    ...(state.fixDelivered ? [{
      taskKey: 'DEMO-4', key: 'DEMO-4', title: 'Fix the calendar export test', projectName: PROJECT,
      watchPath: WATCH_PATH, since: '2026-11-09T08:45:00.000Z', deliveredAt: '2026-11-09T08:44:00.000Z',
      recheckFirst: true,
    }] : []),
    {
      taskKey: 'DEMO-1', key: 'DEMO-1', title: 'Add the calendar export test', projectName: PROJECT,
      watchPath: WATCH_PATH, since: '2026-11-09T08:10:00.000Z', deliveredAt: '2026-11-09T08:05:00.000Z',
      recheckFirst: !state.fixDelivered,
    },
  ];
  return {
    fingerprint: FINGERPRINT, failureClass: 'ReviewInfra/TestReportUnavailable', summary: SUMMARY,
    toolchain: 'tool:dotnet', causeKey: 'DEMO-2', openedAt: '2026-11-09T08:10:00.000Z',
    probeTaskKey: state.probeKey, probeKey: state.probeKey, probeState: state.probeKey ? 'running' : null,
    resolving: false, held: heldCards,
  };
}

async function installRoutes(page: Page, state: BreakerState): Promise<void> {
  await page.clock.setFixedTime(new Date(NOW));
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  await page.route('**:5039/update/status', route => json(route, { isRunning: false, behindBy: 0 }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/cli/quota**', route => json(route, { at: NOW, ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/cli/usage**', route => json(route, { sessions: [] }));
  await page.route('**/api/usage/cockpit**', route => json(route, { workspaceId: null, clis: [], cost: null }));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  // A workspace with no project opens the first-run checklist over the board.
  await page.route(/\/api\/v1\/projects(\?|$)/, route => json(route, [
    { id: PROJECT, displayName: PROJECT, shortCode: 'DA', workspaceId: 'default', color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: WATCH_PATH, archived: false, createdAt: '2026-11-01T08:00:00.000Z' },
  ]));
  await page.route('**/api/v1/studio/board**', route => json(route, board(state)));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, cards(state)));
  await page.route(/\/api\/tasks\/archive(\?|$)/, route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/(DEMO-\d+)(\?|$)/, route => {
    const id = /tasks\/(DEMO-\d+)/.exec(route.request().url())![1];
    const info = cards(state).find(item => item.id === id);
    if (!info) return json(route, { error: 'not found' }, 404);
    return json(route, {
      info, promptMarkdown: 'Diagnose and correct the shared failure.', statusMarkdown: '',
      log: [], promptHistory: [], reviewEvidence: [],
    });
  });
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(?:\?.*)?$/, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, cost: null, config: {},
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/runs(?:\?.*)?$/, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [],
    promptEntries: [], refinements: [], runnerEvents: [], reviewAttemptEpoch: 0, reviewAttemptCycles: [],
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/session-events(?:\?.*)?$/, route => json(route, {
    events: [], sessionChain: [], currentSessionId: null,
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/code-review\/list(?:\?.*)?$/, route => json(route, { entries: [] }));
  await page.route(/\/api\/tasks\/[^/?]+\/plan(?:\?.*)?$/, route => json(route, {
    hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
    softEstimateMedian: null, items: [], unassignedSubActions: [],
  }));
  const find = (url: string) => cards(state).find(item => item.id === /tasks\/(DEMO-\d+)/.exec(url)?.[1]);
  await page.route(/\/api\/tasks\/DEMO-\d+\/core(\?|$)/, route => {
    const info = find(route.request().url());
    return info ? json(route, core(info)) : json(route, { error: 'not found' }, 404);
  });
  await page.route(/\/api\/tasks\/DEMO-\d+\/details\/[a-z]+/, route => {
    const url = route.request().url();
    const info = find(url);
    if (!info) return json(route, { error: 'not found' }, 404);
    const name = /details\/([a-z]+)/.exec(url)![1];
    const data: Record<string, unknown> = {
      documents: { name: new URL(url).searchParams.get('name'), markdown: 'Diagnose and correct the shared failure.', summaryState: null },
      review: { reviewProjection: null, evidence: null },
      git: { mergeSignal: null, integration: null, publishSignal: null, testEvidence: null, commit: null, commits: [], provenance: null },
      overview: { decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null,
        relatedWikiPages: [], creationSource: info.creationSource ?? 'operator', completionClaim: null,
        parkedBlocker: null, needsInput: null,
        references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] }, waitsOn: null },
    };
    return name in data ? json(route, resource(info, name, data[name])) : json(route, { error: 'offline' }, 503);
  });
  await page.route(/\/api\/cause-breakers\/by-cause\/[^/?]+/, route => json(route, { breaker: causeView(state) }));
  await page.route(/\/api\/cause-breakers\/[^/]+\/probe$/, route => {
    const body = route.request().postDataJSON() as { taskKey?: string } | null;
    state.probeRequests.push(body);
    // The service picks the newest waiting delivery unless the owner named a card.
    state.probeKey = body?.taskKey ?? (state.fixDelivered ? 'DEMO-4' : 'DEMO-1');
    return json(route, { fingerprint: FINGERPRINT, state: 'open', probeTaskKey: state.probeKey });
  });
}

async function open(page: Page): Promise<void> {
  await page.goto('/', { waitUntil: 'commit' });
  await page.addStyleTag({ content: HIDE_DEV_DIALOG });
  await dismissDevErrorDialog(page);
}

function boardCard(page: Page, title: string) {
  return page.getByTestId('task-card').filter({ hasText: title });
}

test.describe('Cause breaker recovery from the board (AGT-3617)', () => {
  test('fail x3, fix, re-check, release without the API', async ({ page }, testInfo) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    const state: BreakerState = { failures: 1, fixDelivered: false, probeKey: null, released: false, probeRequests: [] };
    await installRoutes(page, state);
    mkdirSync(RESULTS, { recursive: true });
    const shot = async (name: string, locator: ReturnType<Page['locator']>) => {
      const path = join(RESULTS, `cause-breaker-${name}--mocked.png`);
      await locator.screenshot({ path, style: HIDE_DEV_DIALOG });
      await testInfo.attach(name, { path, contentType: 'image/png' });
    };

    // Below the threshold the card is an ordinary retry in the review queue.
    await open(page);
    const first = boardCard(page, 'Add the calendar export test');
    await expect(first.getByTestId('task-live-current')).toContainText('Waiting for review slot · position 1', { timeout: 45_000 });

    // Fail x3: the breaker opens and the CURRENT line names the hold.
    state.failures = 3;
    await open(page);
    const hold = first.getByTestId('task-live-hold');
    await expect(hold).toHaveText(`Held behind DEMO-2: ${SUMMARY}`, { timeout: 45_000 });
    await expect(first.getByTestId('task-live-current')).not.toContainText('Waiting for review slot');

    // Fix: a later delivery with the corrected test is held behind the same cause.
    state.fixDelivered = true;
    await open(page);
    const fixed = boardCard(page, 'Fix the calendar export test');
    await expect(fixed.getByTestId('task-live-hold')).toHaveText(`Held behind DEMO-2: ${SUMMARY}`, { timeout: 45_000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await shot(`held-card-${theme}`, first);
    }

    // The link on the held card opens the intervention card.
    await first.getByTestId('task-live-hold-link').click();
    const panel = page.getByTestId('cause-breaker-recheck');
    await expect(panel).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('cause-breaker-recheck-line'))
      .toHaveText(`2 cards are held behind this card: ${SUMMARY}`);
    await expect(panel.getByTestId('cause-breaker-held-card').first()).toHaveAttribute('data-key', 'DEMO-4');
    await expect(panel).toContainText('newest delivery (DEMO-4)');
    // One primary action.
    await expect(panel.getByRole('button', { name: 'Re-check now' })).toHaveCount(1);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await shot(`intervention-panel-${theme}`, panel);
    }

    // Re-check: the newest delivery is released as the probe.
    await panel.getByTestId('cause-breaker-recheck-now').click();
    await expect(page.getByTestId('cause-breaker-recheck-state')).toContainText('Re-checking DEMO-4');
    expect(state.probeRequests).toEqual([{}]);
    await expect(page.getByTestId('cause-breaker-recheck-now')).toBeDisabled();
    await shot('intervention-rechecking-dark', panel);

    // Release: the probe's review passed, the breaker closed, every held card got a fresh review.
    state.released = true;
    await open(page);
    await expect(first.getByTestId('task-live-current')).toContainText('Waiting for review slot · position 1', { timeout: 45_000 });
    await expect(first.getByTestId('task-live-hold')).toHaveCount(0);
    await expect(fixed.getByTestId('task-live-hold')).toHaveCount(0);
    await expect(page.getByTestId('cause-breaker-recheck')).toHaveCount(0);
  });

  test('the owner can re-check a chosen card, and the panel fits 390 px', async ({ page }, testInfo) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 390, height: 844 });
    const state: BreakerState = { failures: 3, fixDelivered: true, probeKey: null, released: false, probeRequests: [] };
    await installRoutes(page, state);
    await page.goto(`/?job=DEMO-2&watchPath=${encodeURIComponent(WATCH_PATH)}`, { waitUntil: 'commit' });
    await page.addStyleTag({ content: HIDE_DEV_DIALOG });
    await dismissDevErrorDialog(page);

    const panel = page.getByTestId('cause-breaker-recheck');
    await expect(panel).toBeVisible({ timeout: 45_000 });
    const box = await panel.boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(390);
    mkdirSync(RESULTS, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      const path = join(RESULTS, `cause-breaker-intervention-panel-390-${theme}--mocked.png`);
      await panel.screenshot({ path, style: HIDE_DEV_DIALOG });
      await testInfo.attach(`intervention panel 390 ${theme}`, { path, contentType: 'image/png' });
    }

    await panel.getByTestId('cause-breaker-recheck-card-DEMO-1').click();
    await expect(page.getByTestId('cause-breaker-recheck-state')).toContainText('Re-checking DEMO-1');
    expect(state.probeRequests).toEqual([{ taskKey: 'DEMO-1' }]);
  });
});
