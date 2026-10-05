import { test, expect, Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * F60 — No redundant scrollbars in the super-column layout.
 *
 * In the studio (super-column) layout each lane group stacks lanes
 * vertically. Only .lane-group__lanes may scroll on the Y axis; the
 * per-lane .column__body must NOT produce its own scrollbar.
 *
 * Asserts:
 *   1. Per super-column, .lane-group__lanes is the sole Y-scroll surface.
 *   2. No .column__body inside a super-column has overflow-y: auto|scroll.
 *   3. Specific Auto-Review lane assertion: no overflow-y: auto on any
 *      element except .lane-group__lanes.
 *   4. Screenshots in both themes.
 */

const FIXTURE_WATCH = 'C:/fixtures/no-redundant-scrollbars';
const FIXTURE_PROJECT = 'no-redundant-scrollbars';
const CARDS_PER_LANE = 8;

function jobInfo(id: string, state: string, order: number): Record<string, unknown> {
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: `${state} job ${order}`,
    state,
    order,
    agent: 'claude',
    createdAt: '2026-05-25T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-25T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
  };
}

function makeLane(state: string, count: number): Record<string, unknown>[] {
  return Array.from({ length: count }, (_, i) =>
    jobInfo(`fx-${state}-${i + 1}`, state, i + 1),
  );
}

function fixtureGrouped(): Record<string, unknown[]> {
  return {
    backlog: [],
    preparation: makeLane('1-preparation', CARDS_PER_LANE),
    orchestratorPrep: [],
    ready: makeLane('2-ready', CARDS_PER_LANE),
    progress: makeLane('3-progress', CARDS_PER_LANE),
    failedPickup: [],
    autoReview: makeLane('4-auto-review', CARDS_PER_LANE),
    humanReview: makeLane('5-human-review', CARDS_PER_LANE),
    review: [],
    completed: makeLane('6-completed', CARDS_PER_LANE),
    archive: makeLane('7-archive', 3),
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
            activeJobId: null,
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
      body: JSON.stringify({ at: '2026-05-25T09:00:00Z', ttlSeconds: 600, snapshots: [] }),
    });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-25T09:00:00Z', sections: [] }),
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

async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  const studio = page.getByTestId('studio-board');
  const welcome = page.getByTestId('studio-welcome');
  await Promise.race([
    studio.first().waitFor({ state: 'visible', timeout: 8_000 }),
    welcome.first().waitFor({ state: 'visible', timeout: 8_000 }),
  ]).catch(() => { /* fall through */ });

  if ((await welcome.count()) > 0 && (await welcome.first().isVisible().catch(() => false))) {
    // Use the project picker in the header bar to open the board.
    const trigger = page.getByTestId('studio-project-picker-trigger');
    if ((await trigger.count()) > 0 && (await trigger.isVisible().catch(() => false))) {
      await trigger.click();
      const allItem = page.getByTestId('studio-project-picker-item-__all__');
      await allItem.click({ timeout: 3_000 }).catch(() => { /* nothing */ });
    } else {
      const allProjects = welcome.first().getByRole('button', { name: 'All projects' });
      await allProjects.click({ timeout: 3_000 }).catch(() => { /* nothing */ });
    }
    await studio.first().waitFor({ state: 'visible', timeout: 5_000 }).catch(() => { /* nothing */ });
  }

  await expect(studio, 'studio board should render').toBeVisible({ timeout: 10_000 });
}

function resolveOutDir(): string {
  const job = process.env.JOB_RESULTS_DIR;
  if (job && job.trim().length > 0) {
    fs.mkdirSync(job, { recursive: true });
    return job;
  }
  const fallback = path.join('test-results', 'f60-no-redundant-scrollbars');
  fs.mkdirSync(fallback, { recursive: true });
  return fallback;
}

interface ScrollProbe {
  containerId: string;
  laneGroupOverflowY: string;
  laneGroupScrollbarGutter: string;
  lanes: Array<{
    state: string;
    bodyOverflowY: string;
    innerScrollSurfaces: Array<{ selector: string; overflowY: string }>;
  }>;
}

