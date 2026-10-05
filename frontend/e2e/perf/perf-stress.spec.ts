import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { startLongTaskRecorder } from '../helpers/timing';

/**
 * Cycle 7 stress measurement. Decouples the frontend's render cost from
 * the backend by intercepting the polled HTTP endpoints with `page.route`
 * and serving N synthetic jobs - 10 / 100 / 200 / 500 - so we can find
 * the scaling cliffs without polluting the real workspace.
 *
 * Three layers of metric per N:
 *  1. Render-arrival - time from page load to the first kanban card
 *     becoming visible (DOM + change detection settled).
 *  2. Steady-state cost - DOM node count, JS heap (via CDP), longtask
 *     budget over a 5 s idle window. These show what the browser pays
 *     just to KEEP the board on screen.
 *  3. Interaction smoothness - rAF-based FPS during a programmatic
 *     scroll, click-to-visible for opening a card detail.
 *
 * Gated by RUN_PERF_BASELINE=1; scenario tag picks up from
 * PERF_SCENARIO env var (default `stress`). Each test appends one
 * JSONL row per measurement to logs/perf/stress-<scenario>-<tag>.jsonl
 * for the report generator to consume.
 */

const SCENARIO = process.env.PERF_SCENARIO || 'stress';
const REPO_ROOT = path.resolve(process.cwd(), '..');
const PERF_DIR = path.join(REPO_ROOT, 'logs', 'perf');
const RUN_TAG = process.env.PERF_RUN_TAG || new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
const JSONL_PATH = path.join(PERF_DIR, `stress-${SCENARIO}-${RUN_TAG}.jsonl`);

interface Sample {
  scenario: string;
  N: number;
  metric: string;
  unit: 'ms' | 'count' | 'bytes' | 'fps';
  value: number;
  notes?: string;
}

function record(N: number, metric: string, unit: Sample['unit'], value: number, notes?: string) {
  fs.mkdirSync(PERF_DIR, { recursive: true });
  const row: Sample = { scenario: SCENARIO, N, metric, unit, value, notes };
  fs.appendFileSync(JSONL_PATH, JSON.stringify(row) + '\n');
}

// Use a real project name so the frontend's active-project filter doesn't
// hide synthetic cards. The fixture data is still synthetic (controlled
// by makeJob); we just borrow the project label/path the frontend has
// already accepted from /api/watch-paths.
const PROJECT_NAME = 'Agent Software Studio';
const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

/**
 * Build a single synthetic JobInfo. Fields cover what JobCard actually
 * reads (id, title, state, agent, cliType, lastActivity, createdAt,
 * commit, taskType); optional badges (tokenSummary, autoLoop, summary,
 * pendingIntent) are left null so the card renders its compact state.
 * Sprinkles a few jobs with a fake commit so the git chip exercises its
 * render path, and a few tags so the tag chips run.
 */
function makeJob(i: number, lane: string) {
  const id = `stress-job-${i.toString().padStart(5, '0')}`;
  const hasCommit = i % 7 === 0;
  const hasTokens = i % 4 === 0;
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `Stress Test Job ${i} - lorem ipsum dolor sit amet`,
    state: lane,
    order: i,
    agent: 'claude',
    createdAt: new Date(Date.now() - i * 60_000).toISOString(),
    watchPath: WATCH_PATH,
    projectName: PROJECT_NAME,
    folderPath: `${WATCH_PATH}/${lane}/${id}`,
    lastActivity: new Date(Date.now() - i * 30_000).toISOString(),
    sessionName: null,
    model: 'claude-sonnet-4-6',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: hasCommit ? {
      sha: `${i.toString(16).padStart(40, '0')}`,
      shortSha: i.toString(16).padStart(7, '0').slice(0, 7),
      message: `feat(stress): commit for job ${i}`,
      filesChanged: 1 + (i % 5),
      at: new Date(Date.now() - i * 30_000).toISOString(),
      files: [],
    } : null,
    commits: hasCommit ? [{
      sha: `${i.toString(16).padStart(40, '0')}`,
      shortSha: i.toString(16).padStart(7, '0').slice(0, 7),
      message: `feat(stress): commit for job ${i}`,
      filesChanged: 1 + (i % 5),
      at: new Date(Date.now() - i * 30_000).toISOString(),
      files: [],
    }] : [],
    commitCount: hasCommit ? 1 : 0,
    sessionChain: [],
    fixture: false,
    tokenSummary: hasTokens ? {
      totalTokens: 1000 * i,
      inputTokens: 500 * i,
      outputTokens: 500 * i,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      callCount: 1 + (i % 3),
      runCount: 1,
    } : null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    orchestratorVerdict: null,
    ownerClientId: 'local-default',
    phase: null,
    taskType: i % 3 === 0 ? 'bug' : i % 3 === 1 ? 'feature' : 'chore',
    tags: i % 5 === 0 ? ['stress', 'fixture'] : [],
  };
}

