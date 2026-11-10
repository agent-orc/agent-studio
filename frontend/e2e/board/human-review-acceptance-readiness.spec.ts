import * as path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3564: on 9 November 2026 the Human Review lane held 41 cards and the
// operator had to try each completion to learn which 15 were ready. The lane
// now shows each card's acceptance readiness from the completion's own dry
// run, groups and filters by it, accepts the ready cards through the normal
// batch move, and offers a confirmed round for a refused card.

const PROJECT = 'Readiness fixture';
const WATCH_PATH = '/fixtures/readiness';
const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim() || path.join(process.cwd(), 'test-results');
const RESULT_REF = 'refs/heads/runner/agent-runner-01/AGT-4104';

type Readiness = 'ready' | 'review-concerns' | 'integrated-unverified' | 'not-integrated' | 'verdict-stale'
  | 'no-content-verdict' | 'blocked' | 'waiting-deploy' | 'owner-decision';

interface Fixture {
  id: string;
  key: string;
  title: string;
  readiness: Readiness;
  headline: string;
  reason: string;
  refusal: string;
  concern?: string;
  missing?: string;
}

const FIXTURES: Fixture[] = [
  { id: 'lane-filter-count', key: 'AGT-4101', title: 'Lane filter keeps its count', readiness: 'ready',
    headline: 'Ready to accept', reason: 'Every completion check passes: Content review passed for the current brief.',
    refusal: 'Content review passed for the current brief.' },
  { id: 'search-cursor', key: 'AGT-4102', title: 'Board search keeps the cursor', readiness: 'ready',
    headline: 'Ready to accept', reason: 'Every completion check passes: Content review passed for the current brief.',
    refusal: 'Content review passed for the current brief.' },
  { id: 'cold-badge', key: 'AGT-4103', title: 'Archive row shows the cold badge', readiness: 'ready',
    headline: 'Ready to accept', reason: 'Every completion check passes: Content review passed for the current brief.',
    refusal: 'Content review passed for the current brief.' },
  { id: 'parked-counts', key: 'AGT-4104', title: 'Explorer counts parked cards', readiness: 'review-concerns',
    headline: 'Review concerns',
    reason: 'The requirement-fit review returned concerns: The explorer total still skips parked cards in the Delivery group.',
    refusal: 'Completion refused: requirement-fit verdict was concerns.',
    concern: 'The explorer total still skips parked cards in the Delivery group.',
    missing: 'frontend/src/app/features/studio-shell/project-rows.ts' },
  { id: 'gate-unverified', key: 'AGT-4105', title: 'Release notes link the gate run', readiness: 'integrated-unverified',
    headline: 'Integrated, not verified',
    reason: 'The delivery is on the integration branch, but no gate passed on the merged tree bbbbbbbbbbbb.',
    refusal: 'Acceptance refused: the delivery is on the integration branch but integrated-unverified.' },
  { id: 'stale-verdict', key: 'AGT-4106', title: 'Settings rail keeps the scroll', readiness: 'verdict-stale',
    headline: 'Verdict stale',
    reason: 'The requirement-fit verdict was given for brief 3f2a91c0d4e1; the brief is now 9c41e0a7b2d3.',
    refusal: 'Completion refused: content verdict was for brief 3f2a91c0d4e1, current 9c41e0a7b2d3.' },
  { id: 'research-no-verdict', key: 'AGT-4107', title: 'Research: queue fairness options', readiness: 'no-content-verdict',
    headline: 'No content verdict',
    reason: 'This research card has no requirement-fit verdict: The current run has no review.',
    refusal: 'Completion refused: no content verdict (requirement-fit); the current run has no review.' },
  { id: 'live-measurement', key: 'AGT-4108', title: 'Measure the cold board load', readiness: 'waiting-deploy',
    headline: 'Waiting for a deploy',
    reason: 'Parked until the next deploy: The live measurement needs the next deploy.',
    refusal: 'Content review passed for the current brief.' },
  { id: 'owner-choice', key: 'AGT-4109', title: 'Choose the primary column', readiness: 'owner-decision',
    headline: 'Owner decision', reason: 'Parked for an owner decision: Which column is primary on narrow screens?',
    refusal: 'Content review passed for the current brief.' },
];

