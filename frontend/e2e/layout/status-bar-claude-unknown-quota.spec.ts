import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { setTheme, type Theme } from '../helpers/theme';

/**
 * Claude Code 2.1.202 can expose the tabbed /usage screen without numeric
 * subscription utilization (API Usage Billing). The backend represents that
 * recognized shape as a null-valued "Quota" window. The Studio must keep the
 * Claude card visible and say "Unknown", never drop the card or show an error.
 */

const SHOT_DIR = process.env.JOB_RESULTS_DIR?.trim() || 'test-results';
const THEMES: readonly Theme[] = ['dark', 'light'];

function unknownClaudeQuotaReport() {
  return {
    at: new Date().toISOString(),
    ttlSeconds: 600,
    snapshots: [
      {
        cliType: 'claude',
        fetchedAt: new Date().toISOString(),
        plan: null,
        windows: [
          { label: 'Quota', usedPct: null, used: null, limit: null, unit: '%', resetAt: null, resetLabel: null },
        ],
        source: '/usage',
        rawSample: 'ClaudeCodev2.1.202 SettingsStatusConfigUsageStats Session Totalcost:$0.0000',
        error: null,
      },
    ],
  };
}

test.describe('Status bar quota: Claude 2.1.202 unknown utilization', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.route('**/api/v1/studio/auth/status', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          profile: 'networked',
          bootstrapRequired: false,
          authenticated: true,
          user: {
            id: 'usr_owner',
            username: 'owner',
            displayName: 'Owner',
            role: 'owner',
            projects: [],
            disabled: false,
            mustChangePassword: false,
          },
        }),
      });
    });
    await page.route('**/api/cli/quota', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(unknownClaudeQuotaReport()),
      });
    });
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
  });

  test('keeps the Claude quota visible as Unknown in both themes', async ({ page }) => {
    const card = page.getByTestId('hquota-card-claude');
    const quota = page.getByTestId('hquota-claude-quota');

    await expect(card).toBeVisible();
    await expect(quota).toBeVisible();
    await expect(quota).toContainText('Unknown');
    await expect(card).toHaveAttribute('aria-label', /Claude quota: Quota Unknown/);
    await expect(card).not.toHaveAttribute('data-state', 'error');

    for (const theme of THEMES) {
      await setTheme(page, theme);
      await card.screenshot({
        path: `${SHOT_DIR}/claude-quota-unknown--${theme}--playwright.png`,
      });
    }
  });
});
