import { test, expect, type Page } from '@playwright/test';

/**
 * AGT-2715 — one lane, one word, one colour.
 *
 * Operator report (2026-09-06, AGT-2692): the single lane `5-human-review` was
 * rendered four different ways at once. The task header chip said "Review", the
 * Result tab header said "Human review lane", a badge helper rewrote that to
 * "Human review", and the project workflow section said "Awaiting human
 * review." — in two different tones (a blue chip, an amber dot).
 *
 * This spec locks the fix at the surface level. It walks the three places the
 * operator sees a lane in one session and asserts that all three
 *   (a) print the SAME name, and
 *   (b) paint it with the SAME resolved colour,
 * in both themes. The name is not hard-coded here on purpose: it is read from
 * the board column heading, which is itself a projection of
 * `src/app/models/lane-presentation.ts`. If someone renames the lane there,
 * this spec follows; if someone re-hard-codes a different word in ONE surface,
 * this spec fails.
 *
 * Fully mocked — no backend required.
 */

const PROJECT = 'fixture-lane-presentation';
const WATCH_PATH = 'C:/fixtures/lane-presentation';
const HUMAN_REVIEW = '5-human-review';

function makeTask(id: string, title: string, state: string, order: number) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-09-06T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-09-06T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

const REVIEW_TASK = makeTask('lane-pres-1', 'Lane presentation review card', HUMAN_REVIEW, 1);
const READY_TASK = makeTask('lane-pres-2', 'Lane presentation ready card', '2-ready', 1);

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [READY_TASK],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  humanReview: [REVIEW_TASK],
  completed: [],
  archive: [],
};

/**
 * A finished run whose only decisive signal is the lane: no failure, no
 * orchestrator verdict. That is precisely the case where `deriveProtocolVerdict`
 * elects the lane signal as the leading one, so the Result header names the
 * lane — the surface from the screenshot.
 */
const STATUS_MARKDOWN = [
  '# Status',
  '',
  '- Duration: 4 min',
  '',
  '## Summary',
  '',
  'Ran to completion and is waiting for a decision.',
].join('\n');

function detailFor(task: typeof REVIEW_TASK) {
  return {
    info: task,
    promptMarkdown: `# ${task.title}`,
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: task.state === HUMAN_REVIEW ? STATUS_MARKDOWN : null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  // Broad catch-all FIRST: Playwright runs the last-registered match first, so
  // anything registered after this wins over it.
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  // Without this the shell renders the sign-in gate instead of the board: the
  // catch-all's `[]` is not a valid AuthStatus, so `studioAllowed` is false.
  await page.route('**/api/auth/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));

  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  // Negative lookahead keeps this off `/api/tasks/grouped`.
  await page.route(/\/api\/tasks\/(?!grouped)[^/?]+(\?|$)/, (route) => {
    const url = new URL(route.request().url());
    const id = decodeURIComponent(url.pathname.split('/').pop() ?? '');
    const task = [REVIEW_TASK, READY_TASK].find((t) => t.id === id);
    route.fulfill(task
      ? { status: 200, contentType: 'application/json', body: JSON.stringify(detailFor(task)) }
      : { status: 404, contentType: 'application/json', body: '{}' });
  });

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));

  await page.route('**/api/runner/status**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));

  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-09-06T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-09-06T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  // Mirrors the studio-shell toggle: stamp `data-studio-theme` on <html> (the
  // token bridge) and persist it so the shell's effect does not overwrite it.
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

/**
 * Drop the dev overlay and any benign error dialog the mocked boot popped, so
 * they neither intercept pointer events nor obscure the evidence screenshot.
 */
async function dismissOverlays(page: Page): Promise<void> {
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay, app-error-dialog, [data-testid="error-dialog-overlay"]')
      .forEach((node) => node.remove());
  });
}

/** Resolved colour of a CSS property, as the browser paints it. */
function colorOf(page: Page, selector: string, property: string): Promise<string> {
  return page.evaluate(([sel, prop]) => {
    const node = document.querySelector(sel);
    if (!node) throw new Error(`No element for ${sel}`);
    return getComputedStyle(node).getPropertyValue(prop).trim();
  }, [selector, property] as const);
}

