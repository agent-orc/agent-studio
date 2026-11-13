import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect, type Locator, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';
import { createFirstRunProofCard, prepareFirstRunProject, verifyFirstRunGit } from '../helpers/first-run-setup';
import { ensureReviewHost, killReviewHost, reviewHostGone, startReviewHost, stopReviewHost } from '../helpers/review-host-process';

/**
 * AGT-3690 (operator crisis playthrough AGT-3648, finding S5-F14): killing the
 * review host mid-review produced ExecutorRestarted and a scheduled retry, but
 * the card only said "Reviewing on first-run-review" and then parked, and the
 * host detail's "1 reviews lost" was overwritten by the next restart.
 *
 * Same method as the playthrough, on the real backend and the real runner
 * pair of the `first-run` profile: a proof card reaches review, the review
 * aspect is held, the review daemon and its worker are SIGKILLed, and the
 * daemon is started again with the same identity and state directory. The
 * replacement daemon relaunches a vanished worker within the same attempt up
 * to DurableReviewProcess.MaximumWorkerRelaunches times, so the playthrough
 * kill is repeated until the backend reports the review as lost. The review
 * is lost twice, so the host keeps two restarts and the second retry waits
 * 3 minutes, then once more after the budget the backend reports is spent, so the
 * escalation names the restarts.
 */

interface LiveQueue {
  state?: string;
  executorId?: string | null;
  lost?: { cause: string; hostId?: string | null; executorId?: string | null } | null;
  retry?: { number?: number | null; budget: number; dueAt: string } | null;
}

interface BoardCard {
  id: string;
  key?: string | null;
  liveStatus?: { queue?: LiveQueue | null } | null;
}

async function liveQueue(page: Page, cardId: string): Promise<LiveQueue | null> {
  const response = await page.request.get('/api/v1/studio/board?includeLegacyReviewLane=false');
  if (!response.ok()) return null;
  const board = await response.json() as Record<string, unknown>;
  const cards = Object.values(board).filter(Array.isArray).flat() as BoardCard[];
  return cards.find(card => card.id === cardId)?.liveStatus?.queue ?? null;
}

async function waitForHeldReview(page: Page, cardId: string, held: string): Promise<void> {
  await expect.poll(async () => existsSync(held) && (await liveQueue(page, cardId))?.state === 'reviewing',
    { timeout: 12 * 60_000, intervals: [2_000] }).toBe(true);
}

/** The playthrough's kill: daemon, detached worker and its CLI, then the same daemon again. */
async function restartReviewHost(workspace: string, held: string): Promise<number> {
  const launch = killReviewHost(workspace);
  await expect.poll(() => reviewHostGone(launch), { timeout: 30_000 }).toBe(true);
  rmSync(held, { force: true });
  return startReviewHost(launch, workspace);
}

/**
 * Kill the review host until the attempt is lost and the backend schedules
 * `retry`; a relaunched worker holds the aspect again before the next kill.
 * `budget` is the retry budget the backend reports; once known it caps the kills.
 * Returns the replacement daemon, the kills it took and the live queue that names the loss.
 */
async function loseReview(page: Page, cardId: string, workspace: string, held: string, retry: number | null,
  budget: number | null):
  Promise<{ replacement: number; kills: number; queue: LiveQueue }> {
  for (let kill = 1; ; kill++) {
    const replacement = await restartReviewHost(workspace, held);
    const lost = async () => {
      const queue = await liveQueue(page, cardId);
      return retry === null ? queue?.state !== 'reviewing' && queue?.state !== 'retry-scheduled'
        : queue?.state === 'retry-scheduled' && queue.retry?.number === retry;
    };
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline && !(await lost()) && !existsSync(held)) await page.waitForTimeout(500);
    if (await lost()) return { replacement, kills: kill, queue: (await liveQueue(page, cardId))! };
    const cap = budget ?? (await liveQueue(page, cardId))?.retry?.budget;
    if (cap !== undefined) {
      expect(kill, 'the review host relaunched the worker more often than the backend budget allows').toBeLessThanOrEqual(cap);
    }
    await waitForHeldReview(page, cardId, held);
  }
}

function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

async function shot(target: Page | Locator, results: string | undefined, name: string): Promise<void> {
  if (!results) return;
  await target.screenshot({ path: join(results, `${name}.png`) });
}

