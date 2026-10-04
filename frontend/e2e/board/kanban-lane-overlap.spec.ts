import { test, expect, Page } from '@playwright/test';
import { startLongTaskRecorder } from '../helpers/timing';

/**
 * Robustness regression for the post-ADR-0025/0026/0028/0029 lane
 * catalog (Backlog / In Preparation / Orch Prep / Human Ready / In
 * Progress / Failed Pickup / Auto Review / Human Review / Completed /
 * Archive). The 1b-needs-human-review bounce lane was retired (its
 * "Human decision needed" concept was obsoleted), so it is no longer
 * part of the catalog.
 *
 * The user reported two layout-perf symptoms:
 *
 *   1. Lanes visually overlap or leak into neighbouring lanes at narrow
 *      viewport widths.
 *   2. Toggling a lane open/closed feels sluggish; the board doesn't
 *      reflow until a hover or scroll.
 *
 * The acceptance criteria pinned in the bug task:
 *
 *   - Long-task cumulative < 50 ms during a 5 s scroll burst at every
 *     measured width.
 *   - No two lanes share a horizontal pixel range (their
 *     `getBoundingClientRect()` left/right ranges are pairwise disjoint).
 *   - Collapsing a lane and re-expanding it within 200 ms produces no
 *     overlap, sampled at 50 ms intervals during the transition.
 *
 * Mocks: API-stubbed so the test is independent of whatever live state
 * the running backend is in. The fixture seeds N=30 jobs into
 * 4-auto-review (the densest lane in practice) and >=1 job into every
 * hide-when-empty lane so the full 9-lane catalog is materialised.
 *
 * Selectors: lanes are addressed by their `data-testid` regardless of
 * whether they are expanded (`lane-<state>`) or collapsed into a rail
 * (`lane-rail-<state>`). The disjoint-range assertion runs across the
 * union so a transient mid-collapse double-render is caught too.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-overlap-demo';
const FIXTURE_PROJECT = 'lane-overlap-demo';

const LANE_STATES = [
  '0-backlog',
  '1-preparation',
  '1a-orchestrator-prep',
  '2-ready',
  '3-progress',
  '4-auto-review',
  '5-human-review',
  '6-completed',
  '7-archive',
] as const;

interface JobOverrides {
  id?: string;
  title?: string;
  state?: string;
  order?: number;
  cliType?: string | null;
  execution?: unknown;
  pendingIntent?: unknown;
}

function jobInfo(over: JobOverrides): Record<string, unknown> {
  const id = over.id ?? 'fx-job';
  const state = over.state ?? '2-ready';
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: over.title ?? id,
    state,
    order: over.order ?? 1,
    agent: 'claude',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: over.cliType ?? 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: over.execution ?? null,
    commit: null,
    pendingIntent: over.pendingIntent ?? null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const autoReview = Array.from({ length: 30 }, (_, i) =>
    jobInfo({
      id: `fx-auto-${i + 1}`,
      title: `Auto-review job ${i + 1} with a fairly long descriptive title`,
      state: '4-auto-review',
      order: i + 1,
    }),
  );
  return {
    backlog: [jobInfo({ id: 'fx-backlog-1', title: 'Backlog item', state: '0-backlog' })],
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'In preparation', state: '1-preparation' })],
    orchestratorPrep: [jobInfo({ id: 'fx-orch-prep-1', title: 'Orch prep candidate', state: '1a-orchestrator-prep' })],
    ready: [
      jobInfo({ id: 'fx-ready-1', title: 'Ready human', state: '2-ready', order: 1 }),
      jobInfo({ id: 'fx-ready-intake-1', title: 'Ready intake (orch)', state: '2-ready', order: 2 }),
    ],
    progress: [
      jobInfo({
        id: 'fx-progress-1',
        title: 'Live run',
        state: '3-progress',
        cliType: 'claude',
        execution: {
          jobId: 'fx-progress-1',
          jobKey: `${FIXTURE_WATCH}::fx-progress-1`,
          processId: 9001,
          startedAt: '2026-05-05T08:55:00Z',
          status: 'running',
          exitCode: null,
          durationSeconds: null,
          model: 'claude-opus-4-7',
        },
      }),
    ],
    failedPickup: [],
    autoReview,
    humanReview: [jobInfo({ id: 'fx-human-1', title: 'Human review', state: '5-human-review' })],
    review: autoReview,
    completed: [jobInfo({ id: 'fx-done-1', title: 'Completed', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Archive', state: '7-archive' })],
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = Object.values(grouped).flat();

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });

  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]),
    });
  });
  await page.route('**/api/tasks', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/v1/studio/board', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/v1/studio/runner/status', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [FIXTURE_PROJECT]: {
            projectName: FIXTURE_PROJECT,
            mode: 'manual',
            activeJobId: 'fx-progress-1',
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    });
  });
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    });
  });
  await page.route('**/api/git/summary', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/git/projects', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', ttlSeconds: 600, snapshots: [] }),
    });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', sections: [] }),
    });
  });
  await page.route('**/api/orchestrator/global', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
  });
  await page.route('**/api/projects/*/settings', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }),
    });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }),
    });
  });
}

