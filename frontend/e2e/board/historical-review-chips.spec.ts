import { expect, test, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const PROJECT = 'fixture-history-chips';
const WATCH_PATH = 'C:/fixtures/history-chips';

function job(id: string, title: string, state: string, order: number) {
  return {
    id, jobKey: `${WATCH_PATH}::${id}`, key: id.toUpperCase(), title, state, order,
    agent: 'codex', cliType: 'codex', createdAt: '2026-07-10T06:00:00Z',
    lastActivity: '2026-07-10T07:00:00Z', watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/tasks/${id}`, useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default',
    tags: ['reissue:autoreview', 'abort-review:watchdog'], orchestratorVerdict: null,
  };
}

const REVIEW = job('history-review', 'Historical signals in human review', '5-human-review', 1);
const ESCALATED = job('history-escalated', 'Active signals in escalation', '5e-escalated', 1);
const ALL = [REVIEW, ESCALATED];

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/v1/studio/board**', (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
      review: [], autoReview: [], humanReview: [REVIEW], escalated: [ESCALATED], completed: [], archive: [],
    }),
  }));
  await page.route(/\/api\/tasks(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ALL) }));
  await page.route('**/api/watch-paths**', (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
  }));
  await page.route('**/api/environment**', (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
  }));
  await page.route('**/api/tags', (route) => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{
      id: 'abort-review:watchdog', label: 'Abort: watchdog', color: '#ef4444',
      description: 'The run stopped after a watchdog timeout',
    }]),
  }));
}

async function boot(page: Page): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await page.addInitScript(() => {
    const hideRouteErrors = () => document.querySelectorAll('app-error-dialog')
      .forEach((dialog) => ((dialog as HTMLElement).style.display = 'none'));
    addEventListener('DOMContentLoaded', () => {
      hideRouteErrors();
      new MutationObserver(hideRouteErrors).observe(document.body, { childList: true, subtree: true });
    }, { once: true });
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await expect(page.getByTestId('task-card').first()).toBeVisible({ timeout: 15_000 });
}

function card(page: Page, title: string) {
  return page.getByTestId('task-card').filter({ hasText: title }).first();
}

test.describe('review event history stays off status cards', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`event tags stay absent in Review and Escalated (${theme})`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 1000 });
      await boot(page);
      await setTheme(page, theme);

      const review = card(page, REVIEW.title);
      const escalated = card(page, ESCALATED.title);
      await expect(review).toBeVisible();
      await expect(escalated).toBeVisible();

      await expect(review.locator('[data-tag-id="reissue:autoreview"]')).toHaveCount(0);
      await expect(escalated.locator('[data-tag-id="reissue:autoreview"]')).toHaveCount(0);
      await expect(escalated.getByTestId('task-card-human-review')).toContainText('Escalated');

      const resultDir = process.env.HISTORY_CHIPS_RESULTS_DIR;
      if (resultDir) {
        await page.screenshot({ path: `${resultDir}/history-chips-${theme}--mocked.png`, fullPage: false });
      }
    });
  }
});
