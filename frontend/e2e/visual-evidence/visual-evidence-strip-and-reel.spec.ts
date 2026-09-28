import { test, expect, Page } from '@playwright/test';
import { mkdirSync, copyFileSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { setTheme, dismissDevErrorDialog, sampleColours } from '../helpers/theme';

/**
 * Per-task screenshot strip + lightbox + workspace reel.
 *
 * The spec stubs every backend route the surfaces depend on so it
 * runs against a clean dev frontend without needing real watched
 * projects on disk:
 *
 *   - `/api/tasks/{id}/screenshots` returns three fake entries with
 *     ascending timestamps. The protocol pane renders the strip in
 *     chronological order.
 *   - `/api/workspace/screenshots` returns entries spread across two
 *     hour buckets so the reel groups them deterministically.
 *   - The PNGs themselves are tiny base64-encoded inline images so
 *     <img> elements actually load.
 *
 * Coverage matches the task's three test goals:
 *   1. Strip renders three thumbnails in chronological order.
 *   2. Lightbox prev/next cycles correctly (and wraps).
 *   3. Reel groups entries by hour bucket.
 */

const SCREENSHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? 'test-results';

// 1x1 PNGs encoded inline so the <img> tags actually resolve. The
// bytes vary between three colours so the deliverable screenshot
// shows visually distinct thumbnails. Built lazily inside the route
// handler so the module-load order does not depend on the helper
// declarations defined further below.
let cachedPngs: Buffer[] | null = null;
function inlinePngs(): Buffer[] {
  if (!cachedPngs) {
    cachedPngs = [
      pngBytes(0xf3, 0x8b, 0xa8),
      pngBytes(0xf9, 0xe2, 0xaf),
      pngBytes(0xa6, 0xe3, 0xa1)
    ];
  }
  return cachedPngs;
}

const TASK_JOB_ID = 'visual-evidence-priority-screenshots-clickable';
const PREV_TASK_JOB_ID = 'visual-evidence-priority-previous-task';
const NEXT_TASK_JOB_ID = 'visual-evidence-priority-next-task';
const TASK_WATCH_PATH = 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard';

function taskInfo(id: string, title: string, order: number) {
  return {
    id,
    taskKey: `${TASK_WATCH_PATH}::${id}`,
    title,
    state: '5-human-review',
    order,
    agent: 'claude',
    cliType: 'claude',
    model: null,
    createdAt: new Date().toISOString(),
    watchPath: TASK_WATCH_PATH,
    projectName: 'agent-taskboard',
    folderPath: `${TASK_WATCH_PATH}/5-human-review/${id}`,
    lastActivity: new Date().toISOString(),
    sessionChain: [],
    ownerClientId: 'default',
    commitCount: 0
  };
}

function buildPagerTasks() {
  return [
    taskInfo(PREV_TASK_JOB_ID, 'Visual evidence previous task', 0),
    taskInfo(TASK_JOB_ID, 'Visual evidence priority', 1),
    taskInfo(NEXT_TASK_JOB_ID, 'Visual evidence next task', 2)
  ];
}

function emptyGrouped() {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: buildPagerTasks(),
    review: [],
    completed: [],
    archive: []
  };
}