function card(fixture: Pick<Fixture, 'id' | 'key' | 'title'>) {
  return {
    id: fixture.id,
    taskKey: `${WATCH_PATH}::${fixture.id}`,
    key: fixture.key,
    displayKey: fixture.key,
    title: fixture.title,
    state: '5-human-review',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-11-08T18:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/5-human-review/${fixture.id}`,
    lastActivity: '2026-11-09T06:00:00Z',
    sessionName: null,
    model: 'gpt-5.6-codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

function item(fixture: Fixture) {
  const offersRound = !['ready', 'waiting-deploy', 'owner-decision'].includes(fixture.readiness);
  return {
    jobId: fixture.id,
    taskKey: `${WATCH_PATH}::${fixture.id}`,
    key: fixture.key,
    projectName: PROJECT,
    title: fixture.title,
    readiness: fixture.readiness,
    headline: fixture.headline,
    reason: fixture.reason,
    stage: fixture.readiness === 'ready' ? 'admitted' : 'content',
    refusal: fixture.refusal,
    aspect: 'requirement-fit',
    contentStatus: fixture.readiness === 'review-concerns' ? 'concerns' : 'pass',
    concern: fixture.concern ?? null,
    missing: fixture.missing ?? null,
    latestResultRef: offersRound ? RESULT_REF : null,
    round: offersRound
      ? {
        prompt: `Close the acceptance gap on this card. Human Review acceptance was refused:\n\n${fixture.refusal}`
          + (fixture.concern ? `\n\nrequirement-fit concern: ${fixture.concern}` : '')
          + `\n\nStart from the latest result ${RESULT_REF} and change only what closes this gap, then deliver again.`,
        baseRef: RESULT_REF,
      }
      : null,
  };
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function grouped(humanReview: unknown[]) {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview, escalated: [], completed: [], archive: [],
  };
}

interface Calls {
  readiness: string[][];
  batch: { jobId: string; targetState: string }[][];
  continued: { url: string; body: { prompt: string; baseRef?: string; reason?: string } }[];
}

async function installRoutes(page: Page, fixtures: Fixture[], refuseOnAccept: string[] = []): Promise<Calls> {
  const calls: Calls = { readiness: [], batch: [], continued: [] };
  const lane = fixtures.map(card);
  let live = lane;
  let lastBatch: { jobId: string }[] = [];
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const pathname = new URL(url).pathname;
    if (url.includes('/api/v1/studio/auth/status')) {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (pathname === '/api/v1/projects') return json(route, [{ id: 'PROJ-041', displayName: PROJECT, archived: false }]);
    if (url.includes('/api/v1/studio/board') || url.includes('/api/tasks/grouped')) return json(route, grouped(live));
    if (url.includes('/api/v1/studio/runner/status') || url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    if (url.includes('/api/cli/quota')) return json(route, { at: '2026-11-09T06:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/cli/usage')) return json(route, { at: '2026-11-09T06:00:00Z', sections: [] });
    if (url.includes('/api/orchestrator/global')) return json(route, { session: null });
    if (pathname === '/api/tasks/acceptance-readiness') {
      const body = route.request().postDataJSON() as { items: { jobId: string }[] };
      calls.readiness.push(body.items.map(entry => entry.jobId));
      const items = fixtures.filter(fixture => body.items.some(entry => entry.jobId === fixture.id)).map(item);
      return json(route, { checkedAtUtc: '2026-11-09T10:00:00Z', counts: {}, items });
    }
    if (pathname === '/api/tasks/batch-move' && route.request().method() === 'POST') {
      lastBatch = (route.request().postDataJSON() as { items: { jobId: string; targetState: string }[] }).items;
      calls.batch.push(lastBatch.map(entry => ({ jobId: entry.jobId, targetState: (entry as { targetState: string }).targetState })));
      return json(route, { id: 'batch-1', status: 'queued', total: lastBatch.length, completed: 0, succeeded: 0, failed: 0,
        results: [], message: null, createdAt: '', startedAt: null, finishedAt: null }, 202);
    }
    if (pathname === '/api/tasks/batch-move/batch-1') {
      const results = lastBatch.map((entry, index) => refuseOnAccept.includes(entry.jobId)
        ? { index, jobId: entry.jobId, status: 'integration-failed', durationMs: 4,
          message: 'Acceptance refused: the delivery is on the integration branch but integrated-unverified - no gate passed on the merged tree.' }
        : { index, jobId: entry.jobId, status: 'moved', message: null, durationMs: 4 });
      live = live.filter(cardEntry => !results.some(result => result.status === 'moved' && result.jobId === cardEntry.id));
      return json(route, { id: 'batch-1', status: 'completed', total: results.length, completed: results.length,
        succeeded: results.filter(result => result.status === 'moved').length,
        failed: results.filter(result => result.status !== 'moved').length,
        results, message: null, createdAt: '', startedAt: '', finishedAt: '' });
    }
    if (/\/tasks\/[^/]+\/continue$/.test(pathname)) {
      calls.continued.push({ url: pathname, body: route.request().postDataJSON() });
      return json(route, { status: 'queued' }, 202);
    }
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    return json(route, []);
  });
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  return calls;
}

async function openBoard(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await expect(page.getByTestId('acceptance-readiness-summary')).toBeVisible({ timeout: 60_000 });
  await dismissDevErrorDialog(page);
}

test.describe('Human Review acceptance readiness', () => {
  test.setTimeout(180_000);

  test('groups the lane by readiness, names each reason, and links the review report', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const calls = await installRoutes(page, FIXTURES);
    await openBoard(page);

    const lane = page.getByTestId('lane-5-human-review');
    await expect(page.getByTestId('acceptance-readiness-summary')).toHaveText(/3\s+of\s+9\s+ready to accept/);
    expect(calls.readiness[0].sort()).toEqual(FIXTURES.map(fixture => fixture.id).sort());
    const groups = lane.locator('[data-testid^="review-subsection-"]');
    await expect(groups).toHaveCount(7);
    await expect(groups.first()).toHaveAttribute('data-testid', 'review-subsection-ready');
    await expect(lane.getByTestId('review-subsection-ready')).toContainText('Ready to accept');
    await expect(lane.getByTestId('lane-count-5-human-review')).toContainText('9');

    const concern = lane.locator('[data-testid="task-card"]', { hasText: 'Explorer counts parked cards' });
    await expect(concern.getByTestId('acceptance-readiness-reason'))
      .toHaveText('The requirement-fit review returned concerns: The explorer total still skips parked cards in the Delivery group.');
    await expect(concern.getByTestId('acceptance-readiness-missing')).toContainText('project-rows.ts');
    await expect(concern.getByTestId('acceptance-review-report')).toHaveAttribute('href', /\/tasks\/AGT-4104\?view=evidence:protocol/);
    await expect(concern.getByTestId('acceptance-send-round')).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await concern.scrollIntoViewIfNeeded();
      await concern.screenshot({ path: path.join(RESULTS_DIR, `human-review-readiness--concern-card--${theme}--1280.png`) });
    }

    const deploy = lane.locator('[data-testid="task-card"]', { hasText: 'Measure the cold board load' });
    await expect(deploy.getByTestId('acceptance-readiness-note')).toHaveAttribute('data-readiness', 'waiting-deploy');
    await expect(deploy.getByTestId('acceptance-send-round')).toHaveCount(0);
    const research = lane.locator('[data-testid="task-card"]', { hasText: 'Research: queue fairness options' });
    await expect(research.getByTestId('acceptance-readiness-reason')).toContainText('This research card has no requirement-fit verdict');

    await page.getByTestId('acceptance-filter-ready').click();
    await expect(groups).toHaveCount(1);
    await expect(lane.getByTestId('lane-count-5-human-review')).toContainText('3');
    await page.getByTestId('acceptance-filter-not-ready').click();
    await expect(page.getByTestId('acceptance-accept-ready')).toBeDisabled();
    await expect(lane.getByTestId('lane-count-5-human-review')).toContainText('6');
    await page.getByTestId('acceptance-filter-all').click();

    await concern.getByTestId('acceptance-review-report').click();
    await expect.poll(() => page.evaluate(() => window.location.hash)).toContain('/tasks/AGT-4104?view=evidence:protocol');
  });

  test('accepts the ready cards through the batch move and shows each result', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const calls = await installRoutes(page, FIXTURES, ['cold-badge']);
    await openBoard(page);

    await page.getByTestId('acceptance-accept-ready').click();
    await expect(page.getByTestId('confirm-dialog')).toContainText('Accept 3 ready cards?');
    await page.getByTestId('confirm-dialog-confirm').click();

    const results = page.getByTestId('acceptance-result');
    await expect(results).toHaveCount(3);
    expect(calls.batch).toEqual([[
      { jobId: 'lane-filter-count', targetState: '6-completed' },
      { jobId: 'search-cursor', targetState: '6-completed' },
      { jobId: 'cold-badge', targetState: '6-completed' },
    ]]);
    await expect(page.getByTestId('acceptance-results')).toContainText('2 of 3 accepted');
    const refused = results.filter({ hasText: 'Archive row shows the cold badge' });
    await expect(refused).toHaveAttribute('data-accepted', 'false');
    await expect(refused).toContainText('integrated-unverified');
    await expect.poll(() => calls.readiness.length).toBeGreaterThan(1);
    expect(calls.readiness.at(-1)).not.toContain('lane-filter-count');
    expect(calls.continued).toEqual([]);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await page.getByTestId('acceptance-readiness-bar').screenshot({
        path: path.join(RESULTS_DIR, `human-review-readiness--accept-results--${theme}--1280.png`),
      });
    }
  });

  test('prefills a round for a refused card and sends it only after the owner confirms', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const calls = await installRoutes(page, FIXTURES);
    await openBoard(page);

    const concern = page.locator('[data-testid="task-card"]', { hasText: 'Explorer counts parked cards' });
    await concern.getByTestId('acceptance-send-round').click();
    const dialog = page.getByTestId('acceptance-round-dialog');
    await expect(dialog).toBeVisible();
    await expect(page).not.toHaveURL(/\/tasks\//);
    await expect(page.getByTestId('acceptance-round-prompt')).toHaveValue(/requirement-fit concern: The explorer total still skips parked cards/);
    await expect(page.getByTestId('acceptance-round-base-ref')).toHaveValue(RESULT_REF);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('acceptance-round-dialog').screenshot({
        path: path.join(RESULTS_DIR, `human-review-readiness--round-dialog--${theme}--1280.png`),
      });
    }

    await page.getByTestId('acceptance-round-cancel').click();
    await expect(dialog).toHaveCount(0);
    expect(calls.continued).toEqual([]);

    await concern.getByTestId('acceptance-send-round').click();
    await page.getByTestId('acceptance-round-prompt').fill('Count parked cards in the Delivery group total.');
    await page.getByTestId('acceptance-round-send').click();
    await expect(dialog).toHaveCount(0);
    expect(calls.continued).toHaveLength(1);
    expect(calls.continued[0].url).toMatch(/\/tasks\/parked-counts\/continue$/);
    expect(calls.continued[0].body.prompt).toBe('Count parked cards in the Delivery group total.');
    expect(calls.continued[0].body.baseRef).toBe(RESULT_REF);
  });

  test('captures the lane in both themes at 1280 and 390 px', async ({ page }) => {
    await installRoutes(page, FIXTURES);
    for (const width of [1280, 390] as const) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
      if (width === 1280) await openBoard(page);
      if (width === 390) {
        // On a phone the board shows one lane at a time; the owner picks Human Review in the lane picker.
        await page.getByTestId('mobile-lane-picker').locator('select').selectOption('5-human-review');
        await expect(page.getByTestId('lane-5-human-review')).toBeVisible();
        await expect(page.getByTestId('acceptance-accept-ready')).toBeInViewport();
      }
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await dismissDevErrorDialog(page);
        const lane = page.getByTestId('lane-5-human-review');
        await lane.scrollIntoViewIfNeeded();
        await expect(page.getByTestId('acceptance-readiness-bar')).toBeVisible();
        await lane.screenshot({ path: path.join(RESULTS_DIR, `human-review-readiness--lane--${theme}--${width}.png`) });
        await page.screenshot({ path: path.join(RESULTS_DIR, `human-review-readiness--board--${theme}--${width}.png`) });
      }
    }
  });

  test('captures the grouped lane as one readable sheet per theme', async ({ page, context }) => {
    // The lane is one 312 px column several screens tall, so a lane capture
    // shrinks past reading size. Capture the bar and each readiness group at
    // native size, then lay them out in columns on a blank page. The tall
    // viewport lets the lane's own scroll area hold the largest group whole.
    await page.setViewportSize({ width: 1280, height: 2400 });
    await installRoutes(page, FIXTURES);
    await openBoard(page);
    const lane = page.getByTestId('lane-5-human-review');
    const groups = lane.locator('[data-testid^="review-subsection-"]');
    await expect(groups).toHaveCount(7);
    const sheet = await context.newPage();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      const background = await lane.evaluate((element) => {
        for (let node: Element | null = element; node; node = node.parentElement) {
          const color = getComputedStyle(node).backgroundColor;
          if (color !== 'rgba(0, 0, 0, 0)' && color !== 'transparent') return color;
        }
        return 'Canvas';
      });
      const tiles = [await page.getByTestId('acceptance-readiness-bar').screenshot()];
      for (const group of await groups.all()) {
        await group.scrollIntoViewIfNeeded();
        await expect(group).toBeInViewport({ ratio: 1 });
        tiles.push(await group.screenshot());
      }
      await sheet.setViewportSize({ width: 1340, height: 600 });
      await sheet.setContent(`<body style="margin:0;padding:12px;background:${background}">
        <div style="column-count:4;column-gap:12px">${tiles.map((tile) =>
          `<img style="display:block;max-width:100%;margin-bottom:12px;break-inside:avoid" src="data:image/png;base64,${tile.toString('base64')}">`).join('')}
        </div></body>`);
      await sheet.waitForFunction(() => [...document.images].every((image) => image.complete));
      await sheet.screenshot({ path: path.join(RESULTS_DIR, `human-review-readiness--grouped--${theme}--1280.png`), fullPage: true });
    }
    await sheet.close();
  });

  test('the 41-card lane of 9 November reads 15 ready and accepts exactly those', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const classes: [Readiness, number][] = [
      ['ready', 15], ['review-concerns', 9], ['integrated-unverified', 4], ['verdict-stale', 3],
      ['no-content-verdict', 2], ['waiting-deploy', 5], ['owner-decision', 3],
    ];
    const fixtures = classes.flatMap(([readiness, count]) => Array.from({ length: count }, (_, index) => {
      const template = FIXTURES.find(fixture => fixture.readiness === readiness)!;
      const id = `${readiness}-${index + 1}`;
      return { ...template, id, key: `AGT-5${String(fixtures41Index(readiness, index)).padStart(3, '0')}`, title: `${template.title} ${index + 1}` };
    }));
    expect(fixtures).toHaveLength(41);
    const calls = await installRoutes(page, fixtures);
    await openBoard(page);

    await expect(page.getByTestId('acceptance-readiness-summary')).toHaveText(/15\s+of\s+41\s+ready to accept/);
    await expect(page.getByTestId('lane-count-5-human-review')).toContainText('41');
    await expect(page.getByTestId('lane-5-human-review').getByTestId('review-subsection-ready')).toContainText('15');
    await page.getByTestId('acceptance-filter-ready').click();
    await expect(page.getByTestId('lane-count-5-human-review')).toContainText('15');
    await page.getByTestId('acceptance-accept-ready').click();
    await page.getByTestId('confirm-dialog-confirm').click();
    await expect(page.getByTestId('acceptance-results')).toContainText('15 of 15 accepted');
    expect(calls.batch[0].map(entry => entry.jobId)).toEqual(fixtures.filter(fixture => fixture.readiness === 'ready').map(fixture => fixture.id));
  });
});

function fixtures41Index(readiness: Readiness, index: number): number {
  return ['ready', 'review-concerns', 'integrated-unverified', 'verdict-stale', 'no-content-verdict', 'waiting-deploy', 'owner-decision']
    .indexOf(readiness) * 100 + index;
}
