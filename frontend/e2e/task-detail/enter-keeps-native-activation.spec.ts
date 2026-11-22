import { test, expect, type Locator, type Page, type Request } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';
import { SNAPSHOT } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

/**
 * AGT-3828 - playthrough AGT-3796, finding F1. While a card detail was open, a
 * document-level Enter ran the card's primary action from any focused button
 * or link in Studio and swallowed that control's own action: Enter on Needs
 * you accepted a Human Review card. Enter must activate the focused control;
 * the primary shortcut only runs from the card surface itself.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

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

/** A code-free Human Review card whose Accept is ready, with a current result. */
async function plantReadyHumanReviewCard(wp: WatchPath, label: string): Promise<string> {
  const created = await createJob({
    id: `e2e-enter-${label}-${Date.now()}-${Math.floor(Math.random() * 9999)}`,
    title: `Enter keeps native ${label}`,
    watchPath: wp.path,
    targetState: '2-ready',
    mode: 'concept',
    requiresIntegration: false,
  });
  for (let attempt = 0; attempt < 25; attempt++) {
    try { await getJob(created.id, wp.path); break; }
    catch { await new Promise(resolve => setTimeout(resolve, 200)); }
  }
  const qs = `?watchPath=${encodeURIComponent(wp.path)}`;
  await api(`/api/tasks/${encodeURIComponent(created.id)}/concept-dossier${qs}`, {
    method: 'POST',
    body: JSON.stringify({ noDossierNeeded: true, reason: 'Code-free end-to-end fixture.' }),
  });
  await moveJob(created.id, wp.path, '5-human-review');
  const claim = await api<{ contentReview?: { currentBriefVersion?: string } }>(
    `/api/tasks/${encodeURIComponent(created.id)}/delivery-claim${qs}`);
  const briefVersion = claim.contentReview?.currentBriefVersion;
  if (!briefVersion) throw new Error('The delivery claim names no current brief version.');
  const folder = (await getJob(created.id, wp.path)).folderPath;
  fs.writeFileSync(path.join(folder, 'content-review-context.json'), JSON.stringify({ briefVersion }));
  fs.writeFileSync(path.join(folder, 'aspect-concept-fit.json'),
    JSON.stringify({ status: 'pass', summary: 'Fixture concept fit for the AGT-3828 keyboard spec.' }));
  fs.writeFileSync(path.join(folder, 'status.md'), '# Result\n\nThe current result of the keyboard fixture.\n');
  return created.id;
}

/** Records every move request the page sends. */
function recordMoves(page: Page): Request[] {
  const moves: Request[] = [];
  page.on('request', request => {
    if (request.method() === 'POST' && /\/tasks\/[^/]+\/move(\?|$)/.test(request.url())) moves.push(request);
  });
  return moves;
}

async function dismissBlockingToasts(page: Page): Promise<void> {
  for (let i = 0; i < 5; i++) {
    const close = page.getByTestId('notification-close').first();
    if (!(await close.isVisible({ timeout: 200 }).catch(() => false))) return;
    await close.click({ timeout: 1_000 }).catch(() => {});
  }
}

/** Opens the card and waits until Enter would have accepted it before the fix. */
async function openReadyCard(page: Page, jobId: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  const accept = page.getByTestId('studio-triage-action-mark-done');
  await expect(accept).toBeEnabled({ timeout: 30_000 });
  await expect(accept).not.toHaveAttribute('data-git-loading', 'true');
  await dismissBlockingToasts(page);
  await dismissDevErrorDialog(page);
}

/** Focuses the control (the card's initial focus may land late) and presses Enter on it. */
async function pressEnterOn(control: Locator): Promise<void> {
  await expect(async () => {
    await control.focus();
    await expect(control).toBeFocused({ timeout: 500 });
  }).toPass({ timeout: 10_000 });
  await control.page().keyboard.press('Enter');
}

async function saveScreenshot(page: Page, fileName: string): Promise<void> {
  if (!RESULTS_DIR) return;
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await page.screenshot({ path: path.join(RESULTS_DIR, fileName) });
}

/** Leaves time for a hijacked Enter to reach the network before asserting none did. */
async function expectNoMove(page: Page, moves: Request[], control: string): Promise<void> {
  await page.waitForTimeout(500);
  expect(moves.map(move => move.postData()), `Enter on ${control} sent a move request`).toEqual([]);
}

