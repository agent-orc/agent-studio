import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * AGT-2956 board record reuse (task-switch Dossier card 5).
 *
 * Fully mocked API against the dev frontend: no backend, Git or filesystem.
 * Asserts that task selection never re-reads the grouped board, that each
 * core is requested at most once at a time, that the next two pager cores are
 * warmed after paint, and that the board returns with its filter and lane
 * scroll intact. The measurement case records the in-app
 * `task-core-select-to-ready` span for resident and uncached cores. It is a
 * mocked-latency observation; `TASK_SWITCH_BUDGET=1` also enforces the
 * uncached <=100 ms p95 (the card 6 workstation gate opts in there).
 * `PW_BASE_URL` may point at a served production bundle.
 */

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const REUSE = { id: 'PROJ-REUSE', name: 'Reuse', short: 'REU', path: 'C:/fixtures/Reuse' };
const TWIN = { id: 'PROJ-TWIN', name: 'Twin', short: 'TWN', path: 'C:/fixtures/Twin' };
const LANE_SIZE = 100;
const CORE_DELAY_MS = 15;
const DETAIL_DELAY_MS = 60;

type Project = typeof REUSE;

function slug(i: number): string {
  return `task-${String(i).padStart(2, '0')}`;
}

function job(project: Project, i: number) {
  const id = slug(i);
  return {
    id, taskKey: `${project.path}::${id}`, key: `${project.short}-${100 + i}`, displayKey: `${project.short}-${100 + i}`,
    title: `${project.name} ${id}`, state: '5-human-review', order: i, projectName: project.name,
    watchPath: project.path, folderPath: `${project.path}/tasks/${id}`, createdAt: '2026-09-28T08:00:00Z',
    lastActivity: '2026-09-28T09:00:00Z', agent: 'claude', cliType: 'claude', model: 'sonnet', modelExplicit: true,
    sessionName: null, useOwnSession: null, lastUsage: null, execution: null, commit: null, commits: [],
    ownerClientId: 'local-default', tags: [], pendingIntent: null, autoLoop: null, summaryState: null,
  };
}

