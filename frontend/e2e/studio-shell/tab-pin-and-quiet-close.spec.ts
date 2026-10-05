/**
 * AGT-2672 — shell tab pinning + hover-only close affordance.
 *
 * Locks the two halves of the feature against regression:
 *   1. A pinned tab sits leftmost, renders compact, shows a pin glyph instead
 *      of a close X, ignores middle-click, survives "Close Others", and comes
 *      back pinned after a reload.
 *   2. An unpinned tab shows no close glyph at rest; the X appears on hover
 *      and on the active tab (VS Code style). Middle-click still closes.
 *
 * The API is fully mocked, so the spec needs only the frontend dev server.
 */
import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import type { Theme } from '../helpers/theme';

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
  escalated: [], completed: [], archive: [],
};

const PROJECTS = ['Agent Taskboard', 'Beta Service', 'Gamma Tools'];

const TABS = [
  { kind: 'board', projectName: '__all__' },
  { kind: 'hub', projectName: 'Agent Taskboard', section: 'overview' },
  { kind: 'hub', projectName: 'Beta Service', section: 'overview' },
  { kind: 'epics', projectName: null },
  { kind: 'workspace-settings' },
  { kind: 'hub', projectName: 'Gamma Tools', section: 'overview' },
];

const ACTIVE = 'hub:Beta Service';
const GAMMA = 'hub:Gamma Tools';
const STORAGE_KEY = 'atp.studio.tabs.v1';

/**
 * The shell defaults to the Light theme, so a dark-theme shot has to seed the
 * preference before the first paint rather than flip it afterwards.
 */
async function boot(page: Page, theme: Theme = 'dark'): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify(body),
    });
    if (url.includes('/api/v1/studio/auth/status')) {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/v1/studio/board')) return json(EMPTY_GROUPED);
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) {
      return json(PROJECTS.map((name, i) => ({ name, path: `/mock/project-${i + 1}` })));
    }
    if (url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) return json([]);
    return route.continue();
  });
  // Seed once only: a reload must read what the app persisted, not this seed.
  await page.addInitScript(({ tabs, key, storageKey, studioTheme }) => {
    localStorage.setItem('atp.studio.theme', studioTheme);
    if (!localStorage.getItem(storageKey)) {
      localStorage.setItem(storageKey, JSON.stringify({ v: 1, tabs, activeKey: key }));
    }
  }, { tabs: TABS, key: ACTIVE, storageKey: STORAGE_KEY, studioTheme: theme });
  await page.goto('/');
  await expect(page.getByTestId('studio-tab-list')).toBeVisible({ timeout: 60_000 });
}

const tab = (page: Page, key: string): Locator => page.getByTestId(`studio-tab-${key}`);

/** Left-to-right order of the tab strip, as tab keys. */
async function tabOrder(page: Page): Promise<string[]> {
  return page.getByTestId('studio-tab-list')
    .locator('[data-tab-key]')
    .evaluateAll(nodes => nodes.map(n => (n as HTMLElement).dataset['tabKey'] ?? ''));
}

async function pinViaContextMenu(page: Page, key: string): Promise<void> {
  await tab(page, key).click({ button: 'right' });
  const pin = page.getByTestId('studio-tab-ctx-item-toggle-pin');
  await expect(pin).toHaveText('Pin');
  await pin.click();
  await expect(tab(page, key)).toHaveAttribute('data-pinned', 'true');
}

/**
 * Park the pointer clear of every interactive row so no hover state leaks
 * into a shot. Chromium only re-evaluates `:hover` on a real move, so this
 * takes two hops and waits a frame before the caller screenshots.
 */
async function restPointer(page: Page): Promise<void> {
  await page.mouse.move(900, 400);
  await page.mouse.move(1270, 790);
  await page.evaluate(() => new Promise<void>(resolve => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
  }));
}

async function shotStrip(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const dir = process.env.JOB_RESULTS_DIR;
  const path = dir ? `${dir}/${name}.png` : testInfo.outputPath(`${name}.png`);
  await page.getByTestId('studio-tabbar').screenshot({ path });
  await testInfo.attach(name, { path, contentType: 'image/png' });
}