function makeFixture(N: number) {
  // Stress distribution biased toward lanes that use the full job-card
  // render (which is what the user wants to scale). Archive uses a
  // different lightweight "archive-row" template, so testing scale there
  // measures the wrong thing; the perf concern is the busy lanes
  // (Completed, Human Review, Auto Review, Ready) that hold rich
  // job-cards with all the badges.
  const jobs: ReturnType<typeof makeJob>[] = [];
  const lanes: { lane: string; share: number }[] = [
    { lane: '6-completed',     share: 0.55 },
    { lane: '5-human-review',  share: 0.20 },
    { lane: '4-auto-review',   share: 0.15 },
    { lane: '2-ready',         share: 0.07 },
    { lane: '3-progress',      share: 0.02 },
    { lane: '7-archive',       share: 0.01 }, // a few for variety
  ];
  let i = 0;
  for (const { lane, share } of lanes) {
    const count = Math.max(0, Math.round(N * share));
    for (let k = 0; k < count && i < N; k++, i++) jobs.push(makeJob(i, lane));
  }
  while (i < N) { jobs.push(makeJob(i, '6-completed')); i++; }

  const grouped = {
    backlog: jobs.filter(j => j.state === '0-backlog'),
    preparation: jobs.filter(j => j.state === '1-preparation'),
    orchestratorPrep: [],
    ready: jobs.filter(j => j.state === '2-ready'),
    progress: jobs.filter(j => j.state === '3-progress'),
    failedPickup: [],
    autoReview: jobs.filter(j => j.state === '4-auto-review'),
    humanReview: jobs.filter(j => j.state === '5-human-review'),
    review: jobs.filter(j => j.state === '4-auto-review'), // legacy alias
    completed: jobs.filter(j => j.state === '6-completed'),
    archive: jobs.filter(j => j.state === '7-archive'),
  };
  return { jobs, grouped };
}

async function installRoutes(page: import('@playwright/test').Page, N: number) {
  const { jobs, grouped } = makeFixture(N);

  // Board endpoints - the load-bearing intercepts. The frontend
  // bootstraps from /api/tasks (flat) and /api/tasks/grouped (lane
  // buckets); both must return our synthetic shape. Other endpoints
  // (watch-paths, tags, clients, environment, runner status, snapshot)
  // fall through to the dev backend and use real defaults so the page
  // boots cleanly.
  await page.route(/\/api\/v1\/studio\/board/, async route => route.fulfill({ json: grouped }));
  await page.route(/\/api\/tasks(\?|$)/, async route => route.fulfill({ json: jobs }));

  // All other endpoints (watch-paths, tags, clients, environment,
  // runner status, project snapshot, etc.) fall through to the real dev
  // backend, which serves them fast post-Cycle-5. We only intercept the
  // board-shape endpoints above so we can inject N synthetic cards.
}