test.describe('Enter keeps native activation while a card detail is open (AGT-3828)', () => {
  test.beforeEach(async ({ page }) => {
    await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: structuredClone(SNAPSHOT) }));
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  for (const theme of ['light', 'dark'] as Theme[]) {
    test(`Enter on shell and card controls runs their own action (${theme})`, async ({ page }) => {
      test.setTimeout(240_000);
      const wp = await firstWatchPath();
      const jobId = await plantReadyHumanReviewCard(wp, theme);
      const moves = recordMoves(page);
      try {
        await page.goto('/');
        await setTheme(page, theme);

        await openReadyCard(page, jobId, wp.path);
        const accept = page.getByTestId('studio-triage-action-mark-done');
        await expect(accept).toHaveAttribute('aria-keyshortcuts', 'Enter');
        await expect(accept).toHaveAttribute('aria-description', 'Shortcut: Enter while the card content has focus.');

        // Needs you opens its attention dialog.
        await pressEnterOn(page.getByTestId('studio-attention-trigger'));
        await expect(page.getByTestId('studio-attention-list')).toBeVisible();
        await saveScreenshot(page, `agt-3828-enter-needs-you-1280-${theme}.png`);
        await expectNoMove(page, moves, 'Needs you');
        await page.getByTestId('studio-attention-list-close').click();
        await expect(page.getByTestId('studio-attention-list')).toHaveCount(0);

        // Previous results opens its list inside the card.
        await openReadyCard(page, jobId, wp.path);
        await page.getByTestId('inspector-tab-protocol').click();
        const previous = page.getByTestId('previous-results-toggle');
        await expect(previous).toBeVisible({ timeout: 15_000 });
        await expect(previous).toHaveAttribute('aria-expanded', 'false');
        await pressEnterOn(previous);
        await expect(previous).toHaveAttribute('aria-expanded', 'true');
        await expect(page.getByTestId('previous-results-list')).toBeVisible();
        await saveScreenshot(page, `agt-3828-enter-previous-results-1280-${theme}.png`);
        await expectNoMove(page, moves, 'Previous results');

        // The footer usage chip opens the usage detail.
        const usageChip = page.getByTestId('footer-usage-cost');
        await expect(usageChip).toBeVisible({ timeout: 30_000 });
        await pressEnterOn(usageChip);
        await expect(page.getByTestId('usage-detail-surface')).toBeVisible();
        await saveScreenshot(page, `agt-3828-enter-usage-chip-1280-${theme}.png`);
        await expectNoMove(page, moves, 'the footer usage chip');
        await page.getByTestId('usage-detail-close').click();

        // Settings opens Workspace settings.
        await openReadyCard(page, jobId, wp.path);
        await pressEnterOn(page.getByTestId('studio-titlebar-settings'));
        await expect(page.getByTestId('workspace-settings-title')).toBeVisible({ timeout: 15_000 });
        await saveScreenshot(page, `agt-3828-enter-settings-1280-${theme}.png`);
        await expectNoMove(page, moves, 'Settings');

        expect((await getJob(jobId, wp.path)).state).toBe('5-human-review');

        // A dialog submit runs the dialog's own action: Delete removes the card.
        await openReadyCard(page, jobId, wp.path);
        await page.getByTestId('studio-triage-overflow-btn').click();
        await page.getByTestId('studio-triage-overflow-item-delete').click();
        const dialog = page.getByTestId('confirm-dialog');
        await expect(dialog).toBeVisible();
        const reason = dialog.getByTestId('confirm-dialog-reason-input');
        if (await reason.count()) await reason.fill('Keyboard fixture is no longer needed.');
        const deleted = page.waitForRequest(request => request.method() === 'DELETE'
          && request.url().includes(`/tasks/${encodeURIComponent(jobId)}`), { timeout: 15_000 });
        await saveScreenshot(page, `agt-3828-enter-dialog-submit-1280-${theme}.png`);
        await pressEnterOn(dialog.getByTestId('confirm-dialog-confirm'));
        await deleted;
        await expect(dialog).toHaveCount(0);
        await expectNoMove(page, moves, 'the dialog submit');
      } finally {
        await deleteJob(jobId, wp.path).catch(() => {});
      }
    });
  }

  test('Enter on the card surface still runs the primary action', async ({ page }) => {
    test.setTimeout(120_000);
    const wp = await firstWatchPath();
    const jobId = await plantReadyHumanReviewCard(wp, 'surface');
    const moves = recordMoves(page);
    try {
      await openReadyCard(page, jobId, wp.path);
      await page.getByTestId('inspector-tab-protocol').click();
      const surface = page.getByTestId('pane-protocol-body');
      await expect(async () => {
        await surface.focus();
        await expect(surface).toBeFocused({ timeout: 500 });
      }).toPass({ timeout: 10_000 });

      // The deferred usage surface remains mounted after its native dialog closes.
      const usageChip = page.getByTestId('footer-usage-cost');
      await expect(usageChip).toBeVisible({ timeout: 30_000 });
      await usageChip.click();
      const usage = page.getByTestId('usage-detail-surface');
      await expect(usage).toHaveAttribute('open', '');
      // Focusing outside the desktop popover closes it, so dispatch the card
      // event without changing focus while the dialog still owns the keyboard.
      await surface.evaluate(element => element.dispatchEvent(new KeyboardEvent('keydown', {
        key: 'Enter', bubbles: true, cancelable: true,
      })));
      await expect(usage).toHaveAttribute('open', '');
      await expectNoMove(page, moves, 'the card surface with usage open');
      await page.getByTestId('usage-detail-close').click();
      await expect(usage).not.toHaveAttribute('open', '');
      await expect(usage).toHaveCount(1);

      // An open menu owns Enter even after focus returns to the card surface.
      await page.getByTestId('studio-triage-overflow-btn').click();
      await expect(page.getByTestId('studio-triage-overflow-panel')).toBeVisible();
      await surface.evaluate(element => (element as HTMLElement).focus());
      await page.keyboard.press('Enter');
      await expectNoMove(page, moves, 'the card surface with a menu open');
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('studio-triage-overflow-panel')).toHaveCount(0);

      await surface.focus();
      const move = page.waitForRequest(request => request.method() === 'POST'
        && request.url().includes(`/tasks/${encodeURIComponent(jobId)}/move`), { timeout: 15_000 });
      await page.keyboard.press('Enter');
      expect((await move).postDataJSON()).toMatchObject({ targetState: '6-completed' });
    } finally {
      await deleteJob(jobId, wp.path);
    }
  });
});
