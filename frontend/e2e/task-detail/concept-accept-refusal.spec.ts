import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3704 (AGT-3649 finding F11): a concept card in Human review showed
 * "Checking git status" and then "Await integration" because the Git resource
 * answered integration null, and a refused Accept or lane pick said nothing.
 *
 * Fully mocked (no backend), on the AGT-3096 core-first harness. The card
 * opens through `#/tasks/<key>` with the Git pane closed (the default layout).
 */

const WATCH_PATH = 'C:/fixtures/concept-accept';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const DELIVERY_SHA = 'abcdef0123456789abcdef0123456789abcdef01';

interface Fixture {
  id: string;
  ref: string;
  mode: 'concept' | 'coding';
  /** What the board card carries: null is a projection that has not caught up. */
  boardIntegration: Record<string, unknown> | null;
  /** What the Git resource answers. */
  gitIntegration: Record<string, unknown> | null;
  /** AGT-3874: the review projection on the board card and the review resource. */
  reviewProjection?: Record<string, unknown> | null;
  /** AGT-3873: the park marker projection, the status document and the pipeline record. */
  parkedBlocker?: Record<string, unknown> | null;
  statusMarkdown?: string;
  pipelineExecution?: Record<string, unknown> | null;
}

const CONCEPT: Fixture = {
  id: 'agt-3649-concept', ref: 'AGT-3649', mode: 'concept',
  boardIntegration: null,
  gitIntegration: { status: 'not-applicable', integrationBranch: 'develop', sha: null, deliveryRef: null,
    detail: 'The delivery contract expects no repository change.' },
};

const PENDING: Fixture = {
  id: 'agt-3650-pending', ref: 'AGT-3650', mode: 'coding',
  boardIntegration: { status: 'pending', integrationBranch: 'develop', sha: null,
    deliveryRef: 'runner/agent-runner-01/AGT-3650', detail: 'Not in develop yet.' },
  gitIntegration: { status: 'pending', integrationBranch: 'develop', sha: null,
    deliveryRef: 'runner/agent-runner-01/AGT-3650', detail: 'Not in develop yet.' },
};

const INTEGRATED: Fixture = {
  id: 'agt-3651-integrated', ref: 'AGT-3651', mode: 'coding',
  boardIntegration: { status: 'integrated', integrationBranch: 'develop', sha: DELIVERY_SHA,
    deliveryRef: 'runner/agent-runner-01/AGT-3651', detail: null },
  gitIntegration: { status: 'integrated', integrationBranch: 'develop', sha: DELIVERY_SHA,
    deliveryRef: 'runner/agent-runner-01/AGT-3651', detail: null },
};

const CONCERNS_SUMMARY = 'The 830-card first-paint target remains unmet and lacks idle-host confirmation';

/** AGT-3874: an integrated delivery whose requirement-fit verdict is `concerns`. */
const CONCERNS: Fixture = {
  ...INTEGRATED, id: 'agt-3534-concerns', ref: 'AGT-3534',
  reviewProjection: {
    attempts: [{ plane: 'remote', attemptId: 'review-1', receivedAt: '2026-11-09T09:30:00Z', outcome: 'Pass',
      grade: null, buildTestsResult: 'passed', buildTestsReason: null, subjectSha: DELIVERY_SHA,
      reportRef: 'remote-review-grade-review-1.md', aspects: [
        { aspect: 'requirement-fit', status: 'concerns', classification: 'product', summary: CONCERNS_SUMMARY },
      ] }],
    rounds: 1, latestPlane: 'remote', latestOutcome: 'Pass', latestReceivedAt: '2026-11-09T09:30:00Z',
    blockingAspects: [], delivery: { status: 'integrated', reason: null },
    decisionRequired: { required: false, source: null, reason: null },
  },
};

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function key(f: Fixture): string { return `${WATCH_PATH}::${f.id}`; }

