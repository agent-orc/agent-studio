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
          score: taskType === 'feature' ? 25 : 15,
          economyMode: false,
          economyDowngraded: false,
          correctnessFloorTier: null,
          reason: `${taskType} default`,
          estimatedSavingsPercent: tier === 'sol-medium' ? 0 : 65,
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
      const path = `${process.env['JOB_RESULTS_DIR'] ?? '../results'}/model-routing-policy-${theme}--mocked.png`;
      await suggestion.screenshot({ path });
      await testInfo.attach(`model-routing-policy-${theme}`, { path, contentType: 'image/png' });
    }
  });

  test('shows GPT-6 first with TokenEconomy prices in the picker', async ({ page }, testInfo) => {
    await page.route('**/api/cli/codex/models**', async (route) => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        source: 'mocked-discovery', models: [
          { id: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', vendor: 'openai', isDefault: false, available: true,
            thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'] },
          { id: 'gpt-6-luna', label: 'GPT-6 Luna', vendor: 'openai', isDefault: false, available: true,
            thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'] },
          { id: 'gpt-6-sol', label: 'GPT-6 Sol', vendor: 'openai', isDefault: true, available: true,
            thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'] },
        ],
      }),
    }));
    await page.route('**/api/token-pricing/calculate', async (route) => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({
        provider: 'TokenEconomy', items: [
          { model: 'gpt-6-sol', estimate: { modelKnown: true,
            priceBasis: { inputPerMillion: 2, cacheReadPerMillion: 0.2, outputPerMillion: 10 } } },
          { model: 'gpt-6-luna', estimate: { modelKnown: true,
            priceBasis: { inputPerMillion: 0.1, cacheReadPerMillion: 0.01, outputPerMillion: 0.5 } } },
        ],
      }),
    }));

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();
    await page.getByTestId('create-agent').click();
    const picker = page.getByTestId('create-agent-picker');
    await expect(picker).toBeVisible();
    await picker.getByTestId('create-agent-picker-cli-codex').click();
    const models = picker.getByTestId('create-agent-picker-model-pills').getByRole('radio');
    await expect(models.nth(1)).toContainText('GPT-6 Sol');
    await expect(picker.getByTestId('create-agent-picker-price-gpt-6-sol'))
      .toContainText('$2 in · $0.2 cached · $10 out / MTok');
    const path = `${process.env['JOB_RESULTS_DIR'] ?? '../results'}/gpt6-model-picker--mocked.png`;
    await picker.screenshot({ path });
    await testInfo.attach('gpt6-model-picker', { path, contentType: 'image/png' });
  });
});