function buildJobScreenshots() {
  // Three timestamps within an hour, ordered oldest-first to verify
  // chronological rendering.
  const t0 = new Date(Date.now() - 60 * 60 * 1000);
  const ts = [t0, new Date(t0.getTime() + 5 * 60 * 1000), new Date(t0.getTime() + 12 * 60 * 1000)];

  return [
    {
      jobId: TASK_JOB_ID,
      jobTitle: 'Visual evidence priority',
      projectName: 'agent-taskboard',
      watchPath: TASK_WATCH_PATH,
      fileName: 'home-loaded.png',
      relativePath: 'results/playwright/home/home-loaded.png',
      url: `/api/tasks/${TASK_JOB_ID}/screenshot?path=playwright%2Fhome%2Fhome-loaded.png`,
      caption: 'home',
      status: 'passed',
      localPath: 'C:/Projects/.../results/playwright/home/home-loaded.png',
      timestampUtc: ts[0].toISOString()
    },
    {
      jobId: TASK_JOB_ID,
      jobTitle: 'Visual evidence priority',
      projectName: 'agent-taskboard',
      watchPath: TASK_WATCH_PATH,
      fileName: 'detail-open.png',
      relativePath: 'results/playwright/detail/detail-open.png',
      url: `/api/tasks/${TASK_JOB_ID}/screenshot?path=playwright%2Fdetail%2Fdetail-open.png`,
      caption: 'detail',
      status: 'passed',
      localPath: 'C:/Projects/.../results/playwright/detail/detail-open.png',
      timestampUtc: ts[1].toISOString()
    },
    {
      jobId: TASK_JOB_ID,
      jobTitle: 'Visual evidence priority',
      projectName: 'agent-taskboard',
      watchPath: TASK_WATCH_PATH,
      fileName: 'lightbox-open.png',
      relativePath: 'results/playwright/lightbox/lightbox-open.png',
      url: `/api/tasks/${TASK_JOB_ID}/screenshot?path=playwright%2Flightbox%2Flightbox-open.png`,
      caption: 'lightbox',
      status: 'failed',
      localPath: 'C:/Projects/.../results/playwright/lightbox/lightbox-open.png',
      timestampUtc: ts[2].toISOString()
    }
  ];
}

function buildWorkspaceScreenshots() {
  // Two distinct hour buckets so the reel renders two grouped
  // sections.
  const now = Date.now();
  const earlier = now - 90 * 60 * 1000;     // ~1.5 h ago
  const recent = now - 10 * 60 * 1000;       // 10 min ago

  return [
    {
      jobId: 'task-reel-a',
      jobTitle: 'Project A task',
      projectName: 'project-a',
      watchPath: 'C:/Projects/project-a',
      fileName: 'a1.png',
      relativePath: 'results/a1.png',
      url: `/api/tasks/task-reel-a/screenshot?path=a1.png`,
      caption: 'a1',
      status: null,
      localPath: 'C:/.../a1.png',
      timestampUtc: new Date(recent).toISOString()
    },
    {
      jobId: 'task-reel-b',
      jobTitle: 'Project B task',
      projectName: 'project-b',
      watchPath: 'C:/Projects/project-b',
      fileName: 'b1.png',
      relativePath: 'results/b1.png',
      url: `/api/tasks/task-reel-b/screenshot?path=b1.png`,
      caption: 'b1',
      status: 'passed',
      localPath: 'C:/.../b1.png',
      timestampUtc: new Date(recent - 60 * 1000).toISOString()
    },
    {
      jobId: 'task-reel-c',
      jobTitle: 'Project A older task',
      projectName: 'project-a',
      watchPath: 'C:/Projects/project-a',
      fileName: 'c1.png',
      relativePath: 'results/c1.png',
      url: `/api/tasks/task-reel-c/screenshot?path=c1.png`,
      caption: 'c1',
      status: null,
      localPath: 'C:/.../c1.png',
      timestampUtc: new Date(earlier).toISOString()
    }
  ];
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  // Use a regex so the bare list endpoint does not eclipse
  // `/api/tasks/<id>` and `/api/tasks/<id>/screenshots` route handlers
  // registered later. Playwright glob `?` matches a single char, which
  // is exactly the kind of pattern that does eclipse them.
  await page.route(/\/api\/(?:jobs|tasks)(\?.*)?$/, json(buildPagerTasks()));
  await page.route('**/api/v1/studio/board*', json(emptyGrouped()));
  await page.route('**/api/v1/studio/board*', json(emptyGrouped()));
  await page.route('**/api/watch-paths', json([{ name: 'agent-taskboard', path: TASK_WATCH_PATH }]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/token-summary-aggregate*', json({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stubbed'
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/cli/usage', json({ entries: [] }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/clients', json([]));
}

async function stubScreenshotApis(page: Page, jobScreenshots: any[], workspaceScreenshots: any[]) {
  // Use regex anchors so /screenshots and /screenshot don't shadow
  // each other through Playwright's glob `*` semantics.
  await page.route(/\/api\/(?:jobs|tasks)\/[^/]+\/screenshots(\?.*)?$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ jobId: TASK_JOB_ID, screenshots: jobScreenshots })
    });
  });

  await page.route('**/api/workspace/screenshots*', async (route) => {
    const url = new URL(route.request().url());
    const projectFilter = url.searchParams.get('projectFilter');
    const filtered = projectFilter
      ? workspaceScreenshots.filter((s) => s.projectName === projectFilter)
      : workspaceScreenshots;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        windowHours: Number(url.searchParams.get('windowHours') ?? '72'),
        projectFilter,
        screenshots: filtered
      })
    });
  });

  // Serve any image URL with one of the inline pixel PNGs so <img>
  // elements actually load and the lightbox figure has something to
  // show. Anchored regex so this does not shadow the /screenshots
  // listing endpoint.
  let pngIndex = 0;
  await page.route(/\/api\/(?:jobs|tasks)\/[^/]+\/screenshot(\?.*)?$/, async (route) => {
    const pngs = inlinePngs();
    const body = pngs[pngIndex++ % pngs.length];
    await route.fulfill({ status: 200, contentType: 'image/png', body });
  });
}

