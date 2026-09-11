import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';

/**
 * F47 / ADR-0042 — Workspaces management section.
 *
 * AGT-2035 folded this out of the studio-shell sidebar Settings panel into the
 * "Workspaces" section (Global group) of the one consolidated Settings view.
 * The gear opens the view on Overview; this helper then navigates the rail to
 * the Workspaces section. All the row test ids are preserved.
 */
async function openWorkspacesSection(page: Page): Promise<void> {
  await page.getByTestId('studio-ab-settings').click();
  await page.getByTestId('workspace-settings-rail-workspaces').click({ timeout: 10_000 });
}

test.describe('Settings — Workspaces section (F47)', () => {
  test('renders one row per registry workspace with interactive action buttons', async ({ page, request }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const apiResponse = await request.get('/api/workspaces');
    expect(apiResponse.ok(), 'GET /api/workspaces should respond 2xx').toBeTruthy();
    const apiWorkspaces = (await apiResponse.json()) as Array<{
      id: string;
      displayName: string;
      isDefault: boolean;
      projects: Array<unknown>;
    }>;

    await openWorkspacesSection(page);
    await expect(page.getByTestId('settings-workspaces')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('settings-workspaces-head')).toContainText('Workspaces');

    if (apiWorkspaces.length === 0) {
      await expect(page.getByTestId('settings-workspaces-empty')).toBeVisible();
      await expect(page.getByTestId('settings-workspaces-list')).toHaveCount(0);
    } else {
      const rows = page.getByTestId('settings-workspace-row');
      await expect(rows).toHaveCount(apiWorkspaces.length);
      const renderedIds = await rows.evaluateAll((nodes) =>
        nodes.map((n) => n.getAttribute('data-workspace-id')),
      );
      expect(new Set(renderedIds)).toEqual(new Set(apiWorkspaces.map((w) => w.id)));

      // Rename / color / delete buttons should exist for every row.
      for (const testid of ['settings-workspace-rename', 'settings-workspace-edit-color', 'settings-workspace-delete']) {
        await expect(page.getByTestId(testid)).toHaveCount(apiWorkspaces.length);
      }

      // The default workspace's delete button is disabled; others are enabled.
      const defaultWs = apiWorkspaces.find((w) => w.isDefault);
      if (defaultWs) {
        const defaultRow = page.locator(`[data-workspace-id="${defaultWs.id}"]`);
        await expect(defaultRow.getByTestId('settings-workspace-delete')).toBeDisabled();
        await expect(defaultRow.getByTestId('settings-workspace-edit-color')).toBeEnabled();
      }
    }

    // Create button + show-archived toggle are present and reachable.
    await expect(page.getByTestId('settings-workspace-create')).toBeEnabled();
    await expect(page.getByTestId('settings-workspace-show-archived')).toBeVisible();

    const note = page.getByTestId('settings-workspaces-note');
    await expect(note).toBeVisible();
    await expect(note).toContainText('ADR-0042');
    await expect(note).toContainText('F45b');
  });

  test('blocks delete while a workspace still holds projects, with the reason in the tooltip (F66)', async ({ page }) => {
    // Inject a deterministic workspace list so the gating is exercised without
    // mutating the shared dev registry: one default, one empty non-default
    // (deletable), one populated non-default (blocked). Same route-stub
    // approach the project-drag spec uses to avoid touching real state.
    const now = '2026-01-01T00:00:00Z';
    const project = (id: string, workspaceId: string) => ({
      id, displayName: id, shortCode: id, workspaceId,
      color: null, cliDefault: null, modelDefault: null,
      sortOrder: 0, storageLocation: `C:/proj/${id}`, archived: false, createdAt: now,
    });
    const stub = [
      { id: 'ws-default', displayName: 'Default', sortOrder: 0, isDefault: true, color: null, createdAt: now, projects: [] },
      { id: 'ws-empty', displayName: 'Empty WS', sortOrder: 1, isDefault: false, color: null, createdAt: now, projects: [] },
      {
        id: 'ws-pop', displayName: 'Populated WS', sortOrder: 2, isDefault: false, color: null, createdAt: now,
        projects: [project('PROJ-901', 'ws-pop'), project('PROJ-902', 'ws-pop')],
      },
    ];
    await page.route('**/api/workspaces', async (route, request) => {
      if (request.method() !== 'GET') { await route.continue(); return; }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(stub) });
    });

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await openWorkspacesSection(page);
    await expect(page.getByTestId('settings-workspaces')).toBeVisible({ timeout: 10_000 });

    const deleteIn = (wsId: string) =>
      page.locator(`[data-workspace-id="${wsId}"]`).getByTestId('settings-workspace-delete');

    // Default + populated → delete blocked. Empty non-default → deletable.
    await expect(deleteIn('ws-default')).toBeDisabled();
    await expect(deleteIn('ws-pop')).toBeDisabled();
    await expect(deleteIn('ws-empty')).toBeEnabled();

    // The reason is still readable on hover even though the button is disabled
    // (the tooltip host is the enabled wrapper around the button).
    const popWrap = page.locator('[data-workspace-id="ws-pop"] .studio-settings-workspace-action-wrap');
    await popWrap.hover();
    const tip = page.getByTestId('cac-tooltip');
    await expect(tip).toBeVisible({ timeout: 5_000 });
    await expect(tip.locator('.cac-tooltip__body'))
      .toHaveText('Move all 2 projects out of this workspace before it can be deleted.');

    // Clip the screenshot around the Workspaces section (plus headroom for the
    // tooltip that renders just outside the row) so the captured artifact is
    // legible rather than a full-board thumbnail.
    const panel = await page.getByTestId('settings-workspaces').boundingBox();
    if (panel) {
      const pad = 24;
      await page.screenshot({
        path: 'test-results/workspace-delete-blocked-tooltip.png',
        clip: {
          x: Math.max(0, panel.x - pad),
          y: Math.max(0, panel.y - pad),
          width: panel.width + pad * 2,
          height: panel.height + pad * 3,
        },
      });
    } else {
      await page.screenshot({ path: 'test-results/workspace-delete-blocked-tooltip.png' });
    }
  });

  test('create → rename → delete round-trip via the REST API surface', async ({ page, request }) => {
    // The mutation endpoints require an X-Client-Id header on every write.
    // Register a throwaway identity for the run. The `e2e-` prefix (AGT-2748)
    // lets a later "Delete retired..." purge sweep clean this up even if the
    // finally block below never runs (a crashed or interrupted test).
    const clientId = `e2e-workspace-f47-${Date.now().toString(36)}`;
    const regRes = await request.post('/api/clients/register', { data: { displayName: clientId } });
    expect(regRes.ok(), 'client register should succeed').toBeTruthy();
    const headers = { 'X-Client-Id': clientId };

    // Probe: if the running backend predates the F45b POST endpoint
    // (returns 405 Method Not Allowed instead of accepting the call),
    // skip the round-trip with a clear reason. The first test in this
    // file still pins the read-side surface.
    const uniqueSuffix = Date.now().toString(36);
    const initialName = `Playwright F47 ${uniqueSuffix}`;
    const probe = await request.post('/api/workspaces', {
      headers,
      data: { displayName: initialName },
    });
    test.skip(
      probe.status() === 405,
      'Running backend predates the F45b POST /api/workspaces endpoint. Restart the dev backend to pick up the new code, then re-run.',
    );
    expect(probe.ok(), `POST /api/workspaces should 201, got ${probe.status()} ${await probe.text()}`).toBeTruthy();
    const createdBody = (await probe.json()) as { id: string };
    const wsId = createdBody.id;
    expect(wsId).toMatch(/^ws-/);

    try {
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      await openWorkspacesSection(page);
      const row = page.locator(`[data-workspace-id="${wsId}"]`);
      await expect(row).toBeVisible({ timeout: 10_000 });
      await expect(row.getByTestId('settings-workspace-rename')).toHaveText(initialName);

      const renamed = `Playwright F47 Renamed ${uniqueSuffix}`;
      const renamedRes = await request.put(`/api/workspaces/${wsId}`, {
        headers, data: { displayName: renamed },
      });
      expect(renamedRes.ok()).toBeTruthy();
      await page.reload();
      await page.waitForLoadState('domcontentloaded');
      await openWorkspacesSection(page);
      await expect(page.locator(`[data-workspace-id="${wsId}"]`).getByTestId('settings-workspace-rename'))
        .toHaveText(renamed, { timeout: 10_000 });
    } finally {
      const deleted = await request.delete(`/api/workspaces/${wsId}`, { headers });
      expect(deleted.ok() || deleted.status() === 404).toBeTruthy();
      // Retire then permanently delete the throwaway identity so it never
      // accumulates as retired Execution Hosts history (AGT-2748).
      await request.post(`/api/clients/${clientId}/retire`, { headers }).catch(() => undefined);
      await request.delete(`/api/clients/${clientId}/permanent`, { headers }).catch(() => undefined);
    }
  });
});
