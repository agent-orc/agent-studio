import { test, expect, type Page } from '@playwright/test';

/**
 * Bug `human-decision-needed-bug-navigation-deadend-when-no-task-open`:
 * when every editor tab is closed, the studio shell used to render an
 * empty "Welcome" limbo with no clear way back to the board.
 *
 * The cross-project "All projects" board (`board:__all__`) is now an
 * ordinary, closable tab like every other tab — it carries a close X, no
 * pin glyph, and no `data-sticky` marker. The "no dead end" guarantee is
 * preserved by a recovery path that does not depend on an un-closable tab:
 *
 *   1. Closing every tab does NOT drop the user into blank limbo. The
 *      editor surface renders the creative idle empty-state
 *      (`studio-empty-state`) inside the welcome screen, which offers
 *      explicit ways back in (open project chat / pick a project board).
 * The activity bar no longer carries a dedicated Board button (removed so
 * the cross-project "All projects" board opens only via the grid button in
 * the Explorer panel header). That entry point is covered by
 * `activity-bar-board-removed.spec.ts`.
 *
 * This spec exercises that recovery path so a regression fails loudly.
 */

const ALL_BOARD_KEY = 'board:__all__';

/** Empty-but-valid GroupedJobs payload (mirrors the service signal default). */
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function bootStudio(page: Page): Promise<void> {
  // This spec exercises a purely front-end concern (closable tabs + recovery
  // paths), so it must not depend on a live backend. With the backend down
  // every boot GET 500s and the shell raises a blocking <app-error-dialog>
  // overlay that intercepts pointer events (tab close X). Stub the boot
  // endpoints with empty-but-valid payloads so no overlay ever appears. The
  // stub persists for the page lifetime, covering the initial load, every
  // reload, and background polls. Against a live backend it is a harmless
  // no-data response that does not affect the navigation assertions.
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    // Only these four boot GETs surface a *blocking* error-dialog overlay
    // when they fail; stub them with empty-but-valid payloads.
    if (url.includes('/api/v1/studio/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/v1/studio/board')) return json(EMPTY_GROUPED);
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) {
      return json([{ name: 'Agent Software Studio', path: '/workspace/agent-software-studio' }]);
    }
    // Everything else (quota, workspaces, config, …) is left to fail the way
    // the app already handles it — inline, non-blocking. Returning a wrong-
    // shaped success here would crash a consumer (e.g. the header quota cards).
    return route.continue();
  });

  // Each Playwright test runs in its own fresh browser context, so
  // localStorage already starts empty and the tab-state service seeds the
  // All-projects board tab on construction. We deliberately do NOT register a
  // persistent addInitScript that clears the storage key: such a script
  // re-runs on every navigation, and the seeded tests below set the key and
  // then reload — a persistent clear would wipe the seed before the app boots.
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
}

function tabBy(page: Page, key: string) {
  return page.locator(`.studio-tab[data-tab-key="${key}"]`);
}

test.describe('studio-shell · navigation has no dead end', () => {
  test.setTimeout(45_000);

  test('All-projects board is mounted at boot as a normal, closable tab', async ({ page }) => {
    await bootStudio(page);

    const board = tabBy(page, ALL_BOARD_KEY);
    await expect(board).toBeVisible();
    // It is a plain tab: a close X, no pin glyph, no sticky marker.
    await expect(board.locator('.studio-tab__close')).toBeVisible();
    await expect(board.locator('[data-testid="studio-tab-pin"]')).toHaveCount(0);
    await expect(board).not.toHaveAttribute('data-sticky', 'true');
  });

  test('closing every tab reveals the creative idle empty-state, not blank limbo', async ({ page }) => {
    await bootStudio(page);

    // Seed two extra tabs plus the All-projects board via the persistence
    // boundary so the assertion doesn't depend on real jobs in the fixture
    // backend. The service restores them on next render.
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__' },
          { kind: 'task', taskKey: 'fake-jobkey-a' },
          { kind: 'task', taskKey: 'fake-jobkey-b' },
        ],
        activeKey: 'task:fake-jobkey-b',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

    await expect(page.locator('.studio-tab')).toHaveCount(3);
    const board = tabBy(page, ALL_BOARD_KEY);
    const taskA = tabBy(page, 'task:fake-jobkey-a');
    const taskB = tabBy(page, 'task:fake-jobkey-b');

    // Close every tab, including the All-projects board.
    await taskB.locator('.studio-tab__close').click();
    await expect(taskB).toHaveCount(0);
    await taskA.locator('.studio-tab__close').click();
    await expect(taskA).toHaveCount(0);
    await board.locator('.studio-tab__close').click();
    await expect(board).toHaveCount(0);

    // No closable editor tabs remain — but instead of a blank dead end the
    // shell shows a bounded idle empty-state inside the welcome screen,
    // which offers explicit ways back in. That is the recovery path the bug
    // was filed against.
    await expect(page.locator('.studio-tab[data-tab-key]')).toHaveCount(0);
    await expect(page.getByTestId('studio-welcome')).toBeVisible();
    await expect(page.getByTestId('studio-empty-state')).toBeVisible();
    await expect(page.getByTestId('studio-empty-subtitle')).toHaveText('No tabs open.');
    await expect(page.getByTestId('studio-welcome-chat-hint'))
      .toContainText('Open project chat');
    await expect(page.getByTestId('studio-welcome-open-chat')).toBeVisible();
    await expect(page.getByTestId('studio-welcome-add-task')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'New task', exact: true })).toHaveCount(0);
  });

  test('Epics is a normal closeable editor tab', async ({ page }) => {
    await bootStudio(page);
    await page.evaluate(() => {
      const payload = {
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__' },
          { kind: 'epics', projectName: null },
        ],
        activeKey: 'epics:__all__',
      };
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    });
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

    const epicsTab = tabBy(page, 'epics:__all__');
    await expect(epicsTab).toBeVisible();
    await expect(epicsTab).toHaveClass(/studio-tab--active/);
    await expect(epicsTab).not.toHaveAttribute('data-sticky', 'true');
    await expect(epicsTab.locator('[data-testid="studio-tab-pin"]')).toHaveCount(0);
    await expect(epicsTab.locator('.studio-tab__close')).toBeVisible();
    await expect(page.getByTestId('epic-overview-close')).toHaveCount(0);

    await epicsTab.locator('.studio-tab__close').click();
    await expect(epicsTab).toHaveCount(0);
    // Closing Epics falls back to the trailing tab, the All-projects board.
    await expect(tabBy(page, ALL_BOARD_KEY)).toHaveClass(/studio-tab--active/);
  });

  test('All-projects board tab persists across a reload', async ({ page }) => {
    await bootStudio(page);
    await expect(tabBy(page, ALL_BOARD_KEY)).toBeVisible();
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
    const board = tabBy(page, ALL_BOARD_KEY);
    await expect(board).toBeVisible();
    await expect(board.locator('.studio-tab__close')).toBeVisible();
  });
});