function core(project: Project, id: string) {
  const info = job(project, Number(id.slice(5)));
  return {
    state: 'ready', projectId: project.id, projectName: project.name, id, taskKey: info.taskKey, key: info.key,
    title: info.title, kind: 'task', taskType: 'chore', lane: info.state, enteredLaneAt: '2026-09-28T09:00:00Z',
    order: info.order, mode: 'coding', released: false, pendingIntent: false,
    pins: { model: 'sonnet', modelExplicit: true, thinkingLevelExplicit: false, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
    runtime: { location: 'none', leaseState: 'none' }, runtimeVersion: 'r0',
    statusSummary: { state: 'ready', text: `Status of ${id}`, originalBytes: 12 },
    prompt: { state: 'ready', text: `Prompt of ${id}`, originalBytes: 12 },
    timeline: { state: 'ready', events: [{ sequence: 1, ts: '2026-09-28T09:00:00Z', kind: 'created', actor: 'operator', summary: 'Created' }], originalBytes: 60 },
    coreVersion: 1,
  };
}

function detail(project: Project, id: string) {
  return {
    info: job(project, Number(id.slice(5))), promptMarkdown: `Prompt of ${id}`, statusMarkdown: '', log: [],
    promptHistory: [], titleHistory: [], contextUsage: null, reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

interface Traffic {
  grouped: { at: number }[];
  core: { key: string; at: number }[];
  maxConcurrentPerCore: number;
}

async function installRoutes(page: Page): Promise<Traffic> {
  const traffic: Traffic = { grouped: [], core: [], maxConcurrentPerCore: 0 };
  const inFlight = new Map<string, number>();
  const lanes = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], escalated: [], completed: [], archive: [],
    humanReview: [...Array.from({ length: LANE_SIZE }, (_, i) => job(REUSE, i)), job(TWIN, 0)],
  };
  const projectOf = (handle: string | null) => (handle === TWIN.id ? TWIN : REUSE);
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/tasks/grouped**', route => {
    traffic.grouped.push({ at: Date.now() });
    return route.fulfill(json({ ...lanes, gitStateAt: null, stale: false }));
  });
  await page.route('**/api/tasks/archive**', route => route.fulfill(json({ items: [], total: 0, offset: 0, limit: 50 })));
  await page.route('**/api/workspaces**', route => route.fulfill(json([{
    id: 'WS-REUSE', displayName: 'Fixtures', sortOrder: 0, isDefault: true, color: null, createdAt: '2026-09-28T08:00:00Z',
    projects: [REUSE, TWIN].map((p, i) => ({
      id: p.id, displayName: p.name, shortCode: p.short, workspaceId: 'WS-REUSE', color: null, cliDefault: null,
      modelDefault: null, sortOrder: i, storageLocation: p.path, archived: false, createdAt: '2026-09-28T08:00:00Z', urls: [],
    })),
  }])));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([REUSE, TWIN].map(p => ({
    name: p.name, path: p.path, rootPath: p.path, repositoryPath: p.path,
  })))));
  await page.route('**/api/environment**', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ at: '2026-09-28T08:00:00Z', sessions: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ at: '2026-09-28T08:00:00Z', ttlSeconds: 600, snapshots: [] })));
  await page.route(/\/workbenches(\?|$)/, route => route.fulfill(json({ items: [] })));
  await page.route(/\/api\/runner\/status(\?|$)/, route => route.fulfill(json({ projects: {} })));
  await page.route(/\/api\/tasks\/[^/?]+\/runs(\?|$)/, route => route.fulfill(json({ runs: [] })));
  await page.route(/\/api\/tasks\/[^/?]+\/session-events(\?|$)/, route => route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(\?|$)/, route => route.fulfill(json({
    pipeline: { id: 'standard-task-pipeline', displayName: 'Standard', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false }, config: {},
  })));
  await page.route(/\/api\/tasks\/task-\d+\/core(\?|$)/, async route => {
    const url = new URL(route.request().url());
    const id = url.pathname.split('/')[3];
    const project = projectOf(url.searchParams.get('project'));
    const key = `${project.id}/${id}`;
    traffic.core.push({ key, at: Date.now() });
    const concurrent = (inFlight.get(key) ?? 0) + 1;
    inFlight.set(key, concurrent);
    traffic.maxConcurrentPerCore = Math.max(traffic.maxConcurrentPerCore, concurrent);
    await new Promise(resolve => setTimeout(resolve, CORE_DELAY_MS));
    inFlight.set(key, concurrent - 1);
    await route.fulfill({ ...json(core(project, id)), headers: { ETag: `"core-${key}"` } }).catch(() => undefined);
  });
  await page.route(/\/api\/tasks\/task-\d+(\?|$)/, async route => {
    const url = new URL(route.request().url());
    const id = url.pathname.split('/')[3];
    await new Promise(resolve => setTimeout(resolve, DETAIL_DELAY_MS));
    await route.fulfill(json(detail(projectOf(url.searchParams.get('project')), id))).catch(() => undefined);
  });
  return traffic;
}