test.describe('Frontend stress: render perf at scale', () => {
  test.beforeAll(() => {
    if (process.env.RUN_PERF_BASELINE !== '1') {
      test.skip(true, 'Set RUN_PERF_BASELINE=1 to capture stress data.');
    }
    if (process.env.PERF_RESET === '1' && fs.existsSync(JSONL_PATH)) {
      fs.unlinkSync(JSONL_PATH);
    }
  });

  for (const N of [10, 100, 200, 500]) {
    test(`N=${N}: initial render + steady state + scroll`, async ({ page, context }) => {
      await installRoutes(page, N);

      // 1. Render-arrival: time from goto to first card visible.
      const t0 = Date.now();
      await page.goto('/');
      // Don't wait for networkidle - polled endpoints will keep firing.
      // Wait for the first card to be visible: that's "the user can see
      // the board" by definition.
      await page.getByTestId('job-card').first().waitFor({ state: 'visible', timeout: 15_000 });
      const initialRenderMs = Date.now() - t0;
      record(N, 'initial-render-to-first-card', 'ms', initialRenderMs);

      // Settle so polling has fired at least once and DOM has stabilised.
      await page.waitForTimeout(1000);

      // 2a. DOM node count - cheap proxy for "how big is the rendered tree".
      const domCount = await page.evaluate(() => document.querySelectorAll('*').length);
      record(N, 'dom-node-count', 'count', domCount);

      // 2b. Visible card count - sanity check that the fixture actually
      //     reached the DOM (and to verify any virtualization).
      const cardCount = await page.getByTestId('job-card').count();
      record(N, 'rendered-card-count', 'count', cardCount,
        cardCount < N ? 'fewer cards than fixture - virtualization or hide-when-empty in effect' : 'all cards rendered');

      // 2c. JS heap via CDP. Best available browser-side memory signal.
      try {
        const cdp = await context.newCDPSession(page);
        await cdp.send('Performance.enable');
        const metrics = await cdp.send('Performance.getMetrics');
        const heap = metrics.metrics.find(m => m.name === 'JSHeapUsedSize')?.value || 0;
        const docs = metrics.metrics.find(m => m.name === 'Documents')?.value || 0;
        const nodes = metrics.metrics.find(m => m.name === 'Nodes')?.value || 0;
        record(N, 'js-heap-bytes', 'bytes', heap);
        record(N, 'cdp-document-count', 'count', docs);
        record(N, 'cdp-node-count', 'count', nodes);
      } catch (err) {
        // CDP only on chromium; soft-skip elsewhere.
      }

      // 2d. Long tasks during 5 s steady-state idle. The biggest signal
      //     for "the UI feels stuck while just sitting there".
      const recorder = await startLongTaskRecorder(page);
      await page.waitForTimeout(5_000);
      const longTotal = await recorder.totalMs();
      const longCount = await recorder.count();
      await recorder.stop();
      record(N, 'long-tasks-total-during-5s-idle', 'ms', longTotal,
        `${longCount} long task(s)`);

      // 3a. Scroll FPS - rAF-counted frames during a 2 s programmatic scroll.
      //     Anything below ~50 FPS is visible jank to the user.
      const fps = await page.evaluate(async () => {
        return await new Promise<number>(resolve => {
          // Find the main scroll container - body for the kanban board layout.
          const scroller = document.scrollingElement || document.documentElement;
          let frames = 0;
          const startMark = performance.now();
          let lastTime = startMark;
          let dir = 1;
          function tick() {
            frames++;
            const now = performance.now();
            const elapsed = now - startMark;
            if (elapsed > 2000) {
              resolve(frames / (elapsed / 1000));
              return;
            }
            // Drive scroll by 8 px each frame (alternating direction)
            // so the test exercises layout/paint without ever scrolling
            // off-screen.
            scroller.scrollBy({ top: 8 * dir });
            if (Math.floor(elapsed / 250) % 2 === 1) dir = -1; else dir = 1;
            requestAnimationFrame(tick);
          }
          requestAnimationFrame(tick);
        });
      });
      record(N, 'scroll-fps-2s', 'fps', fps);

      // Click-to-visible deliberately skipped: synthetic jobs don't have
      // a real backend, so the detail GET 404s and the panel never
      // opens. The render metrics above (initial-render, longtask,
      // scroll-fps, dom/heap) capture what matters for the "blazing
      // fast at 500 cards" question; click latency at scale needs a
      // real-data scenario, which the perf-baseline.spec.ts already
      // covers against the live workspace.

      console.log(`[N=${N}] initial=${initialRenderMs}ms cards=${cardCount}/${N} dom=${domCount} ` +
        `longtask=${longTotal.toFixed(0)}ms fps=${fps.toFixed(1)}`);
    });
  }
});

/**
 * Cycle 7h: detail-page stress. The board test above measures the
 * kanban-shell render cost; this one opens a single card and measures
 * what the user pays once they're INSIDE the detail view: long chat
 * history (activity log), 10-file diff in the git pane, polling
 * services that the protocol pane mounts. The user reported "Git-Viewer
 * teilweise hakelig" + "lange Chat-Historie" as the two most painful
 * surfaces; this test instruments both.
 *
 * Per N (chat lines): mock the board with one card, mock the detail
 * endpoint with N cli-output lines + 10 git file changes + a synthetic
 * diff for the first file. Click the card, wait for the detail panes
 * to settle, then measure the same render+steady-state metrics as the
 * board stress, plus a focused activity-log scroll FPS reading.
 *
 * Files mocked here that the board test left to the dev backend:
 *   /api/tasks/{id}?watchPath=...           - JobDetail
 *   /api/tasks/{id}/output?watchPath=...    - CliOutputLine[]
 *   /api/tasks/{id}/runs?watchPath=...      - empty timeline
 *   /api/tasks/{id}/git/status?watchPath=... - 10 file changes
 *   /api/tasks/{id}/git/diff?path=...       - synthetic unified diff
 *   /api/tasks/{id}/git/hygiene?watchPath=...- empty hygiene
 *   /api/tasks/{id}/session-events          - empty
 *   /api/tasks/{id}/screenshots             - empty
 *   /api/tasks/{id}/claude/session-info     - null session
 *   /api/tasks/{id}/git/commit-detail/...   - null
 */

