import { expect, test } from '@playwright/test';
import { installFrontendOverride } from '../helpers/frontend-override';
import { setTheme } from '../helpers/theme';

/**
 * AGT-2903: the Codex picker leads with GPT-6, prices each model from the
 * TokenEconomy cost API, and states a mapped thinking level visibly. The
 * catalogue, recommendation, and price endpoints are mocked so the spec needs
 * no live codex-cli.
 */
const codexModels = [
  { id: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', multiplier: null, vendor: 'openai', isDefault: false, available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultThinkingLevel: 'medium' },
  { id: 'gpt-6-sol', label: 'GPT-6 Sol', multiplier: null, vendor: 'openai', isDefault: true, available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultThinkingLevel: 'medium' },
  { id: 'gpt-6-luna', label: 'GPT-6 Luna', multiplier: null, vendor: 'openai', isDefault: false, available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max'], defaultThinkingLevel: 'medium' },
  { id: 'gpt-5.6-terra', label: 'GPT-5.6 Terra', multiplier: null, vendor: 'openai', isDefault: false, available: true,
    thinkingLevels: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultThinkingLevel: 'medium' },
];

const prices: Record<string, [number, number, number]> = {
  'gpt-6-sol': [2, 0.2, 10],
  'gpt-6-luna': [0.1, 0.01, 0.5],
  'gpt-5.6-sol': [4, 0.4, 20],
  'gpt-5.6-terra': [2, 0.2, 12],
};

test.describe('GPT-6 model picker', () => {
  test.beforeEach(async ({ page }) => {
    await installFrontendOverride(page);
    await page.route('**/api/auth/status', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true }) }));
    await page.route('**/api/crash-recovery/pending', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '{"pending":[]}' }));
    await page.route('**/api/cli/codex/models*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ models: codexModels, source: 'mock', fetchedAt: new Date().toISOString() }) }));
    await page.route('**/api/cli/model-routing/recommendation?*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        policyVersion: '2026-10-04', policyWikiPath: 'docs/system/domains/model-routing-policy.md',
        taskType: 'feature', tier: 'sol-medium', model: 'gpt-6-sol', thinkingLevel: 'medium', score: 51,
        economyMode: false, economyDowngraded: false, correctnessFloorTier: null,
        reason: 'feature default', estimatedSavingsPercent: 0,
      }) }));
    await page.route('**/api/token-pricing/calculate', async (route) => {
      const body = route.request().postDataJSON() as { items: { model: string }[] };
      const items = body.items.map(({ model }) => {
        const price = prices[model];
        return {
          model, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
          calculatedAt: new Date().toISOString(),
          estimate: {
            inputUsd: 0, outputUsd: 0, cacheReadUsd: 0, cacheWriteUsd: 0, total: 0, modelId: model,
            modelKnown: Boolean(price), status: price ? 'priced' : 'unknownModel',
            priceBasis: price ? {
              inputPerMillion: price[0], cacheReadPerMillion: price[1], outputPerMillion: price[2],
              cacheWritePerMillion: price[0], currency: 'USD', validFrom: '2026-09-22T00:00:00Z',
              source: 'mock', note: null, unconfirmed: false,
            } : null,
          },
        };
      });
      await route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ items, provider: 'TokenEconomy' }) });
    });
  });

  test('leads with GPT-6, shows TokenEconomy prices, and states a mapped level', async ({ page }, testInfo) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();
    await page.getByTestId('create-agent').click();
    await page.getByTestId('create-agent-picker-cli-codex').click();

    const pills = page.getByTestId('create-agent-picker-model-pills');
    await expect(pills.getByTestId('create-agent-picker-model-gpt-6-sol')).toBeVisible();
    const order = await pills.locator('[data-testid^="create-agent-picker-model-gpt"]').evaluateAll(
      (nodes) => nodes.map((node) => node.getAttribute('data-testid')!.replace('create-agent-picker-model-', '')));
    expect(order.slice(0, 2).sort()).toEqual(['gpt-6-luna', 'gpt-6-sol']);
    await expect(page.getByTestId('create-agent-picker-older-heading')).toBeVisible();
    await expect(page.getByTestId('create-agent-picker-price-gpt-6-sol')).toHaveText('$2 / $10');
    await expect(page.getByTestId('create-agent-picker-price-gpt-6-luna')).toHaveText('$0.1 / $0.5');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      const path = `../results/gpt6-model-picker-${theme}--mocked.png`;
      await page.locator('[data-testid="create-agent-picker-model-pills"]').screenshot({ path });
      await testInfo.attach(`gpt6-model-picker-${theme}`, { path, contentType: 'image/png' });
    }
  });
});
