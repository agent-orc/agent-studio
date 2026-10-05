import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { setTheme, dismissDevErrorDialog } from '../helpers/theme';

/**
 * AGT-1992: the review-evidence panel renders image references (screenshots
 * harvested into `results/`) as inline thumbnails instead of a bare
 * `📎 results/foo.png` text row, and gives non-image references a
 * file-type-specific glyph instead of one generic artifact icon.
 *
 * Fully mocked (route interception) so it needs only the frontend dev
 * server on :4010 — never the dev backend. The two image references point
 * at a real PNG served from a mocked `/results/` route so the <img> decodes
 * and the thumbnail actually paints in the screenshot evidence.
 */

// --- minimal solid-colour PNG encoder (no image deps) ----------------------
function crc32(buf: Buffer): number {
  let c = ~0;
  for (const byte of buf) {
    c ^= byte;
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1));
  }
  return (~c) >>> 0;
}

function pngChunk(type: string, data: Buffer): Buffer {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crc]);
}

function makePng(width: number, height: number, rgb: [number, number, number]): Buffer {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // colour type: truecolour RGB
  const row = Buffer.alloc(1 + width * 3);
  for (let x = 0; x < width; x++) {
    row[1 + x * 3] = rgb[0];
    row[2 + x * 3] = rgb[1];
    row[3 + x * 3] = rgb[2];
  }
  const raw = Buffer.concat(Array.from({ length: height }, () => row));
  const idat = zlib.deflateSync(raw);
  return Buffer.concat([
    sig,
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', idat),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

const PNG_A = makePng(240, 150, [76, 110, 245]); // blue-ish
const PNG_B = makePng(240, 150, [214, 92, 92]); //  red-ish

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/review-evidence-thumbs';
const JOB_ID = 'evidence-thumb-test';

const DETAIL = {
  info: {
    id: JOB_ID,
    key: 'AGT-1992',
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'Review-evidence thumbnails fixture',
    state: '5-human-review',
    phase: null,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${JOB_ID}`,
    sessionName: null,
    lastUsage: null,
    execution: null,
    order: 1,
    createdAt: '2026-07-09T09:00:00Z',
    lastActivity: '2026-07-09T10:00:00Z',
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
  },
  promptMarkdown: 'Test prompt.',
  statusMarkdown: '',
  log: [],
  promptHistory: [],
  contextUsage: null,
  reviewEvidence: [
    {
      id: 'shot-empty',
      source: 'task-check',
      severity: 'info',
      title: 'pipeline-state-empty--mocked',
      body: 'Harvested screenshot: empty pipeline state.',
      createdAt: '2026-07-09T09:30:00Z',
      runIndex: 1,
      artifacts: ['results/pipeline-state-empty--mocked.png'],
      fileRefs: [],
      acknowledged: false,
      followupJobId: null,
    },
    {
      id: 'audit-token',
      source: 'code-review',
      ruleId: 'QS-NG-002',
      severity: 'high',
      title: 'Angular quality rule matched',
      body: 'The named Quality Studio rule matched this source location.',
      createdAt: '2026-07-09T09:40:00Z',
      runIndex: 1,
      artifacts: ['results/dashboard-populated--mocked.png'],
      fileRefs: ['frontend/src/app/example.component.scss:42', 'results/quality-analysis.json'],
      acknowledged: false,
      followupJobId: null,
    },
  ],
  summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
};

async function installRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/hubs/v1/studio/negotiate**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        connectionId: 'review-evidence-e2e',
        connectionToken: 'review-evidence-e2e',
        negotiateVersion: 1,
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
      }),
    }),
  );
  await page.routeWebSocket('**/hubs/v1/studio**', (socket) => {
    socket.onMessage((message) => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });

  // Broad catch-all first; specific routes registered afterwards win (LIFO).
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined),
  );
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }),
  );
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], autoReview: [], humanReview: [], completed: [], archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-07-10T00:00:00Z', ttlSeconds: 600, snapshots: [] }),
    }),
  );
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
      }),
    }),
  );

  // The harvested screenshots: a real PNG so the thumbnail paints.
  await page.route(/\/results\/pipeline-state-empty--mocked\.png(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'image/png', body: PNG_A }),
  );
  await page.route(/\/results\/dashboard-populated--mocked\.png(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'image/png', body: PNG_B }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/screenshots(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(DETAIL) }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? path.resolve('test-results', 'review-evidence-thumbnails');

test.describe('AGT-1992: review-evidence image thumbnails', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
      } catch { /* private mode */ }
    });
  });

  test('renders image refs as thumbnails, non-image refs as typed rows, opens the shared lightbox', async ({ page }) => {
    test.setTimeout(60_000);
    await installRoutes(page);
    await page.goto(`/#/tasks/${encodeURIComponent(JOB_ID)}?view=evidence%3Aprotocol`);
    await dismissDevErrorDialog(page);

    // The canonical route selects Evidence before the detail surface paints.
    const evidenceTab = page.getByTestId('prompt-tab-evidence');
    await expect(evidenceTab).toBeVisible({ timeout: 15_000 });
    await expect(evidenceTab).toHaveAttribute('aria-selected', 'true');
    await dismissDevErrorDialog(page);

    const panel = page.getByTestId('review-evidence-panel');
    await expect(panel).toBeVisible({ timeout: 10_000 });

    // Image artifact renders as a thumbnail (lazy) that decodes from the served URL.
    const thumb = page.getByTestId('review-evidence-thumb-shot-empty').first();
    await expect(thumb).toBeVisible();
    const thumbImg = thumb.locator('img');
    await expect(thumbImg).toHaveAttribute('loading', 'lazy');
    await expect(thumbImg).toHaveJSProperty('naturalWidth', 240);

    // Non-image references stay as labelled text rows (code + markdown).
    await expect(page.getByTestId('review-evidence-fileref-audit-token').first())
      .toContainText('example.component.scss:42');
    await expect(page.getByTestId('review-evidence-rule-audit-token')).toHaveText('QS-NG-002');

    fs.mkdirSync(RESULTS_DIR, { recursive: true });

    // Dark theme.
    await setTheme(page, 'dark');
    await dismissDevErrorDialog(page);
    await expect(panel).toBeVisible();
    await panel.screenshot({ path: path.join(RESULTS_DIR, 'review-evidence-thumbnails-dark--mocked.png') });

    // Light theme.
    await setTheme(page, 'light');
    await dismissDevErrorDialog(page);
    await expect(panel).toBeVisible();
    await panel.screenshot({ path: path.join(RESULTS_DIR, 'review-evidence-thumbnails-light--mocked.png') });

    // Clicking a thumbnail opens the shared media lightbox as a gallery.
    await setTheme(page, 'dark');
    await thumb.click();
    const lightbox = page.getByTestId('media-lightbox');
    await expect(lightbox).toBeVisible({ timeout: 5_000 });
    await page.screenshot({ path: path.join(RESULTS_DIR, 'review-evidence-lightbox-dark--mocked.png') });
    await page.keyboard.press('Escape');
  });
});
