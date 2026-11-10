import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3576: one header entry answers "what needs me".
 *
 * The header count is the number of rows the attention list shows (R3); the
 * rows are grouped by the decision-event classes of the operator stream and
 * each offers one action. A human review card opens from the list without
 * scrolling the board. Covered at 1280 and 390 px in light and dark themes.
 */

const SHOT_DIR = process.env.JOB_RESULTS_DIR ?? '../results/operator-attention';
const VIEWPORTS = [
  { name: 'desktop', width: 1280, height: 800 },
  { name: 'phone', width: 390, height: 844 },
] as const;

interface WatchPath { path: string; name?: string }

/** Human review is a protected lane: a code-free concept card reaches it from Ready. */
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

async function gotoStudio(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('studio-attention-trigger')).toBeVisible({ timeout: 30_000 });
}

/** Opens the list and checks the header count against the visible rows and group counts. */
async function openAndReconcile(page: Page): Promise<number> {
  const count = Number((await page.getByTestId('studio-attention-count').textContent())?.trim());
  await page.getByTestId('studio-attention-trigger').click();
  const list = page.getByTestId('studio-attention-list');
  await expect(list).toBeVisible();
  await expect(list.getByTestId('attention-row')).toHaveCount(count);
  const groupCounts = await list.getByTestId('attention-count-in-group').allTextContents();
  expect(groupCounts.reduce((sum, value) => sum + Number(value.trim()), 0)).toBe(count);
  await expect(page.getByTestId('studio-attention-count')).toHaveText(String(count));
  return count;
}

async function screenshot(page: Page, name: string): Promise<void> {
  mkdirSync(SHOT_DIR, { recursive: true });
  await page.screenshot({ path: join(SHOT_DIR, `${name}.png`), animations: 'disabled' });
}

