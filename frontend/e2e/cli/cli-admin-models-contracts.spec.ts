import { test, expect } from '../fixtures/dev-backend';
import type { CliCompletionContract } from '../../src/app/features/cli';
import type { CliModelRouteProfile } from '../../src/app/features/quota';
import type { Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultsDir = process.env.JOB_RESULTS_DIR ?? '/tmp/agt-4135-results';

/**
 * Admin/CLI page (T7a): the model-catalog and completion-contract sections in
 * the workspace Settings CLI Management panel, with its completion-contracts explainer.
 *
 * Verifies:
 *   1. GET /api/cli/contracts returns the real per-CLI completion contract
 *      registry (one entry per known CLI; Claude, Codex, and Gemini use typed adapters). The data is sourced from the live adapter mappings, not a
 *      frontend constant.
 *   2. The page renders the new sections and captures a real-backend evidence
 *      screenshot under the job's results/ folder.
 *
 * Uses the `dev-backend` fixture (the only sanctioned path to bring dev's
 * backend up, per AGENTS.md) so the screenshot is a --real shot.
 */

const CLIENT_ID = 'local-default';

async function api<T>(baseUrl: string, path: string): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    headers: { 'content-type': 'application/json', 'x-client-id': CLIENT_ID },
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`API GET ${path} -> ${res.status} ${res.statusText}\n${text}`);
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

test.describe('Admin/CLI page models and completion contracts', () => {
  test('GET /api/cli/contracts returns the real per-CLI registry', async ({ devBackend }) => {
    const contracts = await api<CliCompletionContract[]>(devBackend.baseUrl, '/api/cli/contracts');
    const byType = new Map(contracts.map((c) => [c.cliType, c]));

    expect(byType.has('claude')).toBe(true);
    expect(byType.has('codex')).toBe(true);
    expect(byType.has('gemini')).toBe(true);

    // Typed adapters expose their native completion frame.
    expect(byType.get('claude')!.typed).toBe(true);
    expect(byType.get('claude')!.completionSignal).toContain('result');
    expect(byType.get('codex')!.completionSignal).toContain('turn.completed');

  });

  test('CLI Management renders models and contracts', async ({ page }) => {
    await page.goto('/');
    await page.getByTestId('studio-titlebar-settings').click();
    await page.getByTestId('workspace-settings-rail-caps').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();

    await expect(overlay.getByTestId('cli-admin-models')).toBeVisible();
    await expect(overlay.getByTestId('cli-models-card-claude')).toBeVisible();

    const contracts = overlay.getByTestId('cli-admin-contracts');
    await expect(contracts).toBeVisible();
    // AGT-2035: the completion-contracts surface now carries a short explainer.
    await expect(contracts.getByTestId('cli-admin-contracts-explainer')).toBeVisible();
    await expect(contracts.getByTestId('cli-contract-card-claude')).toBeVisible();
    await expect(contracts.getByTestId('cli-contracts-explainer')).toContainText('read-only registry');
    await expect(contracts.getByTestId('cli-contracts-explainer')).toContainText('not configuration');

    mkdirSync(resultsDir, { recursive: true });
    await overlay.screenshot({ path: join(resultsDir, 'cli-admin-types-models-contracts--real.png') });
  });
});


