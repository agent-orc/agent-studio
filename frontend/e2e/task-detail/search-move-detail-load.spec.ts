import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Response } from '../fixtures/dev-backend';
import { api, BACKEND } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath {
  name: string;
  path: string;
}

interface DetailTrace {
  method: string;
  url: string;
  status: number;
  statusText: string;
}

interface DetailNetworkProof {
  staleLaneReference: DetailTrace;
  boardReference: DetailTrace;
  searchReference: DetailTrace;
}

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(
    `${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE', headers: { 'x-client-id': 'local-default' } },
  );
}

function uniqueId(suffix: string): string {
  return `e2e-search-move-${suffix}-${Date.now()}-${Math.floor(Math.random() * 10_000)}`;
}

async function openSearchResult(page: Page, query: string): Promise<void> {
  await dismissCrashRecovery(page);
  await page.keyboard.press('Control+K');
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused();
  await input.fill(query);
  const result = page.getByTestId('global-search-group-tasks').getByRole('option', { name: new RegExp(query) });
  await expect(result).toBeVisible();
  await result.click();
}

async function dismissCrashRecovery(page: Page): Promise<void> {
  const leaveUncommitted = page.getByRole('button', { name: 'Leave uncommitted' });
  await leaveUncommitted.waitFor({ state: 'visible', timeout: 5_000 }).catch(() => undefined);
  if (await leaveUncommitted.isVisible().catch(() => false)) await leaveUncommitted.click();
}

function isTaskDetailResponse(response: Response, jobId: string): boolean {
  const url = new URL(response.url());
  const match = /^\/api\/v1\/projects\/[^/]+\/tasks\/([^/]+)$/.exec(url.pathname);
  return match !== null && decodeURIComponent(match[1]) === jobId;
}

async function persistScreenshot(page: Page, fileName: string): Promise<void> {
  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (!resultsDir) return;
  mkdirSync(resultsDir, { recursive: true });
  await page.screenshot({ path: path.join(resultsDir, fileName), fullPage: true });
}

test.describe('Search result task detail loading', () => {
  test('resolves board and stale search references through the same lane-independent request', async ({ page, context, devBackend }, testInfo) => {
    void devBackend;
    const watch = await firstWatchPath();
    const jobId = uniqueId('moved');
    const title = `Search move proof ${jobId}`;
    await createJob({
      id: jobId,
      title,
      watchPath: watch.path,
      targetState: '5e-escalated',
      fixture: false,
    });

    const searchTrace: DetailTrace[] = [];
    page.on('response', response => {
      if (isTaskDetailResponse(response, jobId)) {
        searchTrace.push({
          method: response.request().method(),
          url: response.url(),
          status: response.status(),
          statusText: response.statusText(),
        });
      }
    });

    // Model the stale reference Robert captured: the search snapshot still
    // carries the old lane folder even after the API move. Board refreshes can
    // replace the row, so rewrite every grouped snapshot consistently.
    await page.route('**/api/v1/studio/board**', async route => {
      const response = await route.fetch();
      const grouped = await response.json() as Record<string, Record<string, unknown>[]>;
      for (const tasks of Object.values(grouped)) {
        const task = tasks.find(candidate => candidate['id'] === jobId);
        if (!task) continue;
        task['taskKey'] = `${watch.path}/5e-escalated/${jobId}::${jobId}`;
      }
      await route.fulfill({ response, json: grouped });
    });

    try {
      await page.goto('/');
      await dismissCrashRecovery(page);
      await page.keyboard.press('Control+K');
      const input = page.getByTestId('global-search-input');
      await expect(input).toBeFocused();
      await input.fill(jobId);
      await expect(page.getByTestId('global-search-group-tasks')).toContainText(title);

      await moveJob(jobId, watch.path, '0-backlog');
      const staleLanePath = path.join(watch.path, '5e-escalated', jobId);
      const staleUrl = `${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(staleLanePath)}`;
      const staleResponse = await fetch(staleUrl, { headers: { 'x-client-id': 'local-default' } });
      const staleLaneReference: DetailTrace = {
        method: 'GET',
        url: staleUrl,
        status: staleResponse.status,
        statusText: staleResponse.statusText,
      };
      expect(staleLaneReference.status).toBe(404);

      const boardPage = await context.newPage();
      const boardTrace: DetailTrace[] = [];
      boardPage.on('response', response => {
        if (isTaskDetailResponse(response, jobId)) {
          boardTrace.push({
            method: response.request().method(),
            url: response.url(),
            status: response.status(),
            statusText: response.statusText(),
          });
        }
      });
      await boardPage.goto('/');
      await dismissCrashRecovery(boardPage);
      const boardCard = boardPage
        .locator('[data-testid="task-card"], [data-testid="job-card"]')
        .filter({ hasText: jobId })
        .first();
      await expect(boardCard).toBeVisible({ timeout: 15_000 });
      await boardCard.click();
      await expect(boardPage.getByTestId('studio-task')).toContainText(title);
      await expect(boardPage.getByText('Loading task details…')).toHaveCount(0);
      await persistScreenshot(boardPage, 'board-move-task-detail.png');
      expect(boardTrace).toHaveLength(1);
      await boardPage.close();

      await page.getByTestId('global-search-group-tasks').getByRole('option', { name: new RegExp(jobId) }).click();

      await expect(page.getByTestId('studio-task')).toContainText(title);
      await expect(page.getByText('Loading task details…')).toHaveCount(0);
      await persistScreenshot(page, 'search-move-task-detail.png');

      expect(searchTrace).toHaveLength(1);
      const boardRequestUrl = new URL(boardTrace[0].url);
      const searchRequestUrl = new URL(searchTrace[0].url);
      expect(boardTrace[0].status).toBe(200);
      expect(searchTrace[0].status).toBe(200);
      expect(boardRequestUrl.pathname).toBe(searchRequestUrl.pathname);
      expect(boardRequestUrl.searchParams.get('project')).toBeTruthy();
      expect(searchRequestUrl.searchParams.get('project')).toBe(
        boardRequestUrl.searchParams.get('project'),
      );
      expect(boardRequestUrl.searchParams.has('watchPath')).toBe(false);
      expect(searchRequestUrl.searchParams.has('watchPath')).toBe(false);
      const networkProof: DetailNetworkProof = {
        staleLaneReference,
        boardReference: boardTrace[0],
        searchReference: searchTrace[0],
      };
      await testInfo.attach('detail-network-trace', {
        body: Buffer.from(JSON.stringify(networkProof, null, 2)),
        contentType: 'application/json',
      });
      const resultsDir = process.env.JOB_RESULTS_DIR;
      if (resultsDir) {
        writeFileSync(
          path.join(resultsDir, 'search-move-network-trace.json'),
          `${JSON.stringify(networkProof, null, 2)}\n`,
        );
      }
    } finally {
      await deleteTask(jobId, watch.path).catch(() => undefined);
    }
  });

  test('replaces a failed detail skeleton with an error and retries in place', async ({ page, devBackend }) => {
    void devBackend;
    const watch = await firstWatchPath();
    const jobId = uniqueId('retry');
    const title = `Search retry proof ${jobId}`;
    await createJob({
      id: jobId,
      title,
      watchPath: watch.path,
      targetState: '0-backlog',
      fixture: false,
    });

    let failDetailRequests = true;
    await page.route(`**/api/v1/projects/*/tasks/${encodeURIComponent(jobId)}?**`, async route => {
      if (failDetailRequests && route.request().method() === 'GET') {
        await route.fulfill({
          status: 503,
          contentType: 'application/problem+json',
          body: JSON.stringify({ title: 'Temporary detail failure' }),
        });
        return;
      }
      await route.continue();
    });

    try {
      await page.goto('/');
      await openSearchResult(page, jobId);

      const error = page.getByTestId('task-detail-load-error');
      await expect(error).toBeVisible();
      await expect(error).toContainText('Task details could not be loaded');
      await expect(page.getByText('Loading task details…')).toHaveCount(0);
      await persistScreenshot(page, 'task-detail-error-retry.png');

      await page.getByTestId('studio-titlebar-actions').locator('.studio-titlebar__iconbtn').first().click();
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
      await expect(error).toBeVisible();
      await persistScreenshot(page, 'task-detail-error-retry-dark.png');

      failDetailRequests = false;
      await error.getByRole('button', { name: 'Retry' }).click();
      await expect(page.getByTestId('studio-task')).toContainText(title);
      await expect(error).toHaveCount(0);
    } finally {
      await deleteTask(jobId, watch.path).catch(() => undefined);
    }
  });
});
