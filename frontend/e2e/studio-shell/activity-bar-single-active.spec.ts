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
    if (url.includes('/api/v1/studio/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
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

  test('Settings opens from the header without a rail destination', async ({ page }) => {
    await bootStudio(page);
    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-explorer')).toHaveClass(/studio-ab__btn--active/);

    await page.getByTestId('studio-titlebar-settings').click();
    await expect(page.getByTestId('studio-ab-settings')).toHaveCount(0);
    await expect(activeButtons(page)).toHaveCount(0);
    await expect(page.getByTestId('studio-titlebar-settings')).toBeVisible();
  });

  test('keyboard focus on the header Settings control does not change the rail marker', async ({ page }) => {
    await bootStudio(page);
    await page.getByTestId('studio-titlebar-settings').focus();
    await expect(page.getByTestId('studio-titlebar-settings')).toBeFocused();
    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-explorer')).toHaveClass(/studio-ab__btn--active/);
  });

  // AGT-3351: the marker follows the most recent activation — a sidebar panel
  // opened over a Dossier tab takes the marker; the destination takes it back.
  async function openDossierTab(page: Page): Promise<void> {
    await page.getByTestId('studio-ab-workbenches').click();
    await expect(page.getByTestId('studio-ab-workbenches')).toHaveClass(/studio-ab__btn--active/);
    await expect(activeButtons(page)).toHaveCount(1);
  }

  for (const panel of ['chats', 'filters'] as const) {
    test(`Dossier tab + ${panel}: only the ${panel} button is marked`, async ({ page }) => {
      await bootStudio(page);
      await openDossierTab(page);

      await page.getByTestId(`studio-ab-${panel}`).click();

      await expect(activeButtons(page)).toHaveCount(1);
      await expect(page.getByTestId(`studio-ab-${panel}`)).toHaveClass(/studio-ab__btn--active/);
      await expect(page.getByTestId('studio-ab-workbenches')).not.toHaveClass(/studio-ab__btn--active/);
    });
  }

  test('clicking Dossiers moves the marker back while the Chats panel stays open', async ({ page }) => {
    await bootStudio(page);
    await openDossierTab(page);
    await page.getByTestId('studio-ab-chats').click();
    await expect(page.getByTestId('studio-ab-chats')).toHaveClass(/studio-ab__btn--active/);

    await page.getByTestId('studio-ab-workbenches').click();

    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-workbenches')).toHaveClass(/studio-ab__btn--active/);
    await expect(page.locator('app-chat-sessions-panel')).toBeVisible();
  });

  test('hiding the Chats panel hands the marker back to Dossiers', async ({ page }) => {
    await bootStudio(page);
    await openDossierTab(page);
    await page.getByTestId('studio-ab-chats').click();
    await expect(page.getByTestId('studio-ab-chats')).toHaveClass(/studio-ab__btn--active/);

    await page.getByTestId('studio-ab-chats').click();

    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-workbenches')).toHaveClass(/studio-ab__btn--active/);
  });

  test('the Chats marker over a Dossier tab survives a reload', async ({ page }) => {
    await bootStudio(page);
    await openDossierTab(page);
    await page.getByTestId('studio-ab-chats').click();
    await expect(page.getByTestId('studio-ab-chats')).toHaveClass(/studio-ab__btn--active/);

    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });

    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-chats')).toHaveClass(/studio-ab__btn--active/);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`evidence screenshot — Dossier tab with Chats open (${theme})`, async ({ page }, testInfo) => {
      await bootStudio(page);
      await page.evaluate(t => {
        localStorage.setItem('atp.studio.theme', t);
        document.documentElement.dataset['studioTheme'] = t;
      }, theme);
      await openDossierTab(page);
      await page.getByTestId('studio-ab-chats').click();
      await expect(activeButtons(page)).toHaveCount(1);
      await page.waitForTimeout(150);

      const name = `activity-bar-dossier-chats--${theme}.png`;
      const dir = process.env.EVIDENCE_DIR;
      const path = dir ? `${dir}/${name}` : testInfo.outputPath(name);
      await page.getByTestId('studio-activity-bar').screenshot({ path });
      await testInfo.attach(name, { path, contentType: 'image/png' });
    });
  }

  test('the rail has no Runbook item and a stored Runbook panel opens the Explorer', async ({ page }) => {
    await page.addInitScript(() => {
      if (!sessionStorage.getItem('runbook-seeded')) {
        sessionStorage.setItem('runbook-seeded', '1');
        localStorage.setItem('atp.studio.panelState.v1', JSON.stringify({ active: 'runbook', visible: true }));
      }
    });
    await bootStudio(page);

    await expect(page.getByTestId('studio-ab-runbook')).toHaveCount(0);
    await expect(activeButtons(page)).toHaveCount(1);
    await expect(page.getByTestId('studio-ab-explorer')).toHaveClass(/studio-ab__btn--active/);
  });
});