interface LaneRect {
  state: string;
  testid: string;
  left: number;
  right: number;
  width: number;
}

async function readLaneRects(page: Page): Promise<LaneRect[]> {
  return await page.evaluate((laneStates: readonly string[]) => {
    const rects: Array<{ state: string; testid: string; left: number; right: number; width: number }> = [];
    for (const state of laneStates) {
      // Either the expanded column or the collapsed rail will be in the DOM.
      const candidates = [
        document.querySelector(`[data-testid="lane-${state}"]`),
        document.querySelector(`[data-testid="lane-rail-${state}"]`),
      ];
      for (const el of candidates) {
        if (!el) continue;
        const r = (el as HTMLElement).getBoundingClientRect();
        if (r.width === 0 && r.height === 0) continue;
        rects.push({
          state,
          testid: el.getAttribute('data-testid') ?? state,
          left: r.left,
          right: r.right,
          width: r.width,
        });
      }
    }
    return rects;
  }, LANE_STATES as unknown as readonly string[]);
}

function assertDisjointHorizontally(rects: LaneRect[], at: string): void {
  const sorted = [...rects].sort((a, b) => a.left - b.left);
  for (let i = 0; i < sorted.length - 1; i++) {
    const a = sorted[i];
    const b = sorted[i + 1];
    // Allow 0.5 px sub-pixel rounding tolerance. Real overlap (content
    // leaking) shows up as multi-pixel intersection.
    expect(
      a.right,
      `${at}: lane "${a.testid}" (right=${a.right.toFixed(1)}) overlaps lane "${b.testid}" (left=${b.left.toFixed(1)})`,
    ).toBeLessThanOrEqual(b.left + 0.5);
  }
}

const WIDTHS = [
  { width: 1280, height: 900, label: '1280' },
  { width: 1440, height: 900, label: '1440' },
  { width: 1920, height: 1080, label: '1920' },
] as const;

