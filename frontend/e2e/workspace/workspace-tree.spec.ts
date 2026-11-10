import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * F46 Step 1: the Explorer sidebar renders a two-level workspace -> project
 * tree. Each registry workspace becomes its own collapsible folder header
 * (testid `studio-explorer-ws-group-<id>`); the project rows nest under it
 * as the second level. The outer "Workspaces" panel header and the
 * `studio-explorer-project-row-<name>` rows from the F27 contract are
 * preserved so the existing collapse/DnD specs keep passing.
 *
 * Runs against the live dev stack (backend 5030 / frontend 4010). When the
 * backend exposes no projects the tree shows an empty-state and the nesting
 * assertions short-circuit; the panel-header contract still runs.
 *
 * Screenshots land in <JOB_RESULTS_DIR>/screenshots when the agent
 * orchestrator sets JOB_RESULTS_DIR, otherwise next to other Playwright
 * artefacts under test-results/.
 */

const dest = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.join(__dirname, '..', '..', 'test-results', 'f46-screenshots');

const GROUP = '[data-testid^="studio-explorer-ws-group-"]';
const ROW = '[data-testid^="studio-explorer-project-row-"]';

async function gotoStudio(page: Page): Promise<void> {
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

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

test.describe('F46: Explorer two-level workspace -> project tree', () => {
  test.beforeAll(() => {
    fs.mkdirSync(dest, { recursive: true });
  });

  test('panel header is present and at least one workspace folder header renders', async ({ page }) => {
    await gotoStudio(page);

    const panel = page.getByTestId('studio-explorer-workspace-head');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await expect(panel).toHaveAttribute('aria-expanded', 'true');

    const groupCount = await page.locator(GROUP).count();
    if (groupCount === 0) {
      // Empty registry + zero projects => empty-state, no folder headers.
      await expect(page.getByText('No projects loaded')).toBeVisible();
      test.skip(true, 'No workspaces/projects loaded — second-level contract skipped.');
      return;
    }
    await expect(page.locator(GROUP).first()).toBeVisible();
  });

  test('Workspaces header plus opens the create-workspace dialog without collapsing the tree', async ({ page }) => {
    const updateRequests: string[] = [];
    page.on('request', request => { if (request.url().includes(':5039/')) updateRequests.push(request.url()); });
    await gotoStudio(page);

    const panel = page.getByTestId('studio-explorer-workspace-head');
    await expect(panel).toHaveAttribute('aria-expanded', 'true');

    await page.getByTestId('studio-explorer-add-workspace').click();
    const dialog = page.getByTestId('workspace-create-dialog');
    await expect(dialog).toBeVisible();
    await expect(panel).toHaveAttribute('aria-expanded', 'true');

    fs.mkdirSync(dest, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(dest, `explorer-create-${theme}.png`), fullPage: true });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(dest, 'explorer-create-390.png'), fullPage: true });

    await page.getByTestId('workspace-create-cancel').click();
    await expect(dialog).not.toBeVisible();
    expect(updateRequests).toEqual([]);
  });

  test('workspace-row plus opens project onboarding for that workspace without adding a tree row', async ({ page }) => {
    await gotoStudio(page);

    const action = page.locator('[data-testid^="studio-workspace-"][data-testid$="-add-project"]').first();
    if ((await action.count()) === 0) {
      test.skip(true, 'No real workspace folder available — add-project action skipped.');
      return;
    }

    await expect(page.getByText('New project', { exact: true })).toHaveCount(0);
    const testid = await action.getAttribute('data-testid');
    const workspaceId = testid?.replace(/^studio-workspace-/, '').replace(/-add-project$/, '') ?? '';
    expect(workspaceId).not.toBe('');
    const group = page.getByTestId(`studio-explorer-ws-group-${workspaceId}`);
    await expect(group).toHaveAttribute('aria-expanded', 'true');

    await action.click();
    const dialog = page.getByTestId('onboard-project-dialog');
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('onboard-project-workspace')).toHaveValue(workspaceId);
    await expect(group).toHaveAttribute('aria-expanded', 'true');

    await page.getByTestId('onboard-project-cancel').click();
    await expect(dialog).not.toBeVisible();
  });

  test('project rows nest under a workspace folder header', async ({ page }) => {
    await gotoStudio(page);

    const groups = page.locator(GROUP);
    if ((await groups.count()) === 0) {
      test.skip(true, 'No workspace folder headers — nesting contract skipped.');
      return;
    }
    if ((await page.locator(ROW).count()) === 0) {
      test.skip(true, 'No project rows loaded — nesting contract skipped.');
      return;
    }

    // The workspace folder header sits ABOVE the project rows it owns: in
    // document order the first group header must precede the first row.
    const firstGroup = groups.first();
    const firstRow = page.locator(ROW).first();
    const order = await firstGroup.evaluate((g, r) => {
      const pos = g.compareDocumentPosition(r as Node);
      // DOCUMENT_POSITION_FOLLOWING (4) => r comes after g.
      return (pos & Node.DOCUMENT_POSITION_FOLLOWING) ? 'after' : 'before';
    }, await firstRow.elementHandle());
    expect(order).toBe('after');
  });

  test('project rows are visibly indented under their workspace folder header', async ({ page }) => {
    await gotoStudio(page);

    const groups = page.locator(GROUP);
    if ((await groups.count()) === 0 || (await page.locator(ROW).count()) === 0) {
      test.skip(true, 'No groups/rows loaded - indent contract skipped.');
      return;
    }

    // Mechanism: the project-row wrapper carries the subtle nesting indent
    // (padding-left) so the row and its lane children shift right together.
    const wrapperPad = await page.locator(ROW).first().evaluate(
      (el) => Number.parseFloat(getComputedStyle(el).paddingLeft) || 0,
    );
    expect(wrapperPad).toBeGreaterThan(0);

    // Visual result: the project row's chevron sits to the right of the
    // workspace header's chevron, so the hierarchy reads at a glance.
    const wsChev = page.locator('[data-testid^="studio-explorer-ws-chevron-"]').first();
    const projChev = page.locator('[data-testid^="studio-explorer-project-chevron-"]').first();
    const wsBox = await wsChev.boundingBox();
    const projBox = await projChev.boundingBox();
    expect(wsBox).toBeTruthy();
    expect(projBox).toBeTruthy();
    expect(projBox!.x).toBeGreaterThan(wsBox!.x + 6);
  });

  test('a workspace folder header collapses its project rows', async ({ page }) => {
    await gotoStudio(page);

    const groups = page.locator(GROUP);
    if ((await groups.count()) === 0 || (await page.locator(ROW).count()) === 0) {
      test.skip(true, 'No groups/rows loaded — collapse contract skipped.');
      return;
    }

    const firstGroup = groups.first();
    await expect(firstGroup).toHaveAttribute('aria-expanded', 'true');
    const rowsBefore = await page.locator(ROW).count();
    expect(rowsBefore).toBeGreaterThan(0);

    // Collapse the first workspace folder; its rows must drop out of the DOM.
    await firstGroup.click();
    await expect(firstGroup).toHaveAttribute('aria-expanded', 'false');
    await expect.poll(() => page.locator(ROW).count()).toBeLessThan(rowsBefore);

    // Re-expand restores them.
    await firstGroup.click();
    await expect(firstGroup).toHaveAttribute('aria-expanded', 'true');
    await expect.poll(() => page.locator(ROW).count()).toBe(rowsBefore);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`visual evidence — workspace tree (${theme})`, async ({ page }) => {
      await gotoStudio(page);
      await setTheme(page, theme);
      await page.waitForTimeout(250);
      const sidebar = page.getByTestId('studio-sidebar');
      await sidebar.screenshot({ path: path.join(dest, `workspace-tree-${theme}.png`) });
    });
  }
});