function task(f: Fixture) {
  const coded = f.mode === 'coding';
  return {
    id: f.id, taskKey: key(f), key: f.ref, displayKey: f.ref,
    title: coded ? 'Delivery fixture' : 'Concept Dossier fixture', state: '5-human-review',
    kind: 'task', mode: f.mode, taskType: coded ? 'feature' : 'concept',
    agent: 'codex', cliType: 'codex', model: 'gpt-5.2-codex', order: 1,
    createdAt: '2026-11-09T08:00:00Z', lastActivity: '2026-11-09T10:00:00Z',
    watchPath: WATCH_PATH, projectName: 'fixture', folderPath: `${WATCH_PATH}/${f.id}`,
    sessionName: null, useOwnSession: null, lastUsage: null, execution: null,
    commit: coded ? { sha: DELIVERY_SHA, shortSha: 'abcdef0', message: 'feat: delivery',
      filesChanged: 1, files: ['src/delivery.ts'], at: '2026-11-09T09:00:00Z' } : null,
    commits: [],
    integration: f.boardIntegration,
    reviewProjection: f.reviewProjection ?? null,
    parkedBlocker: f.parkedBlocker ?? null,
    requiresIntegration: coded ? null : false,
    estimatedTokens: 0,
  };
}

function core(f: Fixture) {
  return {
    state: 'ready', projectId: 'fixture', projectName: 'fixture',
    id: f.id, taskKey: key(f), key: f.ref, watchPath: WATCH_PATH, folderPath: `${WATCH_PATH}/${f.id}`,
    title: task(f).title, kind: 'task', taskType: task(f).taskType,
    lane: '5-human-review', archiveState: null, enteredLaneAt: '2026-11-09T10:00:00Z',
    order: 1, mode: f.mode, released: false, pendingIntent: false, coreVersion: '3',
    pins: { model: 'gpt-5.2-codex', modelExplicit: true, thinkingLevel: null,
      thinkingLevelExplicit: false, cliType: 'codex', contextMode: null,
      useOwnSession: null, allowWebAccess: false, noBranchExpected: f.mode === 'concept' },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
      blockerDescription: null, needsInput: null, dependencyBlocked: false,
      dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: f.statusMarkdown ?? 'Delivered.', originalBytes: 10, hash: 'status', cursor: null },
    prompt: { state: 'ready', text: '# Fixture\n\nHuman review.', originalBytes: 25,
      hash: 'prompt', cursor: null, continuationUrl: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  };
}

function resource(f: Fixture, name: string, data: unknown) {
  return { id: f.id, taskKey: key(f), projectId: 'fixture', attemptId: null,
    coreVersion: '3', resource: name, version: 'v1', computedAt: '2026-11-09T10:00:00Z',
    state: 'ready', data, reason: null };
}

interface Traffic { gitReads: number; moves: Record<string, unknown>[] }

