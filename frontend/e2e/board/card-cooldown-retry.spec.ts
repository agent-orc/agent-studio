import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as path from 'path';
import { mkdirSync, writeFileSync } from 'fs';

/**
 * DtC step 6 — CooldownRetry banner on a 3-progress card (task
 * `dtc-t6-ui---cooldownretry-banner`).
 *
 * A card that infra-crashed and is holding out its scheduled re-pickup backoff
 * (`runActivity.failed-backoff`, ASS-1751) must read DISTINCTLY from a normal
 * "running" progress card so a cooling task is not mistaken for a fresh stall or
 * a live run. The card renders a warn-toned full-width banner
 * (`infra-crashed · retrying k/3 · in Ns`) sourced only from the already-overlaid
 * `runActivity` (kind + attempt + backoffUntil) — no new side-channel.
 *
 * This spec drops two cards into 3-progress — a normal running card and a
 * failed-backoff cooldown card — and captures:
 *   1. the lane with both cards side by side (the distinctness evidence), and
 *   2. a before/after composite of the SAME cooldown card with the banner removed
 *      (the pre-feature look) beside the banner present (the new look).
 *
 * Fully mocked via route interception, so it runs against any served frontend
 * without a live backend.
 */

const PROJECT = 'fixture-cooldown';
const WATCH_PATH = 'C:/fixtures/cooldown-repo';

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? process.env.JOB_RESULTS_DIR
  : path.resolve(__dirname, '../../test-results/cooldown-retry');

/** Persist a shot both as a report attachment and as a durable file under results/. */
async function saveShot(testInfo: TestInfo, name: string, body: Buffer): Promise<void> {
  await testInfo.attach(name, { body, contentType: 'image/png' });
  try {
    mkdirSync(SHOTS_DIR, { recursive: true });
    writeFileSync(path.join(SHOTS_DIR, name), body);
  } catch {
    /* best-effort: the attachment above is the fallback */
  }
}

const RUNNING_TITLE = 'Live run — normal in-progress card';
const COOLDOWN_TITLE = 'Infra crash — CooldownRetry backoff';

/** A normal, live-running 3-progress card (the "normal in-progress" contrast). */
function runningCard() {
  return {
    id: 'cd-running',
    taskKey: `${WATCH_PATH}::cd-running`,
    key: 'CD-RUNNING',
    title: RUNNING_TITLE,
    state: '3-progress',
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/3-progress/cd-running`,
    createdAt: '2026-07-10T09:00:00Z',
    lastActivity: '2026-07-10T11:00:00Z',
    execution: { status: 'running', model: 'claude-opus-4-8', startedAt: '2026-07-10T10:55:00Z', exitCode: null },
    runActivity: { kind: 'active', processId: 4242, attempt: 0 },
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

/**
 * A failed-backoff 3-progress card. `backoffUntil` is injected fresh at
 * route-install time (now + ~210s) so the "in Ns" countdown is live regardless of
 * wall clock; `attempt: 2` renders `retrying 2/3`.
 */
function cooldownCard(backoffUntilIso: string) {
  return {
    id: 'cd-backoff',
    taskKey: `${WATCH_PATH}::cd-backoff`,
    key: 'CD-BACKOFF',
    title: COOLDOWN_TITLE,
    state: '3-progress',
    order: 2,
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-4-8',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/3-progress/cd-backoff`,
    createdAt: '2026-07-10T09:00:00Z',
    lastActivity: '2026-07-10T11:02:00Z',
    // No live run: the run died. buildExecutionBadge/isRunning stay quiet so the
    // ONLY status the card carries is the cooldown banner.
    execution: null,
    runActivity: {
      kind: 'failed-backoff',
      attempt: 2,
      backoffUntil: backoffUntilIso,
      lastError: 'CLI exited before a terminal verdict (exit 1)',
    },
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const backoffUntil = new Date(Date.now() + 210_000).toISOString();
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [],
    progress: [runningCard(), cooldownCard(backoffUntil)],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], completed: [], archive: [],
  };

  // Catch-all first (lowest priority); specific routes registered later win.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-10T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-10T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

/** Strip dev/error overlays so the evidence frame shows only the board. */
async function stripOverlays(page: Page): Promise<void> {
  const errMsg = await page.locator('[data-testid="error-dialog-message"]').first().textContent().catch(() => null);
  if (errMsg && errMsg.trim()) console.log(`[cooldown spec] global error-dialog present (harness noise): ${errMsg.trim().slice(0, 200)}`);
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    document.querySelectorAll('.overlay--error').forEach((n) => ((n as HTMLElement).style.display = 'none'));
    document.querySelectorAll('app-error-dialog').forEach((n) => ((n as HTMLElement).style.display = 'none'));
  });
}

