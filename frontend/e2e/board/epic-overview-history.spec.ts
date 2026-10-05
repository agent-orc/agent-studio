import * as path from 'node:path';
import { test, expect, type Page } from '@playwright/test';
import { setTheme, type Theme } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/epic-history';
const epicCard = {
  id: 'epic-active', taskKey: `${WATCH_PATH}::epic-active`, title: 'Checkout modernization',
  state: '0-backlog', order: 1, agent: 'codex', cliType: 'codex', kind: 'epic', epicId: null,
  createdAt: '2026-07-01T09:00:00Z', lastActivity: '2026-07-10T09:00:00Z',
  watchPath: WATCH_PATH, projectName: 'Storefront', folderPath: `${WATCH_PATH}/0-backlog/epic-active`,
  tags: [], commits: [],
};

const grouped = {
  backlog: [epicCard], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
  codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
};

const rollups = [
  {
    id: 'epic-active', title: 'Checkout modernization', projectName: 'Storefront', watchPath: WATCH_PATH,
    state: '0-backlog', subTaskTotal: 3, completed: 1, inProgress: 1, open: 1, byState: {},
    subTasks: [
      { id: 'checkout-api', title: 'Checkout API', state: '6-completed', order: 1 },
      { id: 'payment-ui', title: 'Payment UI', state: '3-progress', order: 2 },
      { id: 'receipt-copy', title: 'Receipt copy', state: '2-ready', order: 3 },
    ],
  },
  {
    id: 'epic-completed', title: 'Account migration', projectName: 'Identity', watchPath: WATCH_PATH,
    state: '7-archive', subTaskTotal: 2, completed: 2, inProgress: 0, open: 0, byState: {},
    completedAt: '2026-07-09T12:00:00Z',
    subTasks: [
      { id: 'account-export', title: 'Account export', state: '7-archive', order: 1 },
      { id: 'account-import', title: 'Account import', state: '6-completed', order: 2 },
    ],
  },
  {
    id: 'empty-cleanup', title: 'Old placeholder', projectName: 'Identity', watchPath: WATCH_PATH,
    state: '7-archive', subTaskTotal: 0, completed: 0, inProgress: 0, open: 0, byState: {}, subTasks: [],
  },
];

async function installRoutes(page: Page): Promise<() => number> {
  let completedRollupsRequested = 0;
  await page.route('**/api/**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));
  await page.route('**/api/epics**', route => {
    const status = new URL(route.request().url()).searchParams.get('status');
    if (status === 'completed') completedRollupsRequested += 1;
    const body = status === 'active' ? [rollups[0]] : status === 'completed' ? [rollups[1]] : rollups;
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('**/api/epics/completed/count**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ count: 1 }) }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: 'Storefront', path: WATCH_PATH, rootPath: WATCH_PATH }]),
  }));
  return () => completedRollupsRequested;
}

test.describe('Epic overview history', () => {
  test('shows completed epics and expandable member status in both themes', async ({ page }, testInfo) => {
    await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'epics', projectName: null }],
      activeKey: 'epics:__all__',
    })));
    const completedRequests = await installRoutes(page);
    await page.goto('/');

    const screen = page.getByTestId('epic-overview-screen');
    await expect(screen).toBeVisible();
    const completed = page.getByTestId('epic-overview-section-completed');
    await expect(completed).toBeVisible();
    await expect(completed.getByText('1 completed')).toBeVisible();
    await expect(page.getByText('Account migration')).toHaveCount(0);
    await expect(page.getByTestId('epic-overview-section-active')).toHaveCount(0);
    await expect(page.getByTestId('epic-overview-completed-list')).toHaveCount(0);
    expect(completedRequests()).toBe(0);
    const errorClose = page.getByTestId('error-dialog-close');
    const resultsDir = process.env.JOB_RESULTS_DIR;
    for (const theme of ['dark', 'light'] satisfies Theme[]) {
      await setTheme(page, theme);
      if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
      const filename = `epic-overview-collapsed-${theme}--mocked.png`;
      const screenshotPath = resultsDir ? path.join(resultsDir, filename) : testInfo.outputPath(filename);
      await page.screenshot({ path: screenshotPath, fullPage: false });
      await testInfo.attach(`epic overview collapsed ${theme}`, { path: screenshotPath, contentType: 'image/png' });
    }
    if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
    await completed.getByTestId('epic-overview-completed-toggle').click();
    const completedList = completed.getByTestId('epic-overview-completed-list');
    await expect(completedList.getByText('Account migration')).toBeVisible();
    await expect(page.getByTestId('epic-overview-completed-date')).toBeVisible();
    expect(completedRequests()).toBe(1);
    if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
    await completed.getByTestId('epic-overview-completed-toggle').click();
    await expect(completedList).toHaveCount(0);
    if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
    await completed.getByTestId('epic-overview-completed-toggle').click();
    await expect(completedList.getByText('Account migration')).toBeVisible();
    expect(completedRequests()).toBe(1);
    await expect(screen.getByText('Old placeholder')).toHaveCount(0);

    const completedCard = page.locator('[data-testid="epic-overview-card"][data-epic-id="epic-completed"]');
    await expect(completedCard.getByTestId('epic-overview-card-count')).toHaveText('2 / 2 done');
    if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
    await completedCard.getByTestId('epic-overview-expand').click({ force: true });
    await expect(completedCard.getByTestId('epic-overview-open-sub')).toHaveCount(2);
    await expect(completedCard.getByTestId('epic-overview-sub-project')).toHaveCount(2);

    for (const theme of ['dark', 'light'] satisfies Theme[]) {
      await setTheme(page, theme);
      if (await errorClose.isVisible().catch(() => false)) await errorClose.click();
      const filename = `epic-overview-expanded-${theme}--mocked.png`;
      const screenshotPath = resultsDir ? path.join(resultsDir, filename) : testInfo.outputPath(filename);
      await page.screenshot({ path: screenshotPath, fullPage: false });
      await testInfo.attach(`epic overview ${theme}`, { path: screenshotPath, contentType: 'image/png' });
    }
  });
});
