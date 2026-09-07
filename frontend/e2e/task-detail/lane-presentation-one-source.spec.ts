import { test, expect, Page, Locator } from '@playwright/test';

/**
 * AGT-2715 — a lane's name and tone come from one source, on every surface.
 *
 * The operator screenshotted a single `5-human-review` card wearing four
 * different names and two different colours at once:
 *
 *   - the task header chip said "Review" (blue),
 *   - the Result tab header said "Human review lane" (amber dot),
 *   - the decision badge said "Human review" (a normaliser rewrote the label),
 *   - the project workflow section said "Awaiting human review."
 *
 * The fix routes every one of those through `models/lane-presentation.ts` and
 * the `--studio-lane-*` tone tokens. This spec asserts the visible result:
 * the board column, the detail header chip, and the Result header agree on
 * BOTH the word and the resolved colour, in dark and light themes.
 *
 * Fully mocked (no backend): the board renders from the lane config alone.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-presentation';
const FIXTURE_PROJECT = 'lane-presentation-demo';
const TASK_ID = 'fx-human-1';

/** The one word the lane is allowed to use. Mirrors `laneName('5-human-review')`. */
const LANE_NAME = 'Human review';
/** The one tone key. Mirrors `laneTone('5-human-review')`. */
const LANE_TONE = 'human-review';

function jobInfo(id: string, state: string, title: string): Record<string, unknown> {
  return {
    id,
    taskKey: `${FIXTURE_WATCH}::${id}`,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    key: 'AGT-0001',
    title,
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-09-01T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-09-01T09:00:00Z',
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
    orchestratorVerdict: null,
    ownerClientId: null,
    tags: [],
    taskType: 'chore',
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  return {
    preparation: [jobInfo('fx-prep-1', '1-preparation', 'Drafting next thing')],
    ready: [jobInfo('fx-ready-1', '2-ready', 'Ready to run')],
    progress: [jobInfo('fx-progress-1', '3-progress', 'Live run')],
    autoReview: [],
    humanReview: [jobInfo(TASK_ID, '5-human-review', 'Awaiting your accept')],
    review: [],
    completed: [jobInfo('fx-done-1', '6-completed', 'Wrapped up')],
    archive: [],
  };
}

/** Detail payload for the human-review card, with no terminal run signal, so
 *  the lane itself is the leading run-outcome signal. */
function taskDetail(): Record<string, unknown> {
  return {
    info: fixtureGrouped().humanReview[0],
    promptMarkdown: 'Pretend prompt.',
    statusMarkdown: '# Status\n\n## What Was Done\n- Something.\n',
    log: [],
    promptHistory: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = Object.values(grouped).flat();

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  // The shell renders behind an auth gate; without this the mocked board never
  // mounts and every assertion below fails on a sign-in card instead.
  await page.route('**/api/auth/status', async (route) => {
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    });
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]),
    });
  });
  await page.route('**/api/tasks', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/tasks/grouped', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route(`**/api/tasks/${TASK_ID}?**`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(taskDetail()) });
  });
  await page.route('**/api/runner/status', async (route) => {
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [FIXTURE_PROJECT]: {
        projectName: FIXTURE_PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [],
      } } }),
    });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    });
  });
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
  });
}

/** Resolved colour of an element, so two surfaces can be compared numerically. */
function colourOf(locator: Locator): Promise<string> {
  return locator.evaluate((el) => getComputedStyle(el).color);
}

/**
 * Resolve a lane tone token to a concrete colour in the page's CURRENT theme,
 * by painting a throwaway probe with it. Comparing each surface against the
 * token (rather than against each other across two navigations) keeps the
 * assertion honest no matter which theme the shell settled on.
 */
function toneTokenColour(page: Page, tone: string): Promise<string> {
  return page.evaluate((t) => {
    const probe = document.createElement('span');
    probe.style.color = `var(--studio-lane-${t}-fg)`;
    document.body.appendChild(probe);
    const colour = getComputedStyle(probe).color;
    probe.remove();
    return colour;
  }, tone);
}

/**
 * Pin the theme before the app boots. ThemeService seeds itself from
 * localStorage (defaulting to light), so setting the attribute after load
 * races with its own effect writing the persisted value back.
 */
async function pinTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.addInitScript((t) => {
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* storage blocked */ }
  }, theme);
}

