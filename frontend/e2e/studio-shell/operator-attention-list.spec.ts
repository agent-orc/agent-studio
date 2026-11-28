import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3576: one header entry answers "what needs me".
 *
 * Each header count matches its section in the attention list. The
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
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 120_000 });
  await expect(page.getByTestId('studio-attention-trigger')).toBeVisible({ timeout: 120_000 });
}

/** Opens the list and checks the header count against the visible rows and group counts. */
async function openAndReconcile(page: Page): Promise<number> {
  await page.getByTestId('studio-attention-trigger').click();
  const list = page.getByTestId('studio-attention-list');
  await expect(list).toBeVisible();
  let total = 0;
  await expect.poll(async () => {
    const counts = await list.evaluate(dialog =>
      [['Decisions', 'studio-attention-count'], ['Notices', 'studio-attention-notice-count']].map(([name, id]) => {
        const badge = document.querySelector(`[data-testid="${id}"]`);
        const section = dialog.querySelector(`section[aria-label="${name}"]`);
        const rows = section?.querySelectorAll('[data-testid="attention-row"]').length ?? 0;
        const groups = Array.from(section?.querySelectorAll('[data-testid="attention-count-in-group"]') ?? [])
          .reduce((sum, group) => sum + Number(group.textContent), 0);
        return { header: badge ? Number(badge.textContent) : -1, rows, groups };
      }));
    total = counts.reduce((sum, section) => sum + section.rows, 0);
    return counts.every(section => section.header === section.rows && section.rows === section.groups);
  }).toBe(true);
  return total;
}

async function screenshot(page: Page, name: string): Promise<void> {
  mkdirSync(SHOT_DIR, { recursive: true });
  await page.screenshot({ path: join(SHOT_DIR, `${name}.png`), animations: 'disabled', timeout: 60_000 });
}

