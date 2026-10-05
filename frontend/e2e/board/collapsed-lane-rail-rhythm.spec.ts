import { test, expect, type Page } from '@playwright/test';

/**
 * Collapsed lane-rail vertical-rhythm regression.
 *
 * Operator bug 2026-05-28: rail counters / glyphs / indicators sat at
 * uneven vertical intervals because each child of `.column-rail` had a
 * different intrinsic height and the rail's flex `gap` stacked on top
 * of that. Fix: every direct child of `.column-rail` is a "slot" whose
 * vertical extent comes from `--studio-rail-item-min-height` +
 * `--studio-rail-item-padding-block` (see `_tokens-semantic.scss`).
 *
 * This spec locks the rhythm contract at the DOM-measurement level so
 * future SCSS edits cannot silently regress to per-child intrinsic
 * sizing. A pixel-snapshot would also catch this but introduces
 * cross-OS/font-rendering flakes; uniform getBoundingClientRect().height
 * across every slot is the load-bearing invariant we care about.
 */

const PROJECT = 'fixture-rail-rhythm';
const WATCH_PATH = 'C:/fixtures/rail-rhythm';

function makeJob(id: string, state: string, order: number) {
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `Job ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-29T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-29T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
  };
}

// Use a busy progress lane so the rail has running indicators stacked on
// top of the icon + count + title + expand caret. The rhythm has to read
// as uniform even when extra dot slots are present.
const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [makeJob('ready-1', '2-ready', 1)],
  progress: [
    {
      ...makeJob('prog-1', '3-progress', 1),
      execution: { status: 'running', jobId: 'prog-1', model: 'claude-opus-4-7', startedAt: '2026-05-29T09:00:00Z' },
    },
    makeJob('prog-2', '3-progress', 2),
    makeJob('prog-3', '3-progress', 3),
  ],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: [makeJob('hr-1', '5-human-review', 1)],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-29T08:00:00Z', sessions: [] }),
    }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-29T08:00:00Z', ttlSeconds: 600, snapshots: [] }),
    }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    localStorage.removeItem('collapsedLanes');
  });
}

async function waitForBoard(page: Page): Promise<void> {
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 10_000 });
  await expect(page.locator('[data-testid="job-card"]').first()).toBeVisible({ timeout: 10_000 });
}

test.describe('Collapsed lane-rail vertical rhythm', () => {
  test('every rail slot has the same min-height + padding-block (dark)', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);
    await setTheme(page, 'dark');
    await page.waitForTimeout(200);

    const collapseBtn = page.getByTestId('lane-collapse-3-progress');
    await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
    // dispatchEvent('click') fires the synthetic click directly on the
    // element so a stray notification stack or other overlay cannot
    // intercept it. This spec doesn't care about pointer-hit-test
    // semantics — only the resulting collapsed layout.
    await collapseBtn.dispatchEvent('click');

    const rail = page.getByTestId('lane-rail-3-progress');
    await expect(rail).toBeVisible({ timeout: 1_000 });
    await page.waitForTimeout(150);

    // Confirm the running indicator rendered so the indicators slot is
    // non-empty; this is the variant where the rhythm bug surfaced.
    await expect(rail.locator('.column-rail__indicators .column-rail__dot--running')).toHaveCount(1);

    // Sample computed style of every direct slot.
    const slotStyles = await rail.evaluate((el) => {
      const out: Array<{ cls: string; height: number; minHeight: string; paddingBlock: string }> = [];
      const children = Array.from(el.children) as HTMLElement[];
      for (const c of children) {
        const cs = getComputedStyle(c);
        out.push({
          cls: c.className,
          height: c.getBoundingClientRect().height,
          minHeight: cs.minHeight,
          paddingBlock: `${cs.paddingTop}/${cs.paddingBottom}`,
        });
      }
      return out;
    });

    expect(slotStyles.length, 'rail should have at least 5 direct slot children').toBeGreaterThanOrEqual(5);

    // Every slot reports the same min-height (the token-resolved px).
    // The title is the only flex-grow slot, so its bounding-rect height
    // can exceed the floor; min-height is what matters for rhythm.
    const minHeights = new Set(slotStyles.map((s) => s.minHeight));
    expect(
      minHeights.size,
      `rail slots must share the same min-height; got: ${JSON.stringify(slotStyles)}`,
    ).toBe(1);

    // The min-height must equal the rail-item token value (28px). If
    // somebody changes the token the assertion needs to be updated
    // intentionally — that's the point.
    const onlyMinHeight = [...minHeights][0];
    expect(onlyMinHeight, 'rail slots must use --studio-rail-item-min-height (28px)')
      .toBe('28px');

    // padding-top + padding-bottom must also be uniform across slots
    // (4px each from --studio-rail-item-padding-block).
    const paddingBlocks = new Set(slotStyles.map((s) => s.paddingBlock));
    expect(
      paddingBlocks.size,
      `rail slots must share padding-block; got: ${JSON.stringify(slotStyles)}`,
    ).toBe(1);

    // Non-title slots should have bounding-rect height equal to floor + padding
    // (28 + 4 + 4 = 36). Title is `flex: 1 1 auto` and may exceed.
    const nonTitleHeights = slotStyles
      .filter((s) => !s.cls.includes('column-rail__title'))
      .map((s) => Math.round(s.height));
    const uniqueNonTitleHeights = new Set(nonTitleHeights);
    expect(
      uniqueNonTitleHeights.size,
      `non-title slots must render at uniform height; got: ${JSON.stringify(nonTitleHeights)}`,
    ).toBe(1);

    // Visual evidence — saved to results/ when invoked from the
    // job-folder fixture, plus attached to the Playwright report.
    const screenshot = await rail.screenshot();
    await testInfo.attach('lane-rail-rhythm-dark.png', {
      body: screenshot,
      contentType: 'image/png',
    });
    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'lane-rail-rhythm-dark.png'), screenshot);
    }
  });

  test('light theme keeps the same rhythm contract', async ({ page }) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);
    await setTheme(page, 'light');
    await page.waitForTimeout(200);

    const collapseBtn = page.getByTestId('lane-collapse-3-progress');
    await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
    // dispatchEvent('click') fires the synthetic click directly on the
    // element so a stray notification stack or other overlay cannot
    // intercept it. This spec doesn't care about pointer-hit-test
    // semantics — only the resulting collapsed layout.
    await collapseBtn.dispatchEvent('click');

    const rail = page.getByTestId('lane-rail-3-progress');
    await expect(rail).toBeVisible({ timeout: 1_000 });
    await page.waitForTimeout(150);

    // The tokens resolve identically in both themes (rhythm tokens are
    // theme-agnostic px values), so the same assertions apply.
    const slotMinHeights = await rail.evaluate((el) => {
      const set = new Set<string>();
      for (const c of Array.from(el.children)) {
        set.add(getComputedStyle(c as HTMLElement).minHeight);
      }
      return [...set];
    });

    expect(slotMinHeights, 'light-theme rail slots must share the same min-height as dark')
      .toEqual(['28px']);
  });
});