for (const theme of ['dark', 'light'] as const) {
  for (const width of [1440, 390]) {
    test(`route failures roll back and retry at ${width}px in ${theme}`, async ({ page }) => {
      test.setTimeout(120_000);
      mkdirSync(resultsDir, { recursive: true });
      await page.setViewportSize({ width, height: 900 });
      const profiles: Record<string, CliModelRouteProfile> = Object.fromEntries(['claude', 'codex'].map(cliType => [cliType, {
        cliType, primaryModel: 'original', primaryThinkingLevel: null,
        fallbackCliType: 'codex', fallbackModel: 'automatic', fallbackThinkingLevel: null,
        isFallbackDerived: true,
      }]));
      const writes: { route: Route; profile: CliModelRouteProfile }[] = [];
      await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => route.fulfill({ json: {
        source: 'routing regression fixture',
        models: ['original', 'chosen', 'automatic', 'canonical'].map((id, index) => ({
          id, label: id, isDefault: index === 0, multiplier: null, vendor: null,
        })),
      } }));
      await page.route('**/api/cli/quota/model-routes', async route => {
        if (route.request().method() === 'PUT') {
          writes.push({ route, profile: route.request().postDataJSON() });
        } else await route.fulfill({ json: { profiles, routes: [], states: {} } });
      });
      await page.goto('/#/workspace/settings/caps', { waitUntil: 'domcontentloaded', timeout: 30_000 });
      await setTheme(page, theme);
      await page.getByTestId('cli-models-toggle-claude').click();
      const primary = page.getByTestId('cli-primary-claude');
      const fallback = page.getByTestId('cli-fallback-claude');
      const saving = page.getByTestId('cli-route-saving-claude');
      const error = page.getByTestId('cli-route-error-claude');
      const retry = page.getByTestId('cli-route-retry-claude');
      await expect(primary).toHaveValue('original');
      await expect(fallback).toHaveValue('automatic');
      await primary.selectOption('chosen');
      await expect(primary).toHaveValue('chosen');
      await expect(saving).toBeVisible();
      await expect.poll(() => writes.length).toBe(1);
      expect(writes[0].profile.fallbackModel).toBeNull();
      await page.screenshot({ path: join(resultsDir, `routing-saving-${theme}-${width}.png`) });
      await writes[0].route.fulfill({ status: 503, json: { error: 'Rejected primary' } });
      await expect(primary).toHaveValue('original');
      await expect(fallback).toHaveValue('automatic');
      await expect(saving).toBeHidden();
      await expect(error).toContainText('Could not save');
      await error.scrollIntoViewIfNeeded();
      await expect(retry).toBeInViewport();
      expect(await page.getByTestId('cli-models-card-claude').evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
      await page.screenshot({ path: join(resultsDir, `routing-primary-rejected-${theme}-${width}.png`) });
      await retry.click();
      await expect.poll(() => writes.length).toBe(2);
      expect(writes[1].profile).toEqual(writes[0].profile);
      profiles['claude'] = { ...profiles['claude'], primaryModel: 'canonical' };
      await writes[1].route.fulfill({ json: profiles['claude'] });
      await expect(primary).toHaveValue('canonical');
      await expect(error).toBeHidden();

      // One CLI's acknowledgement must not clear another CLI's saving state.
      await page.getByTestId('cli-models-toggle-codex').click();
      await fallback.selectOption('chosen');
      await primary.selectOption('chosen');
      await expect(primary).toHaveValue('chosen');
      await page.getByTestId('cli-primary-codex').selectOption('chosen');
      await expect.poll(() => writes.length).toBe(4);
      expect(writes[2].profile.cliType).toBe('claude');
      expect(writes[3].profile.cliType).toBe('codex');
      await expect(saving).toBeVisible();
      await expect(page.getByTestId('cli-route-saving-codex')).toBeVisible();
      profiles['codex'] = { ...profiles['codex'], primaryModel: 'chosen' };
      await writes[3].route.fulfill({ json: profiles['codex'] });
      await expect(page.getByTestId('cli-route-saving-codex')).toBeHidden();
      await expect(saving).toBeVisible();
      await writes[2].route.fulfill({ status: 503, json: { error: 'Rejected fallback' } });
      await expect(primary).toHaveValue('canonical');
      await expect(fallback).toHaveValue('automatic');
      await expect(error).toBeVisible();
      await expect(saving).toBeHidden();
      expect(writes).toHaveLength(4);
      await error.scrollIntoViewIfNeeded();
      await expect(retry).toBeInViewport();
      await page.screenshot({ path: join(resultsDir, `routing-fallback-rejected-${theme}-${width}.png`) });
      await retry.click();
      await expect.poll(() => writes.length).toBe(5);
      expect(writes[4].profile).toMatchObject({ primaryModel: 'chosen', fallbackModel: 'chosen', fallbackCliType: 'codex' });
      profiles['claude'] = writes[4].profile;
      await writes[4].route.fulfill({ json: profiles['claude'] });
      await expect(primary).toHaveValue('chosen');
      await expect(fallback).toHaveValue('chosen');
      await expect(error).toBeHidden();
      await expect(saving).toBeHidden();
      await fallback.scrollIntoViewIfNeeded();
      await page.screenshot({ path: join(resultsDir, `routing-retry-saved-${theme}-${width}.png`) });
      await page.reload();
      await page.getByTestId('cli-models-toggle-claude').click();
      await expect(primary).toHaveValue('chosen');
      await expect(fallback).toHaveValue('chosen');
    });
  }
}