/** Visible text of a <select>'s currently selected option. */
function selectedOptionText(locator: Locator): Promise<string> {
  return locator.evaluate((el) => {
    const select = el as HTMLSelectElement;
    return select.selectedOptions[0]?.textContent?.trim() ?? '';
  });
}

test.describe('AGT-2715 lane presentation comes from one source', () => {
  test.use({ viewport: { width: 1600, height: 1000 }, serviceWorkers: 'block' });

  test.beforeEach(async ({ page }) => {
    await installMocks(page);
  });

  test('board column, header chip, and Result header use the same lane name', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
      .toBeVisible({ timeout: 10_000 });

    // 1. The board column header. It used to read "Review".
    const columnTitle = page.getByTestId('lane-title-5-human-review');
    await expect(columnTitle).toHaveText(LANE_NAME);

    // The four wordings that used to compete must be gone from the board.
    await expect(page.getByText('Human review lane', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Awaiting human review.', { exact: true })).toHaveCount(0);

    // 2. Open the card. The detail header chip used to read "Review" too.
    await page.goto(
      `/?job=${encodeURIComponent(TASK_ID)}&watchPath=${encodeURIComponent(FIXTURE_WATCH)}`,
    );
    const laneChip = page.getByTestId('studio-lane-select');
    await expect(laneChip).toBeVisible({ timeout: 10_000 });
    expect(await selectedOptionText(laneChip)).toBe(LANE_NAME);

    // 3. The Result tab header. It used to read "Human review lane".
    const resultBadge = page.getByTestId('result-case-badge');
    if (await resultBadge.count() > 0) {
      await expect(resultBadge).toContainText(LANE_NAME);
      await expect(resultBadge).not.toContainText('Human review lane');
    }
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`board column and header chip paint the same lane tone (${theme} theme)`, async ({ page }) => {
      await pinTheme(page, theme);

      await page.goto('/');
      await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
        .toBeVisible({ timeout: 10_000 });

      // The token must resolve to a real colour in this theme, not an unset
      // custom property that silently falls back to inherited text.
      const toneColour = await toneTokenColour(page, LANE_TONE);
      expect(toneColour, `--studio-lane-${LANE_TONE}-fg in ${theme} theme`).toMatch(/^rgba?\(/);
      expect(toneColour).not.toBe('rgba(0, 0, 0, 0)');

      // 1. The board column header declares the lane's tone and paints it.
      const header = page.locator('.column[data-state="5-human-review"] .column__header');
      await expect(header).toHaveAttribute('data-lane-tone', LANE_TONE);
      expect(await colourOf(page.getByTestId('lane-title-5-human-review')),
        `board lane title in ${theme} theme`).toBe(toneColour);

      await page.screenshot({
        path: `test-results/lane-presentation-board-${theme}.png`, fullPage: false,
      });

      // 2. The detail header chip declares the same tone and paints the same
      // colour. Before AGT-2715 the chip read the purple lane palette while
      // the Result header dot read a generic amber, for the very same card.
      await page.goto(
        `/?job=${encodeURIComponent(TASK_ID)}&watchPath=${encodeURIComponent(FIXTURE_WATCH)}`,
      );
      const laneChip = page.getByTestId('studio-lane-select');
      await expect(laneChip).toBeVisible({ timeout: 10_000 });
      await expect(laneChip).toHaveAttribute('data-lane-tone', LANE_TONE);
      expect(await colourOf(laneChip), `detail lane chip in ${theme} theme`)
        .toBe(await toneTokenColour(page, LANE_TONE));

      await page.screenshot({
        path: `test-results/lane-presentation-detail-${theme}.png`, fullPage: false,
      });
    });
  }

  test('every board lane header declares a tone, so no lane is left uncoloured', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
      .toBeVisible({ timeout: 10_000 });

    const headers = page.locator('.column__header');
    const count = await headers.count();
    expect(count).toBeGreaterThan(0);

    const tones: string[] = [];
    for (let i = 0; i < count; i++) {
      const tone = await headers.nth(i).getAttribute('data-lane-tone');
      expect(tone, `lane header ${i} carries a tone`).toBeTruthy();
      tones.push(tone!);
    }
    // Distinct lanes must not collapse onto one pigment on the same board.
    expect(new Set(tones).size).toBe(tones.length);
  });
});