async function stubJobDetailForTask(page: Page) {
  // The protocol pane only mounts when a job detail loads. Stub the
  // detail endpoint and the cli-output / runs endpoints to a sane
  // empty shape.
  const detailFor = (info: ReturnType<typeof taskInfo>) => ({
    info,
    promptMarkdown: '# Visual evidence priority\n\nSurface screenshots prominently.',
    promptHistory: [],
    statusMarkdown: '# Status\n\n- Result: Success\n- Duration: 3 min\n\n## What Was Done\n- Stubbed screenshots for the visual evidence test.\n',
    contextUsage: null,
    log: [],
    summaryState: { status: 'ready' }
  });

  for (const info of buildPagerTasks()) {
    // The bare detail endpoint. Use a regex anchored to the task id so it
    // does not eclipse the /screenshots subpath route registered above.
    const detailRe = new RegExp(`/api/v1/projects/[^/]+/tasks/${escapeForRegex(info.id)}(\\?.*)?$`);
    await page.route(detailRe, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(detailFor(info))
      });
    });
  }

  // Sub-routes off the detail endpoint that the protocol pane polls.
  // Each one returns an empty-but-valid shape so the panes mount.
  const subRoutes: Record<string, unknown> = {
    output:           { lines: [], totalLines: 0, isRunning: false, startedAt: null },
    runs:             { runs: [], runCount: 0, hasActiveRun: false },
    'session-events': { events: [], sessionChain: [] },
    session:          { sessionInfo: null, rateLimit: null },
    'git/status':     { isRepo: false, branch: null, filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null },
    commits:          []
  };
  for (const [suffix, body] of Object.entries(subRoutes)) {
    for (const info of buildPagerTasks()) {
      await page.route(`**/api/tasks/${info.id}/${suffix}*`, async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
      });
      await page.route(`**/api/tasks/${info.id}/${suffix}*`, async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
      });
    }
  }
}

function escapeForRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

