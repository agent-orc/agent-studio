import { test, expect, type Page } from '@playwright/test';

/**
 * Explorer count-row spacing regression — both axes.
 *
 * Vertical (operator polish 2026-06-03): the stacked count badges down
 * the left Explorer rail (project totals + lane counts, e.g. 6 / 3 / 27 /
 * 14 …) sat too tight under one another. The fix bumps `.tree-row` height
 * so the counts read with breathing room. `app-tree-row` is used ONLY by
 * the Explorer workspace tree, so its row height is the single lever for
 * the spacing between those stacked counts.
 *
 * Horizontal (operator polish 2026-06-04): the same counts kleben zu nah
 * am rechten Rand. The fix routes the right gutter of every rail row
 * (tree-row, list-row, section-header) through one shared token,
 * `--studio-rail-trailing-gutter`, so each trailing count sits the same
 * distance from the rail edge. This spec locks that gutter on the
 * Explorer tree-row counts and the Workspaces section head's trailing
 * action, so a per-surface tweak that breaks the alignment is caught.
 *
 * Both axes are DOM-measurement locks: if somebody changes the height or
 * the gutter the assertion must be updated intentionally — that is the
 * point (same contract style as collapsed-lane-rail-rhythm).
 *
 * The count pill is the shared `<app-count-badge>` (`.count-badge`); the
 * legacy `.tree-row__count` class it replaced is gone, so the locators
 * below target `.count-badge`.
 */

const ROW_HEIGHT = 30; // matches `.tree-row { height }` in tree-row.component.scss
const TRAILING_GUTTER = 24; // matches `--studio-rail-trailing-gutter` (--studio-spacing-5)

function makeJob(project: string, id: string, state: string, order: number) {
  return {
    id,
    jobKey: `C:/fixtures/${project}::${id}`,
    title: `${project} ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-01T08:00:00Z',
    watchPath: `C:/fixtures/${project}`,
    projectName: project,
    folderPath: `C:/fixtures/${project}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-06-01T09:00:00Z',
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

// Build a grouped snapshot with several projects of varied (non-archive)
// totals so the Explorer shows a column of count badges of different
// widths — the surface the operator flagged.
const PROJECTS: Array<{ name: string; backlog: number; progress: number; review: number }> = [
  { name: 'Alpha Studio',     backlog: 3, progress: 2, review: 1 },  // total 6 (active → highlighted)
  { name: 'Bravo Tools',      backlog: 2, progress: 1, review: 0 },  // total 3
  { name: 'Charlie Pipeline', backlog: 14, progress: 8, review: 5 }, // total 27
  { name: 'Delta Service',    backlog: 7, progress: 4, review: 3 },  // total 14
  { name: 'Echo Sandbox',     backlog: 5, progress: 2, review: 2 },  // total 9
];

function buildGrouped() {
  const grouped: Record<string, ReturnType<typeof makeJob>[]> = {
    backlog: [], preparation: [], orchestratorPrep: [],
    ready: [], progress: [], failedPickup: [], review: [], autoReview: [],
    humanReview: [], completed: [], archive: [],
  };
  for (const p of PROJECTS) {
    let n = 0;
    for (let i = 0; i < p.backlog; i++) grouped.backlog.push(makeJob(p.name, `bk-${n++}`, '1-backlog', i));
    for (let i = 0; i < p.progress; i++) grouped.progress.push(makeJob(p.name, `pg-${n++}`, '3-progress', i));
    for (let i = 0; i < p.review; i++) grouped.humanReview.push(makeJob(p.name, `hr-${n++}`, '5-human-review', i));
  }
  return grouped;
}

function buildWorkspaces() {
  // One registry workspace embedding all projects so each project row gets a
  // real PROJ id. Without it the synthetic-group fallback hands every row a
  // null projectId, and `renamingProjectId() (null) === projectId (null)`
  // renders each row as an inline rename input instead of a tree-row.
  return [{
    id: 'WS-1',
    displayName: 'Fixtures',
    sortOrder: 0,
    isDefault: true,
    color: null,
    createdAt: '2026-06-01T08:00:00Z',
    projects: PROJECTS.map((p, i) => ({
      id: `PROJ-${i + 1}`,
      displayName: p.name,
      shortCode: p.name.replace(/[^A-Z]/g, '').slice(0, 3) || p.name.slice(0, 3).toUpperCase(),
      workspaceId: 'WS-1',
      color: null,
      cliDefault: null,
      modelDefault: null,
      sortOrder: i,
      storageLocation: `C:/fixtures/${p.name}`,
      archived: false,
      createdAt: '2026-06-01T08:00:00Z',
    })),
  }];
}

async function installRoutes(page: Page) {
  const grouped = buildGrouped();
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));
  await page.route('**/api/v1/workspaces**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(buildWorkspaces()) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(PROJECTS.map((p) => ({
        name: p.name, path: `C:/fixtures/${p.name}`, rootPath: `C:/fixtures/${p.name}`, repositoryPath: `C:/fixtures/${p.name}`,
      }))),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-01T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-01T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    // A project-scoped board so one project lights up as active (its total
    // badge gets the accent treatment). Pick the LAST project so it
    // auto-expands at the bottom, leaving the earlier project rows collapsed
    // and adjacent for a clean row-to-row spacing measurement.
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: 'Echo Sandbox' }],
      activeKey: 'board:Echo Sandbox',
    }));
    try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