test.describe('Operator attention list', () => {
  test('lists real escalated and human review cards and opens the review card', async ({ page }) => {
    const [watch] = await api<WatchPath[]>('/api/watch-paths');
    expect(watch, 'the isolated stack has a project').toBeTruthy();
    const suffix = Date.now().toString(36);
    const review = await plantReviewCard(watch.path, `Attention review ${suffix}`);
    const escalated = await createJob({
      title: `Attention escalation ${suffix}`, watchPath: watch.path, targetState: '5e-escalated', fixture: false,
    });
    try {
      const reviewCard = await getJob(review.id, watch.path) as unknown as { key?: string; id: string };
      const reviewKey = reviewCard.key || reviewCard.id;
      await gotoStudio(page);
      // The task index and the fact read publish asynchronously.
      await expect.poll(async () => {
        const body = await api<{ items: { cardKey: string | null }[] }>('/api/operator/attention');
        return body.items.map(item => item.cardKey);
      }, { timeout: 30_000 }).toEqual(expect.arrayContaining([reviewKey]));
      await page.reload();
      await expect.poll(async () => Number(await page.getByTestId('studio-attention-count').textContent()),
        { timeout: 30_000 }).toBeGreaterThanOrEqual(2);

      for (const viewport of VIEWPORTS) {
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        for (const theme of ['light', 'dark'] as const) {
          await setTheme(page, theme);
          await openAndReconcile(page);
          const list = page.getByTestId('studio-attention-list');
          await expect(list.getByTestId('attention-group-human-review')).toBeVisible();
          await expect(list.getByTestId('attention-group-escalation')).toBeVisible();
          await screenshot(page, `attention-real-${viewport.width}-${theme}`);
          await page.keyboard.press('Escape');
          await expect(list).toBeHidden();
        }
      }

      await page.setViewportSize({ width: 1280, height: 800 });
      await openAndReconcile(page);
      const reviewRow = page.getByTestId('studio-attention-list').getByTestId('attention-row')
        .filter({ has: page.getByTestId('attention-row-card').getByText(reviewKey, { exact: true }) });
      await expect(reviewRow).toHaveCount(1);
      await reviewRow.getByTestId('attention-row-action').click();
      await expect(page.getByTestId('studio-attention-list')).toBeHidden();
      await expect(page.locator(`[data-tab-kind="task"]`).filter({ hasText: `Attention review ${suffix}` }))
        .toBeVisible({ timeout: 15_000 });
    } finally {
      for (const job of [review, escalated]) {
        await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watch.path)}`,
          { method: 'DELETE' }).catch(() => undefined);
      }
    }
  });

  test('groups every decision-event class and counts exactly the visible rows', async ({ page }) => {
    const observedAt = new Date(Date.now() - 42 * 60_000).toISOString();
    const row = (id: string, type: string, cardKey: string | null, reason: string, project: string | null = 'alpha') =>
      ({ id, type, project, cardKey, cards: cardKey ? [cardKey] : [], reason, observedAt, suggestedOperations: [] });
    const items = [
      row('1', 'decision-card.opened', 'AGT-9001', 'Which store should hold the dossier index?'),
      row('2', 'escalation.opened', 'AGT-9002', 'The gate failed twice on the same test.'),
      row('3', 'human-review.ready', 'AGT-9003', 'AGT-9003 is ready for human review.'),
      row('4', 'batch.red', null, 'Batch b-17 is red: 3 tests failed.'),
      row('5', 'batch.isolating', null, 'Batch b-17 isolates its red members in 2 diagnostic batches.'),
      row('10', 'batch-gate.stuck', null, 'Batch b-18 is running for 151 min with 4 pending members. No automatic next step is recorded.'),
      row('6', 'push.pending-overdue', 'AGT-9004', 'Push of AGT-9004 has been pending for 14 minutes: remote rejected.'),
      row('7', 'quota.exhausted', null, 'claude reached its provider limit until 12:00: rate limited', null),
      row('8', 'quota.fallback-active', 'AGT-9005', 'AGT-9005 runs on the codex fallback.'),
      row('9', 'backend.stalled', null, 'agent-runner-01 refuses claims. Check the host admission.', null),
    ];
    await page.route('**/api/operator/attention', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items, count: items.length }),
    }));
    await gotoStudio(page);
    await expect(page.getByTestId('studio-attention-count')).toHaveText(String(items.length));

    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        expect(await openAndReconcile(page)).toBe(items.length);
        const list = page.getByTestId('studio-attention-list');
        const groups = await list.locator('[data-testid^="attention-group-"]').evaluateAll(nodes =>
          nodes.map(node => node.getAttribute('data-testid')));
        expect(groups).toEqual([
          'attention-group-decision', 'attention-group-escalation', 'attention-group-human-review',
          'attention-group-batch', 'attention-group-push', 'attention-group-quota', 'attention-group-host',
        ]);
        for (const reason of await list.getByTestId('attention-row-reason').allTextContents())
          expect(reason.trim().length).toBeGreaterThan(0);
        await expect(list.locator('[data-attention-type="batch-gate.stuck"] [data-testid="attention-row-action"]'))
          .toHaveText('Open batch panel');
        await expect(list.getByTestId('attention-row-action')).toHaveCount(items.length);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await screenshot(page, `attention-classes-${viewport.width}-${theme}`);
        await page.keyboard.press('Escape');
        await expect(list).toBeHidden();
      }
    }

    await page.setViewportSize({ width: 1280, height: 800 });
    await openAndReconcile(page);
    await page.locator('[data-attention-type="batch-gate.stuck"] [data-testid="attention-row-action"]').click();
    await expect(page.getByTestId('studio-attention-list')).toBeHidden();
    await expect(page.getByTestId('batch-panel')).toBeVisible();
    await expect(page.getByTestId('batch-panel').getByText('alpha', { exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await openAndReconcile(page);
    await page.getByTestId('attention-group-host').getByTestId('attention-row-action').click();
    // The hosts section lives in the Environment settings surface.
    await expect(page).toHaveURL(/workspace\/settings\/environment/);
  });
});