async function mockApplication(page: Page, f: Fixture, move: (route: Route) => Promise<void>): Promise<Traffic> {
  const traffic: Traffic = { gitReads: 0, moves: [] };
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
    color: null, createdAt: '2026-11-09T08:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'fixture', displayName: 'fixture', shortCode: 'AGT',
      workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
      sortOrder: 0, storageLocation: WATCH_PATH, repositoryPath: WATCH_PATH,
      rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-11-09T08:00:00Z',
    }],
  }]));
  await page.route('**/api/workspaces/*/quota/admission**', route => json(route, {
    workspaceId: 'workspace',
    workspace: { projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  await page.route('**/api/v1/management/credential-inventory', route =>
    json(route, { generatedAt: '2026-11-09T10:00:00Z', items: [] }));
  await page.route(`**/api/tasks/${f.id}/artifacts**`, route => json(route, { files: [] }));
  await page.route(`**/api/tasks/${f.id}/screenshots**`, route => json(route, { screenshots: [] }));
  await page.route(`**/api/tasks/${f.id}/code-review/list**`, route => json(route, { entries: [] }));
  await page.route(`**/api/tasks/${f.id}/quality-studio**`, route => json(route, { state: 'not-registered',
    repositoryId: null, currentHead: null, rounds: [], landedSha: null, landedBaseSha: null,
    landedPatchEqualsReviewedPatch: null }));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/usage/cockpit**', route => json(route, {}, 503));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-11-09T10:00:00Z', snapshots: [] }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, [task(f)]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [task(f)],
    escalated: [], completed: [], archive: [],
  }));
  await page.route(`**/api/tasks/${f.id}/runs**`, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null,
    hasActiveRun: false, runs: [], promptEntries: [], refinements: [], runnerEvents: [],
  }));
  await page.route(`**/api/tasks/${f.id}/session-events**`, route => json(route, { events: [], sessionChain: [] }));
  await page.route(`**/api/tasks/${f.id}/plan**`, route => json(route, { hasPlan: false,
    source: null, snapshotCount: 0, activeItemId: null, items: [], unassignedSubActions: [] }));
  await page.route(`**/api/tasks/${f.id}/pipeline**`, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: f.pipelineExecution ?? null, executions: f.pipelineExecution ? [f.pipelineExecution] : [],
    config: {}, cost: null,
  }));
  await page.route(`**/api/tasks/${f.id}/provenance**`, route => json(route, {
    branch: null, base: null, transitions: [], merge: null, landedState: 'on-branch-only',
    ladder: null, commits: [],
  }));
  await page.route(new RegExp(`/api/tasks/(?:${f.ref}|${f.id})/core(\\?|$)`), route => json(route, core(f)));
  // Human Review also reads full detail for the authoritative integration verdict.
  await page.route(new RegExp(`/api/tasks/${f.id}(\\?|$)`), route => json(route, {
    info: { ...task(f), integration: f.gitIntegration },
  }));
  await page.route(`**/api/tasks/${f.id}/details/documents**`, route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource(f, 'documents', { name,
      markdown: name === 'prompt' ? '# Fixture\n\nHuman review.' : f.statusMarkdown ?? 'Delivered.', summaryState: null }));
  });
  await page.route(`**/api/tasks/${f.id}/details/usage**`, route => json(route, { error: 'usage offline' }, 503));
  await page.route(`**/api/tasks/${f.id}/details/review**`, route =>
    json(route, resource(f, 'review', { reviewProjection: f.reviewProjection ?? null, evidence: null })));
  await page.route(`**/api/tasks/${f.id}/details/overview**`, route => json(route, resource(f, 'overview', {
    decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null,
    relatedWikiPages: [], creationSource: 'operator', completionClaim: null,
    parkedBlocker: f.parkedBlocker ?? null, needsInput: null,
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] }, waitsOn: null,
    acceptanceAttemptGap: null,
  })));
  await page.route(`**/api/tasks/${f.id}/details/git**`, route => {
    traffic.gitReads++;
    const card = task(f);
    return json(route, resource(f, 'git', { mergeSignal: null, integration: f.gitIntegration,
      publishSignal: null, testEvidence: null, commit: card.commit, commits: card.commits, provenance: null }));
  });
  await page.route(new RegExp(`/tasks/${f.id}/move(\\?|$)`), route => {
    traffic.moves.push(route.request().postDataJSON() as Record<string, unknown>);
    return move(route);
  });
  return traffic;
}

/** Switch theme and finish the token transitions so a screenshot shows the settled state. */
async function theme(page: Page, value: 'dark' | 'light'): Promise<void> {
  await setTheme(page, value);
  await dismissDevErrorDialog(page);
  await page.evaluate(() => document.getAnimations()
    .filter(animation => animation instanceof CSSTransition)
    .forEach(animation => animation.finish()));
}

async function openCoreFirst(page: Page, f: Fixture): Promise<void> {
  await page.goto(`/#/tasks/${f.ref}`, { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 30_000 });
}

/** Toolbar crop: the triage cluster with the refusal and Accept, plus the open form. */
async function screenshot(page: Page, fileName: string, extra: string[] = []): Promise<void> {
  if (!RESULTS_DIR) return;
  mkdirSync(RESULTS_DIR, { recursive: true });
  const boxes = [];
  for (const testId of ['studio-pane-toggles', 'studio-triage-panel', ...extra]) {
    const box = await page.getByTestId(testId).boundingBox();
    if (box) boxes.push(box);
  }
  const pad = 6;
  const x = Math.max(0, Math.min(...boxes.map(b => b.x)) - pad);
  const y = Math.max(0, Math.min(...boxes.map(b => b.y)) - pad);
  const viewport = page.viewportSize()!;
  await page.screenshot({ path: path.join(RESULTS_DIR, fileName), clip: { x, y,
    width: Math.min(viewport.width - x, Math.max(...boxes.map(b => b.x + b.width)) - x + pad),
    height: Math.max(...boxes.map(b => b.y + b.height)) - y + pad } });
}