test.describe('Visual evidence: per-task strip + lightbox + workspace reel', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 980 });
    await stubBackgroundApis(page);
  });

  test('strip renders three thumbnails in chronological order, lightbox prev/next cycles', async ({ page }) => {
    const jobScreenshots = buildJobScreenshots();
    await stubScreenshotApis(page, jobScreenshots, buildWorkspaceScreenshots());
    await stubJobDetailForTask(page);

    await page.goto(`http://localhost:4010/?job=${encodeURIComponent(TASK_JOB_ID)}&watchPath=${encodeURIComponent(TASK_WATCH_PATH)}`);
    await page.waitForLoadState('domcontentloaded');
    await dismissDevErrorDialog(page);

    // F38 dedup: the screenshot strip used to render twice (once in the
    // protocol pane body, once in the prompt pane's Evidence tab).
    // Visual evidence is now Evidence-tab-only; switch the tab before
    // asserting on the strip. Wait for the tab strip to mount before
    // clicking — the detail view loads the prompt pane asynchronously.
    const evidenceTab = page.getByTestId('prompt-tab-evidence');
    await expect(evidenceTab).toBeVisible({ timeout: 10_000 });
    const evidenceBadge = page.getByTestId('prompt-tab-evidence-badge');
    await expect(evidenceBadge).toHaveText('3');
    await expect(evidenceBadge.locator('.count-badge')).toBeVisible();
    await evidenceTab.click();
    const strip = page.getByTestId('evidence-view').getByTestId('screenshot-strip');
    await expect(strip).toBeVisible({ timeout: 7_000 });
    await dismissDevErrorDialog(page);

    const thumbs = strip.locator('[data-testid="screenshot-thumb"]');
    await expect(thumbs).toHaveCount(3);

    // Chronological order: oldest first. Confirm via the data-index
    // attribute on the rendered thumbs and the accompanying captions.
    const captions = await thumbs.locator('.strip__caption-spec').allTextContents();
    expect(captions).toEqual(['home', 'detail', 'lightbox']);

    // Open the lightbox on thumb #2 (index 1, "detail").
    await thumbs.nth(1).click();
    const lightbox = page.getByTestId('screenshot-lightbox');
    await expect(lightbox).toBeVisible();
    await expect(page.getByTestId('screenshot-lightbox-caption')).toHaveText('detail');
    await expect(page.getByTestId('screenshot-lightbox-index')).toHaveText('2 / 3');

    // Capture the deliverable screenshot: task page with three
    // thumbnails and the lightbox open on the second one.
    await page.screenshot({ path: join(SCREENSHOT_DIR, 'visual-evidence-task-lightbox-second.png'), fullPage: false });

    // Next -> wraps onward.
    await page.getByTestId('screenshot-lightbox-next').click();
    await expect(page.getByTestId('screenshot-lightbox-caption')).toHaveText('lightbox');
    await expect(page.getByTestId('screenshot-lightbox-index')).toHaveText('3 / 3');

    // Next again wraps to the start.
    await page.getByTestId('screenshot-lightbox-next').click();
    await expect(page.getByTestId('screenshot-lightbox-caption')).toHaveText('home');
    await expect(page.getByTestId('screenshot-lightbox-index')).toHaveText('1 / 3');

    // Prev wraps backwards from the start.
    await page.getByTestId('screenshot-lightbox-prev').click();
    await expect(page.getByTestId('screenshot-lightbox-caption')).toHaveText('lightbox');

    // Close.
    await page.getByTestId('screenshot-lightbox-close').click();
    await expect(lightbox).not.toBeVisible();
  });

  test('evidence tab omits the badge when no visual evidence exists', async ({ page }) => {
    await stubScreenshotApis(page, [], buildWorkspaceScreenshots());
    await stubJobDetailForTask(page);

    await page.goto(`http://localhost:4010/?job=${encodeURIComponent(TASK_JOB_ID)}&watchPath=${encodeURIComponent(TASK_WATCH_PATH)}`);
    await page.waitForLoadState('domcontentloaded');
    await dismissDevErrorDialog(page);

    const evidenceTab = page.getByTestId('prompt-tab-evidence');
    await expect(evidenceTab).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('prompt-tab-evidence-badge')).toHaveCount(0);
  });

  test('workspace reel groups entries by hour bucket and supports the lightbox', async ({ page }) => {
    await stubScreenshotApis(page, buildJobScreenshots(), buildWorkspaceScreenshots());
    await stubJobDetailForTask(page);

    await page.goto('http://localhost:4010/#/workspace/screenshots');
    await page.waitForLoadState('domcontentloaded');

    const overlay = page.getByTestId('workspace-screenshots-overlay');
    await expect(overlay).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('workspace-screenshots')).toBeVisible();
    await dismissDevErrorDialog(page);

    // Two distinct hour buckets in the stub payload.
    const buckets = page.getByTestId('wss-bucket');
    await expect(buckets).toHaveCount(2);

    // Each bucket carries its own strip; first bucket has 2, second has 1.
    // F38 dedup: the protocol pane no longer renders a duplicate strip;
    // the workspace reel uses the canonical `screenshot-strip` testid.
    await expect(buckets.nth(0).locator('[data-testid="screenshot-thumb"]')).toHaveCount(2);
    await expect(buckets.nth(1).locator('[data-testid="screenshot-thumb"]')).toHaveCount(1);

    // Open the lightbox on the first thumb of the first bucket and
    // verify the reel-only "Open task" link is present.
    await buckets.nth(0).locator('[data-testid="screenshot-thumb"]').first().click();
    await expect(page.getByTestId('screenshot-lightbox')).toBeVisible();
    await expect(page.getByTestId('screenshot-lightbox-open-task')).toBeVisible();

    await page.screenshot({ path: join(SCREENSHOT_DIR, 'visual-evidence-reel-lightbox.png'), fullPage: false });
  });

  // The visual-evidence reel + strip captions used fixed dark-theme colours and
  // washed out on the light theme. After the Tier-2 token conversion the reel
  // header, window toggle, bucket titles and thumbnail captions must clear
  // WCAG AA on BOTH themes. (The lightbox is intentionally always-dark and is
  // exercised by the cases above, not here.)
  for (const theme of ['dark', 'light'] as const) {
    test(`reel + strip captions stay legible (${theme} theme)`, async ({ page }) => {
      await stubScreenshotApis(page, buildJobScreenshots(), buildWorkspaceScreenshots());
      await stubJobDetailForTask(page);

      await page.goto('http://localhost:4010/#/workspace/screenshots');
      await page.waitForLoadState('domcontentloaded');
      await setTheme(page, theme);

      await expect(page.getByTestId('workspace-screenshots')).toBeVisible({ timeout: 5_000 });
      await dismissDevErrorDialog(page);
      await expect(page.locator('.strip__caption-spec').first()).toBeVisible({ timeout: 5_000 });

      const samples: Array<{ what: string; selector: string }> = [
        { what: 'reel title', selector: '.wss__title' },
        { what: 'reel subtitle', selector: '.wss__sub' },
        { what: 'active window button', selector: '.wss__win-btn--active' },
        { what: 'bucket title', selector: '.wss__bucket-title' },
        { what: 'caption spec', selector: '.strip__caption-spec' },
        { what: 'caption timestamp', selector: '.strip__caption-ts' },
      ];
      for (const { what, selector } of samples) {
        const { color, bg } = await sampleColours(page, selector);
        const ratio = contrastRatio(color, bg);
        expect(
          ratio,
          `${what} contrast ${ratio.toFixed(2)} (${color} on ${bg}) [${theme}]`,
        ).toBeGreaterThanOrEqual(4.5);
      }

      await page.screenshot({ path: join(SCREENSHOT_DIR, `visual-evidence-reel-${theme}.png`), fullPage: false });
    });
  }
});

