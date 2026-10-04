import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { setTheme, dismissDevErrorDialog, sampleColours } from '../helpers/theme';

/**
 * CLI-Management / "Usage caps" settings panel — light + dark legibility.
 *
 * The panel ( <app-cli-admin-panel> ) lives as the "Usage caps" section of the
 * global Workspace-settings home. Its caps section paints a plan chip, a
 * per-window slider, used%/cap% values and a usage bar whose fill/marker carry
 * severity pigments. Those colours were tuned for the dark Mocha base; this
 * spec is the regression guard that every text run keeps WCAG-AA contrast on
 * BOTH themes against whatever surface paints behind it.
 *
 * Runs against a clean dev frontend with every backend route stubbed (no
 * backend required) so the render is deterministic regardless of cached quota.
 */

const SHOT_DIR = process.env.CAPS_SHOT_DIR ?? 'test-results';

function buildQuotaReport() {
  const now = new Date().toISOString();
  return {
    at: now,
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'claude',
        fetchedAt: now,
        plan: 'Max 20x',
        source: 'probe',
        rawSample: null,
        error: null,
        windows: [
          { label: '5h session', usedPct: 42, used: 42, limit: 100, unit: '%', resetAt: null, resetLabel: '3h 12m' },
          // Over the default 95% cap → exercises the blocked text + over-cap bar fill.
          { label: 'Weekly', usedPct: 97, used: 97, limit: 100, unit: '%', resetAt: null, resetLabel: '4d 6h' },
        ],
      },
      {
        cliType: 'codex',
        fetchedAt: now,
        plan: 'Plus',
        source: 'probe',
        rawSample: null,
        error: null,
        windows: [
          { label: '5h session', usedPct: 12, used: 12, limit: 100, unit: '%', resetAt: null, resetLabel: '1h 40m' },
        ],
      },
    ],
  };
}

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/v1/studio/board*', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/v1/studio/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/token-summary-aggregate*', json({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  await page.route('**/api/cli/quota/caps', json({ defaultCapPct: 95, caps: {} }));
  await page.route('**/api/cli/quota', json(buildQuotaReport()));
  await page.route('**/api/cli/usage', json({ sections: [] }));
  await page.route('**/api/adhoc-usage*', json({ entries: [] }));
  await page.route('**/api/workspace/tokens/timeline*', json({
    windowStart: new Date(Date.now() - 24 * 3600 * 1000).toISOString(),
    windowEnd: new Date().toISOString(),
    windowHours: 24, bucketMinutes: 60, bucketCount: 0,
    cells: [], projects: [], fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/clients', json([]));
}

async function openCapsSection(page: Page) {
  await page.getByTestId('status-bar-settings').click();
  await expect(page.locator(
    '[data-testid="workspace-settings-inline"], [data-testid="workspace-settings-overlay"]',
  )).toBeVisible({ timeout: 5_000 });
  await page.getByTestId('workspace-settings-rail-caps').click();
  await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
  await expect(page.getByTestId('cli-admin-panel')).toBeVisible({ timeout: 5_000 });
  // Caps rows depend on the stubbed quota snapshot; wait for the first card.
  await expect(page.locator('[data-testid="cli-admin-overlay"] .cli-card').first()).toBeVisible({ timeout: 5_000 });
}

test.describe('Usage-caps panel legibility', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 980 });
    await stubBackgroundApis(page);
  });

  test('caps section renders plan chip, slider, used/cap values and usage bar', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await dismissDevErrorDialog(page);
    await openCapsSection(page);

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay.getByRole('heading', { name: 'CLI Management' })).toBeVisible();
    await expect(overlay.getByText('Usage caps', { exact: true })).toBeVisible();
    await expect(overlay.locator('.cli-card__plan').first()).toHaveText('Max 20x');
    await expect(overlay.locator('input[type="range"]').first()).toBeVisible();
    await expect(overlay.locator('.cap-row__bar-fill').first()).toBeVisible();
    // The over-cap window flags the used value as blocked.
    await expect(overlay.locator('.cap-row__used--blocked').first()).toBeVisible();
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`caps text stays legible (${theme} theme)`, async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await openCapsSection(page);

      const scope = '[data-testid="cli-admin-overlay"] ';
      const samples: { what: string; selector: string; min: number }[] = [
        { what: 'panel title', selector: `${scope}.cli-admin__title`, min: 4.5 },
        { what: 'panel subtitle', selector: `${scope}.cli-admin__subtitle`, min: 4.5 },
        { what: 'section heading', selector: `${scope}.cli-admin__section-head h3`, min: 4.5 },
        { what: 'section hint', selector: `${scope}.cli-admin__hint`, min: 4.5 },
        { what: 'cli name', selector: `${scope}.cli-card__name`, min: 4.5 },
        { what: 'plan chip', selector: `${scope}.cli-card__plan`, min: 4.5 },
        { what: 'window label', selector: `${scope}.cap-row__label`, min: 4.5 },
        { what: 'used value', selector: `${scope}.cap-row__used strong`, min: 4.5 },
        { what: 'cap value', selector: `${scope}.cap-row__cap strong`, min: 4.5 },
        { what: 'blocked used value', selector: `${scope}.cap-row__used--blocked strong`, min: 4.5 },
      ];

      const failures: string[] = [];
      for (const { what, selector, min } of samples) {
        const { color, bg } = await sampleColours(page, selector);
        const ratio = contrastRatio(color, bg);
        if (ratio < min) {
          failures.push(`${what} contrast ${ratio.toFixed(2)} (${color} on ${bg}) [${theme}]`);
        }
      }
      expect(failures, failures.join('\n')).toEqual([]);

      await page.screenshot({
        path: join(SHOT_DIR, `cli-admin-caps--mocked-${theme}.png`),
        fullPage: false,
      });
    });
  }
});