test.describe('studio-shell · pinned tabs', () => {
  test('pinning moves the tab leftmost, compacts it, and swaps the close X for a pin', async ({ page }, testInfo) => {
    await boot(page);
    expect((await tabOrder(page))[0]).toBe('board:__all__');
    const gammaWidthBefore = (await tab(page, GAMMA).boundingBox())!.width;

    await pinViaContextMenu(page, GAMMA);

    expect((await tabOrder(page))[0]).toBe(GAMMA);
    await expect(page.getByTestId(`studio-tab-unpin-${GAMMA}`)).toBeVisible();
    await expect(page.getByTestId(`studio-tab-close-${GAMMA}`)).toHaveCount(0);
    await expect(tab(page, GAMMA)).toHaveAttribute('aria-label', 'Gamma Tools · Deck (pinned)');
    expect((await tab(page, GAMMA).boundingBox())!.width).toBeLessThan(gammaWidthBefore);

    await restPointer(page);
    await shotStrip(page, testInfo, 'after-04-pinned-tab-dark');
  });

  test('a second pin lands right of the first and unpinning returns it to the unpinned block', async ({ page }) => {
    await boot(page);
    await pinViaContextMenu(page, GAMMA);
    await pinViaContextMenu(page, 'workspace-settings');
    expect((await tabOrder(page)).slice(0, 2)).toEqual([GAMMA, 'workspace-settings']);

    await page.getByTestId(`studio-tab-unpin-${GAMMA}`).click();
    await expect(tab(page, GAMMA)).toHaveAttribute('data-pinned', 'false');
    const order = await tabOrder(page);
    expect(order[0]).toBe('workspace-settings');
    expect(order[1]).toBe(GAMMA);
    await expect(page.getByTestId(`studio-tab-close-${GAMMA}`)).toHaveCount(1);
  });

  test('a pinned tab ignores middle-click while an unpinned tab still closes', async ({ page }) => {
    await boot(page);
    await pinViaContextMenu(page, GAMMA);

    await tab(page, GAMMA).click({ button: 'middle' });
    await expect(tab(page, GAMMA)).toHaveCount(1);

    await tab(page, 'epics:__all__').click({ button: 'middle' });
    await expect(tab(page, 'epics:__all__')).toHaveCount(0);
  });

  test('Close Others keeps pinned tabs; the pin survives a reload', async ({ page }, testInfo) => {
    await boot(page);
    await pinViaContextMenu(page, GAMMA);

    await tab(page, ACTIVE).click({ button: 'right' });
    await page.getByTestId('studio-tab-ctx-item-close-others').click();
    expect(await tabOrder(page)).toEqual([GAMMA, ACTIVE]);

    await page.reload();
    await expect(page.getByTestId('studio-tab-list')).toBeVisible({ timeout: 60_000 });
    expect(await tabOrder(page)).toEqual([GAMMA, ACTIVE]);
    await expect(tab(page, GAMMA)).toHaveAttribute('data-pinned', 'true');

    await restPointer(page);
    await shotStrip(page, testInfo, 'after-06-pin-survives-reload');
  });

  test('renders the pinned form in the light theme too', async ({ page }, testInfo) => {
    await boot(page, 'light');
    await pinViaContextMenu(page, GAMMA);
    await restPointer(page);
    await expect(page.getByTestId(`studio-tab-unpin-${GAMMA}`)).toBeVisible();
    await shotStrip(page, testInfo, 'after-05-pinned-tab-light');
  });
});

test.describe('studio-shell · quiet close affordance', () => {
  test('an inactive tab hides its close X until hover; the active tab keeps it', async ({ page }, testInfo) => {
    await boot(page);
    await restPointer(page);

    const idle = page.getByTestId(`studio-tab-close-${GAMMA}`);
    await expect(idle).toHaveCSS('opacity', '0');
    await expect(page.getByTestId(`studio-tab-close-${ACTIVE}`)).toHaveCSS('opacity', '1');
    await shotStrip(page, testInfo, 'after-01-close-hidden-at-rest-dark');

    await tab(page, GAMMA).hover();
    await expect(idle).toHaveCSS('opacity', '1');
    await shotStrip(page, testInfo, 'after-03-close-on-hover-dark');

    await restPointer(page);
    await expect(idle).toHaveCSS('opacity', '0');
  });

  test('the hidden close X still closes when clicked and stays keyboard-reachable', async ({ page }) => {
    await boot(page);
    // The button keeps its box while hidden, so a click on it lands even
    // before the hover transition finishes.
    await page.getByTestId(`studio-tab-close-${GAMMA}`).click();
    await expect(tab(page, GAMMA)).toHaveCount(0);

    // Focus reveals it for keyboard users rather than trapping them.
    const close = page.getByTestId(`studio-tab-close-${ACTIVE}`);
    await close.focus();
    await expect(close).toBeFocused();
    await expect(close).toHaveCSS('opacity', '1');
  });

  test('light theme hides the close X at rest as well', async ({ page }, testInfo) => {
    await boot(page, 'light');
    await restPointer(page);
    await expect(page.getByTestId(`studio-tab-close-${GAMMA}`)).toHaveCSS('opacity', '0');
    await shotStrip(page, testInfo, 'after-02-close-hidden-at-rest-light');
  });

  test('the Explorer Open-tabs list follows the same rules', async ({ page }, testInfo) => {
    await boot(page);
    await pinViaContextMenu(page, GAMMA);
    await restPointer(page);

    await expect(page.getByTestId(`studio-explorer-unpin-${GAMMA}`)).toBeVisible();
    const explorerRow = page.getByTestId(`studio-explorer-open-tab-${'epics:__all__'}`);
    await expect(explorerRow).toBeVisible();
    await expect(explorerRow.locator('.studio-tree-row__close')).toHaveCSS('opacity', '0');
    await explorerRow.hover();
    await expect(explorerRow.locator('.studio-tree-row__close')).toHaveCSS('opacity', '1');

    await restPointer(page);
    const dir = process.env.JOB_RESULTS_DIR;
    const path = dir
      ? `${dir}/after-07-explorer-open-tabs.png`
      : testInfo.outputPath('after-07-explorer-open-tabs.png');
    await page.screenshot({ path, clip: { x: 0, y: 0, width: 700, height: 620 } });
    await testInfo.attach('after-07-explorer-open-tabs', { path, contentType: 'image/png' });
  });

  test('the tab context menu offers Pin and reflects the pinned state', async ({ page }, testInfo) => {
    await boot(page);
    await tab(page, GAMMA).click({ button: 'right' });
    await expect(page.getByTestId('studio-tab-ctx-item-toggle-pin')).toHaveText('Pin');
    const dir = process.env.JOB_RESULTS_DIR;
    const path = dir
      ? `${dir}/after-08-tab-context-menu.png`
      : testInfo.outputPath('after-08-tab-context-menu.png');
    await page.screenshot({ path, clip: { x: 0, y: 0, width: 1280, height: 460 } });
    await testInfo.attach('after-08-tab-context-menu', { path, contentType: 'image/png' });

    await page.getByTestId('studio-tab-ctx-item-toggle-pin').click();
    await tab(page, GAMMA).click({ button: 'right' });
    await expect(page.getByTestId('studio-tab-ctx-item-toggle-pin')).toHaveText('Unpin');
  });
});