async function openBoard(page: Page, hash: string): Promise<Traffic> {
  await page.addInitScript(() => {
    performance.setResourceTimingBufferSize(20_000);
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  const traffic = await installRoutes(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?perf=1${hash}`);
  await expect(page.getByTestId('task-card').first()).toBeVisible({ timeout: 30_000 });
  // Let boot-time reads settle before the navigation phase starts counting.
  await page.waitForTimeout(500);
  return traffic;
}

/** The Studio board scrolls a lane group vertically, not the lane body. */
function laneScroller(page: Page) {
  return page.locator('section[data-states*="5-human-review"]').getByTestId(/^lane-group-lanes-/);
}

function card(page: Page, project: Project, i: number) {
  return page.getByTestId('task-card').filter({ hasText: `${project.name} ${slug(i)}` }).first();
}

async function backToBoard(page: Page): Promise<void> {
  await page.getByTestId('studio-tab-board:__all__').click();
  await expect(page.getByTestId('studio-board')).toBeVisible();
}

async function waitForTask(page: Page, title: string): Promise<void> {
  await expect(page.getByTestId('studio-task')).toContainText(title, { timeout: 10_000 });
}

async function coreSpans(page: Page): Promise<number[]> {
  return page.evaluate(() =>
    performance.getEntriesByName('task-core-select-to-ready', 'measure').map(entry => entry.duration));
}

/** Split one sampled uncached span into dispatch delay, network and main-thread tail. */
async function uncachedBreakdown(page: Page, id: string, spanIndex: number): Promise<{ dispatch: number; network: number; tail: number }> {
  return page.evaluate(({ taskId, index }) => {
    const span = performance.getEntriesByName('task-core-select-to-ready', 'measure')[index];
    const resource = (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
      .filter(entry => entry.name.includes(`/api/tasks/${taskId}/core`)).at(-1)!;
    return {
      dispatch: resource.startTime - span.startTime,
      network: resource.responseEnd - resource.startTime,
      tail: span.startTime + span.duration - resource.responseEnd,
    };
  }, { taskId: id, index: spanIndex });
}

function percentile(values: number[], p: number): number {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)];
}

test.describe('Task core board reuse (AGT-2956)', () => {
  // A production bundle registers ngsw; its fetches would bypass page.route.
  test.use({ serviceWorkers: 'block' });

  test('board -> A -> B -> board keeps filter and scroll and never refetches grouped', async ({ page }) => {
    const traffic = await openBoard(page, '#/board&filters=projects%3AReuse');
    const lane = laneScroller(page);
    await expect(card(page, TWIN, 0)).toHaveCount(0);
    await lane.evaluate(el => { el.scrollTop = 900; el.dispatchEvent(new Event('scroll')); });
    const scrolled = await lane.evaluate(el => el.scrollTop);
    expect(scrolled).toBeGreaterThan(0);
    const groupedBefore = traffic.grouped.length;

    // Task 04 is on screen at this offset, so the click does not scroll the lane.
    await expect(card(page, REUSE, 4)).toBeInViewport();
    await card(page, REUSE, 4).click();
    await waitForTask(page, 'Reuse task-04');
    await page.getByTestId('studio-task-next').click();
    await waitForTask(page, 'Reuse task-05');
    await page.getByTestId('studio-task-prev').click();
    await waitForTask(page, 'Reuse task-04');
    await backToBoard(page);

    await expect(card(page, REUSE, 20)).toBeAttached();
    await expect(card(page, TWIN, 0)).toHaveCount(0);
    expect(decodeURIComponent(new URL(page.url()).hash)).toContain('filters=projects:Reuse');
    await expect.poll(() => lane.evaluate(el => el.scrollTop)).toBe(scrolled);

    expect(traffic.grouped.length - groupedBefore).toBe(0);
    expect(traffic.maxConcurrentPerCore).toBe(1);
    const keys = traffic.core.map(c => c.key);
    // task-04, its lookahead 05/06, then 07 for the pager window at 05.
    expect(new Set(keys).size).toBe(keys.length);
    expect(keys).toContain('PROJ-REUSE/task-04');
    expect(keys).toContain('PROJ-REUSE/task-06');
    if (RESULTS_DIR) {
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      for (const theme of ['light', 'dark'] as const) {
        await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
        await page.screenshot({ path: path.join(RESULTS_DIR, `board-after-return-${theme}.png`) });
      }
    }
  });

  test('identical slugs in two projects read two cores', async ({ page }) => {
    const traffic = await openBoard(page, '#/board');
    await card(page, REUSE, 0).click();
    await waitForTask(page, 'Reuse task-00');
    await backToBoard(page);
    await card(page, TWIN, 0).click();
    await waitForTask(page, 'Twin task-00');
    const keys = traffic.core.map(c => c.key);
    expect(keys).toContain('PROJ-REUSE/task-00');
    expect(keys).toContain('PROJ-TWIN/task-00');
    expect(traffic.maxConcurrentPerCore).toBe(1);
  });

  test('measures resident and uncached core readiness without grouped reads', async ({ page }) => {
    test.setTimeout(240_000);
    const traffic = await openBoard(page, '#/board');
    const lane = laneScroller(page);
    const phaseStart = Date.now();
    const groupedBefore = traffic.grouped.length;
    const samples = { uncached: [] as number[], resident: [] as number[] };
    const breakdown = { dispatch: [] as number[], network: [] as number[], tail: [] as number[] };
    const uncachedIds: number[] = [];

    // Uncached cohort: board click to a task outside every earlier lookahead
    // window (stride 3), so the core read is always a cold request.
    for (let i = 0; i < 30 * 3; i += 3) uncachedIds.push(i);
    for (const i of uncachedIds) {
      const before = (await coreSpans(page)).length;
      await card(page, REUSE, i).scrollIntoViewIfNeeded();
      await card(page, REUSE, i).click();
      await waitForTask(page, `Reuse ${slug(i)}`);
      await expect.poll(async () => (await coreSpans(page)).length).toBeGreaterThan(before);
      samples.uncached.push((await coreSpans(page)).slice(before)[0]);
      const parts = await uncachedBreakdown(page, slug(i), before);
      breakdown.dispatch.push(parts.dispatch);
      breakdown.network.push(parts.network);
      breakdown.tail.push(parts.tail);
      await backToBoard(page);
    }

    // Resident cohort: page back and forth over visited cores.
    await lane.evaluate(el => { el.scrollTop = 0; });
    await card(page, REUSE, 0).click();
    await waitForTask(page, 'Reuse task-00');
    // Warm-up pass (not sampled): visit tasks 00..03 so every step below is resident.
    for (const direction of ['next', 'next', 'next', 'prev', 'prev', 'prev']) {
      await page.getByTestId(`studio-task-${direction}`).click();
      await page.waitForTimeout(150);
    }
    for (let step = 0; step < 30; step++) {
      const direction = step % 6 < 3 ? 'next' : 'prev';
      const before = (await coreSpans(page)).length;
      await page.getByTestId(`studio-task-${direction}`).click();
      await expect.poll(async () => (await coreSpans(page)).length).toBeGreaterThan(before);
      samples.resident.push((await coreSpans(page)).slice(before)[0]);
    }

    // Grouped reads in the measured phase are the 30 s heartbeat only, never per selection.
    const heartbeatWindows = Math.ceil((Date.now() - phaseStart) / 30_000);
    const phaseGrouped = traffic.grouped.length - groupedBefore;
    expect(phaseGrouped).toBeLessThanOrEqual(heartbeatWindows);
    expect(traffic.maxConcurrentPerCore).toBe(1);

    const summary = {
      source: 'e2e/task-detail/task-core-board-reuse.spec.ts',
      environment: 'Linux runner, Playwright Chromium, dev frontend, fully mocked API',
      span: 'task-core-select-to-ready (selection to core available in TaskSelectionService; not a paint mark)',
      mockedCoreLatencyMs: CORE_DELAY_MS,
      groupedRequestsDuringSelections: phaseGrouped,
      heartbeatWindows,
      selections: samples.uncached.length + samples.resident.length,
      coreRequests: traffic.core.length,
      maxConcurrentRequestsPerCore: traffic.maxConcurrentPerCore,
      uncachedBreakdownMs: Object.fromEntries(Object.entries(breakdown).map(([name, values]) => [name, {
        p50: Number(percentile(values, 50).toFixed(2)),
        p95: Number(percentile(values, 95).toFixed(2)),
      }])),
      note: 'Stage percentiles overlap and do not add to the span p95.',
      cohorts: Object.fromEntries(Object.entries(samples).map(([name, values]) => [name, {
        n: values.length,
        p50: Number(percentile(values, 50).toFixed(2)),
        p95: Number(percentile(values, 95).toFixed(2)),
      }])),
    };
    console.info(JSON.stringify(summary));
    if (RESULTS_DIR) {
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      fs.writeFileSync(path.join(RESULTS_DIR, 'task-core-reuse-measurement.json'), JSON.stringify(summary, null, 2));
    }
    test.info().annotations.push({ type: 'task-core-p95-ms', description: JSON.stringify(summary.cohorts) });
    expect(percentile(samples.resident, 95)).toBeLessThanOrEqual(50);
    // The uncached span is network plus main-thread time and swings with host
    // load. It is always recorded; the enforced <=100 ms gate is Dossier card 6
    // on the workstation profile, which opts in here.
    if (process.env['TASK_SWITCH_BUDGET'] === '1') {
      expect(percentile(samples.uncached, 95)).toBeLessThanOrEqual(100);
    }
  });
});


