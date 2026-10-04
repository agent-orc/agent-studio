import { test, expect, type Page } from '@playwright/test';

/**
 * F39 — Job-card running-state in the light theme.
 *
 * Before this change, `.job-card--running` used hardcoded blue rgba() and a
 * Catppuccin Mantle hex base (`#1e1e2e`) for the surface gradient. On the
 * dark shell the card glowed; on the light shell the base flipped to white
 * by the bridge but the gradient produced a near-invisible wash, leaving
 * running cards visually identical to idle ones. The new design routes
 * everything through theme-aware semantic tokens (`--studio-bg-running`,
 * `--shadow-running`, `--studio-accent-3` family). The running cue is now a
 * whole-card ring/tint plus the existing "Running live" badge, never a
 * single-edge progress strip.
 *
 * Three contracts locked here:
 *  - No `.task-card__progress` element exists on running cards. State must
 *    be whole-card ring/tint and badge based.
 *  - The "Running live" execution pill clears WCAG-AA (≥ 4.5:1 text vs.
 *    background) in both dark + light. That is the regression the prompt
 *    explicitly called out.
 */

const PROJECT = 'fixture-f39';
const WATCH_PATH = 'C:/fixtures/f39-running-card';

interface JobFixtureOverride {
  state?: string;
  execution?: unknown;
  order?: number;
  id?: string;
  title?: string;
}

function makeJob(overrides: JobFixtureOverride = {}) {
  const id = overrides.id ?? 'f39-running-card';
  const state = overrides.state ?? '3-progress';
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: overrides.title ?? 'F39 running-card fixture',
    state,
    order: overrides.order ?? 1,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-23T07:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-23T07:30:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: overrides.execution ?? null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

const RUNNING_JOB = makeJob({
  execution: {
    executionId: 'exec-f39-running',
    jobId: 'f39-running-card',
    jobKey: `${WATCH_PATH}::f39-running-card`,
    status: 'running',
    processId: 12345,
    startedAt: '2026-05-23T07:25:00Z',
    finishedAt: null,
    exitCode: null,
    durationSeconds: null,
    model: 'claude-opus-4-7',
    runOutcome: null,
  },
});

const IDLE_JOB = makeJob({ id: 'f39-idle-card', title: 'F39 idle-card fixture', order: 2 });

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [RUNNING_JOB, IDLE_JOB],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: [],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));

  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
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
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-23T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-23T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'auto-continuous',
            activeJobId: RUNNING_JOB.id,
            activeExecution: RUNNING_JOB.execution,
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

function parseRgb(value: string): [number, number, number, number] {
  // Plain rgb()/rgba() (legacy + space-separated forms).
  const rgbMatch = /rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(value);
  if (rgbMatch) {
    return [
      Number(rgbMatch[1]),
      Number(rgbMatch[2]),
      Number(rgbMatch[3]),
      rgbMatch[4] === undefined ? 1 : Number(rgbMatch[4]),
    ];
  }
  // CSS Color 4 `color(srgb r g b [/ alpha])` — Chromium emits this for
  // resolved color-mix() values, where the channels are normalised 0..1
  // floats. The Tier-2 tokens used by .job-card--running route through
  // color-mix(), so the running-pill text/background land in this branch
  // in real browsers even though the underlying primitive is `#RRGGBB`.
  const colorMatch = /color\(\s*srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*\/\s*([\d.]+))?\s*\)/.exec(value);
  if (colorMatch) {
    return [
      Math.round(Number(colorMatch[1]) * 255),
      Math.round(Number(colorMatch[2]) * 255),
      Math.round(Number(colorMatch[3]) * 255),
      colorMatch[4] === undefined ? 1 : Number(colorMatch[4]),
    ];
  }
  throw new Error(`Cannot parse colour: ${value}`);
}

function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrastRatio(fgRaw: string, bgRaw: string): number {
  const fg = parseRgb(fgRaw);
  const bg = parseRgb(bgRaw);
  const fgRgb: [number, number, number] = [
    Math.round(fg[0] * fg[3] + bg[0] * (1 - fg[3])),
    Math.round(fg[1] * fg[3] + bg[1] * (1 - fg[3])),
    Math.round(fg[2] * fg[3] + bg[2] * (1 - fg[3])),
  ];
  const l1 = luminance(fgRgb);
  const l2 = luminance([bg[0], bg[1], bg[2]]);
  const [light, dark] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (light + 0.05) / (dark + 0.05);
}

