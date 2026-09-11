/**
 * Regression: dropping a dragged card onto another card in the same lane
 * must not make the dragged card disappear, AND must produce a sensible
 * reorder based on the cursor's position relative to the target card's
 * midpoint.
 *
 * History:
 *
 *   Symptom (initial bug): the drop-zone strips between cards are
 *   intentionally narrow (~14 px hit target). When the user released over
 *   an actual card, the drop event bubbled to the column-level
 *   `(drop)="onDrop"` handler, which emitted `jobDrop` with
 *   `targetState === sourceState`. That routed through
 *   `applyOptimisticMove`, which filtered the card out of its `fromLane`
 *   and then aliased `toLane` to the just-filtered array — the card
 *   vanished from its lane until a poll repainted.
 *
 *   First fix (vanish): made same-lane bubbled drops a no-op. The card
 *   stopped vanishing, but the user's "drag to top" gesture also stopped
 *   doing anything when the cursor missed the 14 px strip — and when it
 *   hit strip i=1 instead of strip i=0, the dragged card ended at order 2
 *   instead of order 1 (recurring "Sortieren ist buggy" report).
 *
 *   Second fix (this contract): same-lane bubbled drops now compute a
 *   slot from the cursor Y vs each card's midpoint and emit jobReorder
 *   for that slot. Card never vanishes (jobReorder operates on the
 *   already-rendered lane snapshot) AND drag-to-top lands at order 1
 *   whenever the cursor is above the first card's midpoint.
 *
 * The spec drives the bug with a synthetic drop on the first card with
 * the cursor positioned in that card's upper half, and asserts the
 * dragged card lands at the top of the lane. It also verifies the
 * drop-zone reorder path (drop on a strip between cards) still works in
 * each of the lanes the acceptance calls out.
 *
 * Lanes exercised: 4-auto-review (primary trigger), 2-ready,
 * 5-human-review, 0-backlog. The reorder spec does not exercise
 * 3-progress (active runner state) or the review/archive lanes.
 */
import { test, expect, Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, listJobs, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  // Route through the api() helper so the mutation carries the
  // x-client-id identity header the backend requires; raw fetch omitted it.
  await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  // includeFixtures so we also sweep up trios from a prior run that
  // were created with fixture:true (the helper's old default). Without
  // this, a re-run hits 409 on the same `id`.
  const all = await api<{ id: string; watchPath: string }[]>('/api/tasks?includeFixtures=true');
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith(prefix));
  await Promise.all(stale.map(j => deleteJob(j.id, j.watchPath).catch(() => {})));
}

async function readColumnTitles(page: Page, heading: string): Promise<string[]> {
  return page.evaluate((h) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const target = headings.find(el => el.textContent?.trim() === h);
    if (!target) return [];
    const column = target.closest('.column');
    if (!column) return [];
    const cards = Array.from(column.querySelectorAll('app-job-card .task-card__title-text')) as HTMLElement[];
    return cards.map(el => el.textContent?.trim() ?? '');
  }, heading);
}

/**
 * Dispatch a synthetic drag/drop where the drop event lands on the
 * *target card* itself (not a drop-zone strip). This reproduces the
 * "release cursor over a sibling card" gesture that bypasses the
 * narrow drop-zone hit target. The cursor Y is set to the requested
 * fraction of the target card's height so the column-level
 * `computeDropSlotFromCursor` resolves a deterministic slot.
 */
async function dispatchDropOnCard(
  page: Page,
  columnHeading: string,
  sourceCardTitle: string,
  targetCardTitle: string,
  /** 0 = top edge of target card, 1 = bottom edge. 0.25 lands in the upper half. */
  cursorFraction = 0.25
): Promise<void> {
  await page.evaluate(({ columnHeading, sourceCardTitle, targetCardTitle, cursorFraction }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const heading = headings.find(el => el.textContent?.trim() === columnHeading);
    if (!heading) throw new Error(`Column "${columnHeading}" not found`);
    const column = heading.closest('.column') as HTMLElement | null;
    if (!column) throw new Error(`Column root not found for "${columnHeading}"`);

    const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.task-card__title-text')?.textContent?.trim() ?? '');
    const sourceIdx = titles.indexOf(sourceCardTitle);
    const targetIdx = titles.indexOf(targetCardTitle);
    if (sourceIdx < 0) throw new Error(`Source card "${sourceCardTitle}" not found in "${columnHeading}"`);
    if (targetIdx < 0) throw new Error(`Target card "${targetCardTitle}" not found in "${columnHeading}"`);
    const sourceCard = cards[sourceIdx];
    const targetCard = cards[targetIdx];
    const rect = targetCard.getBoundingClientRect();
    const clientY = Math.round(rect.top + rect.height * cursorFraction);
    const clientX = Math.round(rect.left + rect.width / 2);

    const dataTransfer = new DataTransfer();
    sourceCard.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer, clientX, clientY }));
    targetCard.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer, clientX, clientY }));
    targetCard.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer, clientX, clientY }));
    sourceCard.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer, clientX, clientY }));
  }, { columnHeading, sourceCardTitle, targetCardTitle, cursorFraction });
}

