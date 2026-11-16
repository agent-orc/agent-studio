import { test, expect, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3833 (playthrough AGT-3796 F6): the shell is usable from the keyboard.
 *
 * - Ctrl+K and Cmd+K open search on first use, from any focus position, on
 *   the board and on a task tab. Escape closes it and returns focus.
 * - The first Tab stops are visible skip links to the main content and to
 *   Needs you.
 * - A card opened from Needs you puts focus on its decision area, so the
 *   primary action is at most three Tab stops away.
 */

const SHOT_DIR = process.env.JOB_RESULTS_DIR ?? '../results/shell-keyboard-entry';
const MAX_TABS_TO_PRIMARY = 3;

interface WatchPath { path: string }

async function plantReviewCard(watchPath: string, title: string): Promise<{ id: string }> {
  const created = await createJob({
    title, watchPath, targetState: '2-ready', mode: 'concept', requiresIntegration: false, fixture: false,
  });
  await expect.poll(() => getJob(created.id, watchPath).then(() => true, () => false), { timeout: 10_000 }).toBe(true);
  await api(`/api/tasks/${encodeURIComponent(created.id)}/concept-dossier?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'POST',
    body: JSON.stringify({ noDossierNeeded: true, reason: 'Code-free end-to-end fixture.' }),
  });
  await moveJob(created.id, watchPath, '5-human-review');
  return created;
}

async function cardKey(id: string, watchPath: string): Promise<string> {
  const job = await getJob(id, watchPath) as unknown as { key?: string; id: string };
  return job.key || job.id;
}

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/');
  await expect(page.getByTestId('studio-attention-trigger')).toBeVisible({ timeout: 30_000 });
}

async function screenshot(page: Page, name: string): Promise<void> {
  mkdirSync(SHOT_DIR, { recursive: true });
  await page.screenshot({ path: join(SHOT_DIR, `${name}.png`), animations: 'disabled' });
}

/** Ctrl+K (or Cmd+K) from `opener` opens search; Escape closes it and gives focus back. */
async function expectShortcutRoundTrip(page: Page, opener: Locator, chord: 'Control+k' | 'Meta+k'): Promise<void> {
  await opener.focus();
  await expect(opener).toBeFocused();
  await page.keyboard.press(chord);
  const input = page.getByTestId('global-search-input');
  await expect(input).toBeFocused({ timeout: 10_000 });
  await page.keyboard.press('Escape');
  await expect(input).toHaveCount(0);
  await expect(opener).toBeFocused();
}

async function waitForAttention(key: string): Promise<void> {
  await expect.poll(async () => {
    const body = await api<{ items: { cardKey: string | null }[] }>('/api/operator/attention');
    return body.items.map(item => item.cardKey);
  }, { timeout: 30_000 }).toContain(key);
}

/** Opens `key` from Needs you and counts Tab presses until the lane primary has focus. */
async function tabsFromNeedsYouToPrimary(page: Page, key: string): Promise<number> {
  await expect.poll(async () => {
    await page.getByTestId('studio-attention-trigger').click();
    const found = await page.getByTestId('studio-attention-list').getByTestId('attention-row-card')
      .getByText(key, { exact: true }).count();
    if (!found) await page.keyboard.press('Escape');
    return found;
  }, { timeout: 30_000 }).toBeGreaterThan(0);
  const row = page.getByTestId('studio-attention-list').getByTestId('attention-row')
    .filter({ has: page.getByTestId('attention-row-card').getByText(key, { exact: true }) });
  await row.getByTestId('attention-row-action').focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('studio-attention-list')).toBeHidden();
  const primary = page.getByTestId('studio-triage-panel').locator('[data-action-id]');
  await expect(primary).toBeVisible({ timeout: 15_000 });
  // The decision area takes focus once the card has rendered; give the detail
  // load its moment, then assert focus did not land in a pane.
  await expect.poll(() => page.evaluate(() =>
    !!document.activeElement?.closest('[data-decision-focus]')), { timeout: 10_000 }).toBe(true);
  for (let presses = 0; presses <= MAX_TABS_TO_PRIMARY; presses++) {
    if (await primary.evaluate(element => element === document.activeElement)) return presses;
    await page.keyboard.press('Tab');
  }
  return Number.POSITIVE_INFINITY;
}

test.describe('Studio shell keyboard entry', () => {
  test('Ctrl+K and Cmd+K open search on first use from any focus position, Escape returns focus', async ({ page }) => {
    test.setTimeout(120_000);
    await gotoStudio(page);
    // First use: nothing has opened search with the mouse yet.
    await expect(page.getByTestId('global-search-input')).toHaveCount(0);
    await expectShortcutRoundTrip(page, page.getByTestId('studio-attention-trigger'), 'Control+k');
    await expectShortcutRoundTrip(page, page.getByTestId('studio-titlebar-theme'), 'Meta+k');

    // A text field keeps the shortcut too.
    const boardSearch = page.locator('input:visible').first();
    await expectShortcutRoundTrip(page, boardSearch, 'Control+k');
  });

  test('Cmd+K opens search on first use from a fresh page', async ({ page }) => {
    await gotoStudio(page);
    await expectShortcutRoundTrip(page, page.getByTestId('studio-titlebar-theme'), 'Meta+k');
  });

  test('skip links are the first Tab stops and reach the main content and Needs you', async ({ page }) => {
    test.setTimeout(120_000);
    await gotoStudio(page);
    const toMain = page.getByTestId('studio-skip-to-main');
    const toNeedsYou = page.getByTestId('studio-skip-to-needs-you');
    // Each pass starts from a fresh load: after a blur, Chromium resumes Tab
    // order from the element that last had focus, not from the page top.
    const fromTop = async () => {
      await page.reload();
      await expect(page.getByTestId('studio-attention-trigger')).toBeVisible({ timeout: 30_000 });
      await page.keyboard.press('Tab');
    };
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await fromTop();
      await expect(toMain).toBeFocused();
      await expect(toMain).toBeInViewport();
      await expect(toMain).toHaveText('Skip to main content');
      await screenshot(page, `skip-link-${theme}`);

      await page.keyboard.press('Tab');
      await expect(toNeedsYou).toBeFocused();
      await expect(toNeedsYou).toBeInViewport();
      await expect(toNeedsYou).toHaveText('Skip to Needs you');
      await page.keyboard.press('Enter');
      await expect(page.getByTestId('studio-attention-trigger')).toBeFocused();
      // Off focus the links are not painted on screen.
      await expect(toMain).not.toBeInViewport();
      await expect(toNeedsYou).not.toBeInViewport();
    }

    await fromTop();
    await expect(toMain).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('studio-main')).toBeFocused();
    // Tab continues inside the main content, not back in the header.
    await page.keyboard.press('Tab');
    await expect.poll(() => page.evaluate(() =>
      !!document.querySelector('[data-testid="studio-main"]')?.contains(document.activeElement))).toBe(true);
  });

  test('a card opened from Needs you focuses its decision area; Ctrl+K works on the task tab', async ({ page }) => {
    test.setTimeout(180_000);
    const [watch] = await api<WatchPath[]>('/api/watch-paths');
    expect(watch, 'the isolated stack has a project').toBeTruthy();
    const suffix = Date.now().toString(36);
    const review = await plantReviewCard(watch.path, `Keyboard review ${suffix}`);
    const escalated = await createJob({
      title: `Keyboard escalation ${suffix}`, watchPath: watch.path, targetState: '5e-escalated', fixture: false,
    });
    try {
      const reviewKey = await cardKey(review.id, watch.path);
      const escalatedKey = await cardKey(escalated.id, watch.path);
      await waitForAttention(reviewKey);
      await waitForAttention(escalatedKey);
      await gotoStudio(page);

      const reviewTabs = await tabsFromNeedsYouToPrimary(page, reviewKey);
      expect(reviewTabs, 'Tab presses from the decision area to Accept').toBeLessThanOrEqual(MAX_TABS_TO_PRIMARY);
      await expect(page.getByTestId('studio-triage-panel').locator('[data-action-id]')).toHaveText(/Accept/);
      await screenshot(page, 'needs-you-review-decision-focus');

      // The task tab is another route: the shortcut still opens search and
      // gives focus back to where it was.
      await expectShortcutRoundTrip(page, page.getByTestId('studio-triage-panel').locator('[data-action-id]'), 'Control+k');

      const escalatedTabs = await tabsFromNeedsYouToPrimary(page, escalatedKey);
      expect(escalatedTabs, 'Tab presses from the decision area to the escalated primary').toBeLessThanOrEqual(MAX_TABS_TO_PRIMARY);
      await screenshot(page, 'needs-you-escalated-decision-focus');
    } finally {
      for (const job of [review, escalated]) {
        await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watch.path)}`,
          { method: 'DELETE' }).catch(() => undefined);
      }
    }
  });
});
