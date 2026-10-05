import { test, expect, Page } from '@playwright/test';
import { api } from '../helpers/api';
import type { Job } from '../helpers/jobs';
import { createJob } from '../helpers/jobs';

/**
 * Regression coverage for "Wenn ich von einer Lane in die andere
 * verschiebe, dann merkt er sich nicht die Positionen, an die ich
 * getropft habe". Pre-fix the cross-lane drop emitted only a targetState
 * and the backend left the job's `order` untouched, so the card snapped
 * to whatever its stale source-lane order happened to sort to. The fix
 * routes the desired insertion slot through the `/move` endpoint and
 * rewrites the entire target lane to a dense 1..N sequence with the
 * dropped card pinned at the chosen slot.
 *
 * This spec drives the same DataTransfer dispatch the within-lane spec
 * uses, so it covers the same wire-up: drag a Ready card into a chosen
 * slot of Backlog, assert the card is at that slot in the rendered
 * column and that the backend persisted an order value that sorts the
 * card into the same position after a reload.
 */
interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  // Route through api() so the X-Client-Id header is set; the raw fetch
  // form silently 401s and the next createJob then 409s on the leftover
  // slug, masking the real test outcome.
  await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

async function cleanup(prefix: string, watchPath: string): Promise<void> {
  // includeFixtures so prior runs that seeded as fixtures (or any
  // mid-rename leftover) still get swept; otherwise a stale slug from a
  // previous failed run would surface as a 409 on the next createJob.
  const all = await api<Job[]>('/api/tasks?includeFixtures=true');
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
    const cards = Array.from(column.querySelectorAll('app-job-card .job-card__title')) as HTMLElement[];
    return cards.map(el => el.textContent?.trim() ?? '');
  }, heading);
}

async function dropZoneCount(page: Page, heading: string): Promise<number> {
  return page.evaluate((h) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const t = headings.find(el => el.textContent?.trim() === h);
    const col = t?.closest('.column');
    return col ? col.querySelectorAll('.column__drop-zone').length : 0;
  }, heading);
}

/**
 * Locate the target lane's drop zone that sits IMMEDIATELY BEFORE the
 * card with the given title, then dispatch a synthetic drop on it. This
 * variant exists because Backlog in a live workspace carries many cards
 * beyond the test seeds; an absolute drop-zone index does not mean
 * "between seeded B2 and B3". Anchoring on a specific card's title keeps
 * the test slot deterministic regardless of lane size.
 *
 * Pass `null` for `beforeCardTitle` to anchor on the trailing zone (after
 * the last card).
 */
async function dispatchCrossLaneDropBefore(
  page: Page,
  sourceColumnHeading: string,
  sourceCardTitle: string,
  targetColumnHeading: string,
  beforeCardTitle: string | null
): Promise<void> {
  await page.evaluate(({ sourceColumnHeading, sourceCardTitle, targetColumnHeading, beforeCardTitle }) => {
    const headings = Array.from(document.querySelectorAll('.column__title')) as HTMLElement[];
    const sourceH = headings.find(el => el.textContent?.trim() === sourceColumnHeading);
    if (!sourceH) throw new Error(`Source column "${sourceColumnHeading}" not found`);
    const sourceCol = sourceH.closest('.column') as HTMLElement | null;
    if (!sourceCol) throw new Error('Source column root missing');

    const sourceCards = Array.from(sourceCol.querySelectorAll('app-job-card')) as HTMLElement[];
    const sourceTitles = sourceCards.map(c => c.querySelector('.job-card__title')?.textContent?.trim() ?? '');
    const sourceIdx = sourceTitles.indexOf(sourceCardTitle);
    if (sourceIdx < 0) throw new Error(`Card "${sourceCardTitle}" not found in source column`);
    const card = sourceCards[sourceIdx];

    const targetH = headings.find(el => el.textContent?.trim() === targetColumnHeading);
    if (!targetH) throw new Error(`Target column "${targetColumnHeading}" not found`);
    const targetCol = targetH.closest('.column') as HTMLElement | null;
    if (!targetCol) throw new Error('Target column root missing');

    // The column body alternates: drop-zone, card, drop-zone, card, …,
    // trailing drop-zone. Walk the children of `.column__body` so the
    // strip's exact index is the column's view of "before card N".
    const body = targetCol.querySelector('.column__body');
    if (!body) throw new Error('Target column body missing');
    const children = Array.from(body.children) as HTMLElement[];

    let dropZone: HTMLElement | undefined;
    if (beforeCardTitle === null) {
      // Trailing drop zone is the last `.column__drop-zone--last`.
      const lastZone = targetCol.querySelector('.column__drop-zone--last');
      if (!(lastZone instanceof HTMLElement)) throw new Error('Trailing drop zone not found');
      dropZone = lastZone;
    } else {
      // Find the card with the given title, then walk back to the strip
      // sibling immediately before it.
      const targetCard = children.find(el =>
        el.tagName.toLowerCase() === 'app-job-card' &&
        el.querySelector('.job-card__title')?.textContent?.trim() === beforeCardTitle
      );
      if (!targetCard) throw new Error(`Card "${beforeCardTitle}" not found in target column`);
      const prev = targetCard.previousElementSibling;
      if (!(prev instanceof HTMLElement) || !prev.classList.contains('column__drop-zone')) {
        throw new Error(`Drop zone before "${beforeCardTitle}" not found`);
      }
      dropZone = prev;
    }

    const dataTransfer = new DataTransfer();
    card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }));
    dropZone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
    card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }));
  }, { sourceColumnHeading, sourceCardTitle, targetColumnHeading, beforeCardTitle });
}