test.describe('Concept and held Accept in Human review (AGT-3704)', () => {
  test.use({ serviceWorkers: 'block' });

  test('a concept card shows an enabled Accept with the Git pane closed', async ({ page }) => {
    const traffic = await mockApplication(page, CONCEPT, route => json(route, {}));
    await openCoreFirst(page, CONCEPT);

    await expect(page.getByTestId('studio-pane-toggle-git')).toHaveAttribute('aria-pressed', 'false');
    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toHaveText(/Accept/, { timeout: 15_000 });
    await expect(accept).toBeEnabled({ timeout: 15_000 });
    await expect(page.getByTestId('acceptance-refusal')).toHaveCount(0);
    expect(traffic.gitReads).toBeGreaterThan(0);
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `concept-accept-enabled-${value}.png`);
    }

    await accept.click();
    await expect.poll(() => traffic.moves.length).toBe(1);
    expect(traffic.moves[0]).toMatchObject({ targetState: '6-completed' });
    expect(traffic.moves[0]['operatorOverride']).toBeUndefined();
  });

  test('a held Accept names the reason next to it and accepts with a written owner override', async ({ page }) => {
    const traffic = await mockApplication(page, PENDING, route => json(route, {}));
    await openCoreFirst(page, PENDING);

    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toBeDisabled({ timeout: 15_000 });
    const refusal = page.getByTestId('acceptance-refusal');
    await expect(refusal).toContainText('The delivery is not in develop yet.', { timeout: 15_000 });
    const action = page.getByTestId('acceptance-refusal-action');
    await expect(action).toHaveText('Accept with override…');
    // The refusal sits directly before the Accept button in the toolbar.
    const [refusalBox, acceptBox] = [await refusal.boundingBox(), await accept.boundingBox()];
    expect(refusalBox!.x + refusalBox!.width).toBeLessThanOrEqual(acceptBox!.x + 1);
    expect(Math.abs(refusalBox!.y + refusalBox!.height / 2 - (acceptBox!.y + acceptBox!.height / 2))).toBeLessThan(6);
    // The toolbar never clips its primary action (header guide, 1024 px rule).
    await expect(accept).toBeInViewport({ ratio: 1 });
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `held-accept-refusal-${value}.png`);
    }

    await action.click();
    const submit = page.getByTestId('acceptance-refusal-submit');
    await expect(submit).toBeDisabled();
    await expect(page.getByTestId('acceptance-refusal-form')).toContainText('at least 8 characters');
    await page.getByTestId('acceptance-refusal-override-reason').fill(' short ');
    await expect(submit).toBeDisabled();
    await page.getByTestId('acceptance-refusal-override-reason')
      .fill('Reviewed by the owner; the batch gate is down and the change is docs only.');
    await expect(submit).toBeEnabled();
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `held-accept-override-form-${value}.png`, ['acceptance-refusal-form']);
    }
    await submit.click();

    await expect.poll(() => traffic.moves.length).toBe(1);
    expect(traffic.moves[0]).toMatchObject({
      targetState: '6-completed', operatorOverride: true,
      reason: 'Reviewed by the owner; the batch gate is down and the change is docs only.',
    });
  });

  test('a server refusal of Accept is shown next to the button, never a silent refresh', async ({ page }) => {
    const refusal = 'Acceptance refused: the current delivery is not contained in origin/develop.';
    const traffic = await mockApplication(page, INTEGRATED, route =>
      json(route, { error: refusal, code: 'integration-dead-end' }, 409));
    await openCoreFirst(page, INTEGRATED);

    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toBeEnabled({ timeout: 15_000 });
    await expect(page.getByTestId('acceptance-refusal')).toHaveCount(0);
    await accept.click();

    await expect.poll(() => traffic.moves.length).toBe(1);
    await expect(page.getByTestId('acceptance-refusal')).toContainText(refusal, { timeout: 15_000 });
    await expect(page.getByTestId('acceptance-refusal')).toHaveAttribute('data-refusal-source', 'server');
    await expect(page.getByTestId('acceptance-refusal-action')).toHaveText('Accept with override…');
    await expect(page.getByTestId('notification-message').filter({ hasText: refusal })).toBeVisible();
    await expect(accept).toBeInViewport({ ratio: 1 });
    await theme(page, 'dark');
    await screenshot(page, 'server-refused-accept-dark.png');
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'server-refused-accept-full-dark.png') });
  });

  test('a completion-content-gap refusal offers the override next to Accept, and the override is accepted (AGT-3874)', async ({ page }) => {
    const refusal = 'Completion refused: requirement-fit verdict was concerns.';
    let refused = false;
    const traffic = await mockApplication(page, INTEGRATED, route => {
      if (refused) return json(route, {});
      refused = true;
      return json(route, { error: refusal, code: 'completion-content-gap' }, 409);
    });
    await openCoreFirst(page, INTEGRATED);

    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toBeEnabled({ timeout: 15_000 });
    await accept.click();
    await expect.poll(() => traffic.moves.length).toBe(1);

    const shown = page.getByTestId('acceptance-refusal');
    // AGT-3867 names the server refusal as an acceptance refusal.
    await expect(shown).toContainText(refusal.replace(/^Completion/, 'Acceptance'), { timeout: 15_000 });
    await expect(shown).toHaveAttribute('data-refusal-source', 'server');
    const action = page.getByTestId('acceptance-refusal-action');
    await expect(action).toHaveText('Accept with override…');
    await expect(accept).toBeInViewport({ ratio: 1 });
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `content-gap-refusal-${value}.png`);
    }

    // AGT-3873: the refusal toast docks below the toolbar, and the refused
    // Accept already opened the written-reason form with its field focused.
    const toast = page.getByTestId('notification-stack-below-toolbar')
      .getByTestId('notification-warning').filter({ hasText: 'Accept refused' });
    await expect(toast).toBeVisible();
    await expect(page.getByTestId('acceptance-refusal-form')).toBeVisible();
    await expect(page.getByTestId('acceptance-refusal-override-reason')).toBeFocused();
    await toast.getByTestId('notification-close').click();
    await expect(toast).toHaveCount(0);
    await page.getByTestId('acceptance-refusal-override-reason')
      .fill('Owner accepts: the first-paint target moves to its own card.');
    await page.getByTestId('acceptance-refusal-submit').click();
    await expect.poll(() => traffic.moves.length).toBe(2);
    expect(traffic.moves[1]).toMatchObject({
      targetState: '6-completed', operatorOverride: true,
      reason: 'Owner accepts: the first-paint target moves to its own card.',
    });
  });

  test('a concerns verdict shows the hold next to Accept after a reload, without a prior click (AGT-3874)', async ({ page }) => {
    const traffic = await mockApplication(page, CONCERNS, route => json(route, {}));
    await openCoreFirst(page, CONCERNS);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await dismissDevErrorDialog(page);

    const accept = page.getByTestId('studio-triage-action-mark-done');
    const shown = page.getByTestId('acceptance-refusal');
    await expect(shown).toContainText(CONCERNS_SUMMARY, { timeout: 30_000 });
    await expect(shown).toHaveAttribute('data-refusal-source', 'hold');
    await expect(page.getByTestId('acceptance-refusal-action')).toHaveText('Accept with override…');
    await expect(accept).toBeEnabled();
    await expect(accept).toBeInViewport({ ratio: 1 });
    expect(traffic.moves).toEqual([]);
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `content-verdict-hold-${value}.png`);
    }
  });

  test('picking an empty lane in the lane list says it pages and how to move the task', async ({ page }) => {
    const traffic = await mockApplication(page, CONCEPT, route => json(route, {}));
    await openCoreFirst(page, CONCEPT);
    await expect(page.getByTestId('studio-triage-action-mark-done')).toBeEnabled({ timeout: 15_000 });

    await page.getByTestId('studio-lane-select').selectOption('6-completed');
    await expect(page.getByTestId('notification-message')
      .filter({ hasText: 'This list pages through lanes; it does not move this task.' })).toBeVisible();
    expect(traffic.moves).toEqual([]);
  });

  test('390 px: the held Accept keeps its reason icon and the one action reachable', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApplication(page, PENDING, route => json(route, {}));
    await openCoreFirst(page, PENDING);
    const action = page.getByTestId('acceptance-refusal-action');
    await expect(action).toBeVisible({ timeout: 15_000 });
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      if (RESULTS_DIR) {
        mkdirSync(RESULTS_DIR, { recursive: true });
        await page.screenshot({ path: path.join(RESULTS_DIR, `held-accept-390-${value}.png`) });
      }
    }
    await action.click();
    await expect(page.getByTestId('acceptance-refusal-form')).toBeInViewport();
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'held-accept-390-form-light.png') });
  });
});