test('a killed review host is named on the card with its automatic retry and stays in the host history', async ({ page }) => {
  test.setTimeout(60 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const hold = join(workspace!, 'review-hold');
  const held = join(workspace!, 'review-held');
  const results = process.env.JOB_RESULTS_DIR ? join(process.env.JOB_RESULTS_DIR, 'review-host-restart') : undefined;
  if (results) mkdirSync(results, { recursive: true });
  let replacement: number | null = null;
  try {
    rmSync(held, { force: true });
    writeFileSync(hold, '');
    replacement = ensureReviewHost(workspace!);
    await prepareFirstRunProject(page, workspace!, 'Review Host Restart');
    await page.goto('/#/workspace/settings/first-run');
    await expect(page.getByTestId('first-run-git-setup')).toBeVisible();
    if (await page.getByTestId('first-run-git').getAttribute('data-state') !== 'ready') await verifyFirstRunGit(page);
    await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
    await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready', { timeout: 120_000 });
    await expect(page.getByTestId('first-run-host')).toHaveAttribute('data-state', 'ready');
    const cardId = await createFirstRunProofCard(page);

    // 1. The proof card is under review on first-run-review, held mid-aspect.
    await waitForHeldReview(page, cardId, held);
    await page.goto('/#/board');
    await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 30_000 });
    const card = page.getByTestId('task-card').filter({ hasText: /Reviewing on first-run-review/ });
    await expect(card).toHaveCount(1, { timeout: 30_000 });

    // 2. Kill the review host and start it again: the review is lost, retry 1 of the backend budget.
    let lost = await loseReview(page, cardId, workspace!, held, 1, null);
    replacement = lost.replacement;
    let kills = lost.kills;
    const first = lost.queue;
    expect(first.lost?.cause).toBe('host-restarted');
    expect(first.lost?.executorId).toBe('first-run-review');
    expect(first.retry?.number).toBe(1);
    // Read the budget the backend reports, so this spec cannot drift from AttemptAuthorityService.
    const budget = first.retry!.budget;
    expect(budget).toBeGreaterThanOrEqual(2);
    const host = first.lost!.hostId!;
    // `has` resolves relative to each card, so it takes the bare line locator.
    const lostCard = page.getByTestId('task-card').filter({ has: page.getByTestId('task-live-review-loss') });
    const lossLine = lostCard.getByTestId('task-live-review-loss');
    await expect(lossLine).toHaveText(new RegExp(
      `^\\s*Review lost: review host restarted \\(${host}, \\d\\d:\\d\\d [AP]M\\)\\. `
      + `Automatic retry 1 of ${budget} at ${clock(first.retry!.dueAt)}\\.\\s*$`), { timeout: 30_000 });
    await shot(lostCard, results, 'card-retry-1-light');

    // 3. The retry claims the review again; kill the host once more: retry 2 of the budget, 3 minutes out.
    // killReviewHost SIGKILLs the replacement too; a SIGTERM would hand the review off instead.
    await waitForHeldReview(page, cardId, held);
    lost = await loseReview(page, cardId, workspace!, held, 2, budget);
    replacement = lost.replacement;
    kills += lost.kills;
    const second = lost.queue;
    const retryLine = new RegExp(`Automatic retry 2 of ${budget} at ${clock(second.retry!.dueAt)}\\.`);
    await expect(lossLine).toHaveText(retryLine, { timeout: 30_000 });
    await shot(lostCard, results, 'card-retry-2-light');
    await setTheme(page, 'dark');
    await shot(lostCard, results, 'card-retry-2-dark');
    await page.setViewportSize({ width: 390, height: 844 });
    // A narrow board shows one lane at a time.
    await page.getByTestId('mobile-lane-picker').locator('select').selectOption('4-auto-review');
    await expect(lossLine).toHaveText(retryLine);
    await lostCard.scrollIntoViewIfNeeded();
    await shot(page, results, 'card-retry-2-390-dark');
    await setTheme(page, 'light');
    await shot(page, results, 'card-retry-2-390-light');
    await page.setViewportSize({ width: 1440, height: 900 });

    // 4. The host link opens the host; its detail keeps both restarts and links the lost card.
    await lostCard.getByTestId('task-live-host-link').click();
    await expect(page).toHaveURL(/#\/workspace\/settings\/environment\?host=first-run-review/);
    const history = page.getByTestId('remote-host-restart-history');
    await expect(history).toBeVisible({ timeout: 30_000 });
    // Every kill interrupted the review in flight, so each restart row names it.
    await expect(history.locator('[data-testid="remote-host-restart-row"][data-reviews-lost="1"]')).toHaveCount(kills);
    await expect(history.getByTestId('remote-host-restart-lost-attempt')).toHaveCount(kills);
    await expect(page.getByTestId('remote-host-role-restart')).toContainText('1 review lost');
    await history.scrollIntoViewIfNeeded();
    await shot(history, results, 'host-history-light');
    await setTheme(page, 'dark');
    await shot(history, results, 'host-history-dark');
    await page.setViewportSize({ width: 390, height: 844 });
    await history.scrollIntoViewIfNeeded();
    await shot(page, results, 'host-history-390-dark');
    await setTheme(page, 'light');
    await shot(page, results, 'host-history-390-light');
    await page.setViewportSize({ width: 1440, height: 900 });

    // 5. The lost attempt links back to its card.
    await history.getByTestId('remote-host-restart-lost-attempt').first()
      .getByTestId('remote-host-restart-lost-card').click();
    await expect(page).toHaveURL(/#\/tasks\//);

    // 6. Spend the rest of the budget; the escalation names every restart instead of "cannot be materialized".
    for (let retry = 3; retry <= budget; retry++) {
      await waitForHeldReview(page, cardId, held);
      replacement = (await loseReview(page, cardId, workspace!, held, retry, budget)).replacement;
    }
    await waitForHeldReview(page, cardId, held);
    replacement = (await loseReview(page, cardId, workspace!, held, null, budget)).replacement;
    await page.goto(`/#/tasks/${cardId}`);
    const escalation = page.getByText(new RegExp(
      `The review was lost to ${budget + 1} review host restarts: .*The automatic retry budget of ${budget} is spent`)).first();
    await expect(escalation).toBeVisible({ timeout: 2 * 60_000 });
    await expect(page.getByText(/cannot be materiali[sz]ed/)).toHaveCount(0);
    await escalation.scrollIntoViewIfNeeded();
    await shot(page, results, 'escalation-light');
    await setTheme(page, 'dark');
    await shot(page, results, 'escalation-dark');
    await page.setViewportSize({ width: 390, height: 844 });
    await escalation.scrollIntoViewIfNeeded();
    await shot(page, results, 'escalation-390-dark');
    await setTheme(page, 'light');
    await shot(page, results, 'escalation-390-light');
  } finally {
    rmSync(hold, { force: true });
    stopReviewHost(replacement);
  }
});