test.describe('Cross-lane drop preserves drop position', () => {
  test('drag from Ready into the middle of Backlog: dropped card stays at the chosen slot', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-xlane-';
    await cleanup(PREFIX, watchPath);

    // Three Backlog seeds + one Ready card that will be dragged across.
    // Order: b1, b2, b3 in Backlog; src in Ready.
    const t = Date.now();
    const titleB1 = `${PREFIX}B1-${t}`;
    const titleB2 = `${PREFIX}B2-${t + 1}`;
    const titleB3 = `${PREFIX}B3-${t + 2}`;
    const titleSrc = `${PREFIX}Src-${t + 3}`;
    // fixture: false — the kanban hides fixture jobs and the page never
    // passes ?includeFixtures=true, so a fixture-defaulted seed would land
    // in job.json but never render in the column. The drop test would then
    // pass its preconditions on the API but the DOM event dispatch would
    // find no card to drag. Mirrors lane-reorder-drop-on-card.spec.ts.
    const b1 = await createJob({ id: `${PREFIX}b1`, title: titleB1, watchPath, targetState: '0-backlog', fixture: false });
    const b2 = await createJob({ id: `${PREFIX}b2`, title: titleB2, watchPath, targetState: '0-backlog', fixture: false });
    const b3 = await createJob({ id: `${PREFIX}b3`, title: titleB3, watchPath, targetState: '0-backlog', fixture: false });
    const src = await createJob({ id: `${PREFIX}src`, title: titleSrc, watchPath, targetState: '2-ready', fixture: false });

    // Pin Backlog to [b1, b2, b3] regardless of leftover jobs in the lane.
    // Seed `src` with an order value that, without the fix, would make it
    // land at the BOTTOM of Backlog after the move (large order).
    const all = await api<Job[]>('/api/tasks?includeFixtures=true');
    const backlogOthers = all
      .filter(j => j.state === '0-backlog' && ![b1.id, b2.id, b3.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    await api('/api/tasks/reorder', {
      method: 'POST',
      body: JSON.stringify({
        jobs: [
          ...backlogOthers,
          { jobId: b1.id, watchPath },
          { jobId: b2.id, watchPath },
          { jobId: b3.id, watchPath }
        ]
      })
    });

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

      // Confirm the trio is in Backlog before we drag.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2, titleB3].join('|'));

      // Source card is in Ready.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Ready')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleSrc].join('|'));

      const backlogZones = await dropZoneCount(page, 'Backlog');
      expect(backlogZones).toBeGreaterThan(2);

      // Drag src onto the strip immediately before B3 — slot semantics:
      // "insert before B3" in the FULL Backlog list. The seeds are pinned
      // at the bottom of Backlog, so this is also the strip between B2
      // and B3 within our filtered subset. The dropped card must land
      // exactly there, not at the bottom (the pre-fix symptom) and not
      // snapped to some other slot.
      const movePost = page.waitForResponse(
        r => r.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(src.id)}/move`) && r.request().method() === 'POST'
      );
      await dispatchCrossLaneDropBefore(page, 'Ready', titleSrc, 'Backlog', titleB3);
      const resp = await movePost;
      expect(resp.status()).toBe(200);

      // Optimistic + persisted Backlog order must be B1, B2, Src, B3.
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2, titleSrc, titleB3].join('|'));

      // Reload to prove the position survives a fresh /api/tasks/grouped.
      await page.reload();
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });
      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2, titleSrc, titleB3].join('|'));

      // Persistence assertion at the backend layer: the moved card must
      // sort between b2 and b3 by its rewritten `order` field.
      const after = await api<Job[]>("/api/tasks?includeFixtures=true");
      const ob1 = (after.find(j => j.id === b1.id) as any).order;
      const ob2 = (after.find(j => j.id === b2.id) as any).order;
      const osrc = (after.find(j => j.id === src.id) as any).order;
      const ob3 = (after.find(j => j.id === b3.id) as any).order;
      expect(ob1).toBeLessThan(ob2);
      expect(ob2).toBeLessThan(osrc);
      expect(osrc).toBeLessThan(ob3);
    } finally {
      await deleteJob(b1.id, watchPath).catch(() => {});
      await deleteJob(b2.id, watchPath).catch(() => {});
      await deleteJob(b3.id, watchPath).catch(() => {});
      await deleteJob(src.id, watchPath).catch(() => {});
    }
  });

  test('drag from Ready onto the top drop-zone of Backlog: dropped card lands at order 1', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    const PREFIX = 'e2e-xlane-top-';
    await cleanup(PREFIX, watchPath);

    const t = Date.now();
    const titleB1 = `${PREFIX}B1-${t}`;
    const titleB2 = `${PREFIX}B2-${t + 1}`;
    const titleSrc = `${PREFIX}Src-${t + 2}`;
    const b1 = await createJob({ id: `${PREFIX}b1`, title: titleB1, watchPath, targetState: '0-backlog', fixture: false });
    const b2 = await createJob({ id: `${PREFIX}b2`, title: titleB2, watchPath, targetState: '0-backlog', fixture: false });
    const src = await createJob({ id: `${PREFIX}src`, title: titleSrc, watchPath, targetState: '2-ready', fixture: false });

    const all = await api<Job[]>('/api/tasks?includeFixtures=true');
    const backlogOthers = all
      .filter(j => j.state === '0-backlog' && ![b1.id, b2.id].includes(j.id))
      .map(j => ({ jobId: j.id, watchPath: j.watchPath }));
    await api('/api/tasks/reorder', {
      method: 'POST',
      body: JSON.stringify({
        jobs: [
          ...backlogOthers,
          { jobId: b1.id, watchPath },
          { jobId: b2.id, watchPath }
        ]
      })
    });

    try {
      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 10_000 });

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleB1, titleB2].join('|'));

      // Drop on the strip immediately before B1. With our seeds pinned at
      // the bottom of Backlog this drops src JUST above the seeded trio.
      // The persistence assertion below proves the rewritten order makes
      // src sort before B1 and B2 — independent of other Backlog cards.
      const movePost = page.waitForResponse(
        r => r.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(src.id)}/move`) && r.request().method() === 'POST'
      );
      await dispatchCrossLaneDropBefore(page, 'Ready', titleSrc, 'Backlog', titleB1);
      await movePost;

      await expect.poll(async () => {
        const titles = (await readColumnTitles(page, 'Backlog')).filter(t => t.startsWith(PREFIX));
        return titles.join('|');
      }, { timeout: 10_000 }).toBe([titleSrc, titleB1, titleB2].join('|'));

      const after = await api<Job[]>("/api/tasks?includeFixtures=true");
      const osrc = (after.find(j => j.id === src.id) as any).order;
      const ob1 = (after.find(j => j.id === b1.id) as any).order;
      const ob2 = (after.find(j => j.id === b2.id) as any).order;
      expect(osrc).toBeLessThan(ob1);
      expect(osrc).toBeLessThan(ob2);
    } finally {
      await deleteJob(b1.id, watchPath).catch(() => {});
      await deleteJob(b2.id, watchPath).catch(() => {});
      await deleteJob(src.id, watchPath).catch(() => {});
    }
  });
});
