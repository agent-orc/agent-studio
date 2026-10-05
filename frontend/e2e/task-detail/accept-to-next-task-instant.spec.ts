import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

function uid(prefix: string) {
  return `e2e-${prefix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * Plant a lane of `count` review-ready tasks. The create→move sequence
 * mirrors `triage-actions-in-detail-header.spec.ts` so a fresh scanner
 * cache does not 404 the move.
 */
async function plantHumanReviewJobs(wp: WatchPath, count: number): Promise<{ id: string; title: string }[]> {
  const jobs: { id: string; title: string }[] = [];
  for (let i = 0; i < count; i++) {
    const requestedId = uid(`accept-${i}`);
    const title = `accept fixture ${i} ${requestedId}`;
    // fixture: false so the cards show up in `/api/tasks/grouped` and the
    // lane-pager snapshot effect can capture peers (the prefetch hangs
    // off that snapshot). The runner does not auto-start tasks parked
    // in 5-human-review, so the lane stays stable for the test.
    const created = await createJob({
      id: requestedId,
      title,
      watchPath: wp.path,
      targetState: '2-ready',
      fixture: false,
    });
    for (let attempt = 0; attempt < 25; attempt++) {
      try { await getJob(created.id, wp.path); break; }
      catch { await new Promise(r => setTimeout(r, 200)); }
    }
    await moveJob(created.id, wp.path, '5-human-review');
    jobs.push({ id: created.id, title });
  }
  return jobs;
}

/**
 * Dismiss any stack of toast notifications that an unrelated previous
 * run may have left on screen. The studio slim-tab-bar action cluster
 * sits below the notification stack, so a lingering toast (e.g.
 * "Update failed") intercepts pointer events on the very buttons this
 * spec needs to click. Same fix pattern as `triage-actions-in-detail-header.spec.ts`.
 */
async function dismissBlockingToasts(page: Page): Promise<void> {
  for (let i = 0; i < 5; i++) {
    const closeBtn = page.getByTestId('notification-close').first();
    if (!(await closeBtn.isVisible({ timeout: 200 }).catch(() => false))) return;
    await closeBtn.click({ timeout: 1_000 }).catch(() => undefined);
  }
}

async function openJobInDetail(page: Page, id: string, watchPath: string) {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('studio-triage-panel')).toBeVisible({ timeout: 10_000 });
  await dismissBlockingToasts(page);
}

/**
 * Click into the detail through the board card so the lane-pager
 * snapshot is captured synchronously. The deep-link / URL-restore path
 * does NOT capture by itself (the ensure-snapshot effect can fail to
 * fire when grouped is still loading), and the prefetch effect that
 * makes accept feel instant hangs off that snapshot.
 */
async function openViaBoard(page: Page, id: string): Promise<void> {
  await page.goto('/');
  const card = page.locator('[data-testid="job-card"]', { hasText: id }).first();
  await expect(card).toBeVisible({ timeout: 30_000 });
  await card.click();
  await expect(page.getByTestId('studio-triage-panel')).toBeVisible({ timeout: 15_000 });
  await dismissBlockingToasts(page);
}

/**
 * Accept-to-next-task instant-feel + perf-budget regression for
 * `bug-perf-accept-action-to-next-task-display-latency-dramatic-improvement`.
 *
 * The previous shape awaited the move POST before fetching detail for
 * the next peer, so the user paid both roundtrips in series (visible
 * 1-3 s lag on the original report). The new shape:
 *   1. prefetches the next peer's `JobDetail` on detail open,
 *   2. navigates to the next peer the moment the move click fires
 *      (before the POST has a chance to return), and
 *   3. reverts navigation + repaints the lane on a POST failure.
 *
 * These specs assert all three: an instant swap, a POST that still
 * fires, and a P95 budget under 250 ms over a triage sweep. The budget
 * is intentionally loose vs. the 100 ms acceptance target in the task
 * brief - dev-mode change detection floor is much higher than the
 * production target, and we want a clear-regression canary rather than
 * a flaky tight bound.
 */
test.describe('Accept-to-next-task is instant', () => {
  test('renders the next peer without waiting for the move POST roundtrip', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const jobs = await plantHumanReviewJobs(wp, 3);
    try {
      await openJobInDetail(page, jobs[0].id, wp.path);

      // Wait for the studio slim pager to anchor on the open job. The
      // ensure-snapshot effect captures peers off `/api/tasks/grouped`
      // which polls every ~2-5 s; on a fresh fixture plant the first
      // poll may still be carrying the pre-plant snapshot and the
      // open job's index will read `0 / N`. Once the position is
      // non-zero the snapshot covers the open job and the prefetch
      // effect has fired for the next slot.
      const slimPagerPos = page.getByTestId('studio-task-pager-position');
      await expect(slimPagerPos).toBeVisible({ timeout: 30_000 });
      await expect.poll(
        async () => (await slimPagerPos.textContent())?.trim() ?? '',
        { timeout: 30_000, intervals: [200, 500, 1000, 2000, 2000] },
      ).toMatch(/^([1-9]\d*)\s*\/\s*\d+$/);
      // One settle tick so the lookahead prefetch finishes.
      await page.waitForTimeout(750);
      await dismissBlockingToasts(page);

      // Watch the move POST so the test proves it actually goes out.
      const movePromise = page.waitForResponse(
        r => r.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(jobs[0].id)}/move`),
        { timeout: 10_000 },
      );

      // Click → measure wall time until the URL points at the next peer.
      // The new shape switches the URL synchronously after the optimistic
      // advance lands; the previous shape only swapped after the POST
      // returned. We use the URL change rather than a card-text assertion
      // because the studio header strips text down to the lane action.
      const beforeUrl = page.url();
      const departingId = jobs[0].id;
      const t0 = Date.now();
      // `{ force: true }`: a late-arriving notification toast may stack
      // on top of the slim tab-bar between the dismiss above and the
      // click. Playwright's actionability retry would tax the latency
      // measurement with hundreds of ms of "scroll + re-wait" that
      // belongs to the toast, not the navigation under test.
      await page.getByTestId('studio-triage-action-mark-done').click({ force: true });
      // The URL should swap off the departing slug to any other slug -
      // the live lane order can include other fixtures from prior runs,
      // so we don't bind to a specific next id, only to "not the one we
      // just acted on".
      await page.waitForFunction(
        ({ before, departing }) =>
          window.location.href !== before &&
          !window.location.href.includes(`job=${encodeURIComponent(departing)}`),
        { before: beforeUrl, departing: departingId },
        { timeout: 5_000 },
      );
      const renderedMs = Date.now() - t0;

      const moveResponse = await movePromise;
      expect(moveResponse.status(), 'move POST should be 200').toBe(200);

      console.log(`[accept-to-next-task] render took ${renderedMs} ms`);
      // Dev-mode budget. Production target is < 100 ms, but the dev build
      // pays change-detection + zone overhead on every render. 1 s is the
      // clear-regression line - over that and we are back to the pre-fix
      // serial-roundtrip behaviour.
      expect(renderedMs).toBeLessThan(1000);

      // The original task ended up in 6-completed - prove the optimistic
      // navigation did not silently skip the persist step.
      await expect.poll(
        async () => (await getJob(jobs[0].id, wp.path)).state,
        { timeout: 10_000 }
      ).toBe('6-completed');
    } finally {
      for (const j of jobs) await deleteJob(j.id, wp.path).catch(() => {});
    }
  });

  test('performance.measure(accept-to-next-task) P95 stays under 250 ms across a triage sweep', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const jobs = await plantHumanReviewJobs(wp, 6);
    try {
      // Open the first fixture so we have a real lane-pager iteration.
      await openJobInDetail(page, jobs[0].id, wp.path);
      // Same wait-for-pager-anchor as the instant-feel test: the
      // ensure-snapshot effect needs grouped() to reflect the fresh
      // fixture before the prefetch can warm anything.
      const slimPagerPos = page.getByTestId('studio-task-pager-position');
      await expect(slimPagerPos).toBeVisible({ timeout: 30_000 });
      await expect.poll(
        async () => (await slimPagerPos.textContent())?.trim() ?? '',
        { timeout: 30_000, intervals: [200, 500, 1000, 2000, 2000] },
      ).toMatch(/^([1-9]\d*)\s*\/\s*\d+$/);
      await page.waitForTimeout(750);
      await dismissBlockingToasts(page);

      // Reset the perf buffer so prior boots / navigations don't leak in.
      await page.evaluate(() => {
        try { performance.clearMarks(); performance.clearMeasures(); } catch {}
      });

      // Walk forward through the first five Mark-as-Done clicks. Each
      // click is bracketed by `accept-click` / `next-task-rendered`
      // marks set by JobSelectionService. `{ force: true }` mirrors the
      // instant-feel test - a late toast must not bias the latency
      // measurement.
      for (let i = 0; i < 5; i++) {
        await page.getByTestId('studio-triage-action-mark-done').click({ force: true });
        // Wait for the URL to step so the next iteration's click lands
        // on the new selection.
        await expect.poll(() => page.url(), { timeout: 10_000 })
          .not.toContain(jobs[i].id);
      }

      const samples = await page.evaluate(() => {
        return performance.getEntriesByName('accept-to-next-task', 'measure')
          .map(e => e.duration);
      });

      console.log(`[accept-pipeline-budget] samples=${JSON.stringify(samples.map(n => Math.round(n)))}`);
      expect(samples.length, 'measure() should fire on every click').toBeGreaterThanOrEqual(3);

      // P95 over the sample set. With <= 5 samples that is the max,
      // which is the right behaviour: we want the worst-case to stay
      // within budget on every click, not just on average.
      const sorted = [...samples].sort((a, b) => a - b);
      const p95Index = Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95));
      const p95 = sorted[p95Index];
      // 250 ms is the dev-mode ceiling (zone + change detection floor is
      // higher than the 100 ms production aim from the task brief);
      // anything above this signals a return to serial roundtrips.
      expect(p95, `P95 was ${Math.round(p95)} ms; samples=${samples.join(', ')}`).toBeLessThan(250);
    } finally {
      for (const j of jobs) await deleteJob(j.id, wp.path).catch(() => {});
    }
  });

  test('detail prefetch warms next-1 / next-2 in the lane before the user clicks', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const jobs = await plantHumanReviewJobs(wp, 4);
    try {
      // Capture which job-detail GETs the browser makes after detail open,
      // so we can prove the next peers are prefetched even without the
      // user navigating. Set the listener BEFORE the deep-link goto so we
      // catch every request triggered by the URL-restore path.
      const detailGets = new Set<string>();
      page.on('response', resp => {
        const url = resp.url();
        const match = url.match(/\/api\/v1\/projects\/[^/]+\/tasks\/([^/?]+)(\?|$)/);
        if (!match) return;
        if (resp.request().method() !== 'GET') return;
        // Filter out subresource endpoints like /api/tasks/{id}/output.
        if (url.includes('/output') || url.includes('/runs') || url.includes('/screenshots')) return;
        if (url.includes('/api/v1/studio/board')) return;
        detailGets.add(decodeURIComponent(match[1]));
      });

      await openJobInDetail(page, jobs[0].id, wp.path);

      // Wait for the pager position to anchor on the open job. Until
      // the live grouped lane includes the fresh fixture, the
      // ensure-snapshot effect has nothing to capture and the
      // prefetch effect cannot fire. On a freshly planted set the
      // first /api/tasks/grouped response after page boot may still be
      // pre-plant.
      const slimPagerPos = page.getByTestId('studio-task-pager-position');
      await expect(slimPagerPos).toBeVisible({ timeout: 30_000 });
      await expect.poll(
        async () => (await slimPagerPos.textContent())?.trim() ?? '',
        { timeout: 30_000, intervals: [200, 500, 1000, 2000, 2000] },
      ).toMatch(/^([1-9]\d*)\s*\/\s*\d+$/);
      // One settle tick for the prefetch effect's microtask to land
      // and the background GETs to issue + return.
      await page.waitForTimeout(1500);

      // The open job is always fetched. The prefetch should additionally
      // warm at least one of the next peers (the lookahead is 2 today;
      // we accept either to stay tolerant to lane-order drift).
      expect(detailGets.has(jobs[0].id), 'open job was fetched').toBe(true);
      const prefetched = [jobs[1].id, jobs[2].id, jobs[3].id].some(id => detailGets.has(id));
      expect(prefetched, `expected a prefetch for one of next-1/next-2; saw ${[...detailGets].join(', ')}`).toBe(true);
    } finally {
      for (const j of jobs) await deleteJob(j.id, wp.path).catch(() => {});
    }
  });
});
