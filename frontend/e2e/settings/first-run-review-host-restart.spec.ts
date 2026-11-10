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
 * daemon is started again with the same identity and state directory. That
 * twice, so the host keeps two restarts and the second retry waits 3 minutes.
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

function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

async function shot(target: Page | Locator, results: string | undefined, name: string): Promise<void> {
  if (!results) return;
  await target.screenshot({ path: join(results, `${name}.png`) });
}

test('a killed review host is named on the card with its automatic retry and stays in the host history', async ({ page }) => {
  test.setTimeout(30 * 60_000);
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

    // 2. Kill the review host and start it again: the review is lost, retry 1 of 3.
    replacement = await restartReviewHost(workspace!, held);
    await expect.poll(async () => (await liveQueue(page, cardId))?.state,
      { timeout: 60_000, intervals: [500] }).toBe('retry-scheduled');
    const first = (await liveQueue(page, cardId))!;
    expect(first.lost?.cause).toBe('host-restarted');
    expect(first.lost?.executorId).toBe('first-run-review');
    expect(first.retry?.number).toBe(1);
    expect(first.retry?.budget).toBe(3);
    const host = first.lost!.hostId!;
    // `has` resolves relative to each card, so it takes the bare line locator.
    const lostCard = page.getByTestId('task-card').filter({ has: page.getByTestId('task-live-review-loss') });
    const lossLine = lostCard.getByTestId('task-live-review-loss');
    await expect(lossLine).toHaveText(new RegExp(
      `^\\s*Review lost: review host restarted \\(${host}, \\d\\d:\\d\\d [AP]M\\)\\. `
      + `Automatic retry 1 of 3 at ${clock(first.retry!.dueAt)}\\.\\s*$`), { timeout: 30_000 });
    await shot(lostCard, results, 'card-retry-1-light');

    // 3. The retry claims the review again; kill the host once more: retry 2 of 3, 3 minutes out.
    // killReviewHost SIGKILLs the replacement too; a SIGTERM would hand the review off instead.
    await waitForHeldReview(page, cardId, held);
    replacement = await restartReviewHost(workspace!, held);
    await expect.poll(async () => (await liveQueue(page, cardId))?.retry?.number,
      { timeout: 60_000, intervals: [500] }).toBe(2);
    const second = (await liveQueue(page, cardId))!;
    const retryLine = new RegExp(`Automatic retry 2 of 3 at ${clock(second.retry!.dueAt)}\\.`);
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
    const rows = history.getByTestId('remote-host-restart-row');
    await expect(rows.nth(1)).toBeVisible();
    await expect(rows.nth(0)).toHaveAttribute('data-reviews-lost', '1');
    await expect(rows.nth(1)).toHaveAttribute('data-reviews-lost', '1');
    await expect(history.getByTestId('remote-host-restart-lost-attempt')).toHaveCount(2);
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
  } finally {
    rmSync(hold, { force: true });
    stopReviewHost(replacement);
  }
});
