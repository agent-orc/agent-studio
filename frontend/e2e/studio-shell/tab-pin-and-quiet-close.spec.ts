/** Current tab contract: every tab can close, and context actions persist across reload. */
import { expect, test, type Locator, type Page } from '@playwright/test';

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
  { kind: 'workbenches', projectName: null },
  { kind: 'workspace-settings' },
  { kind: 'hub', projectName: 'Gamma Tools', section: 'overview' },
];
const ACTIVE = 'hub:Beta Service';
const GAMMA = 'hub:Gamma Tools';
const STORAGE_KEY = 'atp.studio.tabs.v1';

async function boot(page: Page): Promise<void> {
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
      return json(PROJECTS.map((name, index) => ({ name, path: `/mock/project-${index + 1}` })));
    }
    if (url.includes('/api/v1/workspaces') || url.includes('/api/workspaces')) return json([]);
    return route.continue();
  });
  await page.addInitScript(({ tabs, key, storageKey }) => {
    if (!localStorage.getItem(storageKey)) {
      localStorage.setItem(storageKey, JSON.stringify({ v: 1, tabs, activeKey: key }));
    }
  }, { tabs: TABS, key: ACTIVE, storageKey: STORAGE_KEY });
  await page.goto('/');
  await expect(page.getByTestId('studio-tab-list')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId(`studio-tab-${ACTIVE}`)).toBeVisible({ timeout: 60_000 });
}

const tab = (page: Page, key: string): Locator => page.getByTestId(`studio-tab-${key}`);

async function tabOrder(page: Page): Promise<string[]> {
  return page.getByTestId('studio-tab-list').locator('[data-tab-key]')
    .evaluateAll(nodes => nodes.map(node => (node as HTMLElement).dataset['tabKey'] ?? ''));
}

test('every tab has an accessible close control, including the default board', async ({ page }) => {
  await boot(page);
  await expect(tab(page, 'board:__all__').getByRole('button', { name: 'Close tab' })).toBeVisible();
  await tab(page, 'board:__all__').getByRole('button', { name: 'Close tab' }).click();
  await expect(tab(page, 'board:__all__')).toHaveCount(0);
  await expect(tab(page, GAMMA).getByRole('button', { name: 'Close tab' })).toBeVisible();
});

test('middle click closes an inactive tab', async ({ page }) => {
  await boot(page);
  await tab(page, GAMMA).click({ button: 'middle' });
  await expect(tab(page, GAMMA)).toHaveCount(0);
  await expect(tab(page, ACTIVE)).toHaveAttribute('aria-selected', 'true');
});

test('Close Others keeps only the selected tab and survives reload', async ({ page }) => {
  await boot(page);
  await tab(page, ACTIVE).click({ button: 'right' });
  await page.getByTestId('studio-tab-ctx-item-close-others').click();
  expect(await tabOrder(page)).toEqual([ACTIVE]);

  await page.reload();
  await expect(page.getByTestId('studio-tab-list')).toBeVisible({ timeout: 60_000 });
  expect(await tabOrder(page)).toEqual([ACTIVE]);
});

test('context menu closes tabs to the right', async ({ page }) => {
  await boot(page);
  await tab(page, ACTIVE).click({ button: 'right' });
  await page.getByTestId('studio-tab-ctx-item-close-right').click();
  expect(await tabOrder(page)).toEqual(['board:__all__', 'hub:Agent Taskboard', ACTIVE]);
});
