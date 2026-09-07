import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import { setTheme } from '../helpers/theme';

/**
 * The bottom status-bar's quota strip now follows a single model:
 *
 * - There is NO hover tooltip / hover popover any more. Hovering a CLI
 *   card does nothing but a subtle highlight.
 * - CLICKING a CLI card opens THAT CLI's own usage-detail modal
 *   (`<app-cli-usage-modal>`, testid `cli-usage-modal-<cli>`). One modal
 *   per CLI — never a grouped multi-CLI view.
 * - The modal lists every quota window the probe reported (so Claude /
 *   Codex show both their 5h and weekly windows) plus that CLI's top
 *   models, and its "Manage usage caps" footer drops into the full
 *   CLI-Management panel where caps are edited.
 *
 * This spec asserts:
 * - The old hover popover is gone (never appears, opens no modal).
 * - Clicking a card opens only that CLI's modal.
 * - Escape and backdrop-click both close the modal.
 * - "Manage usage caps" opens the CLI-Management overlay.
 *
 * Plus screenshots so the visual change is reviewable in chat.
 */

const SCREENSHOT_DIR = process.env.STATUS_BAR_RESULTS_DIR?.trim() || 'test-results';
const CLIS = ['copilot', 'claude', 'codex'] as const;

test.describe('Status bar usage modal', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.route('**/api/crash-recovery/pending**', route => route.fulfill({ json: { pending: [] } }));
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    // Let the first quota poll fire so the strip has cards to render.
    await page.waitForTimeout(800);
  });

  test('the strip has no hover popover / hover tooltip', async ({ page }) => {
    const strip = page.getByTestId('usage-hover-panel');
    await expect(strip).toBeVisible();
    await strip.scrollIntoViewIfNeeded();
    await strip.hover();
    await page.waitForTimeout(400);

    // The old hover popover and mini-popover are gone for good.
    await expect(page.getByTestId('usage-hover-panel-pop')).toHaveCount(0);
    await expect(page.getByTestId('cli-usage-mini-popover')).toHaveCount(0);
    // Hovering must not open any modal either.
    await expect(page.locator('[data-testid^="cli-usage-modal-"]')).toHaveCount(0);
  });

  test('both 5h and weekly windows show on Claude / Codex cards', async ({ page }) => {
    // Requirement #2: every reported window is surfaced in the strip, so
    // Claude and Codex carry both a 5h and a weekly chip. In CI with no
    // sampled quota the cards fall back to a single placeholder chip, so
    // only assert the structure when real data is present.
    for (const cli of ['claude', 'codex'] as const) {
      const fiveH = page.getByTestId(`hquota-${cli}-5h`);
      const weekly = page.getByTestId(`hquota-${cli}-wk`);
      if ((await fiveH.count()) > 0) {
        await expect(fiveH).toBeVisible();
        await expect(weekly).toBeVisible();
      }
    }

    // Close-up of the strip so the two-chip layout is reviewable in chat.
    const strip = page.getByTestId('usage-hover-panel');
    await strip.scrollIntoViewIfNeeded();
    await strip.screenshot({ path: `${SCREENSHOT_DIR}/status-bar-strip-windows.png` });
  });

  test("clicking a CLI card opens that CLI's own modal (and only that CLI)", async ({ page }) => {
    const claudeCard = page.getByTestId('hquota-card-claude');
    await expect(claudeCard).toBeVisible();
    await claudeCard.scrollIntoViewIfNeeded();
    await claudeCard.click();

    const modal = page.getByTestId('cli-usage-modal-claude');
    await expect(modal).toBeVisible({ timeout: 4_000 });

    // One modal per CLI: the codex / copilot modals must NOT be present,
    // and the grouped multi-CLI detail surface is not in the click flow.
    await expect(page.getByTestId('cli-usage-modal-codex')).toHaveCount(0);
    await expect(page.getByTestId('cli-usage-modal-copilot')).toHaveCount(0);
    await expect(page.getByTestId('cli-usage-detail')).toHaveCount(0);

    await page.screenshot({
      path: `${SCREENSHOT_DIR}/status-bar-cli-modal-claude.png`,
      fullPage: false,
    });

    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0, { timeout: 2_000 });
  });

  test('each CLI card opens its matching modal; backdrop closes it', async ({ page }) => {
    for (const cli of CLIS) {
      const card = page.getByTestId(`hquota-card-${cli}`);
      await expect(card).toBeVisible();
      await card.click();

      const modal = page.getByTestId(`cli-usage-modal-${cli}`);
      await expect(modal).toBeVisible({ timeout: 4_000 });

      // Backdrop click (top-left corner, clear of the centred panel) closes.
      await page.getByTestId(`cli-usage-modal-${cli}-overlay`).click({ position: { x: 6, y: 6 } });
      await expect(modal).toHaveCount(0, { timeout: 2_000 });
    }
  });

  test('the modal\'s "Manage usage caps" opens the CLI-Management panel', async ({ page }) => {
    await page.getByTestId('hquota-card-claude').click();
    await expect(page.getByTestId('cli-usage-modal-claude')).toBeVisible({ timeout: 4_000 });

    await page.getByTestId('cli-usage-modal-manage-caps').click();
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('cli-usage-detail')).toBeVisible();
  });

  test('Codex distinguishes used quota from lifetime usage without double-counting cache', async ({ page }) => {
    await page.route('**/api/cli/quota**', async route => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({
        json: {
          at: new Date().toISOString(),
          ttlSeconds: 600,
          snapshots: [{
            cliType: 'codex',
            fetchedAt: new Date().toISOString(),
            plan: 'Pro',
            source: '/status',
            error: null,
            windows: [
              { label: '5-hour', usedPct: 3, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '01:49 on 11 Jul' },
              { label: 'Weekly', usedPct: 0, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '20:49 on 17 Jul' },
            ],
          }],
        },
      });
    });
    await page.route('**/api/runner/token-summary-aggregate**', route => route.fulfill({
      json: {
        projects: 11,
        orchestratorEntries: 13,
        orchestratorLlmCalls: 13,
        totalInputTokens: 50_428_112,
        totalOutputTokens: 164_172,
        totalCacheReadTokens: 48_503_936,
        totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 10.811829,
        allModelsPriced: false,
        byModel: [
          {
            model: 'gpt-5.6-sol', calls: 5,
            inputTokens: 39_646_031, outputTokens: 97_412,
            cacheReadTokens: 38_481_408, cacheCreationTokens: 0,
            estimatedApiCostUsd: 0, modelPriced: false,
          },
          {
            model: 'GPT-5.5', modelId: 'gpt-5.5', calls: 8,
            inputTokens: 10_782_081, outputTokens: 66_760,
            cacheReadTokens: 10_022_528, cacheCreationTokens: 0,
            estimatedApiCostUsd: 10.811829, modelPriced: true,
          },
        ],
        byProject: [],
        fetchedAt: new Date().toISOString(),
        disclaimer: '',
      },
    }));
    await page.route('**/api/adhoc-usage/**', route => route.fulfill({
      json: {
        calls: 11,
        inputTokens: 0,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        estimatedApiCostUsd: 0,
        allModelsPriced: false,
        bySource: [],
        byDay: [],
        byModel: [
          {
            model: 'gpt-5-codex', calls: 4,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            estimatedApiCostUsd: 0, modelPriced: true,
          },
          {
            model: 'gpt-5.6-sol', calls: 7,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            estimatedApiCostUsd: 0, modelPriced: false,
          },
        ],
        logPath: '(bus)',
        logSizeBytes: 0,
        logModifiedAt: null,
        disclaimer: '',
      },
    }));

    await page.reload();
    await page.getByTestId('hquota-card-codex').click();

    const modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    await expect(modal.getByText('3% used')).toBeVisible();
    await expect(modal.getByText('97% left')).toBeVisible();
    await expect(modal.getByText('Lifetime telemetry by model. Independent of the active quota windows above.')).toBeVisible();
    await expect(modal.getByTestId('cli-usage-modal-models').locator('tbody tr')).toHaveCount(2);
    await expect(modal.getByText('PROJECT RUNTIME')).toHaveCount(2);
    await expect(modal.getByText('AD-HOC')).toHaveCount(0);
    await expect(modal.getByText('50.6M')).toHaveCount(2);

    await setTheme(page, 'light');
    await modal.screenshot({ path: `${SCREENSHOT_DIR}/status-bar-cli-modal-codex-corrected-light.png` });
    await setTheme(page, 'dark');
    await modal.screenshot({ path: `${SCREENSHOT_DIR}/status-bar-cli-modal-codex-corrected-dark.png` });
  });

  test('groups low-share models and keeps partial totals readable and remembered', async ({ page, devBackend }) => {
    void devBackend;
    await page.evaluate(() => localStorage.removeItem('atp.tokens.modelUsage.v1'));
    await page.route('**/api/cli/quota**', async route => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({
        json: {
          at: new Date().toISOString(),
          ttlSeconds: 600,
          snapshots: [{
            cliType: 'codex', fetchedAt: new Date().toISOString(), plan: 'Pro', source: '/status', error: null,
            windows: [
              { label: '5-hour', usedPct: 3, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '01:49' },
              { label: 'Weekly', usedPct: 9, used: null, limit: null, unit: '%', resetAt: null, resetLabel: '4d' },
            ],
          }],
        },
      });
    });
    await page.route('**/api/runner/token-summary-aggregate**', route => route.fulfill({
      json: {
        projects: 1, orchestratorEntries: 3, orchestratorLlmCalls: 3,
        totalInputTokens: 9_139_900_000, totalOutputTokens: 1_800_000_000,
        totalCacheReadTokens: 8_823_111_111, totalCacheCreationTokens: 0,
        estimatedApiCostUsd: 59_996.74, allModelsPriced: false,
        unknownModelCount: 1, unpricedModelCount: 1,
        byModel: [
          {
            model: 'GPT-5.5', modelId: 'gpt-5.5', calls: 1,
            inputTokens: 9_138_800_000, outputTokens: 1_800_000_000,
            cacheReadTokens: 8_823_111_111, cacheCreationTokens: 0,
            estimatedApiCostUsd: 59_990, modelPriced: true,
          },
          {
            model: 'gpt-small', calls: 1,
            inputTokens: 600_000, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            estimatedApiCostUsd: 4.24, modelPriced: true,
          },
          {
            model: 'GPT-TINY', calls: 1,
            inputTokens: 500_000, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            estimatedApiCostUsd: 2.5, modelPriced: false,
          },
        ],
        byProject: [], fetchedAt: new Date().toISOString(), disclaimer: '',
      },
    }));
    await page.route('**/api/adhoc-usage/**', route => route.fulfill({
      json: {
        calls: 1, inputTokens: 100_000, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
        estimatedApiCostUsd: 0, allModelsPriced: false, bySource: [], byDay: [],
        byModel: [{
          model: 'legacy label', modelId: 'gpt-tiny', calls: 1,
          inputTokens: 100_000, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
          estimatedApiCostUsd: 0, modelPriced: false,
        }],
        logPath: '(bus)', logSizeBytes: 0, logModifiedAt: null, disclaimer: '',
      },
    }));

    await page.reload();
    await page.getByTestId('hquota-card-codex').click();
    let modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal).toBeVisible();
    await expect(modal.getByTestId('cli-usage-total-cost')).toHaveText('$59,996.74 + 1 unpriced');
    await expect(modal.getByTestId('cli-usage-footer-cost')).toHaveText('$59,996.74 + 1 unpriced');
    await expect(modal).toContainText('10,940.0M');
    await expect(modal.getByText('gpt-5.5', { exact: true })).toBeVisible();
    await expect(modal.getByTestId('cli-usage-other-summary')).toContainText('Other (2 models)');
    await expect(modal.getByTestId('cli-usage-other-summary')).toContainText('$6.74 + 1 unpriced');
    await expect(modal.getByTestId('cli-usage-group-threshold')).toHaveValue('1');

    await setTheme(page, 'light');
    await modal.screenshot({ path: `${SCREENSHOT_DIR}/agt-2752-recorded-model-usage-collapsed-light--mocked.png` });
    await setTheme(page, 'dark');
    await modal.screenshot({ path: `${SCREENSHOT_DIR}/agt-2752-recorded-model-usage-collapsed-dark--mocked.png` });

    await modal.getByTestId('cli-usage-other-toggle').click();
    await expect(modal.getByTestId('cli-usage-other-toggle')).toHaveAttribute('aria-expanded', 'true');
    await expect(modal.getByTestId('cli-usage-other-model-rows').getByTestId('cli-usage-model-row')).toHaveCount(3);
    await expect(modal.getByText('gpt-tiny', { exact: true })).toHaveCount(2);
    await setTheme(page, 'light');
    await modal.screenshot({ path: `${SCREENSHOT_DIR}/agt-2752-recorded-model-usage-expanded-light--mocked.png` });
    await setTheme(page, 'dark');
    await modal.screenshot({ path: `${SCREENSHOT_DIR}/agt-2752-recorded-model-usage-expanded-dark--mocked.png` });

    const threshold = modal.getByTestId('cli-usage-group-threshold');
    await threshold.fill('2');
    await threshold.dispatchEvent('change');
    await page.keyboard.press('Escape');
    await page.getByTestId('hquota-card-codex').click();
    modal = page.getByTestId('cli-usage-modal-codex');
    await expect(modal.getByTestId('cli-usage-group-threshold')).toHaveValue('2');
    await expect(modal.getByTestId('cli-usage-other-toggle')).toHaveAttribute('aria-expanded', 'true');
  });
});
