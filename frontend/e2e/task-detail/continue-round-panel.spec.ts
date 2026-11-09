import { test, expect, type Page, type Request, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

/**
 * AGT-3582: a finished card takes another round from the task detail. The
 * Continue form posts to `POST /api/tasks/{id}/continue` (the route of
 * `studio-op continue`) with the mode, prompt and required reason, and shows
 * the run base read only before it sends. A refused round toasts the server's
 * own message.
 *
 * The cards are real, created in Backlog because the create API refuses a
 * protected lane; the card's own reads are rewritten to Human review or
 * Accepted. The run base read and the continue answer are route-mocked so the
 * round never reaches a runner and the refusal does not need a red batch
 * flight; the backend tests cover the route itself. Light and dark captures go
 * to `$JOB_RESULTS_DIR/continue-round/`.
 */

interface WatchPath { path: string; name?: string }

const BASE_SHA = '3f9c2a7b1d4e5f60718293a4b5c6d7e8f9012345';
const BASE_REF = `refs/heads/agent-studio/results/run_demo_continue/fence-3/${BASE_SHA}`;
const ISOLATION_MESSAGE = 'This card is a member of batch flight flight-demo-7, which is isolating a red result. '
  + 'Eject it from the batch to change it.';

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/** Presents a Backlog card as if it sat in `lane` on every read of that card. */
async function presentInLane(page: Page, id: string, watchPath: string, lane: string): Promise<void> {
  const job = await getJob(id, watchPath) as Awaited<ReturnType<typeof getJob>> & { key?: string };
  const marks = [id, job.key, job.jobKey].filter((value): value is string => !!value).map(value => `/${encodeURIComponent(value)}`);
  await page.route(url => marks.some(mark => url.pathname.includes(mark)), async route => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const body = (await response.text()).replaceAll('"0-backlog"', `"${lane}"`);
    return route.fulfill({ response, body });
  });
}

async function routeBase(page: Page): Promise<void> {
  await page.route(/\/api\/tasks\/[^/]+\/continue\/base(\?|$)/, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      base: { ref: BASE_REF, sha: BASE_SHA, source: 'latest-result', resultAttemptId: 'run_demo_continue' },
    }),
  }));
}

/** Answers the continue POST and records what the form sent. */
async function routeContinue(page: Page, status: number, body: unknown): Promise<Request[]> {
  const sent: Request[] = [];
  await page.route(/\/tasks\/[^/]+\/continue(\?|$)/, route => {
    if (route.request().method() !== 'POST') return route.fallback();
    sent.push(route.request());
    return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  });
  return sent;
}

function evidencePath(testInfo: TestInfo, name: string): string {
  const root = process.env['JOB_RESULTS_DIR']?.trim()
    ? path.resolve(process.env['JOB_RESULTS_DIR'])
    : testInfo.outputDir;
  const folder = path.join(root, 'continue-round');
  fs.mkdirSync(folder, { recursive: true });
  return path.join(folder, name);
}

async function openCard(page: Page, id: string, watchPath: string, theme: Theme): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  await setTheme(page, theme);
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 15_000 });
  await page.getByTestId('prompt-tab-overview').click();
}

async function fillRound(page: Page, mode: 'continue' | 'steer' | 'extend', prompt: string, reason: string) {
  const panel = page.getByTestId('continue-round-panel');
  await expect(panel).toBeVisible({ timeout: 15_000 });
  await panel.getByTestId('continue-round-open').click();
  await expect(panel.getByTestId('continue-round-base-ref')).toHaveText(BASE_REF);
  await expect(panel.getByTestId('continue-round-base-sha')).toHaveText(BASE_SHA.slice(0, 12));
  await panel.getByTestId(`continue-round-mode-${mode}`).click();
  await panel.getByTestId('continue-round-prompt').fill(prompt);
  await expect(panel.getByTestId('continue-round-submit')).toBeDisabled();
  await panel.getByTestId('continue-round-reason').fill(reason);
  await expect(panel.getByTestId('continue-round-submit')).toBeEnabled();
  return panel;
}

async function deleteCard(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' })
    .catch(() => { /* best-effort cleanup */ });
}

for (const theme of ['light', 'dark'] as Theme[]) {
  test(`an accepted card sends a continue round from its run base (${theme})`, async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const { id } = await createJob({
      title: `continue-round-accepted-${theme}-${Date.now()}`,
      watchPath,
      promptMarkdown: '# Delivered card that takes another round',
      targetState: '0-backlog',
    });
    await presentInLane(page, id, watchPath, '6-completed');
    await routeBase(page);
    const sent = await routeContinue(page, 202, {
      status: 'queued',
      queued: { reason: 'lane-not-runnable', position: 1, promotedFromState: '6-completed' },
      base: { ref: BASE_REF, sha: BASE_SHA, source: 'latest-result', resultAttemptId: 'run_demo_continue' },
    });

    try {
      await openCard(page, id, watchPath, theme);
      const panel = await fillRound(page, 'extend', 'Add the missing empty state.', 'The walkthrough found no empty state.');
      await panel.screenshot({ path: evidencePath(testInfo, `continue-round-form-${theme}.png`) });

      await panel.getByTestId('continue-round-submit').click();

      await expect(page.getByTestId('notification-success').last())
        .toContainText(`Round queued. It starts from ${BASE_SHA.slice(0, 12)}.`);
      expect(sent).toHaveLength(1);
      expect(new URL(sent[0].url()).pathname).toMatch(new RegExp(`/tasks/${id}/continue$`));
      expect(sent[0].postDataJSON()).toEqual({
        prompt: 'Add the missing empty state.',
        mode: 'extend',
        reason: 'The walkthrough found no empty state.',
      });
      await expect(panel.getByTestId('continue-round-form')).toHaveCount(0);
    } finally {
      await deleteCard(id, watchPath);
    }
  });

  test(`a refused round toasts the server message (${theme})`, async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const { id } = await createJob({
      title: `continue-round-refused-${theme}-${Date.now()}`,
      watchPath,
      promptMarkdown: '# Card in review whose batch is isolating',
      targetState: '0-backlog',
    });
    await presentInLane(page, id, watchPath, '5-human-review');
    await routeBase(page);
    const sent = await routeContinue(page, 409, {
      error: 'batch-isolation-active',
      message: ISOLATION_MESSAGE,
      flight: 'flight-demo-7',
    });

    try {
      await openCard(page, id, watchPath, theme);
      const panel = await fillRound(page, 'steer', 'Fix the failing test.', 'The batch gate is red.');

      await panel.getByTestId('continue-round-submit').click();

      const toast = page.getByTestId('notification-error').last();
      await expect(toast).toContainText(ISOLATION_MESSAGE);
      await expect(panel.getByTestId('continue-round-error')).toHaveText(ISOLATION_MESSAGE);
      await expect(panel.getByTestId('continue-round-form')).toBeVisible();
      expect(sent).toHaveLength(1);
      expect(sent[0].postDataJSON()).toMatchObject({ mode: 'steer', reason: 'The batch gate is red.' });

      await panel.screenshot({ path: evidencePath(testInfo, `continue-round-refused-${theme}.png`) });
      await toast.screenshot({ path: evidencePath(testInfo, `continue-round-refused-toast-${theme}.png`) });
    } finally {
      await deleteCard(id, watchPath);
    }
  });
}
