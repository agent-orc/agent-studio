import { expect, test } from '@playwright/test';
import { installFrontendOverride } from '../helpers/frontend-override';
import { setTheme } from '../helpers/theme';

test.describe('Task-type model routing suggestion', () => {
  test.beforeEach(async ({ page }) => {
    await installFrontendOverride(page);
    await page.route('**/api/crash-recovery/pending', async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{"pending":[]}' });
    });
    await page.route('**/api/cli/model-routing/recommendation?*', async (route) => {
      const requestUrl = new URL(route.request().url());
      const taskType = requestUrl.searchParams.get('taskType') ?? 'chore';
      const tier = taskType === 'feature' ? 'sol-medium' : 'luna-medium';
      const model = tier === 'sol-medium' ? 'gpt-6-sol' : 'gpt-6-luna';
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          policyVersion: '2026-10-04',
          policyWikiPath: 'docs/system/domains/model-routing-policy.md',
          taskType,
          tier,
          model,
          thinkingLevel: 'medium',
          score: taskType === 'feature' ? 51 : 15,
          economyMode: false,
          economyDowngraded: false,
          correctnessFloorTier: null,
          reason: `${taskType} default`,
          estimatedSavingsPercent: tier === 'sol-medium' ? 0 : 95,
        }),
      });
    });
  });

  test('shows policy provenance, follows task type, and resets an override in one click', async ({ page }, testInfo) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const suggestion = page.getByTestId('create-model-policy-suggestion');
    await expect(suggestion).toBeVisible();
    await expect(suggestion).toHaveAttribute('data-tier', 'luna-medium');
    await expect(suggestion).toContainText('Policy 2026-10-04');

    await page.getByTestId('create-task-type-feature').click();
    await expect(suggestion).toHaveAttribute('data-tier', 'sol-medium');
    await expect(suggestion).toContainText('feature → sol-medium');

    await page.getByTestId('create-agent').click();
    const modelChoices = page.getByTestId('create-agent-picker-model-pills').getByRole('radio');
    await expect(modelChoices.first()).toBeVisible();
    await modelChoices.first().click();

    await expect(suggestion).toHaveAttribute('data-source', 'override');
    await page.getByTestId('create-use-policy-model').click();
    await expect(suggestion).toHaveAttribute('data-source', 'policy');
    await expect(suggestion).toContainText('gpt-6-sol · medium');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      const path = `../results/model-routing-policy-${theme}--mocked.png`;
      await suggestion.screenshot({ path });
      await testInfo.attach(`model-routing-policy-${theme}`, { path, contentType: 'image/png' });
    }
  });
});
