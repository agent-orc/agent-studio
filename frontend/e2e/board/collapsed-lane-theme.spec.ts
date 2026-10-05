import { test, expect, type Page } from '@playwright/test';
import { contrastRatio, parseRgb } from '../helpers/contrast';

/**
 * Collapsed-lane rail + tooltip theme regression.
 *
 * Locks two contracts:
 *  1. The collapsed rail's background is theme-aware (not hardcoded #000
 *     or a dark-only literal). Both dark and light themes must use the
 *     same surface token family as the expanded column.
 *  2. The rail tooltip (rendered by TooltipController) picks up theme
 *     tokens so it reads as a cohesive popover, not a dark slab on a
 *     light shell.
 *  3. WCAG-AA contrast for the rail's label, count badge, and expand
 *     caret in both themes.
 */

/**
 * Alpha-composite a semi-transparent foreground colour onto an opaque
 * backdrop and return the resulting opaque `rgb(...)` string. Used when a
 * badge/chip background is `rgba(...)` layered on the rail's solid surface.
 */
function compositeOnto(fg: string, bg: string): string {
  const f = parseRgb(fg);
  const b = parseRgb(bg);
  const r = Math.round(f[0] * f[3] + b[0] * (1 - f[3]));
  const g = Math.round(f[1] * f[3] + b[1] * (1 - f[3]));
  const bl = Math.round(f[2] * f[3] + b[2] * (1 - f[3]));
  return `rgb(${r}, ${g}, ${bl})`;
}

const PROJECT = 'fixture-collapsed-lane';
const WATCH_PATH = 'C:/fixtures/collapsed-lane-theme';

