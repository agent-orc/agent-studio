import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import type { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

/**
 * ASS-658/ASS-597: a project node in the Workspaces Explorer exposes exactly
 * five baseline project-scoped links: "Board" (the kanban), "Deck", "Wiki",
 * "Dossiers", and "Epics" (overview). A catalogue-owned Living Style Guide
 * may add one promoted sibling. The retired per-lane children (active /
 * human review / archive) stay gone, and Epics opens scoped to the
 * clicked project rather than the global rollup.
 *
 * Runs against the live backend's project list; short-circuits when the
 * configured board has no projects.
 */

async function gotoStudio(page: Page): Promise<void> {
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local',
      bootstrapRequired: false,
      authenticated: true,
      user: null,
    }),
  }));
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 10_000 });
}

async function proxyBackend(page: Page, baseUrl: string): Promise<void> {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname)) {
      await json({ models: [], source: 'deck-icon-evidence' });
      return;
    }
    if (url.pathname === '/api/cli/quota') {
      await json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
      return;
    }
    if (url.pathname === '/api/cli/usage') {
      await json({ at: new Date().toISOString(), sessions: [] });
      return;
    }
    const response = await route.fetch({
      url: `${baseUrl}${url.pathname}${url.search}`,
      timeout: 30_000,
    });
    await route.fulfill({ response });
  });
}

/** Expand the first project row and return its locator + name. */
async function expandFirstProject(page: Page): Promise<{ row: ReturnType<Page['locator']>; name: string } | null> {
  const row = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
  if (!await row.isVisible({ timeout: 10_000 }).catch(() => false)) return null;
  const name = (await row.getAttribute('data-project-name')) ?? '';
  const label = row.locator('button.tree-row').first();
  const children = row.locator('.studio-tree-children');
  if (!(await children.count()) || !(await children.isVisible())) {
    await label.click();
    await expect(children).toBeVisible({ timeout: 3_000 });
  }
  return { row, name };
}

const DECK_ICON_SCREENSHOTS = process.env['JOB_RESULTS_DIR']
  ? path.resolve(process.env['JOB_RESULTS_DIR'])
  : path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'deck-icon');
const LEGACY_DECK_NAME = ['Project', 'Hub'].join(' ');

