import { test, expect, type Page } from '@playwright/test';

/**
 * Feature `ui-allprojects-board-only-via-explorer-button-remove-openboard-nav`:
 * the cross-project "All projects" board used to have a dedicated Board
 * button at the top of the activity bar. That entry point is removed; the
 * board now opens only via the grid button in the Explorer panel header.
 * The Explorer panel toggle becomes the topmost element of the activity
 * bar. This spec locks all three requirements:
 *
 *   1. The activity-bar Board button (`studio-ab-board`) no longer exists.
 *   2. Explorer (`studio-ab-explorer`) is the first / topmost button.
 *   3. The Explorer-header grid button (`studio-explorer-show-all-projects`)
 *      focuses the always-mounted sticky `board:__all__` tab.
 *
 * Front-end-only concern, so the boot endpoints are stubbed with
 * empty-but-valid payloads (mirrors navigation-no-deadend.spec.ts) and no
 * live backend is required.
 */

const STICKY_TAB_KEY = 'board:__all__';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function bootStudio(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.includes('/api/v1/studio/board')) return json(EMPTY_GROUPED);
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) return json([]);
    return route.continue();
  });

  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
}

function tabBy(page: Page, key: string) {
  return page.locator(`.studio-tab[data-tab-key="${key}"]`);
}

test.describe('studio-shell · All-projects board opens only via Explorer header', () => {
  test.setTimeout(45_000);

  test('activity bar no longer carries a Board button', async ({ page }) => {
    await bootStudio(page);
    await expect(page.getByTestId('studio-activity-bar')).toBeVisible();
    await expect(page.getByTestId('studio-ab-board')).toHaveCount(0);
  });

  test('Explorer is the topmost element of the activity bar', async ({ page }) => {
    await bootStudio(page);
    // The first activity-bar button is the Explorer panel toggle.
    const firstBtn = page.locator('[data-testid="studio-activity-bar"] .studio-ab__btn').first();
    await expect(firstBtn).toHaveAttribute('data-testid', 'studio-ab-explorer');
    await expect(firstBtn).toHaveAttribute('data-panel', 'explorer');
  });

  test('Explorer-header grid button focuses the all-projects sticky board', async ({ page }) => {
    await bootStudio(page);

    // Move active focus off the sticky board so the grid button has to
    // switch context (not just no-op on an already-active tab).
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__', sticky: true },
          { kind: 'task', taskKey: 'fake-jobkey-grid' },
        ],
        activeKey: 'task:fake-jobkey-grid',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
    await expect(tabBy(page, 'task:fake-jobkey-grid')).toHaveClass(/studio-tab--active/);

    // The Explorer panel is the default panel, so its header grid button
    // is visible at boot. Clicking it opens / focuses the cross-project board.
    const gridBtn = page.getByTestId('studio-explorer-show-all-projects');
    await expect(gridBtn).toBeVisible();
    // The button follows the project tooltip standard: an `appTooltip`
    // directive plus an `aria-label` for assistive tech — it carries no
    // plain `title` attribute. Assert the accessible name instead.
    await expect(gridBtn).toHaveAttribute('aria-label', /Show all projects/);
    await gridBtn.click();

    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
    await expect(tabBy(page, 'task:fake-jobkey-grid')).not.toHaveClass(/studio-tab--active/);
  });
});