function makeJob(id: string, state: string, order: number) {
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `Job ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-26T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-26T09:00:00Z',
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
  };
}

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [makeJob('ready-1', '2-ready', 1)],
  progress: [makeJob('prog-1', '3-progress', 1), makeJob('prog-2', '3-progress', 2)],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: [makeJob('hr-1', '5-human-review', 1)],
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
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-26T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-26T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
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
    localStorage.removeItem('collapsedLanes');
  });
}

async function waitForBoard(page: Page): Promise<void> {
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 10_000 });
  await expect(page.locator('[data-testid="job-card"]').first()).toBeVisible({ timeout: 10_000 });
}

test.describe('Collapsed lane-rail theme regression', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`rail background is not literal black and text clears WCAG-AA (${theme})`, async ({ page }, testInfo) => {
      await seedBoardTab(page);
      await installRoutes(page);
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await waitForBoard(page);
      await setTheme(page, theme);
      await page.waitForTimeout(200);

      const collapseBtn = page.getByTestId('lane-collapse-3-progress');
      await expect(collapseBtn).toBeVisible({ timeout: 3_000 });

      // Collapse the lane.
      await collapseBtn.click();
      const rail = page.getByTestId('lane-rail-3-progress');
      await expect(rail).toBeVisible({ timeout: 1_000 });
      await page.waitForTimeout(100);

      // Sample the rail's effective background.
      const railBg = await rail.evaluate((el) => getComputedStyle(el).backgroundColor);

      // The rail background must not be literal black.
      expect(
        railBg,
        `[${theme}] rail background must not be literal black`,
      ).not.toBe('rgb(0, 0, 0)');

      // Rail title (vertical text).
      const railTitle = rail.locator('.column-rail__title');
      const titleFg = await railTitle.evaluate((el) => getComputedStyle(el).color);
      const titleRatio = contrastRatio(titleFg, railBg);
      expect(
        titleRatio,
        `[${theme}] rail title contrast ${titleRatio.toFixed(2)} (${titleFg} on ${railBg})`,
      ).toBeGreaterThanOrEqual(4.5);

      // Rail count badge — its own bg is semi-transparent (tinted chip
      // on the rail surface). Composite it onto the rail's opaque bg
      // before computing the text-on-badge contrast.
      const railCount = rail.locator('.column-rail__count');
      const countStyles = await railCount.evaluate((el) => {
        const cs = getComputedStyle(el);
        return { fg: cs.color, bg: cs.backgroundColor };
      });
      const countEffBg = compositeOnto(countStyles.bg, railBg);
      const countRatio = contrastRatio(countStyles.fg, countEffBg);
      expect(
        countRatio,
        `[${theme}] rail count contrast ${countRatio.toFixed(2)} (${countStyles.fg} on ${countEffBg})`,
      ).toBeGreaterThanOrEqual(3);

      // Rail expand caret.
      const railExpand = rail.locator('.column-rail__expand');
      const expandFg = await railExpand.evaluate((el) => getComputedStyle(el).color);
      const expandRatio = contrastRatio(expandFg, railBg);
      expect(
        expandRatio,
        `[${theme}] rail caret contrast ${expandRatio.toFixed(2)} (${expandFg} on ${railBg})`,
      ).toBeGreaterThanOrEqual(3);

      const screenshot = await page.screenshot({ fullPage: false });
      await testInfo.attach(`collapsed-rail-${theme}.png`, {
        body: screenshot,
        contentType: 'image/png',
      });
      if (process.env.RESULTS_DIR) {
        const fs = await import('fs');
        const path = await import('path');
        fs.writeFileSync(path.join(process.env.RESULTS_DIR, `collapsed-rail-${theme}.png`), screenshot);
      }

      // Re-expand and verify the lane comes back.
      await rail.click();
      await expect(page.getByTestId('lane-3-progress')).toBeVisible({ timeout: 1_000 });
    });

    test(`tooltip on collapsed rail uses theme tokens (${theme})`, async ({ page }, testInfo) => {
      await seedBoardTab(page);
      await installRoutes(page);
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await waitForBoard(page);
      await setTheme(page, theme);
      await page.waitForTimeout(200);

      // Collapse the In Progress lane.
      const collapseBtn = page.getByTestId('lane-collapse-3-progress');
      await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
      await collapseBtn.click();
      const rail = page.getByTestId('lane-rail-3-progress');
      await expect(rail).toBeVisible({ timeout: 1_000 });

      // Hover the rail to trigger the tooltip.
      await rail.hover();
      const tooltip = page.getByTestId('cac-tooltip');
      await expect(tooltip).toBeVisible({ timeout: 2_000 });
      await page.waitForTimeout(100);

      const tooltipStyles = await tooltip.evaluate((el) => {
        const cs = getComputedStyle(el);
        return { bg: cs.backgroundColor, fg: cs.color };
      });

      // Tooltip background must not be literal black.
      expect(
        tooltipStyles.bg,
        `[${theme}] tooltip background must not be literal black`,
      ).not.toBe('rgb(0, 0, 0)');

      // On light theme, the tooltip background should be light (luminance > 0.5).
      if (theme === 'light') {
        const { parseRgb, luminance } = await import('../helpers/contrast');
        const bgRgba = parseRgb(tooltipStyles.bg);
        const bgLum = luminance([bgRgba[0], bgRgba[1], bgRgba[2]]);
        expect(
          bgLum,
          `[light] tooltip bg luminance ${bgLum.toFixed(3)} should be > 0.5 for a light-theme popover`,
        ).toBeGreaterThan(0.5);
      }

      // Tooltip text contrast.
      const ratio = contrastRatio(tooltipStyles.fg, tooltipStyles.bg);
      expect(
        ratio,
        `[${theme}] tooltip text contrast ${ratio.toFixed(2)} (${tooltipStyles.fg} on ${tooltipStyles.bg})`,
      ).toBeGreaterThanOrEqual(4.5);

      const tooltipScreenshot = await page.screenshot({ fullPage: false });
      await testInfo.attach(`collapsed-rail-tooltip-${theme}.png`, {
        body: tooltipScreenshot,
        contentType: 'image/png',
      });
      if (process.env.RESULTS_DIR) {
        const fs = await import('fs');
        const path = await import('path');
        fs.writeFileSync(path.join(process.env.RESULTS_DIR, `collapsed-rail-tooltip-${theme}.png`), tooltipScreenshot);
      }
    });
  }
});