test.describe('Explorer · project links to Board / Deck / Wiki / Epics', () => {
  test('Deck naming is visible in the tree and opened surface in both themes', async ({ page, devBackend }, testInfo) => {
    test.setTimeout(120_000);
    expect(devBackend.port).toBeGreaterThan(0);
    mkdirSync(DECK_ICON_SCREENSHOTS, { recursive: true });
    const clientResponse = await fetch(`${devBackend.baseUrl}/api/clients/register`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ displayName: `deck-icon-evidence-${Date.now().toString(36)}` }),
    });
    const clientText = await clientResponse.text();
    expect(clientResponse.ok, clientText).toBe(true);
    const client = JSON.parse(clientText) as { id: string };
    const mutationHeaders = {
      'content-type': 'application/json',
      'X-Client-Id': client.id,
    };
    const workspaceResponse = await fetch(`${devBackend.baseUrl}/api/workspaces`);
    const workspaces = await workspaceResponse.json() as { id: string }[];
    let createdWorkspaceId: string | null = null;
    if (workspaces.length === 0) {
      const createWorkspaceResponse = await fetch(`${devBackend.baseUrl}/api/workspaces`, {
        method: 'POST',
        headers: mutationHeaders,
        body: JSON.stringify({ displayName: 'Deck Icon Evidence' }),
      });
      const createWorkspaceText = await createWorkspaceResponse.text();
      expect(createWorkspaceResponse.ok, createWorkspaceText).toBe(true);
      const workspace = JSON.parse(createWorkspaceText) as { id: string };
      createdWorkspaceId = workspace.id;
      workspaces.push(workspace);
    }
    const uniqueSuffix = Date.now().toString(36);
    const projectResponse = await fetch(`${devBackend.baseUrl}/api/projects`, {
      method: 'POST',
      headers: mutationHeaders,
      body: JSON.stringify({
        sourceType: 'local-folder',
        workspaceId: workspaces[0].id,
        displayName: `Deck Icon Evidence ${uniqueSuffix}`,
        shortCode: `DI${uniqueSuffix.slice(-4)}`.toUpperCase(),
        rootPath: devBackend.workspace,
        repositoryPath: devBackend.workspace,
      }),
    });
    const projectText = await projectResponse.text();
    expect(projectResponse.ok, projectText).toBe(true);
    const createdProject = JSON.parse(projectText) as { id: string };
    await proxyBackend(page, devBackend.baseUrl);

    try {
      for (const theme of ['light', 'dark'] satisfies Theme[]) {
        await gotoStudio(page);
        await setTheme(page, theme);
        await dismissDevErrorDialog(page);

        const expanded = await expandFirstProject(page);
        expect(expanded, 'The dev-backend fixture must expose a project').not.toBeNull();
        const { name } = expanded!;
        const deckRow = page.getByTestId(`studio-explorer-project-deck-${name}`);

        await expect(deckRow).toContainText('Deck');
        await expect(deckRow.getByRole('button', { name: `Deck, ${name}` })).toBeVisible();
        const deckIcon = deckRow.locator('app-studio-icon svg');
        await expect(deckIcon).toHaveCount(1);
        await expect(deckIcon).toHaveAttribute('viewBox', '0 0 24 24');
        await expect(deckIcon).toHaveAttribute('stroke', 'currentColor');
        await expect(deckIcon.locator('path')).toHaveAttribute('d', 'M9 3v18M9 10h12');
        await expect(deckIcon.locator('circle')).toHaveAttribute('cy', '15.5');

        const deckShortcut = page.locator('button[aria-label="Open Deck"]').first();
        await expect(deckShortcut).toHaveAttribute('aria-label', 'Open Deck');

        await deckRow.getByRole('button', { name: `Deck, ${name}` }).click();
        await expect(page.getByRole('tab', { name: / · Deck$/ }))
          .toHaveAttribute('aria-selected', 'true');
        await expect(page.getByTestId('deck-sidebar-header')).toContainText('Deck');
        await page.getByTestId('project-shell').hover({ position: { x: 400, y: 300 } });

        await expect(page.locator('body')).not.toContainText(LEGACY_DECK_NAME);
        await expect(page.locator(
          `[aria-label*="${LEGACY_DECK_NAME}"], [title*="${LEGACY_DECK_NAME}"]`,
        )).toHaveCount(0);

        const screenshotPath = path.join(
          DECK_ICON_SCREENSHOTS,
          `deck-icon-in-context--real-${theme}.png`,
        );
        await page.screenshot({ path: screenshotPath, fullPage: true });
        await testInfo.attach(`Deck tree and opened surface (${theme})`, {
          path: screenshotPath,
          contentType: 'image/png',
        });
      }
    } finally {
      await fetch(`${devBackend.baseUrl}/api/projects/${createdProject.id}`, {
        method: 'DELETE',
        headers: { 'X-Client-Id': client.id },
      });
      if (createdWorkspaceId) {
        await fetch(`${devBackend.baseUrl}/api/workspaces/${createdWorkspaceId}`, {
          method: 'DELETE',
          headers: { 'X-Client-Id': client.id },
        });
      }
    }
  });

  test('expanded project shows the baseline project links, optional Style Guide, and no lane rows', async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — project-link contract skipped.');
      return;
    }
    const { row, name } = expanded;
    const children = row.locator('.studio-tree-children');

    // Five baseline rows plus the catalogue-promoted Style Guide when this
    // project owns the living standard.
    const rows = children.locator('.tree-row');
    const rowCount = await rows.count();
    expect(rowCount).toBeGreaterThanOrEqual(5);
    expect(rowCount).toBeLessThanOrEqual(6);

    // Each is addressable by its stable testid.
    await expect(page.getByTestId(`studio-explorer-project-board-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-deck-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-wiki-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-workbenches-${name}`)).toBeVisible();
    await expect(page.getByTestId(`studio-explorer-project-epics-${name}`)).toBeVisible();

    // The retired per-lane labels must not appear under the project node.
    const childText = (await children.innerText()).toLowerCase();
    for (const gone of ['active', 'human review', 'archive', 'project view']) {
      expect(childText).not.toContain(gone);
    }
    expect(childText).toContain('board');
    expect(childText).toContain('deck');
    expect(childText).not.toContain('backlog');
    expect(childText).toContain('epics');
  });

  test('"Board" opens the project kanban', async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded — Board-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-board-${expanded.name}`).click();
    await expect(page.getByTestId(`studio-tab-board:${expanded.name}`))
      .toHaveAttribute('aria-selected', 'true', { timeout: 5_000 });

    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(expanded.name);
    await expect(page.getByTestId('studio-titlebar-workspace')).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-active-tab')).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-crumbs')).not.toContainText('Board');
  });

  test('"Deck" opens the Deck', async ({ page, devBackend }) => {
    expect(devBackend.port).toBeGreaterThan(0);
    await gotoStudio(page);
    const expanded = await expandFirstProject(page);
    if (!expanded) {
      test.skip(true, 'No projects loaded - Deck-link contract skipped.');
      return;
    }
    await page.getByTestId(`studio-explorer-project-deck-${expanded.name}`)
      .getByRole('button', { name: `Deck, ${expanded.name}` })
      .click();
    await expect(page.getByRole('tab', { name: / · Deck$/ }))
      .toHaveAttribute('aria-selected', 'true', { timeout: 5_000 });
  });

});