test.describe('Kanban lane robustness across widths and during collapse', () => {
  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    // Make sure no prior-run state lingers in this browser context.
    await page.addInitScript(() => {
      try {
        window.localStorage.removeItem('collapsedLanes');
      } catch {
        /* ignore */
      }
    });
  });

  for (const { width, height, label } of WIDTHS) {
    test(`lanes do not overlap horizontally at ${label} px`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      await page.goto('/');
      await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('lane-4-auto-review')).toBeVisible();

      // Wait for the auto-review lane's 30 cards to settle.
      await expect.poll(async () =>
        await page.locator('[data-testid="lane-4-auto-review"] [data-testid="job-card"]').count(),
        { timeout: 5_000 }
      ).toBeGreaterThanOrEqual(30);

      const rects = await readLaneRects(page);
      // Every catalog lane is expected to render either as a column or
      // as a rail. The fixture stocks every hide-when-empty lane.
      const seenStates = new Set(rects.map(r => r.state));
      for (const state of LANE_STATES) {
        expect(seenStates.has(state), `lane ${state} should render at ${label}`).toBe(true);
      }

      assertDisjointHorizontally(rects, `width=${label}`);

      await page.screenshot({
        path: `test-results/kanban-lane-overlap-${label}.png`,
        fullPage: false,
      });
    });

    test(`long-task budget stays under 50 ms during a 5 s scroll burst at ${label} px`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      await page.goto('/');
      await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('lane-4-auto-review')).toBeVisible();

      await expect.poll(async () =>
        await page.locator('[data-testid="lane-4-auto-review"] [data-testid="job-card"]').count(),
        { timeout: 5_000 }
      ).toBeGreaterThanOrEqual(30);

      const recorder = await startLongTaskRecorder(page);
      // Settle for one frame so any buffered long tasks from the
      // initial mount are delivered to the observer (`buffered: true`
      // in `startLongTaskRecorder` replays prior entries the moment
      // observe() runs). The acceptance criterion is the *scroll-
      // window* budget, not the page-mount budget.
      await page.waitForTimeout(100);
      const baselineMs = await recorder.totalMs();
      const baselineCount = await recorder.count();

      // Scroll burst: the auto-review lane is the densest and the most
      // likely target of vertical scrolling. We drive the lane's body
      // (a flex column) up and down for 5 s in 100 ms increments. This
      // is meant to provoke layout/paint plus any Angular change
      // detection that runs without OnPush.
      const start = Date.now();
      let direction = 1;
      while (Date.now() - start < 5_000) {
        await page.evaluate((dy) => {
          const lane = document.querySelector('[data-testid="lane-4-auto-review"] .column__body') as HTMLElement | null;
          if (lane) lane.scrollBy({ top: dy, behavior: 'instant' as ScrollBehavior });
          else window.scrollBy({ top: dy, behavior: 'instant' as ScrollBehavior });
        }, direction * 200);
        await page.waitForTimeout(100);
        if (Math.random() < 0.2) direction *= -1;
      }

      const totalMsAfter = await recorder.totalMs();
      const countAfter = await recorder.count();
      await recorder.stop();

      const total = totalMsAfter - baselineMs;
      const count = countAfter - baselineCount;

      expect(
        total,
        `${label}px: scrolling the auto-review lane for 5 s blocked the main thread for ` +
        `${total.toFixed(0)} ms across ${count} long tasks. Browser definition: any task > 50 ms ` +
        `of main-thread blocking is a long task. The acceptance criterion is < 50 ms cumulative ` +
        `(this is the delta over the scroll window; mount-time long tasks recorded as a ` +
        `${baselineMs.toFixed(0)} ms / ${baselineCount}-task baseline are excluded).`,
      ).toBeLessThan(50);
    });
  }

  test('collapse and re-expand of a lane completes in <= 200 ms with no transient overlap', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('lane-4-auto-review')).toBeVisible();

    // Pick the Ready lane: it is in the middle of the Backlog group and
    // has neighbours on both sides, so a transient overlap during the
    // collapse animation would be catchable from either direction.
    const collapseBtn = page.getByTestId('lane-collapse-2-ready');
    await expect(collapseBtn).toBeVisible();

    // Collapse: click triggers the @if switch that swaps the .column
    // box for a .column-rail. We sample the lane bounding boxes at
    // 50 ms intervals across a 200 ms window, then assert no two lanes
    // ever overlapped horizontally during that window. The rail must
    // also become visible inside the same 200 ms - the user's "the
    // lane is now collapsed" signal.
    const collapseSamples: LaneRect[][] = [];
    await collapseBtn.click();
    for (let t = 0; t < 200; t += 50) {
      collapseSamples.push(await readLaneRects(page));
      await page.waitForTimeout(50);
    }

    const rail = page.getByTestId('lane-rail-2-ready');
    await expect(rail, 'Ready lane should be a rail within 200 ms of collapse').toBeVisible({ timeout: 200 });

    for (let i = 0; i < collapseSamples.length; i++) {
      assertDisjointHorizontally(collapseSamples[i], `collapse t=${i * 50}ms`);
    }

    // Re-expand and assert the same.
    const expandSamples: LaneRect[][] = [];
    await rail.click();
    for (let t = 0; t < 200; t += 50) {
      expandSamples.push(await readLaneRects(page));
      await page.waitForTimeout(50);
    }

    await expect(
      page.getByTestId('lane-collapse-2-ready'),
      'Ready lane should be a column within 200 ms of re-expand',
    ).toBeVisible({ timeout: 200 });

    for (let i = 0; i < expandSamples.length; i++) {
      assertDisjointHorizontally(expandSamples[i], `expand t=${i * 50}ms`);
    }
  });
});