test.describe('F60 — no redundant scrollbars in super-column layout', () => {
  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    await page.addInitScript(() => {
      try { window.localStorage.removeItem('collapsedLanes'); } catch { /* ignore */ }
      // Pre-seed the studio tab state so the board tab is active on load,
      // bypassing the Welcome screen.
      try {
        window.localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
          v: 1,
          tabs: [{ kind: 'board', projectName: '__all__' }],
          activeKey: 'board:__all__',
        }));
      } catch { /* ignore */ }
    });
  });

  test('each super-column has exactly one Y-scroll surface (lane-group__lanes)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);
    await expect(page.locator('.column__body').first()).toBeVisible({ timeout: 5_000 });

    const probes: ScrollProbe[] = await page.evaluate(() => {
      const groups = Array.from(document.querySelectorAll('[data-testid^="lane-group-"]')) as HTMLElement[];
      return groups.map((group) => {
        const containerId = group.getAttribute('data-testid') ?? '';
        const lanesEl = group.querySelector('.lane-group__lanes') as HTMLElement | null;
        const lgStyle = lanesEl ? window.getComputedStyle(lanesEl) : null;

        // Query expanded lanes by their .column container (inside
        // app-job-column hosts). Collapsed lanes show as .column-rail
        // and are not relevant here.
        const columns = Array.from(group.querySelectorAll('.column[data-state]')) as HTMLElement[];
        const lanes = columns.map((col) => {
          const state = col.getAttribute('data-state') ?? '';
          const body = col.querySelector('.column__body') as HTMLElement | null;
          const bodyOverflowY = body ? window.getComputedStyle(body).overflowY : '<no-body>';
          const innerScrollSurfaces: Array<{ selector: string; overflowY: string }> = [];
          for (const el of Array.from(col.querySelectorAll('*')) as HTMLElement[]) {
            const oy = window.getComputedStyle(el).overflowY;
            if (oy === 'auto' || oy === 'scroll') {
              const selector = el.className
                ? '.' + el.className.toString().split(/\s+/).filter(Boolean).join('.')
                : el.tagName.toLowerCase();
              innerScrollSurfaces.push({ selector, overflowY: oy });
            }
          }
          return { state, bodyOverflowY, innerScrollSurfaces };
        });

        return {
          containerId,
          laneGroupOverflowY: lgStyle?.overflowY ?? '<none>',
          laneGroupScrollbarGutter: lgStyle?.scrollbarGutter ?? '<none>',
          lanes,
        };
      });
    });

    expect(probes.length, 'expected 3 super-columns').toBeGreaterThanOrEqual(3);

    for (const probe of probes) {
      // 1. lane-group__lanes is the sole scroll surface.
      expect(
        probe.laneGroupOverflowY,
        `${probe.containerId}: .lane-group__lanes overflow-y="${probe.laneGroupOverflowY}" must be auto or scroll`,
      ).toMatch(/^(auto|scroll)$/);

      expect(
        probe.laneGroupScrollbarGutter,
        `${probe.containerId}: .lane-group__lanes scrollbar-gutter="${probe.laneGroupScrollbarGutter}" must be stable`,
      ).toMatch(/\bstable\b/);

      // 2. No per-lane .column__body has overflow-y: auto|scroll.
      for (const lane of probe.lanes) {
        expect(
          lane.bodyOverflowY,
          `${probe.containerId} lane ${lane.state}: .column__body overflow-y="${lane.bodyOverflowY}" — ` +
          `must be visible, not auto/scroll. Only .lane-group__lanes scrolls.`,
        ).toBe('visible');

        // 3. No inner element within a lane has overflow-y: auto|scroll.
        expect(
          lane.innerScrollSurfaces,
          `${probe.containerId} lane ${lane.state}: found inner scroll surfaces: ` +
          `${JSON.stringify(lane.innerScrollSurfaces)} — no element inside a lane should scroll.`,
        ).toHaveLength(0);
      }
    }
  });

  test('auto-review lane specifically has zero inner scroll containers', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const autoReviewLane = page.locator('[data-testid="lane-4-auto-review"]');
    await expect(autoReviewLane).toBeVisible({ timeout: 5_000 });

    const scrollSurfaces = await autoReviewLane.evaluate((lane) => {
      const results: Array<{ tag: string; cls: string; overflowY: string }> = [];
      for (const el of Array.from(lane.querySelectorAll('*')) as HTMLElement[]) {
        const oy = window.getComputedStyle(el).overflowY;
        if (oy === 'auto' || oy === 'scroll') {
          results.push({
            tag: el.tagName.toLowerCase(),
            cls: el.className.toString().slice(0, 80),
            overflowY: oy,
          });
        }
      }
      return results;
    });

    expect(
      scrollSurfaces,
      `Auto-Review lane has inner scroll containers: ${JSON.stringify(scrollSurfaces)} — ` +
      `expected none; scrolling should come from .lane-group__lanes only.`,
    ).toHaveLength(0);
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`screenshot — ${theme} theme`, async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 900 });
      await gotoBoard(page);
      await expect(page.locator('.column__body').first()).toBeVisible({ timeout: 5_000 });

      await page.evaluate((t) => {
        document.documentElement.setAttribute('data-studio-theme', t);
      }, theme);
      await page.waitForTimeout(120);

      const outDir = resolveOutDir();
      const file = path.join(outDir, `f60-board-no-redundant-scrollbars-${theme}.png`);
      await page.screenshot({ path: file, fullPage: false });
      expect(fs.existsSync(file), `screenshot landed at ${file}`).toBe(true);
    });
  }

  test('screenshot — auto-review lane closeup', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const autoReviewLane = page.locator('[data-testid="lane-4-auto-review"]');
    await expect(autoReviewLane).toBeVisible({ timeout: 5_000 });

    const outDir = resolveOutDir();
    const file = path.join(outDir, 'f60-auto-review-lane-closeup.png');
    await autoReviewLane.screenshot({ path: file });
    expect(fs.existsSync(file), `screenshot landed at ${file}`).toBe(true);
  });
});
