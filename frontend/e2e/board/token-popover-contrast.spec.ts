import { test, expect, type Page } from '@playwright/test';
import { contrastRatio, parseRgb } from '../helpers/contrast';

/**
 * Token-popover WCAG-AA contrast regression.
 *
 * Verifies that every text element inside the job-card token popover
 * meets WCAG-AA contrast ratios against the popover background in both
 * dark and light themes.
 *
 * Thresholds:
 *   - Normal text (< 18pt / < 14pt bold): >= 4.5:1
 *   - Large text (>= 18pt or >= 14pt bold): >= 3:1
 *
 * All popover text is 10-11px, so the 4.5:1 threshold applies everywhere.
 */

const PROJECT = 'fixture-token-popover';
const WATCH_PATH = 'C:/fixtures/token-popover-contrast';
const SHOTS = 'screenshots/token-popover-contrast';
const WCAG_AA = 4.5;

function makeJob() {
  return {
    id: 'token-contrast-job',
    jobKey: `${WATCH_PATH}::token-contrast-job`,
    title: 'Token contrast fixture',
    state: '2-ready',
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-27T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/2-ready/token-contrast-job`,
    lastActivity: '2026-05-27T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    tokenSummary: {
      calls: 3,
      inputTokens: 120_000,
      outputTokens: 18_000,
      cacheReadTokens: 250_000,
      cacheCreationTokens: 12_000,
      totalTokens: 400_000,
      lastModel: 'claude-opus-4-7',
      lastUpdate: '2026-05-27T08:30:00Z',
      entries: [
        { ts: '2026-05-27T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 50_000, outputTokens: 6_000, cacheReadTokens: 100_000, cacheCreationTokens: 4_000 },
        { ts: '2026-05-27T08:15:00Z', model: 'claude-opus-4-7', inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 80_000, cacheCreationTokens: 4_000 },
        { ts: '2026-05-27T08:30:00Z', model: 'claude-opus-4-7', inputTokens: 30_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000 },
      ],
    },
  };
}

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [makeJob()],
  progress: [],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: [],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-27T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-27T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
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

/**
 * Alpha-composite a semi-transparent foreground onto an opaque backdrop.
 */
function compositeOnto(fg: string, bg: string): string {
  const f = parseRgb(fg);
  const b = parseRgb(bg);
  const r = Math.round(f[0] * f[3] + b[0] * (1 - f[3]));
  const g = Math.round(f[1] * f[3] + b[1] * (1 - f[3]));
  const bl = Math.round(f[2] * f[3] + b[2] * (1 - f[3]));
  return `rgb(${r}, ${g}, ${bl})`;
}

test.describe('Token popover WCAG-AA contrast', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`all popover text clears WCAG-AA in ${theme} theme`, async ({ page }) => {
      await seedBoardTab(page);
      await installRoutes(page);
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await setTheme(page, theme);
      await page.waitForTimeout(200);

      const card = page.locator('[data-testid="job-card"]').first();
      await expect(card).toBeVisible({ timeout: 10_000 });

      const bubble = card.locator('[data-testid="job-card-token-bubble"]');
      await expect(bubble).toBeVisible({ timeout: 5_000 });

      // Open the popover via focus (keyboard-accessible path).
      await bubble.focus();
      const popover = card.locator('[data-testid="job-card-token-popover"]');
      await expect(popover).toBeVisible({ timeout: 3_000 });
      await page.waitForTimeout(100);

      // Sample the popover background. It may be semi-transparent, so
      // we composite it onto a black page backdrop (worst case for dark
      // theme, generous for light since the page bg is lighter).
      const popoverBg = await popover.evaluate((el) => getComputedStyle(el).backgroundColor);
      const pageBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
      const effectiveBg = compositeOnto(popoverBg, pageBg);

      // Sample representative text elements.
      const titleEl = popover.locator('.job-card__token-popover-title');
      const thEl = popover.locator('.job-card__token-table th').first();
      const tdEl = popover.locator('.job-card__token-table td').first();
      const linkEl = popover.locator('.job-card__token-link');

      const titleColor = await titleEl.evaluate((el) => getComputedStyle(el).color);
      const thColor = await thEl.evaluate((el) => getComputedStyle(el).color);
      const tdColor = await tdEl.evaluate((el) => getComputedStyle(el).color);
      const linkColor = await linkEl.evaluate((el) => getComputedStyle(el).color);

      // Per-run table rows (if the per-run section is rendered).
      const runsTitle = popover.locator('.job-card__token-runs-title');
      let runsTitleColor: string | undefined;
      if (await runsTitle.isVisible().catch(() => false)) {
        runsTitleColor = await runsTitle.evaluate((el) => getComputedStyle(el).color);
      }
      const runsTd = popover.locator('.job-card__token-table--runs td').first();
      let runsTdColor: string | undefined;
      if (await runsTd.isVisible().catch(() => false)) {
        runsTdColor = await runsTd.evaluate((el) => getComputedStyle(el).color);
      }

      // Screenshot evidence.
      await page.screenshot({ path: `${SHOTS}/${theme}-popover.png`, fullPage: false });

      // Assert WCAG-AA (4.5:1) for all text elements.
      const checks = [
        { name: 'title', color: titleColor },
        { name: 'label (th)', color: thColor },
        { name: 'value (td)', color: tdColor },
        { name: 'link', color: linkColor },
      ];
      if (runsTitleColor) checks.push({ name: 'per-run title', color: runsTitleColor });
      if (runsTdColor) checks.push({ name: 'per-run value', color: runsTdColor });

      for (const { name, color } of checks) {
        const ratio = contrastRatio(color, effectiveBg);
        expect(ratio, `${theme} ${name}: ${color} on ${effectiveBg} = ${ratio.toFixed(2)}:1 (need ${WCAG_AA}:1)`).toBeGreaterThanOrEqual(WCAG_AA);
      }
    });
  }
});