/**
 * AGT-3873 (owner report, 16 November 2026): Accept on AGT-3534 was refused
 * for a `concerns` verdict. The toast had no control and covered the toolbar
 * action that resolved it, the banner named the override without offering it
 * or the concern, the park read "No reason was recorded", and the Result head
 * called the merged, reviewed delivery a "Pipeline failure".
 */
const CONTENT_GAP = 'Completion refused: requirement-fit verdict was concerns.';
const OWNER_REASON = 'Owner accepts: the first-paint target moves to its own card.';

const SCAFFOLD_STATUS = `<!-- agent-studio:result-scaffold -->
# Status

- Result: Success
- Case: generic
- Provenance: Synthesized by Agent Studio because no generated status.md was available.

## What Was Done
- The task reached \`5-human-review\`.

## Open Items
- None recorded in this synthesized scaffold.
`;

/** AGT-3534 as the owner saw it: merged, reviewed, parked in Human review with no prose reason. */
const AGT_3534: Fixture = {
  ...CONCERNS,
  parkedBlocker: {
    blockerType: 'operator-decision', conditionKind: 'manual',
    conditionDescription: 'Only a person can clear this park; no automatic precondition is recorded.',
    parkedAt: '2026-11-10T09:00:00Z', reason: 'external-completion: codex-cloud', recallStatus: 'blocked',
    lastEvaluatedAt: '2026-11-16T09:00:00Z', detail: 'Only a person can clear this park.',
    lane: '5-human-review', decision: null, needsInputFile: null, evaluationAgeSeconds: 600,
    evaluationStale: false, requiresDecisionCard: false, cause: 'external-completion', causeDetail: 'codex-cloud',
  },
  statusMarkdown: SCAFFOLD_STATUS,
  pipelineExecution: {
    pipelineId: 'standard', pipelineVersion: 1, jobId: 'agt-3534-concerns', project: 'fixture',
    startedAt: '2026-11-09T08:00:00Z', completedAt: '2026-11-09T09:35:00Z',
    steps: [{ stepId: 'core-agent-run', status: 'completed' }, { stepId: 'post-status-report', status: 'failed' }],
  },
};

