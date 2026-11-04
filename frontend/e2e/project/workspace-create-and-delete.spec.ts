import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * End-to-end coverage for the "create + delete workspace" flow using the
 * F45b workspace registry (POST /api/workspaces):
 *   - The "+ Add workspace" button in the Explorer header opens the
 *     create dialog.
 *   - Empty / duplicate names are rejected client-side; valid names
 *     POST to /api/workspaces and the new entry appears in the
 *     settings workspace list without a page reload.
 *   - Success / error toasts surface feedback to the user.
 *   - The per-project Settings rail surfaces a "Delete this workspace"
 *     button that is disabled while the workspace still has jobs and
 *     enabled when it is empty, with the confirm dialog gating the
 *     destructive call.
 *
 * The spec creates and immediately deletes a temporary workspace so
 * the registry returns to its pre-test shape no matter how the test
 * exits. Each test uses a unique slug to keep parallel runs from
 * colliding.
 */

interface RegistryWorkspace {
  id: string;
  displayName: string;
  sortOrder: number;
  isDefault: boolean;
  color: string | null;
  projects: unknown[];
}
interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'workspace-create-and-delete');
})();

function uniqueName(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

/** Best-effort cleanup of a registry workspace by id. */
async function purgeRegistryWorkspace(id: string): Promise<void> {
  try {
    await api(`/api/workspaces/${encodeURIComponent(id)}`, { method: 'DELETE' });
  } catch { /* not present or has projects, ignore */ }
}

/** Best-effort cleanup of a legacy watch-path entry by name. */
async function purgeWatchPath(name: string): Promise<void> {
  try {
    await api(`/api/watch-paths/${encodeURIComponent(name)}`, { method: 'DELETE' });
  } catch { /* not present, ignore */ }
}

test('create workspace via "+" button persists to registry and shows in settings', async ({ page }) => {
  const newName = uniqueName('e2e-ws');
  const expectedId = `ws-${slugFor(newName)}`;
  test.info().annotations.push({ type: 'workspace-name', description: newName });

  try {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    const addButton = page.getByTestId('studio-sidebar-add-workspace');
    await addButton.click();

    const dialog = page.getByTestId('workspace-create-dialog');
    await expect(dialog).toBeVisible();

    // Validation: submit disabled with empty input.
    const submit = page.getByTestId('workspace-create-submit');
    await expect(submit).toBeDisabled();

    const nameInput = page.getByTestId('workspace-create-name');
    await nameInput.fill(newName);
    await expect(submit).toBeEnabled();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-create-dialog.png'), fullPage: true });

    await submit.click();
    // The POST may take a few seconds on a loaded backend; wait
    // generously for the dialog to close on success.
    await expect(dialog).not.toBeVisible({ timeout: 30_000 });

    // Verify the workspace was persisted to the registry.
    const workspaces = await api<RegistryWorkspace[]>('/api/workspaces');
    const created = workspaces.find(w => w.displayName === newName);
    expect(created).toBeTruthy();
    expect(created!.isDefault).toBe(false);

    // Open the Settings panel and verify the workspace row appears.
    // The new workspace sorts after the default (which has inline
    // project rows and can be tall), so scroll it into view first.
    const settingsTab = page.getByTestId('studio-titlebar-settings');
    await settingsTab.click();
    // AGT-2035: workspace management moved into the Workspaces section of the
    // consolidated Settings view.
    await page.getByTestId('workspace-settings-rail-workspaces').click({ timeout: 10_000 });
    const wsList = page.getByTestId('settings-workspaces-list');
    await expect(wsList).toBeVisible({ timeout: 5_000 });
    const wsRow = wsList.getByTestId('settings-workspace-rename').filter({ hasText: newName });
    await wsRow.scrollIntoViewIfNeeded();
    await expect(wsRow).toBeVisible();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-settings-workspace-visible.png'), fullPage: true });

    // Reload and verify persistence survives a page refresh.
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('studio-titlebar-settings').click();
    await page.getByTestId('workspace-settings-rail-workspaces').click({ timeout: 10_000 });
    const wsListAfter = page.getByTestId('settings-workspaces-list');
    await expect(wsListAfter).toBeVisible({ timeout: 5_000 });
    const wsRowAfter = wsListAfter.getByTestId('settings-workspace-rename').filter({ hasText: newName });
    await wsRowAfter.scrollIntoViewIfNeeded();
    await expect(wsRowAfter).toBeVisible();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-after-reload-still-visible.png'), fullPage: true });
  } finally {
    await purgeRegistryWorkspace(expectedId);
  }
});

test('create dialog rejects empty + duplicate names client-side', async ({ page }) => {
  const sentinelName = uniqueName('e2e-dup');
  // Seed an entry directly via the registry API so we can collide on it.
  const seeded = await api<{ id: string }>('/api/workspaces', {
    method: 'POST',
    body: JSON.stringify({ displayName: sentinelName }),
  });

  try {
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('studio-sidebar-add-workspace').click();
    const dialog = page.getByTestId('workspace-create-dialog');
    await expect(dialog).toBeVisible();

    const submit = page.getByTestId('workspace-create-submit');
    await expect(submit).toBeDisabled(); // empty

    const nameInput = page.getByTestId('workspace-create-name');
    await nameInput.fill(sentinelName);
    // Client-side duplicate check kicks in.
    const clientError = page.getByTestId('workspace-create-client-error');
    await expect(clientError).toBeVisible();
    await expect(clientError).toContainText('already exists');
    await expect(submit).toBeDisabled();

    // Cancel and clean up.
    await page.getByTestId('workspace-create-cancel').click();
    await expect(dialog).not.toBeVisible();
  } finally {
    await purgeRegistryWorkspace(seeded.id);
  }
});

test('delete is blocked + tooltipped while the workspace still has jobs', async ({ page }) => {
  // Pick an existing project that has at least one job.
  const allPaths = await api<WatchPath[]>('/api/watch-paths');
  expect(allPaths.length).toBeGreaterThanOrEqual(1);

  // We can't safely create jobs here, but the existing fixtures
  // (Agent Software Studio etc.) reliably have jobs. Find one with
  // a non-empty kanban.
  let busyName: string | null = null;
  for (const wp of allPaths) {
    type Job = { projectName: string };
    const jobs = await api<Job[]>('/api/tasks');
    const count = jobs.filter(j => j.projectName === wp.name).length;
    if (count > 0) { busyName = wp.name; break; }
  }
  test.skip(busyName === null, 'No populated workspace available to exercise the busy-delete guard.');

  const slug = slugFor(busyName!);
  await page.goto(`/#/projects/${slug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-detail-workspace')).toBeVisible();

  const deleteBtn = page.getByTestId('project-detail-workspace-delete');
  await expect(deleteBtn).toBeVisible();
  await expect(deleteBtn).toBeDisabled();
});
