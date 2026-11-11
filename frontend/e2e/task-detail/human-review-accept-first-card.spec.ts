import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

/**
 * AGT-3621 - newcomer playthrough AGT-3560, finding H5. The first card in
 * Human Review showed "Checking git status…" on a disabled Accept for over two
 * minutes. The Git pane is closed by default, so the provenance read that the
 * button waited for never started. A person must be able to accept the first
 * card with one click, and the pending state must end within the 10 s R6
 * pending-button contract even when the provenance read hangs.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const PENDING_CONTRACT_MS = 10_000;

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  });
}

/** One code-free card in Human Review, created the way the triage specs do. */
async function plantHumanReviewCard(wp: WatchPath, label: string): Promise<string> {
  const requestedId = `e2e-first-accept-${label}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
  const created = await createJob({
    id: requestedId,
    title: `First accept ${label}`,
    watchPath: wp.path,
    targetState: '2-ready',
    mode: 'concept',
    requiresIntegration: false,
  });
  for (let attempt = 0; attempt < 25; attempt++) {
    try { await getJob(created.id, wp.path); break; }
    catch { await new Promise(resolve => setTimeout(resolve, 200)); }
  }
  await api(`/api/tasks/${encodeURIComponent(created.id)}/concept-dossier?watchPath=${encodeURIComponent(wp.path)}`, {
    method: 'POST',
    body: JSON.stringify({ noDossierNeeded: true, reason: 'Code-free end-to-end fixture.' }),
  });
  await moveJob(created.id, wp.path, '5-human-review');
  await seedPassingConceptReview(created.id, wp.path);
  return created.id;
}

/**
 * Completion requires a passing content verdict for the current brief. The
 * fixture writes the local review runner's two evidence files into the card
 * of the disposable e2e workspace, so the acceptance the spec clicks is real.
 */
async function seedPassingConceptReview(jobId: string, watchPath: string): Promise<void> {
  const qs = `?watchPath=${encodeURIComponent(watchPath)}`;
  const claim = await api<{ contentReview?: { currentBriefVersion?: string } }>(
    `/api/tasks/${encodeURIComponent(jobId)}/delivery-claim${qs}`);
  const briefVersion = claim.contentReview?.currentBriefVersion;
  if (!briefVersion) throw new Error('The delivery claim names no current brief version.');
  const folder = (await getJob(jobId, watchPath)).folderPath;
  fs.writeFileSync(path.join(folder, 'content-review-context.json'), JSON.stringify({ briefVersion }));
  fs.writeFileSync(path.join(folder, 'aspect-concept-fit.json'),
    JSON.stringify({ status: 'pass', summary: 'Fixture concept fit for the AGT-3621 acceptance spec.' }));
}

async function dismissBlockingToasts(page: Page): Promise<void> {
  for (let i = 0; i < 5; i++) {
    const close = page.getByTestId('notification-close').first();
    if (!(await close.isVisible({ timeout: 200 }).catch(() => false))) return;
    await close.click({ timeout: 1_000 }).catch(() => {});
  }
}

/**
 * Opens the card and returns how long Accept took to become clickable after
 * the card's actions appeared, which is when a person sees the button.
 */
async function openAndTimeAccept(page: Page, jobId: string, watchPath: string): Promise<number> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  const accept = page.getByTestId('studio-triage-action-mark-done');
  await expect(accept).toBeVisible({ timeout: 30_000 });
  const started = Date.now();
  await expect(accept).toBeEnabled({ timeout: PENDING_CONTRACT_MS });
  const elapsed = Date.now() - started;
  await expect(accept).not.toHaveAttribute('data-git-loading', 'true');
  await expect(accept).toContainText('Accept');
  await dismissBlockingToasts(page);
  await dismissDevErrorDialog(page);
  return elapsed;
}

/** Clicks Accept and fails with the move answer when the backend refuses it. */
async function clickAcceptAndExpectMove(page: Page, jobId: string): Promise<void> {
  const move = page.waitForResponse(response =>
    response.url().includes(`/tasks/${encodeURIComponent(jobId)}/move`)
    && response.request().method() !== 'GET', { timeout: 30_000 });
  await page.getByTestId('studio-triage-action-mark-done').click();
  const answer = await move;
  expect(answer.status(), `move answered ${answer.status()}: ${await answer.text()}`).toBe(200);
}

async function saveScreenshot(page: Page, fileName: string): Promise<void> {
  if (!RESULTS_DIR) return;
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await page.screenshot({ path: path.join(RESULTS_DIR, fileName) });
}

test.describe('First Human Review card is accepted with one click (AGT-3621)', () => {
  // A fresh context starts with the default layout, so the Git pane is closed.
  for (const theme of ['light', 'dark'] as Theme[]) {
    test(`Accept is ready within the pending contract and accepts the card (${theme})`, async ({ page }) => {
      const wp = await firstWatchPath();
      const jobId = await plantHumanReviewCard(wp, theme);
      try {
        await page.setViewportSize({ width: 1280, height: 800 });
        await page.goto('/');
        await setTheme(page, theme);

        const elapsed = await openAndTimeAccept(page, jobId, wp.path);
        expect(elapsed, `Accept became clickable after ${elapsed} ms`).toBeLessThan(PENDING_CONTRACT_MS);
        await expect(page.getByTestId('overview-status')).toBeVisible({ timeout: 30_000 });
        await expect(page.getByTestId('parked-blocker')).toHaveCount(0);
        await expect(page.getByTestId('integration-dead-end')).toHaveCount(0);
        await expect(page.getByText('No test evidence assigned: card has no commit')).toHaveCount(0);
        await saveScreenshot(page, `agt-3621-human-review-accept-ready-1280-${theme}.png`);

        await clickAcceptAndExpectMove(page, jobId);
        await expect(page.getByText(`Accepted "First accept ${theme}"`, { exact: true })).toBeVisible();
        await expect.poll(async () => (await getJob(jobId, wp.path)).state,
          { timeout: PENDING_CONTRACT_MS }).toBe('6-completed');
        await saveScreenshot(page, `agt-3621-human-review-accepted-1280-${theme}.png`);
      } finally {
        await deleteJob(jobId, wp.path);
      }
    });
  }

  test('Accept is ready on a 390 px viewport', async ({ page }) => {
    const wp = await firstWatchPath();
    const jobId = await plantHumanReviewCard(wp, 'narrow');
    try {
      await page.setViewportSize({ width: 390, height: 844 });
      await page.goto('/');
      await setTheme(page, 'light');

      const elapsed = await openAndTimeAccept(page, jobId, wp.path);
      expect(elapsed).toBeLessThan(PENDING_CONTRACT_MS);
      await expect(page.getByText('No test evidence assigned: card has no commit')).toHaveCount(0);
      await saveScreenshot(page, 'agt-3621-human-review-accept-ready-390-light.png');
      await clickAcceptAndExpectMove(page, jobId);
      await expect(page.getByText('Accepted "First accept narrow"', { exact: true })).toBeVisible();
      await saveScreenshot(page, 'agt-3666-human-review-accepted-390-light.png');
    } finally {
      await deleteJob(jobId, wp.path);
    }
  });

  test('a hanging provenance read releases Accept within the pending contract', async ({ page }) => {
    const wp = await firstWatchPath();
    const jobId = await plantHumanReviewCard(wp, 'hang');
    try {
      // The graph read never answers; Accept must not wait for it past 10 s.
      await page.route(/\/api\/tasks\/[^/]+\/provenance(\?|$)/, () => { /* never fulfilled */ });
      await page.setViewportSize({ width: 1280, height: 800 });

      const elapsed = await openAndTimeAccept(page, jobId, wp.path);
      expect(elapsed, `Accept became clickable after ${elapsed} ms`).toBeLessThan(PENDING_CONTRACT_MS);

      await clickAcceptAndExpectMove(page, jobId);
      await expect.poll(async () => (await getJob(jobId, wp.path)).state,
        { timeout: PENDING_CONTRACT_MS }).toBe('6-completed');
    } finally {
      await deleteJob(jobId, wp.path);
    }
  });
});
