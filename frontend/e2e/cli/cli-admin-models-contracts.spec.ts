import { test, expect } from '../fixtures/dev-backend';
import type { CliCompletionContract } from '../../src/app/features/cli';

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

test.describe('Admin/CLI page — models & completion contracts', () => {
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
    await page.getByTestId('status-bar-settings').click();
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

    await page.getByTestId('workspace-settings-rail-caps').click();
    await overlay.screenshot({ path: '../results/cli-admin-types-models-contracts--real.png' });
  });
});
