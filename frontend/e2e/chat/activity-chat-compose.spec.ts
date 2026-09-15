import { test, expect } from '@playwright/test';
import { listJobs } from '../helpers/jobs';
import { api } from '../helpers/api';

async function findJobWithOutput(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  for (const j of jobs) {
    try {
      const out = await api<unknown[]>(`/api/tasks/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`);
      if (Array.isArray(out) && out.length > 0) return { id: j.id, watchPath: j.watchPath };
    } catch { /* ignore */ }
  }
  return null;
}

/**
 * Activity tab — chat compose strip + auto-scroll.
 *
 * The activity log behaves like a chat agent transcript: it sticks to the
 * bottom as new lines arrive, and exposes a follow-up textarea + Send button
 * directly below so the user can pause & intervene.
 */

test.describe('Activity tab — chat compose', () => {
  test('compose strip is visible with Send disabled when empty', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    // Switch to the Activity tab inside the inspector.
    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const compose = page.getByTestId('activity-chat-compose');
    await expect(compose).toBeVisible({ timeout: 5_000 });

    const input = page.getByTestId('activity-chat-input');
    const send = page.getByTestId('activity-chat-send');
    await expect(input).toBeVisible();
    await expect(send).toBeVisible();
    await expect(send).toBeDisabled();

    await input.fill('please reply with OK');
    await expect(send).toBeEnabled();

    // Clearing should re-disable.
    await input.fill('');
    await expect(send).toBeDisabled();
  });

  test('compose strip shows the card mode next to Send (AGT-2825)', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();

    const compose = page.getByTestId('activity-chat-compose');
    await expect(compose).toBeVisible({ timeout: 5_000 });

    // The operator sees the card's execution mode before sending, since a
    // concept or planning card refuses an implementation-flavored prompt
    // with 409 unless the request explicitly overrides the mode.
    const modeLabel = page.getByTestId('activity-chat-mode');
    await expect(modeLabel).toBeVisible();
    await expect(modeLabel).toContainText(/mode$/i);
  });

  test('activity log body has the auto-scroll testid', async ({ page }) => {
    const target = await findJobWithOutput();
    if (!target) {
      test.skip(true, 'No job with CLI output available');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await page.getByTestId('inspector-tab-activity').click();

    const body = page.getByTestId('activity-log-body');
    await expect(body).toBeVisible({ timeout: 5_000 });

    // Initially the body should be scrolled to the bottom (sticky default).
    // Allow a small delay for the rAF that performs the scroll.
    await page.waitForTimeout(100);
    const atBottom = await body.evaluate((el) => {
      const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
      return distance <= 24;
    });
    expect(atBottom).toBe(true);
  });
});
