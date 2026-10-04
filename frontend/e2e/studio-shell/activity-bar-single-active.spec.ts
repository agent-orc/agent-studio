import { test, expect, type Page } from '@playwright/test';

/**
 * AGT-2042 regression: the Activity Bar must mark exactly ONE item as active
 * at a time — the item whose surface is currently shown.
 *
 * Before the fix the active marker came from two independent sources: the
 * sidebar toggle (`activePanel` + `sidebarVisible`) lit Explorer, while the
 * editor route (`activeTab().kind`) independently lit editor destinations.
 * Opening one while the Explorer sidebar stayed open lit
 * BOTH buttons. The shell now funnels both sources through one resolved key
 * (`resolveActiveActivityKey`), so `.studio-ab__btn--active` can appear on at
 * most one button.
 *
 * Front-end-only concern, so the boot endpoints are stubbed with
 * empty-but-valid payloads (mirrors activity-bar-board-removed.spec.ts) and no
 * live backend is required.
 */

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

const activeButtons = (page: Page) =>
  page.locator('[data-testid="studio-activity-bar"] .studio-ab__btn--active');

test.describe('studio-shell · Activity Bar marks exactly one active item', () => {
  test.setTimeout(45_000);

  test('opening Settings while Explorer sidebar is open leaves only ONE active marker', async ({ page }) => {
    await bootStudio(page);

    // Baseline: Explorer sidebar is the default panel, so exactly one marker
    // (Explorer) is active.
    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-explorer')).toHaveClass(/studio-ab__btn--active/);

    await page.getByTestId('studio-ab-settings').click();

    // Still exactly one marker, and it moved to Settings (the shown surface).
    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-settings')).toHaveClass(/studio-ab__btn--active/);
    await expect(page.getByTestId('studio-ab-explorer')).not.toHaveClass(/studio-ab__btn--active/);
  });

  test('opening Settings never lights a second marker', async ({ page }) => {
    await bootStudio(page);
    await page.getByTestId('studio-ab-settings').click();

    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-settings')).toHaveClass(/studio-ab__btn--active/);
  });

  test('keyboard focus does not set an active marker (focus != active)', async ({ page }) => {
    await bootStudio(page);
    await expect(activeButtons(page)).toHaveCount(1);

    // Move keyboard focus onto the Settings button WITHOUT activating it.
    await page.getByTestId('studio-ab-settings').focus();
    await expect(page.getByTestId('studio-ab-settings')).toBeFocused();

    // Focusing changes nothing about the active marker: still exactly one, and
    // still Explorer (the shown surface), not the focused Settings button.
    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-settings')).not.toHaveClass(/studio-ab__btn--active/);
    await expect(page.getByTestId('studio-ab-explorer')).toHaveClass(/studio-ab__btn--active/);
  });

  test('evidence screenshot — one active marker with Settings open', async ({ page }, testInfo) => {
    await bootStudio(page);
    await page.getByTestId('studio-ab-settings').click();
    await expect(activeButtons(page)).toHaveCount(1);

    const bar = page.getByTestId('studio-activity-bar');
    const dir = process.env.EVIDENCE_DIR;
    const path = dir
      ? `${dir}/activity-bar-single-active--mocked.png`
      : testInfo.outputPath('activity-bar-single-active--mocked.png');
    await bar.screenshot({ path });
    await testInfo.attach('activity-bar-single-active--mocked', { path, contentType: 'image/png' });
  });
});
