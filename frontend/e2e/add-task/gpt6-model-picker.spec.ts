import { expect, test } from '@playwright/test';
import { join } from 'node:path';
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

const emptyBoard = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};
const projectName = 'GPT-6 Fixture';
const watchPath = 'C:/fixtures/gpt6-picker';
const project = {
  id: 'PROJ-GPT6', displayName: projectName, shortCode: 'GPT', workspaceId: 'ws-gpt6',
  color: null, cliDefault: 'codex', modelDefault: null, sortOrder: 0,
  storageLocation: watchPath, repositoryPath: null, rootPath: null,
  repositoryUrl: null, urls: [], archived: false, createdAt: '2026-09-25T00:00:00Z',
};

test.describe('GPT-6 model picker', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript((name) => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1, tabs: [{ kind: 'board', projectName: name }], activeKey: `board:${name}`,
      }));
    }, projectName);
    await page.route('**/api/**', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
    await page.route('**/api/v1/studio/auth/status', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }) }));
    await page.route('**/api/v1/workspaces*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
        id: 'ws-gpt6', displayName: 'GPT-6 Workspace', sortOrder: 0, isDefault: true,
        color: null, createdAt: '2026-09-25T00:00:00Z', projects: [project],
      }]) }));
    await page.route('**/api/v1/projects*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([project]) }));
    await page.route('**/api/watch-paths*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
        name: projectName, path: watchPath, rootPath: watchPath, repositoryPath: watchPath,
      }]) }));
    await page.route('**/api/projects/*/workbenches*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ projectName, items: [] }) }));
    await page.route('**/api/v1/studio/runner/status*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: '{"projects":{}}' }));
    await page.route('**/api/v1/studio/board*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(emptyBoard) }));
    await page.route('**/api/tasks/archive*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: '{"items":[],"total":0,"offset":0,"limit":50}' }));
    await page.route('**/api/cli/quota*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: '{"at":"2026-09-25T00:00:00Z","ttlSeconds":600,"snapshots":[]}' }));
    await page.route('**/api/cli/usage*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: '{"at":"2026-09-25T00:00:00Z","sessions":[]}' }));
    await page.route('**/api/environment*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: '{"isDev":false,"devTools":{}}' }));
    await page.route('**/hubs/v1/studio/negotiate*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        negotiateVersion: 1, connectionId: 'gpt6-picker', connectionToken: 'gpt6-picker',
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
      }) }));
    await page.routeWebSocket('**/hubs/v1/studio**', (socket) => {
      socket.onMessage((message) => {
        if (typeof message === 'string' && message.includes('"protocol"')) socket.send('{}\u001e');
      });
    });
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
    await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
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
      const path = join(process.env.JOB_RESULTS_DIR ?? '../results', `gpt6-model-picker-${theme}--mocked.png`);
      await page.locator('[data-testid="create-agent-picker-model-pills"]').screenshot({ path });
      await testInfo.attach(`gpt6-model-picker-${theme}`, { path, contentType: 'image/png' });
    }
  });

  test('reports a project migration that failed every pinned card update', async ({ page }, testInfo) => {
    const id = 'AGT-fail';
    const card = {
      id, taskKey: `${watchPath}::${id}`, title: 'Pinned migration fixture',
      state: '2-ready', order: 1, agent: 'codex', cliType: 'codex',
      createdAt: '2026-09-25T00:00:00Z', lastActivity: '2026-09-25T00:00:00Z',
      watchPath, projectName, folderPath: `${watchPath}/2-ready/${id}`,
      model: 'gpt-5.6-sol', modelExplicit: true, thinkingLevel: 'ultra',
      sessionName: null, useOwnSession: null, lastUsage: null,
      execution: null, commit: null, commits: [], ownerClientId: 'local-default',
      tags: [], pendingIntent: null, autoLoop: null, summaryState: null,
    };
    await page.route('**/api/v1/studio/board*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ ...emptyBoard, ready: [card] }) }));
    await page.route('**/api/cli/model-migrations*', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        version: 'fixture', wikiPath: '', migrations: [{
          from: 'gpt-5.6-sol', to: 'gpt-6-sol', family: 'gpt-sol', safeAuto: false,
          reason: 'Proposal only.',
        }],
      }) }));
    await page.route('**/api/projects/*/model-migrations/apply', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        from: 'gpt-5.6-sol', to: 'gpt-6-sol', updatedTaskIds: [], failedTaskIds: [id],
      }) }));

    await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
    await page.getByTestId('task-card-model-migration-dot').click();
    await page.getByTestId('task-card-model-migration-apply-project').click();
    await expect(page.getByText(`0 cards updated to gpt-6-sol; 1 failed (${id}).`)).toBeVisible();
    await expect(page.getByText('Model updated to gpt-6-sol on 0 cards.')).toHaveCount(0);

    const path = join(process.env.JOB_RESULTS_DIR ?? '../results', 'gpt6-project-migration-failed--mocked.png');
    await page.screenshot({ path });
    await testInfo.attach('gpt6-project-migration-failed', { path, contentType: 'image/png' });
  });
});
