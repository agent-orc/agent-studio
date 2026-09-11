import { test, expect, Page } from '@playwright/test';

/**
 * AGT-2035 — Settings consolidation acceptance.
 *
 * One consolidated Settings view (studio / vsCode layout, rendered as an editor
 * tab) with a clean Global-vs-Workspace split. This spec pins the operator's
 * per-item decisions:
 *   - The activity-bar gear opens the ONE view (title "Settings"), and the old
 *     sidebar Settings panel is gone.
 *   - Rail groups General / Global / Workspace; Summary removed.
 *   - Theme lives in Appearance (global) and flips the document theme; the
 *     Activity-bar toggle stays; Project chat rail + Card density are gone.
 *   - Updates is one status line + one action + a history link.
 *   - Usage caps carries the completion-contracts explainer and no longer
 *     duplicates the usage detail (that lives in Token usage now).
 *   - Working memory is its own section; Token usage hosts the usage detail.
 *
 * Fully route-stubbed so it runs against a dev frontend with no backend.
 */

const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

async function stub(page: Page) {
  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', json([]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/cli/quota', json({ ttlMs: 1, snapshots: [] }));
  await page.route('**/api/cli/quota/caps', json({ defaultCapPct: 95, caps: {} }));
  await page.route('**/api/cli/quota/model-routes', json({ profiles: {} }));
  await page.route('**/api/cli/contracts', json([]));
  await page.route('**/api/cli/working-memory*', json({ available: false, root: null, capturedAt: new Date().toISOString(), entries: [] }));
  await page.route('**/api/cli/sessions*', json({ sessions: [] }));
  await page.route('**/api/cli/models*', json({ types: [] }));
  await page.route('**/api/cli/usage', json({ entries: [] }));
  await page.route('**/api/clients', json([]));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/admin/prompts', json({ overrideDirectory: 'stub', items: [] }));
  await page.route('**/api/workspaces*', json([]));
  await page.route('**/api/v1/management/retention/policy', json({ scope: 'workspace', version: 0, updatedAt: '', updatedBy: 'platform', rules: [], fullBackups: { daily: 7, weekly: 4, monthly: 12 } }));
  await page.route('**/api/v1/management/retention/runs', json([]));
  await page.route('**/api/v1/management/retention/schedule', json({ enabled: false, serverLocalHour: 3, nextRunAt: null }));
  await page.route('**/api/v1/management/backups/full', json({ backups: [] }));
  await page.route('**/api/workspace/tokens/timeline*', json({
    windowStart: new Date().toISOString(), windowEnd: new Date().toISOString(),
    windowHours: 24, bucketMinutes: 60, bucketCount: 0, cells: [], projects: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stub',
  }));
  await page.route('**/api/runner/token-summary-aggregate*', json({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stub',
  }));
}

async function openSettings(page: Page) {
  await page.getByTestId('studio-ab-settings').click();
  await expect(page.getByTestId('workspace-settings-inline')).toBeVisible({ timeout: 10_000 });
}

test.describe('Settings consolidation (AGT-2035)', () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 1000 });
    await stub(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
  });

  test('the gear opens ONE Settings view; rail is grouped Global vs Workspace, no Summary, no old sidebar panel', async ({ page }) => {
    await openSettings(page);
    await expect(page.getByTestId('workspace-settings-title')).toHaveText('Settings');

    for (const key of ['overview', 'appearance', 'updates', 'workspaces', 'caps', 'working-memory', 'prompts', 'retention', 'tokens', 'screenshots']) {
      await expect(page.getByTestId(`workspace-settings-rail-${key}`)).toBeVisible();
    }
    // Summary is gone.
    await expect(page.getByTestId('workspace-settings-rail-summary')).toHaveCount(0);
    // Rail groups read General / Global / Workspace.
    await expect(page.getByTestId('workspace-settings-group-general')).toContainText('General');
    await expect(page.getByTestId('workspace-settings-group-global')).toContainText('Global');
    await expect(page.getByTestId('workspace-settings-group-workspace')).toContainText('Workspace');
    // The legacy studio-shell sidebar Settings panel no longer exists.
    await expect(page.getByTestId('studio-settings')).toHaveCount(0);
    await expect(page.getByTestId('studio-chat-rail')).toHaveCount(0);
  });

  test('Appearance holds Theme (global, flips the document) + Activity bar; no chat-rail or density controls', async ({ page }) => {
    await page.addInitScript(() => { try { localStorage.setItem('atp.studio.theme', 'light'); } catch { /* ignore */ } });
    await openSettings(page);
    await page.getByTestId('workspace-settings-rail-appearance').click();

    await expect(page.getByRole('group', { name: 'Theme' })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Activity bar' })).toBeVisible();

    await page.getByTestId('settings-theme-dark').click();
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
    await page.getByTestId('settings-theme-light').click();
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');

    const appearance = page.getByTestId('appearance-settings');
    await expect(appearance).not.toContainText(/chat rail/i);
    await expect(appearance).not.toContainText(/density/i);
  });

  test('Updates is compact: one status line + one action + a history link', async ({ page }) => {
    await openSettings(page);
    await page.getByTestId('workspace-settings-rail-updates').click();
    await expect(page.getByTestId('settings-update-status')).toBeVisible();
    await expect(page.getByTestId('settings-update-trigger')).toBeVisible();
    await expect(page.getByTestId('settings-update-open-center')).toBeVisible();
  });

  test('Usage caps carries the contracts explainer and no longer duplicates usage detail', async ({ page }) => {
    await openSettings(page);
    await page.getByTestId('workspace-settings-rail-caps').click();
    await expect(page.getByTestId('cli-admin-contracts-explainer')).toBeVisible();
    await expect(page.getByTestId('cli-admin-panel')).not.toContainText('Usage detail');
  });

  test('Working memory is its own section; Token usage hosts the relocated usage detail', async ({ page }) => {
    await openSettings(page);
    await page.getByTestId('workspace-settings-rail-working-memory').click();
    await expect(page.getByTestId('workspace-working-memory')).toBeVisible();

    await page.getByTestId('workspace-settings-rail-tokens').click();
    await expect(page.getByTestId('token-usage-section')).toBeVisible();
    await expect(page.getByTestId('token-usage-detail')).toBeVisible();
  });
});