async function seedBoardTab(page: Page): Promise<void> {
  // The studio-shell boots into the "Welcome" pane unless a persisted tab
  // tells it which board to restore. We pre-seed the localStorage key the
  // tab service reads on startup so the kanban-dashboard renders straight
  // away (no need to script a click through the project picker).
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

test.describe('F39 - running task-card across themes', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`running card uses whole-card ring + readable pill (${theme})`, async ({ page }, testInfo) => {
      await seedBoardTab(page);
      await installRoutes(page);
      await page.goto('/?includeFixtures=true');
      await page.waitForLoadState('domcontentloaded');
      // vsCodeLayout (default since 2026) uses data-testid="studio-board"
      // for the kanban surface; legacy "kanban-dashboard" only renders under
      // the flag-off path. Wait for either to be visible.
      await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
        .toBeVisible({ timeout: 10_000 });
      await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 10_000 });
      await setTheme(page, theme);
      await page.waitForTimeout(300);

      const runningCard = page.locator('[data-testid="task-card"][data-running="true"]').first();
      await expect(runningCard).toBeVisible({ timeout: 5_000 });

      // 1. No one-edge progress strip exists. Running must be card ring/tint
      //    plus the execution badge.
      await expect(runningCard.locator('[data-testid="task-card-progress"]')).toHaveCount(0);
      const cardChrome = await runningCard.evaluate((el) => {
        const cs = getComputedStyle(el);
        return {
          borderTop: cs.borderTopColor,
          borderRight: cs.borderRightColor,
          borderBottom: cs.borderBottomColor,
          borderLeft: cs.borderLeftColor,
          boxShadow: cs.boxShadow,
        };
      });
      expect(cardChrome.borderTop).toBe(cardChrome.borderRight);
      expect(cardChrome.borderTop).toBe(cardChrome.borderBottom);
      expect(cardChrome.borderTop).toBe(cardChrome.borderLeft);
      expect(cardChrome.boxShadow).not.toBe('none');

      // 2. The execution pill stays readable: text-on-background contrast
      //    clears WCAG-AA (≥ 4.5:1). This is the regression the prompt
      //    explicitly called out for the light theme.
      const pill = runningCard.locator('.task-card__execution-pill--running');
      await expect(pill).toBeVisible();
      const pillSample = await pill.evaluate((el) => {
        const cs = getComputedStyle(el);
        return { fg: cs.color, bg: cs.backgroundColor };
      });
      const pillRatio = contrastRatio(pillSample.fg, pillSample.bg);
      expect(
        pillRatio,
        `[${theme}] running-pill contrast ${pillRatio.toFixed(2)} (${pillSample.fg} on ${pillSample.bg})`
      ).toBeGreaterThanOrEqual(4.5);

      // 3. The card surface is distinct from the idle card surface — i.e.
      //    --studio-bg-running actually tints the card. Compare against an
      //    idle sibling on the same lane.
      const idleCard = page.locator('[data-testid="task-card"]:not([data-running="true"])').first();
      await expect(idleCard).toBeVisible();
      const [runningBg, idleBg] = await Promise.all([
        runningCard.evaluate((el) => getComputedStyle(el).backgroundColor),
        idleCard.evaluate((el) => getComputedStyle(el).backgroundColor),
      ]);
      expect(runningBg, `[${theme}] running vs idle bg`).not.toBe(idleBg);

      await testInfo.attach(`f39-running-card-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      if (process.env.F39_RESULTS_DIR) {
        await page.screenshot({
          path: `${process.env.F39_RESULTS_DIR}/f39-running-card-${theme}.png`,
          fullPage: false,
        });
      }
    });
  }

  test('reduced-motion still has no edge progress strip', async ({ browser }, testInfo) => {
    const context = await browser.newContext({ reducedMotion: 'reduce' });
    const page = await context.newPage();
    try {
      await seedBoardTab(page);
      await installRoutes(page);
      await page.goto('/?includeFixtures=true');
      await page.waitForLoadState('domcontentloaded');
      // vsCodeLayout (default since 2026) uses data-testid="studio-board"
      // for the kanban surface; legacy "kanban-dashboard" only renders under
      // the flag-off path. Wait for either to be visible.
      await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
        .toBeVisible({ timeout: 10_000 });
      await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 10_000 });
      await setTheme(page, 'light');
      await page.waitForTimeout(300);

      const runningCard = page.locator('[data-testid="task-card"][data-running="true"]').first();
      await expect(runningCard).toBeVisible({ timeout: 5_000 });

      await expect(runningCard.locator('[data-testid="task-card-progress"]')).toHaveCount(0);
      await expect(runningCard.locator('.task-card__execution-pill--running')).toBeVisible();

      if (process.env.F39_RESULTS_DIR) {
        await page.screenshot({
          path: `${process.env.F39_RESULTS_DIR}/f39-running-card-reduced-motion.png`,
          fullPage: false,
        });
      }
      await testInfo.attach('f39-running-card-reduced-motion.png', {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
    } finally {
      await context.close();
    }
  });
});
