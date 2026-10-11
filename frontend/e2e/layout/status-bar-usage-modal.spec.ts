import { test, expect } from '@playwright/test';
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
    await page.route('**/api/v1/studio/runner/status', route => route.fulfill({ json: { projects: {} } }));
    await page.route('**/api/watch-paths', route => route.fulfill({ json: [] }));
    await page.route('**/api/v1/studio/board**', route => route.fulfill({ json: {
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
      codeNotComplete: [], autoReview: [], humanReview: [], escalated: [], review: [],
      completed: [], archive: [], gitStateAt: null, stale: false,
    } }));

    await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
      profile: 'local', bootstrapRequired: false, authenticated: true,
      user: { id: 'fixture', username: 'fixture', displayName: 'Fixture', role: 'owner',
        projects: [], disabled: false, mustChangePassword: false },
    } }));
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
        estimatedApiCostUsd: 10,
        allModelsPriced: false,
        byModel: [
          {
            model: 'gpt-5.6-sol', modelId: 'gpt-5.6-sol', cli: 'codex', calls: 5,
            inputTokens: 39_646_031, outputTokens: 97_412,
            cacheReadTokens: 38_481_408, cacheCreationTokens: 0,
            estimatedApiCostUsd: 10, modelPriced: true,
          },
          {
            model: 'GPT-5.5', modelId: 'gpt-5.5', cli: 'codex', calls: 8,
            inputTokens: 10_782_081, outputTokens: 66_760,
            cacheReadTokens: 10_022_528, cacheCreationTokens: 0,
            estimatedApiCostUsd: 0, modelPriced: false,
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
            model: 'gpt-5-codex', modelId: 'gpt-5-codex', cli: 'codex', calls: 4,
            inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
            estimatedApiCostUsd: 0, modelPriced: true,
          },
          {
            model: 'gpt-5.6-sol', modelId: 'gpt-5.6-sol', cli: 'codex', calls: 7,
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

    await page.route('**/api/token-pricing/calculate', async route => {
      const request = route.request().postDataJSON() as { items: Array<Record<string, unknown>> };
      await route.fulfill({ json: {
        provider: 'TokenEconomy',
        items: request.items.map(item => ({
          ...item, calculatedAt: new Date().toISOString(),
          estimate: {
            inputUsd: 2, outputUsd: 1, cacheReadUsd: 0.5, cacheWriteUsd: 0,
            total: 3.5, modelId: item.model, modelKnown: true, status: 'Resolved',
            priceBasis: {
              inputPerMillion: 2, outputPerMillion: 10, cacheReadPerMillion: 0.2,
              cacheWritePerMillion: 2, currency: 'USD', validFrom: '2026-01-01T00:00:00Z',
              source: 'Fixture price', note: null, unconfirmed: false,
            },
          },
        })),
      } });
    });

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
    await modal.getByTestId('cli-usage-modal-total-cost').click();
    const costDialog = page.getByTestId('cost-breakdown-dialog');
    await expect(costDialog).toBeVisible();
    await expect(costDialog).toContainText("today's price");
    await expect(costDialog.getByTestId('cost-breakdown-model').first()).toContainText('gpt-5.6-sol');
    await setTheme(page, 'light');
    await costDialog.screenshot({ path: `${SCREENSHOT_DIR}/cli-recorded-cost-dialog-light.png` });
    await setTheme(page, 'dark');
    await costDialog.screenshot({ path: `${SCREENSHOT_DIR}/cli-recorded-cost-dialog-dark.png` });
    const recordedTotal = costDialog.getByTestId('cost-breakdown-recorded-total');
    await expect(recordedTotal).toContainText('$10.00');
    await expect(costDialog.getByTestId('cost-breakdown-total')).toContainText('$7.00');
    await recordedTotal.scrollIntoViewIfNeeded();
    await setTheme(page, 'light');
    await costDialog.screenshot({ path: `${SCREENSHOT_DIR}/cli-recorded-cost-total-light.png` });
    await setTheme(page, 'dark');
    await costDialog.screenshot({ path: `${SCREENSHOT_DIR}/cli-recorded-cost-total-dark.png` });


  });
});