test.describe('Explorer count-row vertical spacing', () => {
  test('project rows render at the bumped row height and counts are spaced by it', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const sidebar = page.getByTestId('studio-sidebar');
    await expect(sidebar).toBeVisible({ timeout: 10_000 });

    // The project-label tree-rows (one per project). Their testid is
    // `studio-explorer-project-<name>` (button), distinct from the wrapper
    // `studio-explorer-project-row-<name>` (div).
    const projectRows = page.locator('button[data-testid^="studio-explorer-project-"]');
    await expect.poll(() => projectRows.count()).toBeGreaterThanOrEqual(PROJECTS.length);

    // Every Explorer tree-row reports the bumped height.
    const heights = await sidebar.locator('button.tree-row').evaluateAll((els) =>
      els.map((e) => Math.round((e as HTMLElement).getBoundingClientRect().height)));
    expect(heights.length, 'sidebar should render tree-rows').toBeGreaterThan(0);
    const uniqueHeights = new Set(heights);
    expect(uniqueHeights, `all tree-rows must share the bumped height; got ${JSON.stringify(heights)}`)
      .toEqual(new Set([ROW_HEIGHT]));

    // Two consecutive project count badges sit ROW_HEIGHT apart (vertical
    // centre delta). Before the polish this was 24px; the bump makes the
    // stacked counts read with breathing room.
    const badges = projectRows.locator('.count-badge');
    await expect.poll(() => badges.count()).toBeGreaterThanOrEqual(2);
    const c0 = await badges.nth(0).boundingBox();
    const c1 = await badges.nth(1).boundingBox();
    expect(c0 && c1).toBeTruthy();
    const delta = Math.abs((c1!.y + c1!.height / 2) - (c0!.y + c0!.height / 2));
    expect(delta, `consecutive count badges must be spaced by the row height; got ${delta}`)
      .toBeGreaterThanOrEqual(ROW_HEIGHT - 1);

    // Expand a project so its lane child counts (backlog/active/review/archive)
    // also stack — they must share the same row rhythm.
    await page.getByTestId('studio-explorer-project-Charlie Pipeline').click();
    await page.waitForTimeout(150);

    // Visual evidence — dark theme.
    await setTheme(page, 'dark');
    await page.waitForTimeout(150);
    const darkShot = await sidebar.screenshot();
    await testInfo.attach('explorer-count-spacing-dark.png', { body: darkShot, contentType: 'image/png' });
    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'explorer-count-spacing-dark.png'), darkShot);

      // Before/after comparison: re-render the same tree with the OLD 24px
      // height via an injected override, so the screenshot pair shows the
      // spacing the operator asked us to grow.
      await page.addStyleTag({ content: '.tree-row{height:24px !important;}' });
      await page.waitForTimeout(120);
      const beforeShot = await sidebar.screenshot();
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'explorer-count-spacing-before-24px.png'), beforeShot);
    }
  });

  test('trailing counts sit one shared gutter from the rail right edge', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const sidebar = page.getByTestId('studio-sidebar');
    await expect(sidebar).toBeVisible({ timeout: 10_000 });

    const projectRows = page.locator('button[data-testid^="studio-explorer-project-"]');
    await expect.poll(() => projectRows.count()).toBeGreaterThanOrEqual(PROJECTS.length);

    // For each project row the count badge is the last flex child, so the
    // distance from the badge's right edge to the row's right edge is exactly
    // the row's `padding-right` — the shared rail gutter. Every count must
    // report the same gutter (uniform) and it must equal TRAILING_GUTTER.
    const rowGutters: number[] = [];
    const rowCount = await projectRows.count();
    for (let i = 0; i < rowCount; i++) {
      const row = projectRows.nth(i);
      const badge = row.locator('.count-badge').first();
      if ((await badge.count()) === 0) continue;
      const rowBox = await row.boundingBox();
      const badgeBox = await badge.boundingBox();
      if (!rowBox || !badgeBox) continue;
      rowGutters.push(Math.round((rowBox.x + rowBox.width) - (badgeBox.x + badgeBox.width)));
    }
    expect(rowGutters.length, 'at least one project count badge measured').toBeGreaterThan(0);
    for (const g of rowGutters) {
      expect(Math.abs(g - TRAILING_GUTTER),
        `tree-row count gutter must be the shared rail gutter; got ${g}, want ${TRAILING_GUTTER}. all=${JSON.stringify(rowGutters)}`)
        .toBeLessThanOrEqual(1);
    }

    // Cross-surface uniformity: the Workspaces section head's trailing
    // element (the "show all" action) must align to the same gutter, so the
    // section count column and the tree-row count column share one right edge.
    const head = page.getByTestId('studio-explorer-workspace-head');
    const showAll = page.getByTestId('studio-explorer-show-all-projects');
    const headBox = await head.boundingBox();
    const showAllBox = await showAll.boundingBox();
    expect(headBox && showAllBox).toBeTruthy();
    const headGutter = Math.round((headBox!.x + headBox!.width) - (showAllBox!.x + showAllBox!.width));
    expect(Math.abs(headGutter - TRAILING_GUTTER),
      `section-header trailing gutter must match the rail gutter; got ${headGutter}, want ${TRAILING_GUTTER}`)
      .toBeLessThanOrEqual(1);

    // Visual evidence — the count column with the widened gutter.
    await setTheme(page, 'dark');
    await page.waitForTimeout(120);
    const shot = await sidebar.screenshot();
    await testInfo.attach('explorer-count-right-gutter-dark.png', { body: shot, contentType: 'image/png' });
    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'explorer-count-right-gutter-dark.png'), shot);

      // Before/after: re-render with the OLD per-surface gutters injected so
      // the screenshot pair shows the counts moving off the edge.
      await page.addStyleTag({ content:
        '.tree-row{padding-right:16px !important;}' +
        '.list-row{padding-right:16px !important;}' +
        '.section-header{padding-right:12px !important;}' });
      await page.waitForTimeout(120);
      const beforeShot = await sidebar.screenshot();
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'explorer-count-right-gutter-before.png'), beforeShot);
    }
  });
});