const VARIANTS = [
  { width: 1280, theme: 'light' }, { width: 1280, theme: 'dark' },
  { width: 390, theme: 'light' }, { width: 390, theme: 'dark' },
] as const;

/** Refuses the first Accept with the concerns verdict and admits the override. */
function refuseOnce(): (route: Route) => Promise<void> {
  let refused = false;
  return route => {
    if (refused) return json(route, {});
    refused = true;
    return json(route, { error: CONTENT_GAP, code: 'completion-content-gap' }, 409);
  };
}

async function fullShot(page: Page, name: string): Promise<void> {
  if (!RESULTS_DIR) return;
  // Keep the Accept hover tooltip out of the evidence.
  await page.mouse.move(1, 1);
  await page.getByTestId('cac-tooltip').first().waitFor({ state: 'hidden', timeout: 3_000 }).catch(() => undefined);
  const dir = path.join(RESULTS_DIR, 'agt3873');
  mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, name) });
}

/**
 * The mocked install has no first-run proof, so the shell may open its
 * checklist first. A phone shows only the active tab; the Board tab then
 * sits in the tab overflow menu.
 */
async function showBoardTab(page: Page): Promise<void> {
  const tab = page.getByRole('tab', { name: /Board/ }).first();
  const overflow = page.getByTestId('studio-tab-overflow');
  await expect(tab.or(overflow).first()).toBeVisible({ timeout: 30_000 });
  if (await tab.isVisible()) {
    await tab.click();
    return;
  }
  await overflow.click();
  await page.locator('[data-testid^="studio-tab-overflow-menu-item-"]').filter({ hasText: 'Board' }).first().click();
  await expect(tab).toBeVisible();
}

