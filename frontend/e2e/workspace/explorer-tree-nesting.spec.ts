import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * AGT-2057 regression: the Explorer workspace tree renders three visible
 * levels: workspace folder -> project -> project destinations (Board /
 * Project Hub / Wiki / Dossiers, plus any Project URL rows). The destinations
 * must be inset ONE level below their project row so the project reads as
 * clearly superordinate; they must never render flush beside it.
 *
 * How the regression shipped: the AGT-2037 nav "pull the 3rd level up to the
 * 2nd" change flattened the destination rows from `level="child"` (44px inset)
 * to `level="root"` (8px), so Board/Hub/Wiki/Dossiers aligned with the project
 * avatar and looked like siblings. The unit spec had been updated to *expect*
 * the flat layout, so it stayed green; a real-browser geometry check is the
 * only thing that catches this, which is why this lives in E2E.
 *
 * Runs against the live stack (dev 4010 / stable 4011 via PW_TARGET). Skips
 * gracefully when the backend exposes no expandable projects.
 */

const dest = process.env.JOB_RESULTS_DIR
  ? path.join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : path.join(__dirname, '..', '..', 'test-results', 'agt-2057-nesting');

const ROW = '[data-testid^="studio-explorer-project-row-"]';

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-testid^="studio-explorer-project-row-"]').first())
    .toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(400);
}

/** First project row that actually owns a chevron (i.e. is expandable). */
async function firstExpandableRow(page: Page) {
  const rows = page.locator(ROW);
  const count = await rows.count();
  for (let i = 0; i < count; i++) {
    const row = rows.nth(i);
    if (await row.locator('button.tree-row .tree-row__chev').count()) return row;
  }
  return null;
}

test.describe('AGT-2057: Explorer tree destination nesting', () => {
  test.beforeAll(() => {
    fs.mkdirSync(dest, { recursive: true });
  });

  test('project destinations inset one level below the project row', async ({ page }) => {
    await gotoStudio(page);

    const row = await firstExpandableRow(page);
    if (!row) {
      test.skip(true, 'No expandable project rows loaded, nesting contract skipped.');
      return;
    }

    // Expand the project so its destination rows render.
    const projectButton = row.locator('button.tree-row').first();
    if ((await row.locator('.studio-tree-children').count()) === 0) {
      await projectButton.click();
      await expect(row.locator('.studio-tree-children')).toHaveCount(1);
    }

    const board = row.locator('[data-testid^="studio-explorer-project-board-"]').first();
    await expect(board).toBeVisible();

    // Mechanism: the destination rows carry the `tree-row--child` inset, the
    // project row stays `tree-row--root`. This is what AGT-2037 broke.
    await expect(board).toHaveClass(/tree-row--child/);
    await expect(projectButton).toHaveClass(/tree-row--root/);
    expect(await row.locator('.studio-tree-children .tree-row--root').count()).toBe(0);

    // Visual result: the destination glyph sits clearly to the right of the
    // project's own glyph/avatar, so the child reads as nested, not a sibling.
    const projGlyph = projectButton.locator('.tree-row__glyph, .tree-row__glyph-icon').first();
    const boardGlyph = board.locator('.tree-row__glyph-icon, .tree-row__glyph').first();
    const projBox = await projGlyph.boundingBox();
    const boardBox = await boardGlyph.boundingBox();
    expect(projBox, 'project glyph box').toBeTruthy();
    expect(boardBox, 'board glyph box').toBeTruthy();
    // A full inset step (44px child vs 8px root, minus the project's chevron
    // column) lands the child glyph well to the right of the project glyph.
    expect(boardBox!.x).toBeGreaterThan(projBox!.x + 8);
  });

  test('project expand/collapse toggles reliably across repeated clicks', async ({ page }) => {
    await gotoStudio(page);

    const row = await firstExpandableRow(page);
    if (!row) {
      test.skip(true, 'No expandable project rows loaded, toggle contract skipped.');
      return;
    }
    const projectButton = row.locator('button.tree-row').first();
    const children = row.locator('.studio-tree-children');

    // Normalise to collapsed.
    if ((await children.count()) > 0) {
      await projectButton.click();
      await expect(children).toHaveCount(0);
    }

    // Three full expand/collapse cycles must each take effect.
    for (let cycle = 0; cycle < 3; cycle++) {
      await projectButton.click();
      await expect(children, `expand cycle ${cycle}`).toHaveCount(1);
      await expect(projectButton).toHaveAttribute('aria-expanded', 'true');

      await projectButton.click();
      await expect(children, `collapse cycle ${cycle}`).toHaveCount(0);
      await expect(projectButton).toHaveAttribute('aria-expanded', 'false');
    }
  });

  test('visual evidence: expanded tree nesting', async ({ page }) => {
    await gotoStudio(page);

    const rows = page.locator(ROW);
    const count = await rows.count();
    if (count === 0) {
      test.skip(true, 'No project rows loaded, screenshot skipped.');
      return;
    }
    // Expand up to four projects so the nesting reads across several projects.
    for (let i = 0; i < Math.min(count, 4); i++) {
      const row = rows.nth(i);
      if (!(await row.locator('button.tree-row .tree-row__chev').count())) continue;
      if ((await row.locator('.studio-tree-children').count()) === 0) {
        await row.locator('button.tree-row').first().click();
        await page.waitForTimeout(120);
      }
    }
    await page.waitForTimeout(250);
    const sidebar = page.getByTestId('studio-sidebar');
    await sidebar.screenshot({ path: path.join(dest, 'explorer-tree-nesting.png') });
  });
});