async function dispatchDropOnZone(
  page: Page,
  columnHeading: string,
  sourceCardTitle: string,
  /** Drop-zone index — 0 means before first card, jobs.length means trailing zone. */
  targetDropZoneIndex: number
): Promise<void> {
  await page.evaluate(({ columnHeading, sourceCardTitle, targetDropZoneIndex }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const heading = headings.find(el => el.textContent?.trim() === columnHeading);
    if (!heading) throw new Error(`Column "${columnHeading}" not found`);
    const column = heading.closest('.column') as HTMLElement | null;
    if (!column) throw new Error(`Column root not found for "${columnHeading}"`);

    const cards = Array.from(column.querySelectorAll('app-job-card')) as HTMLElement[];
    const titles = cards.map(c => c.querySelector('.task-card__title-text')?.textContent?.trim() ?? '');
    const sourceIdx = titles.indexOf(sourceCardTitle);
    if (sourceIdx < 0) throw new Error(`Card "${sourceCardTitle}" not found in column`);
    const card = cards[sourceIdx];

    const dropZones = Array.from(column.querySelectorAll('.column__drop-zone')) as HTMLElement[];
    const dropZone = dropZones[targetDropZoneIndex];
    if (!dropZone) throw new Error(`Drop zone ${targetDropZoneIndex} not found (have ${dropZones.length})`);

    const dataTransfer = new DataTransfer();
    card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
    card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }));
  }, { columnHeading, sourceCardTitle, targetDropZoneIndex });
}

interface LaneCase {
  state: string;
  heading: string;
  /**
   * If non-null, jobs are created in 0-backlog (or whatever the create
   * default is) and then moved into `state`. `2-ready` is creatable
   * directly so its createTarget is null.
   */
  createTarget: string | null;
}

const LANES: LaneCase[] = [
  { state: '4-auto-review', heading: 'Post Processing', createTarget: '4-auto-review' },
  { state: '2-ready', heading: 'Ready', createTarget: null },
  { state: '5-human-review', heading: 'Human review', createTarget: '5-human-review' },
  { state: '0-backlog', heading: 'Backlog', createTarget: null },
];

async function seedThreeCardsIn(state: string, watchPath: string, prefix: string, createTarget: string | null) {
  const titleA = `${prefix}A-${Date.now()}`;
  const titleB = `${prefix}B-${Date.now() + 1}`;
  const titleC = `${prefix}C-${Date.now() + 2}`;
  const startState = createTarget ?? state;
  const createState = startState === '0-backlog' ? '0-backlog' : startState === '2-ready' ? '2-ready' : '0-backlog';
  // fixture: false so the seeded cards are visible on the kanban. The
  // backend's /api/tasks/grouped endpoint hides fixture jobs by default,
  // and the frontend never passes ?includeFixtures=true, so a fixture
  // job would be invisible to the UI assertions below. Cleanup in the
  // surrounding test deletes the trio whether the test passes or fails.
  const a = await createJob({ id: `${prefix}A`, title: titleA, watchPath, targetState: createState, fixture: false });
  const b = await createJob({ id: `${prefix}B`, title: titleB, watchPath, targetState: createState, fixture: false });
  const c = await createJob({ id: `${prefix}C`, title: titleC, watchPath, targetState: createState, fixture: false });
  if (createState !== state) {
    await moveJob(a.id, watchPath, state);
    await moveJob(b.id, watchPath, state);
    await moveJob(c.id, watchPath, state);
  }
  // Order the trio at the bottom of the lane so other (non-fixture) jobs
  // do not interfere with the assertions.
  const all = await listJobs();
  const others = all
    .filter(j => j.state === state && ![a.id, b.id, c.id].includes(j.id))
    .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
  await api('/api/tasks/reorder', {
    method: 'POST',
    body: JSON.stringify({
      jobs: [
        ...others,
        { jobId: a.id, watchPath },
        { jobId: b.id, watchPath },
        { jobId: c.id, watchPath }
      ]
    })
  });
  return { a: { id: a.id, title: titleA }, b: { id: b.id, title: titleB }, c: { id: c.id, title: titleC } };
}