test.describe('Operator attention list', () => {
  test('coverage survives failed polls, recovers, and ages retained and new rows while open', async ({ page }) => {
    test.setTimeout(300_000);
    const start = Date.now();
    await page.clock.install({ time: start });
    let fail = true;
    let items: object[] = [];
    let backupFail = false;
    let backupState = 'ok';
    await page.route('**/api/workbenches', route => route.fulfill({ json: { items: [] } }));
    await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
      json: { generatedAt: new Date(start).toISOString(), items: [] },
    }));
    await page.route('**/api/operator/backups', route => route.fulfill(backupFail
      ? { status: 503, json: {} }
      : { json: { state: backupState, stale: false, generatedAt: new Date(start).toISOString(),
        warnings: [], backups: [], offHost: {} } }));
    await page.route('**/api/operator/attention', route => route.fulfill(fail
      ? { status: 503, json: {} } : { json: { items, count: items.length } }));
    await gotoStudio(page);
    const trigger = page.getByTestId('studio-attention-trigger');
    await expect(trigger).toHaveAccessibleName(/Partial coverage/);
    await expect(page.getByTestId('attention-trigger-coverage')).toHaveText('Partial');
    await expect(page.getByTestId('studio-attention-count')).toHaveText('0');
    await trigger.click();
    await expect(page.getByTestId('attention-list-coverage')).toContainText('Attention: unavailable');
    await expect(page.getByTestId('attention-list-empty')).toHaveCount(0);
    await expect(page.getByTestId('attention-row')).toHaveCount(0);
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize(viewport);
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await screenshot(page, `attention-unavailable-${viewport.width}-${theme}--mocked`);
        await page.keyboard.press('Escape');
        await screenshot(page, `attention-partial-trigger-${viewport.width}-${theme}--mocked`);
        await trigger.click();
        await expect(page.getByTestId('attention-list-coverage')).toBeVisible();
      }
    }
    await page.keyboard.press('Escape');
    fail = false;
    await trigger.click();
    await expect(page.getByTestId('attention-list-empty')).toBeVisible();
    await expect(trigger).toHaveAccessibleName(/Nothing needs you/);
    await screenshot(page, 'attention-clear-390-dark--mocked');
    await page.keyboard.press('Escape');
    // A failed read after an empty successful snapshot is still incomplete.
    fail = true;
    await trigger.click();
    await expect(page.getByTestId('attention-list-coverage')).toContainText('Attention: stale');
    await expect(page.getByTestId('studio-attention-count')).toHaveText('0');
    await expect(page.getByTestId('attention-list-empty')).toHaveCount(0);
    await page.keyboard.press('Escape');
    const fact = (id: string, observedAt: number) => ({ id, type: 'decision-card.opened',
      project: 'alpha', cardKey: id, cards: [id], title: `Decision ${id}`, reason: 'Choose the storage location.',
      observedAt: new Date(observedAt).toISOString(), suggestedOperations: [] });
    fail = false;
    backupState = 'disabled';
    const ageStart = await page.evaluate(() => Date.now());
    items = [fact('AGT-9001', ageStart)];
    await openAndReconcile(page);
    await page.keyboard.press('Escape');
    fail = true;
    backupFail = true;
    await openAndReconcile(page);
    await expect(page.getByTestId('attention-list-coverage')).toContainText('Backups: stale');
    await expect(page.getByTestId('studio-attention-count')).toHaveText('1');
    await expect(page.getByTestId('attention-group-installation')).toContainText('Backups off');
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize(viewport);
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await expect(page.getByTestId('attention-row')).toHaveCount(2);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await screenshot(page, `attention-stale-${viewport.width}-${theme}--mocked`);
      }
    }
    await page.clock.fastForward(ageStart + 125_000 - await page.evaluate(() => Date.now()));
    const retained = page.getByTestId('attention-row').filter({ hasText: 'AGT-9001' });
    await expect(retained.getByTestId('attention-row-age')).toHaveText('2 min');
    const addedAt = await page.evaluate(() => Date.now());
    items = [fact('AGT-9001', ageStart), fact('AGT-9002', addedAt)];
    fail = false;
    backupFail = false;
    backupState = 'ok';
    await page.clock.fastForward(15_000);
    const added = page.getByTestId('attention-row').filter({ hasText: 'AGT-9002' });
    await expect(added.getByTestId('attention-row-age')).toHaveText('just now');
    await page.clock.fastForward(60_000);
    await expect(added.getByTestId('attention-row-age')).toHaveText('1 min');
    await expect(retained.getByTestId('attention-row-age')).toHaveText('3 min');
    await expect(page.getByTestId('attention-list-coverage')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('studio-attention-list')).toBeHidden();
  });

  test('backup action opens installation Operations and scrolls to the backup block', async ({ page }) => {
    test.setTimeout(120_000);
    await page.route('**/api/workbenches', route => route.fulfill({ json: { items: [] } }));
    await page.route('**/api/operator/attention', route => route.fulfill({ json: { items: [], count: 0 } }));
    await page.route('**/api/operator/backups', route => route.fulfill({ json: {
      state: 'disabled', stale: false, generatedAt: new Date().toISOString(), warnings: [], backups: [], offHost: {},
    } }));
    // Hold the real Security response so its later expansion cannot be hidden by a fast read.
    let releaseSecurity!: () => void;
    const securityReady = new Promise<void>(resolve => { releaseSecurity = resolve; });
    await page.route('**/api/security/posture', async route => {
      const response = await route.fetch();
      await securityReady;
      await route.fulfill({ response });
    });
    await gotoStudio(page);
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize(viewport);
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await openAndReconcile(page);
        await page.getByTestId('attention-row').filter({ hasText: 'Backups off' })
          .getByTestId('attention-row-action').click();
        await expect(page).toHaveURL(/workspace\/settings\/operations/);
        const panel = page.getByTestId('operations-backups');
        await expect(panel).toBeVisible();
        await expect(page.getByTestId('operations-backups-state')).toBeVisible();
        releaseSecurity();
        await expect(page.getByTestId('operations-security-summary')).toBeVisible();
        await expect(page.getByRole('heading', { name: 'Installation backups', exact: true })).toBeInViewport();
        await screenshot(page, `attention-backup-destination-${viewport.width}-${theme}--mocked`);
      }
    }
  });

  test('shows an open environment incident first with its two destinations in both themes and on a phone', async ({ page }) => {
    const [watch] = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
    const project = watch.name ?? 'alpha';
    const incident = {
      id: `environment.incident-open|${project}|production/20261116`, type: 'environment.incident-open',
      project, cardKey: 'health-production-1', cards: ['health-production-1'],
      title: `${project} · Production incident`, reason: 'Production since 2026-11-16 01:00 UTC: http: HTTP 503',
      observedAt: '2026-11-16T01:00:00Z', suggestedOperations: ['card.inspect'], evidence: [],
    };
    await page.route('**/api/operator/attention', route => route.fulfill({ json: { items: [incident], count: 1 } }));
    await gotoStudio(page);
    for (const { name, width, height } of VIEWPORTS) {
      await page.setViewportSize({ width, height });
      for (const theme of (name === 'desktop' ? ['light', 'dark'] : ['light']) as ('light' | 'dark')[]) {
        await setTheme(page, theme);
        await page.getByTestId('studio-attention-trigger').click();
        const row = page.getByTestId('attention-group-incident').getByTestId('attention-row');
        await expect(row).toContainText('Production since 2026-11-16 01:00 UTC');
        await expect(row).toContainText('HTTP 503');
        await expect(row.getByTestId('attention-row-action')).toHaveText('Open incident card');
        await expect(row.getByTestId('attention-open-operations')).toBeVisible();
        await screenshot(page, `incident-${name}-${theme}--mocked`);
        await page.getByTestId('studio-attention-list').getByRole('button', { name: 'Close' }).click();
      }
    }
    await page.getByTestId('studio-attention-trigger').click();
    await page.getByTestId('attention-open-operations').click();
    await expect(page).toHaveURL(/\/projects\/[^/]+\/operations/);
  });

  test('overlap starvation names both cards and permits Run anyway from Needs you', async ({ page }) => {
    const item = { id: 'dispatch.overlap-starved|Agent Studio/AGT-3988|comparison', type: 'dispatch.overlap-starved',
      project: 'Agent Studio', cardKey: 'AGT-3988', cards: ['AGT-3988', 'AGT-3481'],
      reason: 'AGT-3988 waits for AGT-3481 on backend/Features/Runner/ProjectRunner.cs. Only scope overlap prevents dispatch while slots are free.',
      observedAt: '2026-11-24T03:26:04Z', suggestedOperations: ['dispatch.run-anyway', 'card.inspect'], evidence: [] };
    let permitted = false;
    let fail = true;
    await page.route('**/api/operator/attention', route => route.fulfill({ json: { items: permitted ? [] : [item], count: permitted ? 0 : 1 } }));
    await page.route('**/api/projects/Agent%20Studio/dispatch/run-anyway', route => {
      expect(route.request().method()).toBe('POST');
      expect(route.request().postDataJSON()).toEqual({ fact: item.id });
      if (fail) return route.fulfill({ status: 409, json: { error: 'Comparison changed.' } });
      permitted = true;
      return route.fulfill({ json: { permitted: true, expiresInMinutes: 10 } });
    });
    await page.route('**/api/workbenches', route => route.fulfill({ json: { items: [] } }));
    await gotoStudio(page);
    for (const { name, width, height } of VIEWPORTS) {
      await page.setViewportSize({ width, height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await openAndReconcile(page);
        const row = page.getByTestId('attention-group-overlap').getByTestId('attention-row');
        await expect(row).toContainText('AGT-3988 waits for AGT-3481');
        await expect(row.getByTestId('attention-run-anyway')).toBeEnabled();
        await row.scrollIntoViewIfNeeded();
        await screenshot(page, `overlap-${name}-${theme}--mocked`);
        await page.getByTestId('studio-attention-list').getByRole('button', { name: 'Close' }).click();
      }
    }
    await openAndReconcile(page);
    await page.getByTestId('attention-run-anyway').click();
    await expect(page.getByTestId('attention-overlap-error')).toBeVisible();
    await expect(page.getByTestId('attention-run-anyway')).toBeEnabled();
    fail = false;
    await page.getByTestId('attention-run-anyway').click();
    await expect(page.getByTestId('attention-group-overlap')).toHaveCount(0);
  });

  test('disk pressure appears in Needs you with a host action', async ({ page }) => {
    await page.route('**/api/host/filesystems', route => route.fulfill({ json: {
      host: 'studio-api', observedAt: new Date().toISOString(), level: 'healthy', reason: 'Healthy', filesystems: [],
      remoteHosts: [{ id: 'gate-ssh:gate-host', host: 'gate-host', observedAt: new Date().toISOString(),
        level: 'critical', reason: 'Gate disk is critical; admission paused.',
        filesystems: [{ purpose: 'gate work', path: '/srv/gates', freeBytes: 6 * 1073741824, totalBytes: 246 * 1073741824 }] }],
    } }));
    const item = { id: 'host.disk-pressure|review-01', type: 'host.disk-pressure', project: null,
      cardKey: null, cards: [], title: 'Host review-01 disk space low',
      reason: 'Host review-01: review work file system at / is critical: 6.5 GiB free (2.6%). New work is paused.',
      observedAt: new Date().toISOString(), suggestedOperations: ['stalls.inspect'], evidence: [] };
    await page.route('**/api/operator/attention', route => route.fulfill({ json: { items: [item], count: 1 } }));
    await gotoStudio(page);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await openAndReconcile(page);
      const row = page.getByTestId('attention-group-disk').getByTestId('attention-row');
      await expect(row).toContainText('6.5 GiB free');
      await expect(row).toContainText('New work is paused');
      await expect(row.getByTestId('attention-row-action')).toHaveText('Open hosts');
      await row.scrollIntoViewIfNeeded();
      await screenshot(page, `disk-pressure-desktop-${theme}--mocked`);
      await page.getByTestId('studio-attention-list').getByRole('button', { name: 'Close' }).click();
    }
    await openAndReconcile(page);
    await page.getByTestId('attention-group-disk').getByTestId('attention-row-action').click();
    await expect(page).toHaveURL(/environment/);
    await page.getByTestId('remote-host-primary-row').filter({ hasText: 'gate-host' }).click();
    const load = page.getByTestId('remote-host-detail-toggle-load');
    if (await load.getAttribute('aria-expanded') !== 'true') await load.click();
    const disks = page.getByTestId('remote-host-filesystems');
    await expect(disks).toContainText('/srv/gates');
    await expect(disks).toContainText('free of');
    await disks.scrollIntoViewIfNeeded();
    await screenshot(page, 'gate-disk-operations-dark--mocked');
  });

  test('review host drift shows a status cue and opens its host detail', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.route('**/api/workbenches', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }),
    }));
    await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ generatedAt: new Date().toISOString(), items: [] }),
    }));
    await page.route('**/api/operator/backups', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ state: 'ok', warnings: [], backups: [], offHost: {} }),
    }));
    const item = { id: 'review-host.drift|first-run-review', type: 'review-host.drift', project: null,
      cardKey: null, cards: ['AGT-3648'],
      reason: 'Review host first-run-review worked on AGT-3648 while running commit f947f2b, image review@sha256:bbbb; Stable expects commit 3d61e8b, image review@sha256:aaaa. Graded cards: AGT-3648.',
      observedAt: new Date().toISOString(), suggestedOperations: [],
      evidence: [{ label: 'Review host', href: '/workspace/settings/environment?host=first-run-review' }] };
    await page.route('**/api/operator/attention', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items: [item], count: 1 }),
    }));
    await gotoStudio(page);
    await setTheme(page, 'light');
    await expect(page.getByTestId('review-host-drift-cue')).toBeVisible();
    await page.getByTestId('studio-attention-trigger').click();
    await expect(page.locator('[data-attention-type="review-host.drift"]')).toContainText('AGT-3648');
    await expect(page.locator('[data-attention-type="review-host.drift"] [data-testid="attention-row-reason"]'))
      .toHaveCSS('display', 'block');
    await screenshot(page, 'review-host-drift-attention-1280-light');
    await setTheme(page, 'dark');
    await screenshot(page, 'review-host-drift-attention-1280-dark');
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByTestId('review-host-drift-cue')).toBeVisible();
    await screenshot(page, 'review-host-drift-attention-390-dark');
    await page.locator('[data-attention-type="review-host.drift"] [data-testid="attention-row-action"]').click();
    await expect(page).toHaveURL(/environment\?host=first-run-review/);
  });

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

  test('counts decisions and notices separately and dismisses an escalation group with a reason', async ({ page }) => {
    test.setTimeout(360_000);
    const [watch] = await api<WatchPath[]>('/api/watch-paths');
    const cards = [];
    const titles = ['Review the rollout', 'Choose the recovery step'];
    try {
      for (const title of titles)
        cards.push(await createJob({ title, watchPath: watch.path, targetState: '5e-escalated', mode: 'concept',
          requiresIntegration: false, fixture: false }));
      // Only the notice is synthetic. The two decisions and the dismissal use the isolated server.
      await page.route('**/api/operator/attention', async route => {
        const response = await route.fetch();
        const body = await response.json();
        const items = body.items.filter((item: { type: string; title: string }) =>
          item.type === 'escalation.opened' && titles.includes(item.title));
        items.push({ id: 'disk-fixture', type: 'host.disk-pressure', project: null, cards: [],
          cardKey: null, title: 'Review host', reason: 'Review disk space is low.', isDecision: false,
          observedAt: new Date().toISOString(), suggestedOperations: [] });
        await route.fulfill({ response, json: { items, count: items.length } });
      });
      await page.route('**/api/workbenches', route => route.fulfill({ json: { items: [] } }));
      await page.route('**/api/operator/backups', route => route.fulfill({ json: {
        state: 'ok', stale: false, warnings: [], backups: [], offHost: {},
      } }));
      await page.route('**/api/v1/management/credential-inventory', route => route.fulfill({ json: {
        generatedAt: new Date().toISOString(), items: [],
      } }));
      await gotoStudio(page);
      await expect(page.getByTestId('studio-attention-count')).toHaveText('2', { timeout: 45_000 });
      await expect(page.getByTestId('studio-attention-notice-count')).toHaveText('1');
      for (const surface of ['header', 'list']) {
        if (surface === 'list') await openAndReconcile(page);
        for (const width of [1024, 390]) {
          await page.setViewportSize({ width, height: 850 });
          for (const theme of ['light', 'dark'] as const) {
            await setTheme(page, theme);
            await screenshot(page, `attention-decisions-notices-${surface}-${width}-${theme}`);
          }
        }
      }
      await page.getByTestId('attention-group-escalation').getByTestId('attention-dismiss-group').click();
      await expect(page.getByTestId('confirm-dialog-confirm')).toBeDisabled();
      await page.getByTestId('confirm-dialog-reason-input').fill('Both decisions are tracked in the handover.');
      await page.getByTestId('confirm-dialog-confirm').click();
      await expect(page.getByTestId('studio-attention-count')).toHaveText('0');
      await page.reload({ waitUntil: 'commit', timeout: 90_000 });
      await expect(page.getByTestId('studio-attention-notice-count')).toHaveText('1', { timeout: 60_000 });
      await openAndReconcile(page);
      await expect(page.getByTestId('attention-group-escalation')).toHaveCount(0);
      await expect(page.getByTestId('attention-row')).toHaveCount(1);
    } finally {
      for (const card of cards) await moveJob(card.id, watch.path, '7-archive');
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
      row('10', 'batch-gate.stuck', null, 'Batch b-18 is running for 151 min with 4 pending members. No automatic next step is recorded.'),
      row('6', 'push.pending-overdue', 'AGT-9004', 'Push of AGT-9004 has been pending for 14 minutes: remote rejected.'),
      row('11', 'push.blocked', 'AGT-9006', 'Push of AGT-9006 is blocked: Publication stopped: the approved merge result c58e247a1 is not on the local develop integration line, so nothing was pushed to origin/develop.'),
      row('7', 'quota.exhausted', null, 'claude reached its provider limit until 12:00: rate limited', null),
      row('8', 'quota.fallback-active', 'AGT-9005', 'AGT-9005 runs on the codex fallback.'),
      row('9', 'backend.stalled', null, 'agent-runner-01 refuses claims. Check the host admission.', null),
    ];
    await page.route('**/api/operator/attention', route => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify({ items, count: items.length }),
    }));
    await gotoStudio(page);
    await expect(page.getByTestId('studio-attention-count')).toHaveText('4');

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
        await expect(list.locator('[data-attention-type="batch-gate.stuck"] [data-testid="attention-row-action"]'))
          .toHaveText('Open batch panel');
        await expect(list.getByTestId('attention-row-action')).toHaveCount(items.length);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await screenshot(page, `attention-classes-${viewport.width}-${theme}`);
        if (viewport.width === 390) {
          const stuck = list.locator('[data-attention-type="batch-gate.stuck"]');
          await stuck.scrollIntoViewIfNeeded();
          expect(await stuck.getByTestId('attention-row-reason').evaluate(element =>
            element.scrollHeight <= element.clientHeight + 1)).toBe(true);
          await screenshot(page, `attention-batch-stuck-390-${theme}`);
        }
        await page.keyboard.press('Escape');
        await expect(list).toBeHidden();
      }
    }

    await page.setViewportSize({ width: 1280, height: 800 });
    await openAndReconcile(page);
    await page.locator('[data-attention-type="batch-gate.stuck"] [data-testid="attention-row-action"]').click();
    await expect(page.getByTestId('studio-attention-list')).toBeHidden();
    await expect(page.getByTestId('batch-panel')).toBeVisible();
    await expect(page.getByTestId('batch-panel').getByText(/^alpha · /)).toBeVisible();
    await page.keyboard.press('Escape');
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
    let decisionRecorded = false;
    await page.route('**/api/tasks/acceptance-readiness', route => {
      const requested = (route.request().postDataJSON() as { items: { jobId: string }[] }).items;
      const review = requested.map(({ jobId }) => {
        const ready = jobId === 'DEMO-1' || (decisionRecorded && jobId === 'DEMO-2');
        return { taskKey: jobId, key: jobId, projectName: 'Owner day', readiness: ready ? 'ready' : 'no-content-verdict',
          headline: ready ? 'Ready to accept' : 'No content verdict',
          reason: ready ? 'All completion checks pass.' : 'The review has no content verdict. Add a settled content review before accepting.' };
      });
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        checkedAtUtc: new Date().toISOString(), counts: { ready: review.filter(item => item.readiness === 'ready').length }, items: review,
      }) });
    });
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
    await expect(page.getByTestId('studio-attention-count')).toHaveText('10');
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await openAndReconcile(page);
        const list = page.getByTestId('studio-attention-list');
        await expect(list.getByTestId('attention-group-dossier')).toContainText('2 open decisions');
        await expect(list.getByTestId('attention-group-installation')).toContainText('Backups off');
        await expect(list.getByTestId('attention-row-title').filter({ hasText: 'Blocked deployment' })).toBeVisible();
        await expect(list.getByTestId('attention-review-ready-count')).toHaveText(/1 of 8 ready to accept/);
        const reviewRows = list.getByTestId('attention-group-human-review').getByTestId('attention-row');
        await expect(reviewRows.first().getByTestId('attention-review-status')).toContainText('Ready to accept');
        await expect(reviewRows.nth(1).getByTestId('attention-review-status')).toContainText('No content verdict');
        await expect(reviewRows.nth(1).getByTestId('attention-row-reason')).toContainText('Add a settled content review');
        if (viewport.width === 390) {
          await reviewRows.nth(1).scrollIntoViewIfNeeded();
          expect(await reviewRows.nth(1).getByTestId('attention-row-reason').evaluate(element =>
            element.scrollHeight <= element.clientHeight + 1)).toBe(true);
        }
        await screenshot(page, `owner-day-${viewport.width}-${theme}`);
        await list.getByTestId('attention-group-installation').scrollIntoViewIfNeeded();
        await screenshot(page, `owner-day-installation-${viewport.width}-${theme}`);
        await page.keyboard.press('Escape');
      }
    }
    decisionRecorded = true;
    await openAndReconcile(page);
    await expect(page.getByTestId('attention-review-ready-count')).toHaveText(/2 of 8 ready to accept/);
    await page.keyboard.press('Escape');
  });
});