for (const theme of ['dark', 'light'] as const) {
  test(`5-human-review reads the same name and the same tone on board, header chip, and Result (${theme})`, async ({ page }, testInfo) => {
    await installRoutes(page);
    await page.goto('/?includeFixtures=true');
    await page.waitForLoadState('domcontentloaded');
    await setTheme(page, theme);

    // ---- Surface 1: the board column ------------------------------------
    const column = page.locator(`[data-testid="lane-${HUMAN_REVIEW}"]`).first();
    await expect(column).toBeVisible({ timeout: 20_000 });

    const boardName = (await column.getByTestId(`lane-title-${HUMAN_REVIEW}`).textContent())?.trim();
    // The wording itself: the operator asked for "Human review", not "Review".
    expect(boardName).toBe('Human review');

    const boardTone = await colorOf(page, `[data-testid="lane-tone-dot-${HUMAN_REVIEW}"]`, 'background-color');
    expect(boardTone, 'board lane dot must be painted').not.toBe('');
    expect(boardTone).not.toBe('rgba(0, 0, 0, 0)');

    // ---- Surface 2: the detail header lane chip --------------------------
    await column.locator('app-job-card').filter({ hasText: REVIEW_TASK.title }).first().click();

    const laneChip = page.getByTestId('studio-lane-select');
    await expect(laneChip).toBeVisible({ timeout: 15_000 });
    await expect(laneChip).toHaveValue(HUMAN_REVIEW);

    // The selected <option> is what the operator reads in the chip.
    const chipName = await laneChip.evaluate((el) => {
      const select = el as HTMLSelectElement;
      return select.options[select.selectedIndex]?.text.trim() ?? '';
    });
    expect(chipName, 'header chip and board column must use one word').toBe(boardName);

    // The mocked shell pops a benign error dialog for whatever sub-resource
    // the catch-all could not satisfy; its overlay swallows pointer events.
    await dismissOverlays(page);

    // Park the pointer somewhere neutral first: opening the card swaps the
    // board out for the task tab, which can leave the cursor resting on the
    // header chip and silently measure its hover state instead.
    await page.mouse.move(0, 0);
    const chipTone = await colorOf(page, '[data-testid="studio-lane-select"]', 'color');
    expect(chipTone, 'header chip and board column must use one tone').toBe(boardTone);

    // Hover must not change the lane's colour. The base `.studio-tab-action`
    // hover rule outranks the chip's resting colour, so this is a real
    // regression surface, not a theoretical one.
    await laneChip.hover();
    const chipHoverTone = await colorOf(page, '[data-testid="studio-lane-select"]', 'color');
    expect(chipHoverTone, 'hovering the chip must not change the lane tone').toBe(boardTone);
    await page.mouse.move(0, 0);

    // ---- Surface 3: the Result tab header --------------------------------
    const resultBadge = page.getByTestId('result-case-badge');
    await expect(resultBadge).toBeVisible({ timeout: 15_000 });
    await expect(resultBadge).toHaveAttribute('data-lane', HUMAN_REVIEW);

    const resultName = (await resultBadge.locator('.result__case-label').textContent())?.trim();
    expect(resultName, 'Result header and board column must use one word').toBe(boardName);

    // The dot is `background: currentColor`, so its painted colour is the
    // badge's resolved tone. Amber (the old needs-decision colour) would fail.
    const resultTone = await colorOf(page, '[data-testid="result-case-dot"]', 'background-color');
    expect(resultTone, 'Result header dot and board column must use one tone').toBe(boardTone);

    // Evidence: the detail surface showing the chip and the Result header
    // agreeing, plus the board column behind it after paging back.
    await dismissOverlays(page);
    const detailShot = await page.screenshot({ fullPage: false });
    await testInfo.attach(`lane-presentation-detail-${theme}.png`, { body: detailShot, contentType: 'image/png' });

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await page.screenshot({ path: `${resultsDir}/lane-presentation-detail-${theme}.png`, fullPage: false });
      await page.goto('/?includeFixtures=true');
      await page.waitForLoadState('domcontentloaded');
      await setTheme(page, theme);
      await expect(page.locator(`[data-testid="lane-${HUMAN_REVIEW}"]`).first()).toBeVisible({ timeout: 20_000 });
      await dismissOverlays(page);
      await page.screenshot({ path: `${resultsDir}/lane-presentation-board-${theme}.png`, fullPage: false });
    }
  });
}