const DETAIL_JOB_ID = 'stress-detail-target';

function makeDetailJob(lane: string) {
  return {
    ...makeJob(0, lane),
    id: DETAIL_JOB_ID,
    jobKey: `${WATCH_PATH}::${DETAIL_JOB_ID}`,
    title: 'Detail Stress Test - long chat, 10 file diff',
  };
}

function makeOutputLines(count: number) {
  // Mix of [user] / [assistant] / [orchestrator] streams to exercise
  // the conversation-turn parser inside activity-log-view. Tool-call
  // markers sprinkled so the tool-burst grouping path runs too.
  const streams = ['user', 'stdout', 'stdout', 'stdout', 'orchestrator'];
  const lines: { timestamp: string; stream: string; text: string }[] = [];
  const now = Date.now();
  for (let i = 0; i < count; i++) {
    const stream = streams[i % streams.length];
    let text: string;
    if (i % 17 === 0) {
      text = `[tool] Read file=src/app/services/some-service-${i}.ts`;
    } else if (i % 11 === 0) {
      text = `## Heading ${i}\n\nLorem ipsum dolor sit amet, consectetur adipiscing elit. Quisque ut nibh massa.`;
    } else if (stream === 'user') {
      text = `Could you also handle the case for ${i}? Make sure to update the tests.`;
    } else {
      text = `Working on item ${i}. Here's a longer paragraph that simulates a real assistant turn with multiple sentences. The cost of rendering hundreds of these adds up; that's exactly what the test is measuring.`;
    }
    lines.push({ timestamp: new Date(now - (count - i) * 1000).toISOString(), stream, text });
  }
  return lines;
}

function makeGitStatus(fileCount: number) {
  const files = [];
  for (let i = 0; i < fileCount; i++) {
    files.push({
      status: i % 4 === 0 ? 'A' : i % 4 === 1 ? 'M' : i % 4 === 2 ? 'D' : 'R',
      path: `src/app/components/stress-${i.toString().padStart(2, '0')}/some-component.ts`,
      added: 5 + (i * 7) % 50,
      removed: 2 + (i * 3) % 30,
    });
  }
  return {
    isRepo: true,
    branch: 'stress-test',
    filesChanged: fileCount,
    totalAdded: files.reduce((s, f) => s + f.added, 0),
    totalRemoved: files.reduce((s, f) => s + f.removed, 0),
    files,
    error: null,
  };
}

function makeUnifiedDiff(filePath: string) {
  // Realistic-ish unified diff ~50 lines so diff2html does real work.
  const lines = [
    `diff --git a/${filePath} b/${filePath}`,
    `index 0000000..1111111 100644`,
    `--- a/${filePath}`,
    `+++ b/${filePath}`,
    `@@ -1,40 +1,42 @@`,
  ];
  for (let i = 0; i < 40; i++) {
    if (i % 11 === 0) lines.push(`-  const oldName = ${i};`);
    else if (i % 11 === 1) lines.push(`+  const newName = ${i};  // renamed for clarity`);
    else lines.push(` // unchanged context line ${i} - lorem ipsum dolor`);
  }
  return lines.join('\n');
}

