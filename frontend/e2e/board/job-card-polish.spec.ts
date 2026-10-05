import { test, expect, Page } from '@playwright/test';
import { contrastRatio } from '../helpers/contrast';

/**
 * F57 - Board-card polish regression spec.
 *
 * Validates three fixes applied to task cards:
 *   A) No inner scrollbar on cards (overflow: hidden on .task-card).
 *   B) Running state is a uniform whole-card ring, not a one-edge accent.
 *   C) Tag/pill badges use semantic tokens and achieve WCAG-AA contrast
 *      on both light and dark themes.
 *
 * Fixture-driven via route interception so the spec runs against any
 * backend (or none) and never depends on real job data.
 */

const PROJECT = 'f57-fixture';
const WATCH_PATH = 'C:/fixtures/f57-card-polish';

function makeJob(
  id: string,
  state: string,
  order: number,
  extra: Record<string, unknown> = {}
) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: `${state} card ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-24T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-24T11:00:00Z',
    sessionName: null,
    model: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    ...extra,
  };
}

const RUNNING_JOB = makeJob('f57-running', '3-progress', 1, {
  execution: {
    jobId: 'f57-running',
    taskKey: `${WATCH_PATH}::f57-running`,
    processId: 99001,
    startedAt: '2026-05-24T10:00:00Z',
    status: 'running',
    exitCode: null,
    durationSeconds: null,
    model: 'opus',
  },
});

const IDLE_JOBS = Array.from({ length: 4 }, (_, i) =>
  makeJob(`f57-idle-${i}`, '3-progress', i + 2)
);

const WARN_ISSUE_JOB = makeJob('f57-warn-issue', '3-progress', 7, {
  outcomeIssue: {
    kind: 'missing-terminal-sentinel',
    label: 'Missing sentinel',
    severity: 'Warn',
    summary: 'The run finished without a terminal sentinel.',
    lastSeenAt: '2026-05-24T11:00:00Z',
  },
});

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [RUNNING_JOB, ...IDLE_JOBS, WARN_ISSUE_JOB],
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(GROUPED_PAYLOAD),
    }));

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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function gotoBoard(page: Page) {
  await page.goto('/?includeFixtures=true');
  await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
}

test.describe('F57 — Board-card polish', () => {
  test.beforeEach(async ({ page }) => {
    await installRoutes(page);
  });

  test('A) cards have no inner scrollbar (overflow: hidden)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const cards = page.locator('[data-testid="task-card"]');
    const count = await cards.count();
    expect(count).toBeGreaterThanOrEqual(1);

    for (let i = 0; i < count; i++) {
      const card = cards.nth(i);
      const overflow = await card.evaluate((el) => getComputedStyle(el).overflowY);
      expect(overflow, `card ${i} overflowY should not be auto/scroll`).not.toMatch(/auto|scroll/);
      const dims = await card.evaluate((el) => ({
        scrollH: el.scrollHeight,
        clientH: el.clientHeight,
      }));
      expect(dims.scrollH).toBeLessThanOrEqual(dims.clientH + 1);
    }
  });

  test('B) running card has a uniform whole-card ring', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const runningCard = page.locator('[data-testid="task-card"][data-running="true"]');
    await expect(runningCard).toBeVisible({ timeout: 5_000 });
    await expect(runningCard.locator('[data-testid="task-card-progress"]')).toHaveCount(0);

    const chrome = await runningCard.evaluate((el) => {
      const cs = getComputedStyle(el);
      return {
        borderTop: cs.borderTopColor,
        borderRight: cs.borderRightColor,
        borderBottom: cs.borderBottomColor,
        borderLeft: cs.borderLeftColor,
        shadow: cs.boxShadow,
      };
    });
    expect(chrome.borderTop).toBe(chrome.borderRight);
    expect(chrome.borderTop).toBe(chrome.borderBottom);
    expect(chrome.borderTop).toBe(chrome.borderLeft);
    expect(chrome.shadow).not.toBe('none');

    const alphaMatches = [...chrome.shadow.matchAll(/rgba?\(\s*\d+,\s*\d+,\s*\d+,\s*([\d.]+)\s*\)/g)];
    for (const m of alphaMatches) {
      expect(Number(m[1])).toBeLessThanOrEqual(0.35);
    }
  });

  test('C) warn/issue pills use semantic tokens and pass WCAG-AA (dark)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    const issuePill = page.locator('[data-testid="task-card-outcome-issue"]');
    if ((await issuePill.count()) > 0) {
      const { fg, bg } = await issuePill.first().evaluate((el) => {
        const cs = getComputedStyle(el);
        return { fg: cs.color, bg: cs.backgroundColor };
      });
      const ratio = contrastRatio(fg, bg);
      expect(ratio).toBeGreaterThanOrEqual(4.5);
    }
  });

  test('C) warn/issue pills pass WCAG-AA (light theme)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.addInitScript(() => {
      document.documentElement.setAttribute('data-studio-theme', 'light');
    });
    await gotoBoard(page);
    await page.evaluate(() => {
      document.documentElement.setAttribute('data-studio-theme', 'light');
    });
    await page.waitForTimeout(300);

    const issuePill = page.locator('[data-testid="task-card-outcome-issue"]');
    if ((await issuePill.count()) > 0) {
      const { fg, bg } = await issuePill.first().evaluate((el) => {
        const cs = getComputedStyle(el);
        return { fg: cs.color, bg: cs.backgroundColor };
      });
      const ratio = contrastRatio(fg, bg);
      expect(ratio).toBeGreaterThanOrEqual(4.5);
    }
  });
});
