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
  await page.getByTestId('studio-attention-trigger').click();
  const list = page.getByTestId('studio-attention-list');
  await expect(list).toBeVisible();
  await expect.poll(async () => {
    const count = Number((await page.getByTestId('studio-attention-count').textContent())?.trim());
    const rows = await list.getByTestId('attention-row').count();
    const groups = (await list.getByTestId('attention-count-in-group').allTextContents())
      .reduce((sum, value) => sum + Number(value.trim()), 0);
    return count > 0 && count === rows && count === groups;
  }).toBe(true);
  return Number((await page.getByTestId('studio-attention-count').textContent())?.trim());
}

async function screenshot(page: Page, name: string): Promise<void> {
  mkdirSync(SHOT_DIR, { recursive: true });
  await page.screenshot({ path: join(SHOT_DIR, `${name}.png`), animations: 'disabled' });
}

test.describe('Operator attention list', () => {
  test('accepts three Human Review cards from Needs you and auto-advance with a stale board projection', async ({ page }) => {
    test.setTimeout(240_000);
    const [watch] = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
    expect(watch).toBeTruthy();
    const suffix = Date.now().toString(36);
    const cards: { id: string; title: string }[] = [];
    try {
      for (let index = 0; index < 3; index++) {
        const title = `Acceptance queue ${suffix} ${index}`;
        cards.push({ ...await plantReviewCard(watch.path, title), title });
      }
      const cardDetails = await Promise.all(cards.map(card => getJob(card.id, watch.path)));
      const coreUrl = (id: string) => `/api/tasks/${encodeURIComponent(id)}/core?project=${encodeURIComponent(cardDetails[0].projectName)}`;
      await expect.poll(async () => Promise.all(cards.map(async card => {
        try { return (await api<{ state: string }>(coreUrl(card.id))).state; }
        catch (error) { return String(error); }
      })), { timeout: 60_000 }).toEqual(['ready', 'ready', 'ready']);
      const coreKeys = await Promise.all(cards.map(card => api<{ taskKey: string }>(coreUrl(card.id))));
      expect(coreKeys.map(core => core.taskKey)).toEqual(cardDetails.map(card =>
        (card as unknown as { taskKey: string }).taskKey));
      const ids = new Set(cards.map(card => card.id));
      // Reproduce the owner route: the board snapshot is behind the fresh
      // detail endpoint, even though all three cards need no integration.
      await page.route('**/api/v1/studio/board**', async route => {
        const headers = route.request().headers();
        delete headers['if-none-match'];
        delete headers['if-modified-since'];
        const response = await route.fetch({ headers });
        const body = await response.json() as { humanReview?: { id: string; integration?: unknown }[] };
        body.humanReview = [
          ...(body.humanReview ?? []).filter(card => !ids.has(card.id)),
          ...[...cardDetails].reverse().map(card => ({
            ...card, integration: { status: 'pending', integrationBranch: 'develop' },
          })),
        ];
        await route.fulfill({ response, json: body });
      });
      // These planted concept cards have no settled content review. Keep
      // the UI's normal Accept request, then give the backend a fixture-only
      // content override so the test can exercise three persisted moves.
      await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/[^/]+\/move(\?|$)/, async route => {
        const id = new URL(route.request().url()).pathname.split('/').at(-2);
        const body = route.request().postDataJSON() as { targetState: string };
        if (!id || !ids.has(id) || body.targetState !== '6-completed') return route.continue();
        const response = await route.fetch({ postData: JSON.stringify({ ...body,
          operatorOverride: true,
          reason: 'Browser fixture has no settled content review; its integration verdict is under test.',
        }) });
        await route.fulfill({ response });
      });
      await gotoStudio(page);
      await expect(page.getByRole('region', { name: 'Human review cards' }))
        .toContainText(cards[2].title, { timeout: 20_000 });

      const newest = cards[2];
      const newestDetail = await getJob(newest.id, watch.path) as unknown as { id: string; key?: string };
      const key = newestDetail.key || newest.id;
      await expect.poll(async () => {
        const attention = await api<{ items: { cardKey: string | null }[] }>('/api/operator/attention');
        return attention.items.map(item => item.cardKey);
      }, { timeout: 30_000 }).toContain(key);
      await page.getByTestId('studio-attention-trigger').click();
      const row = page.getByTestId('attention-group-human-review').getByTestId('attention-row')
        .filter({ has: page.getByTestId('attention-row-card').getByText(key, { exact: true }) });
      await expect(row).toBeVisible({ timeout: 30_000 });
      await row.getByTestId('attention-row-action').click();

      const primary = page.getByTestId('studio-triage-action-mark-done');
      for (const [index, card] of [cards[2], cards[1], cards[0]].entries()) {
        await expect(page.locator('[data-tab-kind="task"]').filter({ hasText: card.title }))
          .toBeVisible({ timeout: 15_000 });
        await expect(primary).toHaveText(/Accept/, { timeout: 2_000 });
        await expect(primary).toBeEnabled({ timeout: 2_000 });
        if (index === 0) {
          await expect(page.getByTestId('studio-task')).toContainText(card.title, { timeout: 15_000 });
          await setTheme(page, 'light');
          await screenshot(page, 'accept-queue-light');
          await setTheme(page, 'dark');
          await screenshot(page, 'accept-queue-dark');
          await page.setViewportSize({ width: 390, height: 844 });
          await screenshot(page, 'accept-queue-390px');
          await page.setViewportSize({ width: 1280, height: 800 });
        }
        const moveResponse = page.waitForResponse(response => response.request().method() === 'POST'
          && response.url().includes(`/tasks/${card.id}/move`));
        await primary.click();
        const persisted = await moveResponse;
        expect(persisted.status(), await persisted.text()).toBeLessThan(300);
        await expect.poll(() => getJob(card.id, watch.path).then(job => job.state),
          { timeout: 30_000 }).toBe('6-completed');
      }
    } finally {
      for (const card of cards)
        await api(`/api/tasks/${encodeURIComponent(card.id)}?watchPath=${encodeURIComponent(watch.path)}`,
          { method: 'DELETE' }).catch(() => undefined);
    }
  });

  test('lists real escalated and human review cards and opens the review card', async ({ page }) => {
    test.setTimeout(180_000);
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
    await page.route('**/api/workbenches', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }),
    }));
    await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ generatedAt: new Date().toISOString(), items: [] }),
    }));
    await page.route('**/api/operator/backups', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ state: 'ok', warnings: [], backups: [], offHost: {} }),
    }));
    const observedAt = new Date(Date.now() - 42 * 60_000).toISOString();
    const row = (id: string, type: string, cardKey: string | null, reason: string, project: string | null = 'alpha') =>
      ({ id, type, project, cardKey, cards: cardKey ? [cardKey] : [], reason, observedAt, suggestedOperations: [] });
    const items = [
      row('1', 'decision-card.opened', 'AGT-9001', 'Which store should hold the dossier index?'),
      row('2', 'escalation.opened', 'AGT-9002', 'The gate failed twice on the same test.'),
      row('3', 'human-review.ready', 'AGT-9003', 'AGT-9003 is ready for human review.'),
      row('4', 'batch.red', null, 'Batch b-17 is red: 3 tests failed.'),
      row('5', 'batch.isolating', null, 'Batch b-17 isolates its red members in 2 diagnostic batches.'),
      row('6', 'push.pending-overdue', 'AGT-9004', 'Push of AGT-9004 has been pending for 14 minutes: remote rejected.'),
      row('10', 'push.blocked', 'AGT-9006', 'Push of AGT-9006 is blocked: Publication stopped: the approved merge result c58e247a1 is not on the local develop integration line, so nothing was pushed to origin/develop.'),
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
          'attention-group-push-blocked', 'attention-group-batch', 'attention-group-push', 'attention-group-quota', 'attention-group-host',
        ]);
        for (const reason of await list.getByTestId('attention-row-reason').allTextContents())
          expect(reason.trim().length).toBeGreaterThan(0);
        await expect(list.getByTestId('attention-row-action')).toHaveCount(items.length);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await screenshot(page, `attention-classes-${viewport.width}-${theme}`);
        await page.keyboard.press('Escape');
        await expect(list).toBeHidden();
      }
    }

    await page.setViewportSize({ width: 1280, height: 800 });
    await openAndReconcile(page);
    await page.getByTestId('attention-group-host').getByTestId('attention-row-action').click();
    // The hosts section lives in the Environment settings surface.
    await expect(page).toHaveURL(/workspace\/settings\/environment/);
  });

  test('owner day shows titles, decisions, installation warnings and reconciled counts', async ({ page }) => {
    const observedAt = new Date(Date.now() - 2 * 60 * 60_000).toISOString();
    const row = (id: string, type: string, cardKey: string | null, title: string, reason: string) => ({
      id, type, project: 'Owner day', cardKey, cards: cardKey ? [cardKey] : [], title, reason,
      observedAt, suggestedOperations: [],
    });
    const items = [
      row('e9', 'escalation.opened', 'DEMO-9', 'Blocked deployment', 'Gate failed twice.'),
      ...Array.from({ length: 8 }, (_, i) => row(`r${i}`, 'human-review.ready', `DEMO-${i + 1}`,
        `Review card ${i + 1}`, i === 5 ? 'Cannot be accepted: brief changed.' :
          `Grade ${i === 4 ? 'B' : i === 7 ? 'C' : 'A'} · ${i === 4 ? 2 : 0} concerns.`)),
      row('batch', 'batch.red', null, 'Batch gate', 'Three tests failed.'),
    ];
    await page.route('**/api/workbenches', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items: [{
        projectName: 'Owner day', workbench: { id: 'concept', key: 'DOC-17',
          title: 'Concept Dossier', status: 'decision-pending', openDecisionCount: 2,
          updatedAtUtc: observedAt },
      }] }),
    }));
    await page.route('**/api/operator/attention', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items, count: items.length }),
    }));
    await page.route('**/api/operator/backups', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ state: 'disabled', stale: false, generatedAt: observedAt,
        warnings: [], backups: [], offHost: {} }),
    }));
    await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ generatedAt: observedAt, items: [{ id: 'cred-1', label: 'Provider token',
        state: 'expiring', detail: 'Expires soon.' }] }),
    }));
    await gotoStudio(page);
    await expect(page.getByTestId('studio-attention-count')).toHaveText(/1[23]/);
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await openAndReconcile(page);
        const list = page.getByTestId('studio-attention-list');
        await expect(list.getByTestId('attention-group-dossier')).toContainText('2 open decisions');
        await expect(list.getByTestId('attention-group-installation')).toContainText('Backups off');
        await expect(list.getByTestId('attention-row-title').filter({ hasText: 'Blocked deployment' })).toBeVisible();
        await expect(list.getByTestId('attention-row-reason').filter({ hasText: 'brief changed' })).toBeVisible();
        await screenshot(page, `owner-day-${viewport.width}-${theme}`);
        await list.getByTestId('attention-group-installation').scrollIntoViewIfNeeded();
        await screenshot(page, `owner-day-installation-${viewport.width}-${theme}`);
        await page.keyboard.press('Escape');
      }
    }
  });
});