async function installDetailRoutes(page: import('@playwright/test').Page, chatLines: number) {
  const detailJob = makeDetailJob('3-progress');
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [],
    ready: [], progress: [detailJob], failedPickup: [],
    autoReview: [], humanReview: [], review: [], completed: [], archive: [],
  };

  // Board: one card, makes click target obvious.
  await page.route(/\/api\/v1\/studio\/board/, async route => route.fulfill({ json: grouped }));
  await page.route(/\/api\/tasks(\?|$)/, async route => route.fulfill({ json: [detailJob] }));

  // JobDetail.
  const log = makeOutputLines(chatLines);
  const detail = {
    info: detailJob,
    promptMarkdown: `# Stress test prompt\n\nPretend this is a long task description that the user wrote. ${'Lorem ipsum. '.repeat(20)}`,
    promptHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: log.map(l => ({ timestamp: l.timestamp, event: l.stream, detail: l.text })),
    summaryState: null,
    reviewEvidence: [],
  };
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${DETAIL_JOB_ID}(\\?|$)`), async route =>
    route.fulfill({ json: detail }));

  // Output buffer that activity-log-view actually reads.
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/output`), async route =>
    route.fulfill({ json: log }));

  // Git status with 10 changes.
  const gitStatus = makeGitStatus(10);
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/git/status`), async route =>
    route.fulfill({ json: gitStatus }));

  // Diff for any path - the same synthetic diff for whichever file the
  // user clicks.
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/git/diff`), async route => {
    const url = new URL(route.request().url());
    const p = url.searchParams.get('path') ?? gitStatus.files[0].path;
    await route.fulfill({ contentType: 'text/plain', body: makeUnifiedDiff(p) });
  });

  // Hygiene + commit-detail empty.
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/git/hygiene`), async route =>
    route.fulfill({ json: { project: PROJECT_NAME, dirty: true, unpushed: 0, branch: 'stress-test' } }));
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/git/commit-detail`), async route =>
    route.fulfill({ json: null }));

  // Other per-job pollers - empty so they don't bias the measurement.
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/runs`), async route =>
    route.fulfill({ json: { runs: [], runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false } }));
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/session-events`), async route =>
    route.fulfill({ json: { events: [], sessionChain: [] } }));
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/screenshots`), async route =>
    route.fulfill({ json: { screenshots: [] } }));
  await page.route(new RegExp(`/api/tasks/${DETAIL_JOB_ID}/claude/session-info`), async route =>
    route.fulfill({ json: { sessionInfo: null, rateLimit: null } }));
}

test.describe('Frontend stress: detail page (long chat + 10-file diff)', () => {
  test.beforeAll(() => {
    if (process.env.RUN_PERF_BASELINE !== '1') {
      test.skip(true, 'Set RUN_PERF_BASELINE=1 to capture detail stress data.');
    }
  });

  for (const N of [100, 500, 2000]) {
    test(`chat=${N}: open detail + steady state + git pane`, async ({ page, context }) => {
      await installDetailRoutes(page, N);

      page.on('console', msg => {
        if (msg.type() === 'error') {
          const text = msg.text();
          if (!text.includes('favicon') && !text.includes('SignalR') && !text.includes('ERR_CONNECTION')) {
            console.log(`CONSOLE.error: ${text.slice(0, 250)}`);
          }
        }
      });
      page.on('response', resp => {
        if (resp.status() === 404 && resp.url().includes('/api/')) {
          console.log(`HTTP 404: ${resp.url()}`);
        }
      });

      await page.goto('/');
      await page.getByTestId('job-card').first().waitFor({ state: 'visible', timeout: 15_000 });

      // 1. Click-to-detail-visible. Split into:
      //    - network: time from click to /api/tasks/{id}? response received
      //    - render:  time from response to detail-panes visible
      const card = page.getByTestId('job-card').first();
      // Settle a little so any pending polls don't queue behind the click.
      await page.waitForTimeout(200);
      const t0 = Date.now();
      const responsePromise = page.waitForResponse(
        r => new RegExp(`/api/v1/projects/[^/]+/tasks/${DETAIL_JOB_ID}(\\?|$)`).test(r.url()) || r.url().includes(`/api/tasks/${DETAIL_JOB_ID}`),
        { timeout: 10_000 }
      );
      await card.click();
      const tNetEnd = await responsePromise.then(() => Date.now());
      await page.getByTestId('detail-panes').waitFor({ state: 'visible', timeout: 10_000 });
      const tDetailVisible = Date.now();
      record(N, 'click-to-detail-visible', 'ms', tDetailVisible - t0);
      record(N, 'click-to-detail-network-ms', 'ms', tNetEnd - t0,
        'time from click to /api/tasks/{id} response');
      record(N, 'click-to-detail-render-ms', 'ms', tDetailVisible - tNetEnd,
        'time from response to detail-panes visible');

      // Settle so detail-pane pollers have fired their first call.
      await page.waitForTimeout(1500);

      // 2a. DOM count after detail open.
      const domCount = await page.evaluate(() => document.querySelectorAll('*').length);
      record(N, 'detail-dom-node-count', 'count', domCount);

      // 2b. Activity-log lines actually rendered.
      // Activity log renders conversation turns (one per turn) inside
       // the .convo container, NOT one DOM node per CLI line. Count
       // direct children of .convo as a "rendered turn" proxy.
      // Cycle 7i: under CDK virtual scroll the convo container holds
      // a wrapper plus only the visible turns; counting `.convo-turn`
      // descendants gives the truth either way.
      const visibleLogLines = await page.evaluate(() =>
        document.querySelectorAll('[data-testid="activity-log-conversation"] .convo-turn').length
      );
      record(N, 'activity-log-rendered-lines', 'count', visibleLogLines,
        visibleLogLines < N ? 'fewer rendered than fixture - virtualization or windowing in effect' : 'all lines rendered');

      // 2c. Heap.
      try {
        const cdp = await context.newCDPSession(page);
        await cdp.send('Performance.enable');
        const metrics = await cdp.send('Performance.getMetrics');
        const heap = metrics.metrics.find(m => m.name === 'JSHeapUsedSize')?.value || 0;
        const nodes = metrics.metrics.find(m => m.name === 'Nodes')?.value || 0;
        record(N, 'detail-js-heap-bytes', 'bytes', heap);
        record(N, 'detail-cdp-node-count', 'count', nodes);
      } catch {}

      // 3. Long task during 5 s steady-state on detail pane.
      const recorder = await startLongTaskRecorder(page);
      await page.waitForTimeout(5_000);
      const longTotal = await recorder.totalMs();
      const longCount = await recorder.count();
      await recorder.stop();
      record(N, 'detail-long-tasks-during-5s-idle', 'ms', longTotal,
        `${longCount} long task(s)`);

      // 4. Activity-log scroll FPS - 2 s programmatic scroll INSIDE the
      //    .activity-log__body container (not window).
      const fps = await page.evaluate(async () => {
        // Cycle 7i: the CDK virtual scroll viewport is the actual
        // scroller now; falls back to .activity-log__body for the
        // pre-Cycle-7i measurement runs.
        const scroller = (document.querySelector('cdk-virtual-scroll-viewport.activity-log__virtual')
          ?? document.querySelector('.activity-log__body')) as HTMLElement | null;
        if (!scroller) return 0;
        return await new Promise<number>(resolve => {
          let frames = 0;
          const startMark = performance.now();
          let dir = 1;
          function tick() {
            frames++;
            const now = performance.now();
            const elapsed = now - startMark;
            if (elapsed > 2000) { resolve(frames / (elapsed / 1000)); return; }
            scroller!.scrollBy({ top: 16 * dir });
            if (Math.floor(elapsed / 250) % 2 === 1) dir = -1; else dir = 1;
            requestAnimationFrame(tick);
          }
          requestAnimationFrame(tick);
        });
      });
      record(N, 'activity-log-scroll-fps-2s', 'fps', fps);

      // 5. Git pane diff render: click git-diff-col first file, time
      //    until the rendered diff content appears. The git pane lazy-
      //    loads diff2html on first non-empty diff (Cycle 7f).
      try {
        // The git tree is on the left of the git view body. Find a file row.
        // We don't know the exact testid; click the first list item under
        // [data-testid="git-tree-col"]. If diff2html is lazy-loaded, the
        // first render takes longer than subsequent ones.
        const treeCol = page.getByTestId('git-tree-col');
        if (await treeCol.isVisible().catch(() => false)) {
          const firstFile = treeCol.locator('[data-testid^="git-file-"], button, [role="button"]').first();
          if (await firstFile.isVisible().catch(() => false)) {
            const tDiff = Date.now();
            await firstFile.click();
            // Diff content lands inside the git-diff-col. Wait for any
            // diff2html-rendered element (their root has class
            // d2h-file-wrapper) or fallback to a non-empty <pre>.
            await page.locator('.d2h-file-wrapper, .git-view__diff pre').first()
              .waitFor({ state: 'visible', timeout: 10_000 });
            record(N, 'git-diff-render-ms', 'ms', Date.now() - tDiff);
          }
        }
      } catch (err) {
        // Git pane may not be visible by default in this layout - record
        // a sentinel so the report can show "not measured" rather than
        // implying success.
        record(N, 'git-diff-render-ms', 'ms', -1, 'git pane not reachable in this run');
      }

      console.log(`[detail chat=${N}] click-to-visible=${tDetailVisible - t0}ms (net=${tNetEnd - t0}ms render=${tDetailVisible - tNetEnd}ms) ` +
        `log-lines=${visibleLogLines}/${N} dom=${domCount} longtask=${longTotal.toFixed(0)}ms log-scroll-fps=${fps.toFixed(1)}`);
    });
  }
});
