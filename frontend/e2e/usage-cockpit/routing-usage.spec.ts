import { test, expect } from '@playwright/test';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { SNAPSHOT, FIXTURE_NOW } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

for (const theme of ['light', 'dark'] as const) {
  for (const width of [1440, 390]) {
    test(`provider switch usage and reserve in ${theme} at ${width}`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.clock.setFixedTime(FIXTURE_NOW);
      await page.setViewportSize({ width, height: 1000 });
      await page.route('**/api/**', route => route.fulfill({ json: [] }));
      await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
        profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
      } }));
      await page.route('**/api/v1/studio/board**', route => route.fulfill({ json: {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
        review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
      } }));
      await page.route('**/api/v1/studio/runner/status', route => route.fulfill({ json: { projects: {} } }));
      await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: {
        ...structuredClone(SNAPSHOT), shortStepReservePoints: 10,
        routingUsage: [{ provider: 'claude', executions: 10, switchExecutions: 3 },
          { provider: 'codex', executions: 8, switchExecutions: 5 }],
      } }));
      await page.goto('/');
      await dismissDevErrorDialog(page);
      await setTheme(page, theme);
      await page.getByTestId('footer-usage-cost').click();
      await expect(page.getByTestId('routing-short-step-reserve')).toContainText('10 percentage points');
      await expect(page.getByTestId('routing-usage-claude')).toContainText('3 of 10 model calls');
      await expect(page.getByTestId('routing-usage-codex')).toContainText('5 of 8 model calls');
      await page.getByTestId('routing-usage-claude').scrollIntoViewIfNeeded();
      await page.screenshot({ path: join(process.env.JOB_RESULTS_DIR ?? 'test-results',
        `routing-usage-${theme}-${width}--mocked.png`) });
    });
  }
}
