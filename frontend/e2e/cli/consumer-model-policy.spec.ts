import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

test('consumer policy previews historical costs before saving and renders both themes', async ({ page }) => {
  const results = process.env.JOB_RESULTS_DIR ?? 'test-results';
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  const writes: unknown[] = [];
  const consumerOverrides: Record<string, unknown> = {};
  let activeSwitches = [{
    id: 'switch-1', kind: 'switched', at: '2026-11-04T12:00:00Z', consumer: 'coding', project: 'alpha', taskKey: 'AGT-1',
    from: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
    to: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' },
    rule: 'quota-cap', returnToPrimaryAt: '2026-11-07T12:00:00Z',
  }];
  mkdirSync(results, { recursive: true });
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const policy = {
      version: 1,
      consumers: {
        coding: { route: 'qualification', primary: null, fallbacks: 'catalogue' },
        'review-aspect': { route: null, primary: { cli: 'claude', model: 'claude-opus-5', thinking: 'high' },
          fallbacks: [{ cli: 'codex', model: 'gpt-6-sol', thinking: 'high' }] },
        ...consumerOverrides,
      },
      caps: {}, costGuardFactor: 2,
      hysteresis: { returnBelowCapPoints: 10, minDwellMinutes: 30 },
      migration: { inheritedSteps: 36, explicitSteps: 36, projects: 12, applied: true },
    };
    if (path.startsWith('/api/model-selection/policy/consumers/')) {
      const setting = route.request().postDataJSON();
      writes.push(setting);
      consumerOverrides[path.split('/').pop()!] = setting;
      await route.fulfill({ json: { ...policy, consumers: { ...policy.consumers, ...consumerOverrides } } }); return;
    }
    const body = path === '/api/token-pricing/route-models' ? { models: [
      { cli: 'claude', model: 'claude-opus-5', label: 'Opus 5', thinkingLevels: ['high'], price: { inputPerMillion: 5, outputPerMillion: 25 } },
      { cli: 'claude', model: 'claude-sonnet-5', label: 'Sonnet 5', thinkingLevels: ['low', 'medium', 'high'], price: { inputPerMillion: 3, outputPerMillion: 15 } },
    ] }
      : path === '/api/token-pricing/what-if' ? { receipts: 12, actualUsd: 39.78, alternativeUsd: 15.91,
        savingUsd: 23.87, savingPercent: 60, projectedWeeklySavingUsd: 23.87, unpricedActual: 0, unpricedAlternative: 0,
        coverage: 'complete', qualityCaveat: 'Model quality and retries may change. Same tokens at historical prices.',
        correctnessFloor: 'Correctness floors still apply at admission. This preview does not authorize a downgrade.' }
      : path === '/api/model-selection/policy' ? policy
      : path === '/api/model-selection/active' ? activeSwitches
      : path === '/api/model-selection/effective' ? { routes: [
        { consumer: 'coding', stepId: null,
          configured: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' },
          effective: { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' }, rule: 'consumer-default', reason: null },
        { consumer: 'review-aspect', stepId: 'aspect-security',
          configured: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' },
          effective: { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' }, rule: 'consumer-default', reason: null },
      ] }
      : path === '/api/v1/studio/auth/status'
        ? { profile: 'local', bootstrapRequired: false, authenticated: true, user: null }
      : path === '/api/v1/studio/runner/status' ? { projects: {} }
      : path === '/api/watcher/status' ? { options: { enabled: false }, contingent: null }
      : path === '/api/watch-paths' || path === '/api/tasks' || path === '/api/tags'
        || path === '/api/cli/contracts'
        || path === '/api/clients' || path === '/api/clients/'
        || path === '/api/v1/workspaces' || path === '/api/v1/projects'
        || path === '/api/v1/management/remote-hosts' || path === '/api/v1/management/links' ? []
      : path.startsWith('/api/v1/studio/board') ? { preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }
      : path === '/api/cli/quota/model-routes' ? { profiles: {}, routes: [], states: {}, callersCannotReroute: [], catalogueVersion: 'mocked' }
      : path === '/api/cli/model-routing/policy' ? { version: 'mocked', economyMode: false, tiers: [], taskTypeDefaults: {} }
      : path === '/api/cli/quota/caps' ? { defaultCapPct: 95, caps: {} }
      : path === '/api/cli/quota' ? { at: new Date().toISOString(), snapshots: [] }
      : path.match(/^\/api\/cli\/[^/]+\/models$/) ? { source: 'mocked', models: [] }
      : path === '/api/cli/usage' ? { sections: [] }
      : path === '/api/usage/cockpit' ? { workspaceId: 'mocked', clis: [], cost: null }
      : null;
    await route.fulfill({ status: body === null ? 503 : 200, contentType: 'application/json', body: JSON.stringify(body ?? {}) });
  });
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-caps').click();
  const editor = page.getByTestId('consumer-policy-editor');
  await expect(editor).toBeVisible();
  await expect(editor.getByTestId('consumer-policy-review-aspect')).toBeVisible();
  await expect(editor.getByTestId('consumer-policy-coding')).toBeVisible();
  if (process.env.ROUTING_BASELINE === '1') await editor.screenshot({ path: join(results, 'routing-before--mocked.png') });
  const comparison = editor.getByTestId('routing-cost-preview');
  await expect(comparison.getByRole('button', { name: 'Preview cost', exact: true })).toBeDisabled();
  await comparison.getByLabel('Alternative model').selectOption('claude-sonnet-5');
  await comparison.getByLabel('From (UTC)', { exact: true }).fill('2026-11-01');
  await comparison.getByLabel('To (UTC, exclusive)', { exact: true }).fill('2026-11-08');
  await comparison.getByRole('button', { name: 'Preview cost', exact: true }).click();
  await expect(comparison.getByTestId('routing-cost-result')).toContainText('Saving $23.87');
  await expect(comparison).toContainText('Correctness floors still apply');
  const review = editor.getByTestId('consumer-policy-review-aspect');
  await expect(review).toContainText('aspect-security');
  await expect(review.getByLabel('Thinking for review-aspect', { exact: true })).toHaveValue('high');
  await expect(review.getByLabel('Fallback thinking 1 for review-aspect', { exact: true })).toHaveValue('high');
  await review.getByLabel('Primary route for review-aspect', { exact: true }).selectOption('claude:claude-sonnet-5');
  await expect(review.getByRole('button', { name: 'Save route', exact: true })).toBeDisabled();
  expect(writes).toHaveLength(0);
  await review.getByRole('button', { name: 'Preview weekly saving', exact: true }).click();
  await expect(review.getByRole('button', { name: 'Save route', exact: true })).toBeEnabled();
  await review.getByRole('button', { name: 'Save route', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toMatchObject({ primary: { cli: 'claude', model: 'claude-sonnet-5' } });
  await expect(review.getByLabel('Primary route for review-aspect', { exact: true })).toHaveValue('claude:claude-sonnet-5');
  const economy = page.getByTestId('model-routing-economy-mode');
  await economy.check();
  await expect(page.getByTestId('save-economy-mode')).toBeDisabled();
  const economyPreview = page.getByTestId('model-routing-policy').getByTestId('routing-cost-preview');
  await economyPreview.getByRole('button', { name: 'Preview cost', exact: true }).click();
  await expect(page.getByTestId('save-economy-mode')).toBeEnabled();
  const strip = page.getByTestId('active-model-switches');
  await expect(strip).toContainText('Active fallbacks (1)');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await page.setViewportSize({ width: 1280, height: 1000 });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(editor).toBeVisible();
    await editor.screenshot({ path: join(results, `consumer-model-policy--mocked-${theme}.png`) });
    await strip.screenshot({ path: join(results, `active-model-switches--mocked-${theme}.png`) });
    await page.getByTestId('model-routing-policy').screenshot({ path: join(results, `economy-preview--mocked-${theme}.png`) });
  }
  for (const theme of ['light', 'dark'] as const) {
    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, theme);
    await comparison.getByText('Compare routing costs', { exact: true }).evaluate(el => el.scrollIntoView({ block: 'start' }));
    await page.screenshot({ path: join(results, `consumer-model-policy--mocked-phone-${theme}.png`) });
    await page.getByTestId('model-routing-policy').getByText('Automatic task routing', { exact: true }).evaluate(el => el.scrollIntoView({ block: 'start' }));
    await page.screenshot({ path: join(results, `economy-preview--mocked-phone-${theme}.png`) });
  }
  expect(pageErrors).toEqual([]);
  await page.setViewportSize({ width: 1280, height: 1000 });
  activeSwitches = [];
  await page.reload();
  await dismissDevErrorDialog(page);
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-caps').click();
  await expect(page.getByTestId('active-model-switches')).toContainText('Active fallbacks (0)');
});