test.describe('Within-lane drag-drop never drops the card from the lane', () => {
  for (const lane of LANES) {
    test(`drop on a sibling card in ${lane.state} keeps the card in the lane and reorders by cursor half`, async ({ page }) => {
      const wp = await getFirstWatchPath();
      const watchPath = wp.path;
      const PREFIX = `e2e-dropcard-${lane.state.replace(/[^a-z0-9]/gi, '')}-`;
      await cleanup(PREFIX, watchPath);

      const trio = await seedThreeCardsIn(lane.state, watchPath, PREFIX, lane.createTarget);

      try {
        await page.goto('/');
        await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.join('|');
        }, { timeout: 10_000 }).toBe([trio.a.title, trio.b.title, trio.c.title].join('|'));

        // Drop the third card directly onto the first card with the cursor
        // in the first card's UPPER half. Pre-vanish-fix this made the card
        // vanish. Pre-this-fix the gesture silently did nothing. Now the
        // column-level handler reads the cursor Y, sees it above the first
        // card's midpoint, and emits jobReorder for slot 0 — the dragged
        // card lands at the top of the lane.
        await dispatchDropOnCard(page, lane.heading, trio.c.title, trio.a.title, 0.25);

        // Optimistic paint flips immediately to [C, A, B]. Trio still in
        // lane (no vanish), and the dragged card is now first.
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.length === 3 ? titles.join('|') : `count=${titles.length}|${titles.join(',')}`;
        }, { timeout: 1500 }).toBe([trio.c.title, trio.a.title, trio.b.title].join('|'));

        // And after a polling tick (which would have repainted from the
        // server snapshot if the optimistic UI ever actually broke), the
        // backend has persisted the same order.
        await page.waitForTimeout(2500);
        const finalTitles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
        expect(finalTitles).toEqual([trio.c.title, trio.a.title, trio.b.title]);
      } finally {
        await deleteJob(trio.a.id, watchPath).catch(() => {});
        await deleteJob(trio.b.id, watchPath).catch(() => {});
        await deleteJob(trio.c.id, watchPath).catch(() => {});
      }
    });

    test(`drop on a drop-zone in ${lane.state} reorders and persists across reload`, async ({ page }) => {
      const wp = await getFirstWatchPath();
      const watchPath = wp.path;
      const PREFIX = `e2e-dropzone-${lane.state.replace(/[^a-z0-9]/gi, '')}-`;
      await cleanup(PREFIX, watchPath);

      const trio = await seedThreeCardsIn(lane.state, watchPath, PREFIX, lane.createTarget);

      try {
        await page.goto('/');
        await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.join('|');
        }, { timeout: 10_000 }).toBe([trio.a.title, trio.b.title, trio.c.title].join('|'));

        // Move A to the trailing drop-zone -> [B, C, A].
        const dropZoneCount = await page.evaluate((heading) => {
          const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
          const h = headings.find(el => el.textContent?.trim() === heading);
          const col = h?.closest('.column');
          return col ? col.querySelectorAll('.column__drop-zone').length : 0;
        }, lane.heading);
        expect(dropZoneCount).toBeGreaterThan(0);

        const reorderResp = page.waitForResponse(
          r => r.url().includes('/api/tasks/reorder') && r.request().method() === 'POST'
        );
        await dispatchDropOnZone(page, lane.heading, trio.a.title, dropZoneCount - 1);
        await reorderResp;

        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.join('|');
        }, { timeout: 5_000 }).toBe([trio.b.title, trio.c.title, trio.a.title].join('|'));

        // Reload and confirm the order survived.
        await page.reload();
        await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
        await expect.poll(async () => {
          const titles = (await readColumnTitles(page, lane.heading)).filter(t => t.startsWith(PREFIX));
          return titles.join('|');
        }, { timeout: 10_000 }).toBe([trio.b.title, trio.c.title, trio.a.title].join('|'));
      } finally {
        await deleteJob(trio.a.id, watchPath).catch(() => {});
        await deleteJob(trio.b.id, watchPath).catch(() => {});
        await deleteJob(trio.c.id, watchPath).catch(() => {});
      }
    });
  }
});
