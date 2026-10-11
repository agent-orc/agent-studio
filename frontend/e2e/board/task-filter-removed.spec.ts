import { test, expect, type Page } from '@playwright/test';

/**
 * POLISH: "Taskfilter aus Filter-Liste entfernen".
 *
 * The operator asked for a standalone "Task" filter axis to be removed from
 * the board filter list. Investigation showed the board only ever shipped
 * four faceted axes - owner / project / type / tag - and never a separate
 * "task" axis (see results/diagnosis.md in the job folder). This spec is the
 * regression guard that locks that contract in:
 *
 *   1. The filter panel exposes the four real axes and NO standalone "Task"
 *      axis. The only heading containing the word "Task" is "Task type",
 *      which is the Type axis and must stay (acceptance #5).
 *   2. A legacy bookmark hash carrying `task:...` is silently ignored - it
 *      applies no filter and raises no error.
 *   3. Once the user touches a real filter, the rewritten hash is normalised
 *      and no longer carries the stale `task:` segment.
 */
test.describe('Task filter axis removed from filter list', () => {
  async function openBoard(page: Page): Promise<void> {
    const trigger = page.getByTestId('studio-project-picker-trigger');
    await expect(trigger).toBeVisible({ timeout: 10_000 });
    await trigger.click();
    const allItem = page.getByTestId('studio-project-picker-item-__all__');
    await expect(allItem).toBeVisible({ timeout: 5000 });
    await allItem.click();
    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 10_000 });
  }

  async function openFilterPanel(page: Page): Promise<void> {
    const filterBtn = page.getByTestId('studio-ab-filters');
    await expect(filterBtn).toBeVisible();
    await filterBtn.click();
    await expect(page.getByTestId('studio-sidebar')).toBeVisible();
    await expect(page.getByTestId('kanban-filter-sidesheet-inline')).toBeVisible();
  }

  test.beforeEach(async ({ page }) => {
    // Stub the UpdateService status endpoint so a stuck rollback modal
    // never blocks the UI (the update service runs on port 5039).
    await page.route('**/update/status', route =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ phase: 'idle', isRunning: false, behindBy: 0 }),
      })
    );
    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
    });
  });

  test('filter panel exposes the four faceted axes and no standalone Task axis', async ({ page }) => {
    await page.goto('/');
    await openBoard(page);
    await openFilterPanel(page);

    // The Type axis stays (acceptance #5): its section heading is "Task type"
    // and its chips render with the `kanban-filter-type-*` testids.
    await expect(page.getByRole('heading', { name: 'Task type' })).toBeVisible();
    await expect(page.getByTestId('kanban-filter-type-bug')).toBeVisible();

    // Tag + Owner axes stay. (AGT-2035 removed the Visibility / compact-cards
    // axis along with the abolished card-density feature.)
    await expect(page.getByTestId('kanban-filter-tag-list').or(page.getByText('No tags on this board.'))).toBeVisible();
    await expect(page.getByTestId('kanban-filter-owner-list').or(page.getByText('No owners registered.'))).toBeVisible();
    await expect(page.getByTestId('kanban-filter-visibility-compact')).toHaveCount(0);

    // No standalone "Task" filter axis: there is no `kanban-filter-task-*`
    // element (the type chips are `kanban-filter-type-*`, deliberately not
    // matched here), and no section heading is exactly "Task" or "Tasks".
    await expect(page.locator('[data-testid^="kanban-filter-task-"]')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Task', exact: true })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Tasks', exact: true })).toHaveCount(0);
  });

  test('legacy hash with task:... is silently ignored (no filter applied, no error)', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(String(e)));

    await page.goto('/#filters=task:legacy-bookmark-value');
    await openBoard(page);

    // The unknown `task:` segment must apply no filter, so the activity-bar
    // filter badge (which counts active faceted filters) stays absent.
    await expect(page.getByTestId('studio-ab-badge-filters')).toHaveCount(0);
    // And nothing threw while parsing the legacy hash.
    expect(errors).toEqual([]);
  });

  test('hash is normalised without task: after a real filter is applied', async ({ page }) => {
    await page.goto('/#filters=task:legacy-bookmark-value');
    await openBoard(page);
    await openFilterPanel(page);

    // Apply a real Type filter; this triggers the hash write path.
    await page.getByTestId('kanban-filter-type-bug').click();
    await expect(page.getByTestId('studio-ab-badge-filters')).toBeVisible();

    const decodedHash = await page.evaluate(() => decodeURIComponent(window.location.hash));
    expect(decodedHash).toContain('type:bug');
    expect(decodedHash).not.toContain('task:');
  });
});