type Box = { x: number; y: number; width: number; height: number };
const overlaps = (a: Box, b: Box) =>
  a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;

/** The refusal toast sits below the tab toolbar and never over the open override form. */
async function expectToastClear(page: Page, toast: ReturnType<Page['getByTestId']>): Promise<void> {
  await expect.poll(async () => {
    const [bar, box, formBox] = await Promise.all([page.getByTestId('studio-tabbar').boundingBox(),
      toast.boundingBox(), page.getByTestId('acceptance-refusal-form').boundingBox()]);
    return !!bar && !!box && !!formBox && box.y >= bar.y + bar.height && !overlaps(box, formBox);
  }).toBe(true);
}

test.describe('Refused Accept offers its next step where it is named (AGT-3873)', () => {
  test.use({ serviceWorkers: 'block' });

  for (const { width, theme: value } of VARIANTS) {
    test(`toolbar Accept: the form opens in place, the toast and banner carry the actions (${width} ${value})`, async ({ page }) => {
      await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
      const traffic = await mockApplication(page, AGT_3534, refuseOnce());
      await openCoreFirst(page, AGT_3534);
      await theme(page, value);

      const accept = page.getByTestId(width < 600 ? 'studio-mobile-triage-action-mark-done' : 'studio-triage-action-mark-done');
      await expect(accept).toBeEnabled({ timeout: 15_000 });
      await accept.click();
      await expect.poll(() => traffic.moves.length).toBe(1);

      // 1. The refused Accept opens the written-reason form in place, focused.
      const form = page.getByTestId('acceptance-refusal-form');
      await expect(form).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId('acceptance-refusal-override-reason')).toBeFocused();
      await expect(form).toContainText(`The review's concern: ${CONCERNS_SUMMARY}`);

      // 2. The toast carries the actions and never covers the toolbar.
      const toast = page.getByTestId('notification-stack-below-toolbar')
        .getByTestId('notification-warning').filter({ hasText: 'Accept refused' });
      await expect(toast).toBeVisible();
      await expect(toast).toContainText(`The review's concern: ${CONCERNS_SUMMARY}`);
      await expect(toast.getByTestId('acceptance-refusal-toast-override')).toHaveText('Accept with a written reason');
      await expect(toast.getByTestId('acceptance-refusal-toast-review')).toHaveText('Review again');
      await expectToastClear(page, toast);
      const toastBox = (await toast.boundingBox())!;
      for (const testId of ['studio-triage-panel', 'acceptance-refusal-action']) {
        const control = await page.getByTestId(testId).boundingBox();
        expect(control && overlaps(control, toastBox), `${testId} stays uncovered`).toBeFalsy();
      }
      if (width < 600) expect(toastBox.width).toBeGreaterThan(width - 40);

      // 3. The banner names the concern and offers both steps.
      const banner = page.getByTestId('task-move-refusal');
      await expect(banner).toContainText('Acceptance refused: requirement-fit verdict was concerns.');
      await expect(banner.getByTestId('task-move-refusal-concern')).toContainText(CONCERNS_SUMMARY);
      await expect(banner.getByTestId('task-move-refusal-override')).toBeVisible();
      await expect(banner.getByTestId('task-move-refusal-review')).toBeVisible();
      await fullShot(page, `agt3873-refused-accept-${width}-${value}.png`);

      // The toast and the banner open the same form, focused.
      await page.getByTestId('acceptance-refusal-cancel').click();
      await expect(form).toHaveCount(0);
      await toast.getByTestId('acceptance-refusal-toast-override').click();
      await expect(form).toBeVisible();
      await expect(page.getByTestId('acceptance-refusal-override-reason')).toBeFocused();
      await page.getByTestId('acceptance-refusal-cancel').click();
      await banner.getByTestId('task-move-refusal-override').click();
      await expect(page.getByTestId('acceptance-refusal-override-reason')).toBeFocused();
      await fullShot(page, `agt3873-banner-override-${width}-${value}.png`);

      await page.getByTestId('acceptance-refusal-override-reason').fill(OWNER_REASON);
      await page.getByTestId('acceptance-refusal-submit').click();
      await expect.poll(() => traffic.moves.length).toBe(2);
      expect(traffic.moves[1]).toMatchObject({ targetState: '6-completed', operatorOverride: true, reason: OWNER_REASON });
    });

    test(`board Accept: the refused card opens with the override focused (${width} ${value})`, async ({ page }) => {
      await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
      const traffic = await mockApplication(page, AGT_3534, refuseOnce());
      await page.goto('/#/board', { waitUntil: 'domcontentloaded', timeout: 60_000 });
      await dismissDevErrorDialog(page);
      await theme(page, value);
      await showBoardTab(page);
      // A phone shows one lane at a time.
      if (width < 600) await page.getByLabel('Board lane').selectOption('5-human-review');

      const card = page.getByTestId('task-card').filter({ hasText: AGT_3534.ref });
      await expect(card).toBeVisible({ timeout: 30_000 });
      await card.click({ button: 'right' });
      await page.getByTestId('card-ctx-item-move-card:6-completed').click();
      await expect.poll(() => traffic.moves.length).toBe(1);

      await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 30_000 });
      await expect(page.getByTestId('acceptance-refusal-form')).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId('acceptance-refusal-override-reason')).toBeFocused();
      await expect(page.getByTestId('error-dialog')).toHaveCount(0);
      const toast = page.getByTestId('notification-stack-below-toolbar')
        .getByTestId('notification-warning').filter({ hasText: 'Accept refused' });
      await expect(toast).toBeVisible();
      await expectToastClear(page, toast);
      await fullShot(page, `agt3873-board-accept-${width}-${value}.png`);
    });

    test(`park and Result read the owner hold and the merged delivery (${width} ${value})`, async ({ page }) => {
      await page.setViewportSize({ width, height: width < 600 ? 844 : 900 });
      await mockApplication(page, AGT_3534, route => json(route, {}));
      await openCoreFirst(page, AGT_3534);
      await theme(page, value);

      // 4. A park without a recorded reason says what it waits for.
      const park = page.getByTestId('parked-blocker');
      await expect(park).toBeVisible({ timeout: 15_000 });
      await expect(park).toContainText("Held for the owner's decision in Human review.");
      await expect(park).not.toContainText('No reason was recorded');
      await park.scrollIntoViewIfNeeded();
      await fullShot(page, `agt3873-park-${width}-${value}.png`);

      // 5. The Result head states the lane and the merged delivery, never "Pipeline failure".
      if (width < 600) await page.getByTestId('detail-mobile-pane-result').click();
      const result = page.getByTestId('result-view');
      await expect(result.getByTestId('result-case-badge')).toContainText('Human review', { timeout: 15_000 });
      await expect(result.getByTestId('result-case-badge')).not.toContainText('Pipeline failure');
      await expect(result.getByTestId('result-scaffold-delivery'))
        .toHaveText('The delivery is merged into develop and its review passed.');
      await expect(result.getByTestId('result-scaffold-notice')).toContainText('generated automatically from task.json');
      await result.getByTestId('result-scaffold-notice').scrollIntoViewIfNeeded();
      await fullShot(page, `agt3873-result-${width}-${value}.png`);
    });
  }

  test('Review again in the banner asks for a content review of the current brief', async ({ page }) => {
    const reviews: string[] = [];
    await mockApplication(page, AGT_3534, refuseOnce());
    await page.route(/\/content-review(\?|$)/, route => {
      reviews.push(route.request().url());
      return json(route, { reviewAttemptId: 'review-2', briefVersion: 'b2' });
    });
    await openCoreFirst(page, AGT_3534);
    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toBeEnabled({ timeout: 15_000 });
    await accept.click();
    await page.getByTestId('acceptance-refusal-cancel').click();
    await page.getByTestId('task-move-refusal-review').click();
    await expect.poll(() => reviews.length).toBe(1);
    await expect(page.getByTestId('notification-message')
      .filter({ hasText: 'Content review requested for the current brief.' })).toBeVisible();
  });
});
