import type { Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { test, expect } from '../fixtures/dev-backend';

/**
 * Delete a retired runner and bulk-purge by prefix (AGT-2748).
 *
 * Companion to `remote-hosts.spec.ts`: that file covers the grouped table,
 * retired filtering, and Drain/Retire/Revive. This file covers the
 * permanent-delete path added on top: a "Delete" action next to "Revive" on
 * a retired role row, and the toolbar's "Delete retired…" dry-run + purge
 * dialog. All network calls are mocked; no real identity is deleted.
 */

const SHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? '../results/remote-hosts';

async function stubBackgroundApis(page: Page) {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/tasks', json([]));
  await page.route('**/api/tasks/grouped', json({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/auth/status', json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/crash-recovery/pending', json({ pending: [] }));
  await page.route('**/api/watch-paths', json([{ name: 'agent-taskboard', path: 'C:/projects/agent-taskboard', rootPath: 'C:/projects' }]));
  await page.route('**/api/runner/status', json({ projects: {} }));
  await page.route('**/api/runner/queue-starvation', json({
    active: false, waitingTaskCount: 0, availableSlots: 0, thresholdMinutes: 30,
    observedAt: new Date().toISOString(), oldestEnteredLaneAt: null, items: [],
  }));
  await page.route('**/api/cli/quota', json({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/v1/management/remote-hosts', json([]));
  await page.route('**/api/v1/management/remote-hosts/link-health', json([]));
  await page.route('**/api/clients/*/telemetry?window=*', json({ clientId: 'mock', window: '14d', points: [], findings: [] }));
  await page.route('**/api/dev-tools/flags', json({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/workspaces*', json([]));
}

/** Two retired e2e- identities: one deletable, one still holding an active lease. */
function retiredClients(now: string) {
  return [
    { id: 'local-default', displayName: 'operator-workstation', kind: 'human', registeredAt: now, lastSeenAt: now },
    { id: 'e2e-owner-alpha', displayName: 'e2e-owner-Alpha', kind: 'retired', registeredAt: now, lastSeenAt: now },
    { id: 'e2e-owner-busy', displayName: 'e2e-owner-Busy', kind: 'retired', registeredAt: now, lastSeenAt: now },
  ];
}

test.describe('Execution Hosts: delete + purge retired runners', () => {
  test.use({ serviceWorkers: 'block' });

  test.beforeEach(async ({ page }) => {
    mkdirSync(SHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 950 });
    await page.addInitScript(() => { try { localStorage.setItem('atp.flag.vsCodeLayout', '0'); } catch { /* ignore */ } });
    await stubBackgroundApis(page);
    const now = new Date().toISOString();
    await page.route('**/api/clients', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify(retiredClients(now)),
    }));
    await page.goto('/#/workspace/settings/execution-hosts');
    await page.waitForLoadState('domcontentloaded');
    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('remote-hosts-table')).toBeVisible();
    await page.getByTestId('remote-hosts-retired-filter').click();
  });

  test('deletes one retired runner from its role-row menu', async ({ page, devBackend: _devBackend }) => {
    void _devBackend;
    let deleteRequested = false;
    await page.route('**/api/clients/e2e-owner-alpha/permanent', async route => {
      deleteRequested = true;
      await route.fulfill({ status: 204 });
    });

    const row = page.getByTestId('remote-host-role-row').filter({ hasText: 'e2e-owner-alpha' });
    await expect(row).toBeVisible();
    await row.getByTestId('remote-host-action-delete').click();

    await expect(page.getByTestId('remote-host-confirm')).toBeVisible();
    await expect(page.getByTestId('remote-host-confirm')).toContainText('e2e-owner-alpha');
    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-delete-confirm-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'remote-host-delete-confirm-dark--mocked.png'), fullPage: false });

    await page.getByTestId('remote-host-confirm-submit').click();
    await expect(row).toHaveCount(0);
    expect(deleteRequested).toBe(true);
  });

  test('previews and applies a prefix purge, skipping a leased identity', async ({ page, devBackend: _devBackend }) => {
    void _devBackend;
    const purgeRequests: { dryRun: boolean }[] = [];
    await page.route('**/api/clients/retired/purge', async route => {
      const body = route.request().postDataJSON() as { prefix: string; dryRun: boolean };
      purgeRequests.push({ dryRun: body.dryRun });
      const results = [
        { id: 'e2e-owner-alpha', displayName: 'e2e-owner-Alpha', outcome: body.dryRun ? 'would-delete' : 'deleted' },
        { id: 'e2e-owner-busy', displayName: 'e2e-owner-Busy', outcome: 'skipped-active-lease' },
      ];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ dryRun: body.dryRun, results }) });
    });

    await page.getByTestId('remote-hosts-purge-retired').click();
    await expect(page.getByTestId('purge-retired-dialog')).toBeVisible();
    await expect(page.getByTestId('purge-retired-prefix')).toHaveValue('e2e-');

    await page.getByTestId('purge-retired-preview').click();
    const list = page.getByTestId('purge-retired-list');
    await expect(list).toContainText('e2e-owner-Alpha');
    await expect(list).toContainText('e2e-owner-Busy');
    await expect(list).toContainText('Skipped');

    await setTheme(page, 'light');
    await page.screenshot({ path: join(SHOT_DIR, 'purge-retired-preview-light--mocked.png'), fullPage: false });
    await setTheme(page, 'dark');
    await page.screenshot({ path: join(SHOT_DIR, 'purge-retired-preview-dark--mocked.png'), fullPage: false });

    const confirm = page.getByTestId('purge-retired-confirm');
    await expect(confirm).toContainText('Delete 1 permanently');
    await confirm.click();

    await expect(page.getByTestId('purge-retired-done')).toContainText('1 deleted');
    await page.getByTestId('purge-retired-close').click();
    await expect(page.getByTestId('purge-retired-dialog')).toHaveCount(0);

    expect(purgeRequests).toEqual([{ dryRun: true }, { dryRun: false }]);
  });
});