/**
 * Build a minimal valid 1x1 PNG with a solid-colour pixel. Used to
 * keep the spec self-contained so it does not need any real image
 * fixture on disk.
 */
function pngBytes(r: number, g: number, b: number): Buffer {
  // Hand-built PNG: signature + IHDR + IDAT + IEND.
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = chunk('IHDR', Buffer.from([
    0, 0, 0, 1,         // width
    0, 0, 0, 1,         // height
    8,                  // bit depth
    2,                  // colour type: truecolour
    0, 0, 0
  ]));
  // IDAT: zlib of the filter byte (0) + RGB triple.
  const zlib = require('node:zlib') as typeof import('node:zlib');
  const raw = Buffer.from([0, r, g, b]);
  const idat = chunk('IDAT', zlib.deflateSync(raw));
  const iend = chunk('IEND', Buffer.alloc(0));
  return Buffer.concat([sig, ihdr, idat, iend]);
}

function chunk(type: string, data: Buffer): Buffer {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const t = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  // Compute CRC32 over type + data.
  crc.writeUInt32BE(crc32(Buffer.concat([t, data])), 0);
  return Buffer.concat([len, t, data, crc]);
}

let crcTable: Uint32Array | null = null;
function crc32(buf: Buffer): number {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
      crcTable[n] = c >>> 0;
    }
  }
  let c = 0xffffffff;
  for (const byte of buf) {
    c = (crcTable[(c ^ byte) & 0xff] ^ (c >>> 8)) >>> 0;
  }
  return (c ^ 0xffffffff) >>> 0;
}
