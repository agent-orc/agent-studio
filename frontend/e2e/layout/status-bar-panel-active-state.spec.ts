import { test, expect, Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * ASS-693: status-bar panel-trigger buttons must show a pressed/active
 * state bound to the open flag of the panel they toggle, and clicking an
 * active button closes the panel (toggle). The active state is exposed via
 * `aria-pressed` + the `statusbar__item--active` class.
 *
 * Isolate from the live backend's stored client defaults so app boot
 * doesn't clobber state (mirrors status-bar-and-header.spec.ts).
 */
test.beforeEach(async ({ page }) => {
  await page.route('**/api/clients/*/defaults', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ defaultCliType: null, defaultModel: null }),
    });
  });
});

async function gotoBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(800);
}

/**
 * Stub the shell's boot GETs so the app renders without a live backend: a
 * catch-all 200 keeps any unmocked poll from surfacing the global error
 * dialog (whose full-screen overlay would intercept status-bar clicks),
 * with a few endpoints shaped so the shell chrome mounts cleanly. Scoped to
 * the single test that uses it — the file's other tests target a live stack.
 * Mirrors the mocking pattern in e2e/board/card-live-state-by-lane.spec.ts.
 */
async function installBootMocks(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route
      .fulfill({ status: 200, contentType: 'application/json', body: '[]' })
      .catch(() => undefined));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  // Grouped board payload is a lane-keyed object (every lane an array); an
  // empty [] leaves the archive lane's `.length` read undefined and throws
  // into the global error dialog, so ship all lanes present-but-empty.
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [],
        progress: [], failedPickup: [], review: [], autoReview: [],
        humanReview: [], completed: [], archive: [],
      }),
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  // Archive lane pages GET /api/tasks/archive expecting { items, total };
  // the catch-all [] would leave `items` undefined and blow up the lane's
  // `archiveRemaining` computed, so shape it explicitly.
  await page.route('**/api/tasks/archive**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0 }) }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  // Quota report is a QuotaReport { snapshots: [...] }; the header-quota strip
  // does `r.snapshots.find(...)`, so the catch-all [] (→ snapshots undefined)
  // would throw into the global error dialog. Ship an empty snapshots list.
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ snapshots: [] }) }));
  // The CLI-usage detail panel (rendered when the caps section opens) folds
  // the token timeline; its sparkline iterates `timeline.cells`, so the
  // endpoints must return a TokenTimeline whose `cells`/`projects` are
  // present-but-empty arrays (an empty [] leaves them undefined → throws).
  await page.route('**/api/workspace/tokens/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ cells: [], projects: [] }) }));
  await page.route('**/api/workspace/tokens/expensive-jobs**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ jobs: [] }) }));
  // Per-CLI working-memory panel iterates `report.entries`.
  await page.route('**/api/cli/*/working-memory**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ entries: [] }) }));
}

/**
 * A panel trigger starts un-pressed, becomes pressed when clicked open, and
 * returns to un-pressed when clicked again (toggle). Asserted via
 * aria-pressed so it doubles as an accessibility check.
 */
async function expectTogglesActive(page: Page, testid: string): Promise<void> {
  const statusBar = page.getByTestId('status-bar');
  const button = statusBar.getByTestId(testid);
  await expect(button, `${testid} visible`).toBeVisible();
  await expect(button, `${testid} starts un-pressed`).toHaveAttribute('aria-pressed', 'false');

  await button.click();
  await expect(button, `${testid} pressed after open`).toHaveAttribute('aria-pressed', 'true');

  await button.click();
  await expect(button, `${testid} un-pressed after toggle close`).toHaveAttribute(
    'aria-pressed',
    'false',
  );
}

test.describe('Status bar panel buttons - active/toggle state', () => {
  test('Usage button reflects + toggles its open state', async ({ page }) => {
    await gotoBoard(page);
    const statusBar = page.getByTestId('status-bar');
    const usage = statusBar.getByTestId('status-bar-usage');
    await expect(usage).toHaveAttribute('aria-pressed', 'false');

    await usage.click();
    await expect(usage).toHaveAttribute('aria-pressed', 'true');

    // Usage now opens the workspace-settings home at its CLI-Management
    // section (the loose CLI-usage sidesheet was retired). That home is a
    // full-screen modal whose backdrop covers the status bar, so — like the
    // Settings button — it closes via the home's own ✕, not a re-click of
    // the now-occluded trigger. The active state must clear afterwards.
    await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
    await page.getByTestId('workspace-settings-close').click();
    await expect(usage).toHaveAttribute('aria-pressed', 'false');
  });

  test('Orchestrator button reflects + toggles its open state', async ({ page }) => {
    await gotoBoard(page);
    await expectTogglesActive(page, 'orch-side-sheet-toggle');
  });

  test('Settings button reflects its open state', async ({ page }) => {
    await gotoBoard(page);
    const statusBar = page.getByTestId('status-bar');
    const settings = statusBar.getByTestId('status-bar-settings');
    await expect(settings).toHaveAttribute('aria-pressed', 'false');

    await settings.click();
    await expect(settings).toHaveAttribute('aria-pressed', 'true');

    // The workspace-settings home is a full-screen modal whose backdrop
    // covers the status bar, so it closes via its own ✕ rather than by
    // re-clicking the (now-occluded) trigger. The active state must clear
    // once the panel's open flag flips back.
    await page.getByTestId('workspace-settings-close').click();
    await expect(settings).toHaveAttribute('aria-pressed', 'false');
  });

  test('each button tracks its own panel independently', async ({ page }) => {
    await gotoBoard(page);
    // Force dark explicitly — a fresh context defaults to light, so without
    // this the "dark" evidence screenshot would actually render in light.
    await setTheme(page, 'dark');
    const statusBar = page.getByTestId('status-bar');
    const usage = statusBar.getByTestId('status-bar-usage');
    const orchestrator = statusBar.getByTestId('orch-side-sheet-toggle');

    // Usage and Settings are now two sections of one home modal, so they are
    // no longer independent overlays. The orchestrator side sheet is a push
    // panel (it does not occlude the status bar), so pair it with Usage to
    // assert the active state tracks each panel's own flag, not a shared one.
    await orchestrator.click();
    await expect(orchestrator).toHaveAttribute('aria-pressed', 'true');
    await expect(usage).toHaveAttribute('aria-pressed', 'false');

    // Active-state evidence: capture the bar with one button pressed (taken
    // before opening the Usage home, whose modal backdrop covers the bar).
    await statusBar.screenshot({
      path: 'test-results/status-bar-active-dark.png',
    });

    // Opening the Usage home must not clear the orchestrator's own pressed
    // state — each button tracks its own panel's flag rather than a shared one.
    await usage.click();
    await expect(usage).toHaveAttribute('aria-pressed', 'true');
    await expect(orchestrator).toHaveAttribute('aria-pressed', 'true');
  });

  test('Usage and Settings never both show active at once', async ({ page }) => {
    // AGT-1809: "Usage" (CLI caps) is structurally the 'caps' section of the
    // one Workspace-settings home, while "Settings" opens that same home on
    // its overview. Because both pills carry the single `--studio-accent`
    // active fill, opening either must light EXACTLY one — never both (see
    // docs/quality/frontend/design-system.md, "one accent per rail").
    await installBootMocks(page);
    await gotoBoard(page);
    await setTheme(page, 'dark');
    const statusBar = page.getByTestId('status-bar');
    const usage = statusBar.getByTestId('status-bar-usage');
    const settings = statusBar.getByTestId('status-bar-settings');

    await expect(usage).toHaveAttribute('aria-pressed', 'false');
    await expect(settings).toHaveAttribute('aria-pressed', 'false');

    // Open Usage → its 'caps' section. Usage lights up; Settings must NOT,
    // even though the shared home (and thus `settingsOpen`) is now open.
    await usage.click();
    await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
    await expect(usage, 'Usage active while caps section open').toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(settings, 'Settings NOT active while Usage section open').toHaveAttribute(
      'aria-pressed',
      'false',
    );
    // Evidence: the bar with only Usage lit (never both pills orange).
    await statusBar.screenshot({
      path: 'test-results/status-bar-usage-only-active--mocked-dark.png',
    });

    // Switch the same home to its overview (non-'caps') section via the
    // Settings pill. The accent must move with it: Settings lights up and
    // Usage goes dark — the two are mutually exclusive, never both lit.
    await settings.click();
    await expect(settings, 'Settings active on overview section').toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(usage, 'Usage NOT active once off the caps section').toHaveAttribute(
      'aria-pressed',
      'false',
    );
    await statusBar.screenshot({
      path: 'test-results/status-bar-settings-only-active--mocked-dark.png',
    });
  });

  test('active state renders in light theme', async ({ page }) => {
    await gotoBoard(page);
    // setTheme stamps the attribute AND persists to localStorage; otherwise
    // the shell's theme effect reverts it on the next change-detection.
    await setTheme(page, 'light');
    const statusBar = page.getByTestId('status-bar');
    const usage = statusBar.getByTestId('status-bar-usage');
    await usage.click();
    await expect(usage).toHaveAttribute('aria-pressed', 'true');
    await statusBar.screenshot({
      path: 'test-results/status-bar-active-light.png',
    });
  });
});