async function gotoBoard(page: Page): Promise<void> {
  await seedBoardTab(page);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title });
}

/**
 * Stitch a before/after composite on an isolated blank page (no live app chrome
 * bleeding into the shot): the same card WITHOUT the banner beside WITH it.
 */
async function captureComposite(
  page: Page,
  testInfo: TestInfo,
  theme: 'dark' | 'light',
  before: Buffer,
  after: Buffer,
): Promise<void> {
  const backdrop = { dark: '#1e1e2e', light: '#eff1f5' } as const;
  const caption = { dark: '#a6adc8', light: '#5c5f77' } as const;
  const b64 = (buf: Buffer) => buf.toString('base64');
  await page.setContent(
    `<!doctype html><html><body style="margin:0">`
    + `<div id="cmp" style="background:${backdrop[theme]};padding:24px;`
    + `display:inline-flex;gap:24px;align-items:flex-start;font-family:system-ui,sans-serif">`
    + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
    + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">Before · failed-backoff card, no distinct banner</figcaption>`
    + `<img alt="before" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(before)}"></figure>`
    + `<figure style="margin:0;display:flex;flex-direction:column;gap:8px">`
    + `<figcaption style="font:600 12px/1.4 system-ui;letter-spacing:.04em;text-transform:uppercase;color:${caption[theme]}">After · CooldownRetry banner (DtC step 6)</figcaption>`
    + `<img alt="after" style="display:block;box-shadow:0 0 0 1px rgba(128,128,128,.25)" src="data:image/png;base64,${b64(after)}"></figure>`
    + `</div></body></html>`,
  );
  await page.waitForTimeout(100);
  const shot = await page.locator('#cmp').screenshot();
  await saveShot(testInfo, `card-cooldown-before-after-${theme}--composite-mocked.png`, shot);
}

test.describe('DtC step 6 — CooldownRetry banner on the progress card', () => {
  test.beforeEach(() => test.setTimeout(90_000));

  test('failed-backoff card shows a distinct CooldownRetry banner; a live card does not', async ({ page }, testInfo) => {
    await gotoBoard(page);

    const running = cardByTitle(page, RUNNING_TITLE);
    const cooldown = cardByTitle(page, COOLDOWN_TITLE);
    await expect(running).toHaveCount(1);
    await expect(cooldown).toHaveCount(1);

    const banner = cooldown.getByTestId('task-card-cooldown-retry');

    // 1. The banner is present ONLY on the failed-backoff card.
    await expect(banner).toHaveCount(1);
    await expect(running.getByTestId('task-card-cooldown-retry')).toHaveCount(0);

    // 2. It reads the DtC infra-retry budget and the live countdown from runActivity.
    await expect(banner).toContainText('infra-crashed');
    await expect(banner).toContainText('retrying 2/3');
    await expect(banner).toHaveAttribute('data-attempt', '2');
    await expect(banner).toContainText(/in \d+s/);

    // 3. The live card carries the normal in-progress presentation (running tint),
    //    proving the cooldown card is the visual exception, not a board-wide state.
    await expect(running).toHaveAttribute('data-running', 'true');
    await expect(cooldown).not.toHaveAttribute('data-running', 'true');

    // Lane evidence: both cards in one frame, per theme.
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await page.waitForTimeout(250);
      await stripOverlays(page);
      await page.setViewportSize({ width: 1440, height: 1000 });
      await cooldown.scrollIntoViewIfNeeded();
      await page.waitForTimeout(150);
      const laneShot = await page.screenshot({ fullPage: false });
      await saveShot(testInfo, `card-cooldown-retry-${theme}--mocked.png`, laneShot);

      // Before/after composite from the SAME card: shoot WITH the banner (after),
      // then remove just the banner element (the pre-feature look) and shoot again.
      const after = await cooldown.screenshot();
      await page.evaluate((title) => {
        const cards = Array.from(document.querySelectorAll('[data-testid="task-card"]'));
        const card = cards.find((c) => c.textContent?.includes(title)) as HTMLElement | undefined;
        card?.querySelector('[data-testid="task-card-cooldown-retry"]')?.remove();
      }, COOLDOWN_TITLE);
      await page.waitForTimeout(80);
      const before = await cooldown.screenshot();
      await captureComposite(page, testInfo, theme, before, after);

      // Restore the banner for the next theme iteration by reloading the board.
      if (theme === 'dark') await gotoBoard(page);
    }
  });
});
