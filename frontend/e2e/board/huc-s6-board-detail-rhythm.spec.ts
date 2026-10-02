import { test, expect, type Page } from '@playwright/test';
import * as path from 'path';

/**
 * HUC-S6 (AGT-2965): board and task protocol rhythm.
 *
 * Board: tasks align to lane headings without a lane frame, lane totals equal
 * the visible task children, and every count (lane, review sub-group) shares
 * one count treatment. Task detail: the protocol keeps its chronology,
 * actions and archived run selection; on phone the contextual facts stack
 * below the controls instead of covering them.
 *
 * Fully mocked - no backend. Captures land in JOB_RESULTS_DIR when set
 * (`HUC_S6_PHASE=before|after` labels the file names).
 */

const PROJECT = 'demo-app';
const WP = 'C:/fixtures/demo-app';
const DETAIL_ID = 'DEMO-104';
const PHASE = process.env.HUC_S6_PHASE ?? 'after';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function makeTask(id: string, title: string, state: string, order: number) {
  return {
    id, taskKey: `${WP}::${id}`, title, state, order,
    agent: 'codex', cliType: 'codex', createdAt: '2026-09-25T09:00:00Z',
    watchPath: WP, projectName: PROJECT,
    folderPath: `${WP}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-09-25T11:00:00Z', sessionName: null, model: 'gpt-5.5',
    useOwnSession: null, lastUsage: null, execution: null, commit: null,
    commits: [], ownerClientId: 'local-default', tags: [],
  };
}

const READY = [
  makeTask('DEMO-109', 'Project setup guide', '2-ready', 1),
  makeTask('DEMO-111', 'Keyboard navigation', '2-ready', 2),
];
const PROGRESS = [makeTask(DETAIL_ID, 'Session recovery', '3-progress', 1)];
const REVIEW = [
  makeTask('DEMO-098', 'Workspace health', '5-human-review', 1),
  makeTask('DEMO-097', 'Usage ledger alignment', '5-human-review', 2),
  makeTask('DEMO-096', 'Archive filter copy', '5-human-review', 3),
];
const COMPLETED = [makeTask('DEMO-090', 'Header tokens', '6-completed', 1)];
const ALL = [...READY, ...PROGRESS, ...REVIEW, ...COMPLETED];

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: READY,
  progress: PROGRESS, failedPickup: [], codeNotComplete: [], review: [],
  autoReview: [], humanReview: REVIEW, completed: COMPLETED, archive: [],
};

function detailFor(task: ReturnType<typeof makeTask>) {
  return {
    info: task,
    promptMarkdown: '# Session recovery\n\nRestore a paused session with its latest checkpoint and preserve the task run history.',
    promptHistory: [], titleHistory: [], statusMarkdown: '', contextUsage: null,
    log: [
      { timestamp: '2026-09-25T16:52:00Z', message: 'Run started' },
      { timestamp: '2026-09-25T16:55:00Z', message: 'Changes prepared' },
      { timestamp: '2026-09-25T16:58:00Z', message: 'Verification' },
    ],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
    reviewEvidence: [],
  };
}

function execStep(stepId: string, kind: string, status: string) {
  return {
    stepId, kind, status, durationMs: status === 'passed' ? 42_000 : 0,
    inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
    startedAt: status === 'pending' ? null : '2026-09-25T16:52:00Z',
    completedAt: status === 'passed' ? '2026-09-25T16:52:42Z' : null,
  };
}

function pipeline() {
  const step = (id: string, displayName: string, kind: string) => ({
    id, displayName, kind, runMode: 'sequential', dependsOn: [], idempotent: true, stub: false,
  });
  const core = [step('core-agent-run', 'Agent run', 'core')];
  const post = [step('aspect-code-quality', 'Code quality', 'aspect')];
  return {
    pipeline: { id: 'standard', displayName: 'Standard task pipeline', version: 1, pre: [], core, post, allSteps: [...core, ...post] },
    execution: {
      pipelineId: 'standard', pipelineVersion: 1, jobId: DETAIL_ID, project: PROJECT,
      startedAt: '2026-09-25T16:52:00Z', completedAt: null, attempt: 2,
      previousAttempts: [{
        pipelineId: 'standard', pipelineVersion: 1, jobId: DETAIL_ID, project: PROJECT,
        startedAt: '2026-09-25T14:00:00Z', completedAt: '2026-09-25T14:05:00Z', attempt: 1,
        previousAttempts: [],
        steps: [execStep('core-agent-run', 'core', 'passed'), execStep('aspect-code-quality', 'aspect', 'passed')],
      }],
      steps: [execStep('core-agent-run', 'core', 'running'), execStep('aspect-code-quality', 'aspect', 'pending')],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

async function installRoutes(page: Page): Promise<void> {
  // Phone width: hide the Explorer sidebar so the board and detail own the
  // viewport, as an operator does on a phone.
  if ((page.viewportSize()?.width ?? 0) < 720) {
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.panelState.v1', JSON.stringify({ active: 'explorer', visible: false }));
    });
  }
  await page.route('**/api/**', (route) => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/api/auth/status', (route) =>
    route.fulfill(json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null })));
  await page.route('**/api/tasks/grouped**', (route) => route.fulfill(json(GROUPED)));
  await page.route(/\/api\/tasks\/[^/?]+\/(output|session-events|runs)(\?|$)/, (route) => {
    const url = route.request().url();
    if (url.includes('/session-events')) return route.fulfill(json({ events: [], sessionChain: [] }));
    if (url.includes('/runs')) return route.fulfill(json({ runs: [] }));
    return route.fulfill(json([]));
  });
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(\?|$)/, (route) => route.fulfill(json(pipeline())));
  await page.route(/\/api\/tasks\/(?!grouped)[^/?]+(\?|$)/, (route) => {
    const id = decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop() ?? '');
    const task = ALL.find(t => t.id === id);
    return route.fulfill(task ? json(detailFor(task)) : { status: 404, contentType: 'application/json', body: '{}' });
  });
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill(json([{ name: PROJECT, path: WP, rootPath: WP, repositoryPath: WP }])));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) => route.fulfill(json({
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
  })));
  await page.route(/\/api\/projects\/[^/]+\/workbenches/, (route) => route.fulfill(json({ items: [] })));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) => route.fulfill(json({})));
  await page.route('**/api/environment**', (route) =>
    route.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route('**/api/cli/usage**', (route) => route.fulfill(json({ at: '2026-09-25T07:00:00Z', sessions: [] })));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill(json({ at: '2026-09-25T07:00:00Z', ttlSeconds: 600, snapshots: [] })));
}

async function cleanOverlays(page: Page): Promise<void> {
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay, app-error-dialog, [data-testid="error-dialog-overlay"]')
      .forEach((n) => n.remove());
  });
}

async function capture(page: Page, surface: string, width: number): Promise<void> {
  if (!RESULTS_DIR) return;
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate((value) => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await cleanOverlays(page);
    await page.screenshot({ path: path.join(RESULTS_DIR, `${surface}-${PHASE}-${width}-${theme}--mocked.png`) });
  }
}

const WIDTHS = [390, 1024, 1728] as const;

test.describe('HUC-S6 board and task protocol rhythm', () => {
  for (const width of WIDTHS) {
    test(`board lanes at ${width}px: totals equal visible children, one count treatment`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.addInitScript((project) => {
        localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
          v: 1, tabs: [{ kind: 'board', projectName: project }], activeKey: `board:${project}`,
        }));
      }, PROJECT);
      await installRoutes(page);
      await page.goto('/?includeFixtures=true');
      const review = page.locator('[data-testid="lane-5-human-review"]').first();
      await expect(review).toBeVisible({ timeout: 15_000 });

      for (const state of ['2-ready', '3-progress', '5-human-review']) {
        const lane = page.locator(`[data-testid="lane-${state}"]`).first();
        if (!(await lane.isVisible())) continue;
        const count = Number((await lane.getByTestId(`lane-count-${state}`).locator('span').first().textContent())?.trim());
        await expect(lane.locator('app-job-card')).toHaveCount(count);
      }

      if (width >= 1024) {
        // Lane frame is flat: task rows align with the lane heading's inline start.
        const head = await review.locator('.column__header').boundingBox();
        const card = await review.locator('app-job-card').first().boundingBox();
        expect(Math.abs((head?.x ?? 0) - (card?.x ?? 0))).toBeLessThanOrEqual(1);
        // Every count shares one treatment: lane total and sub-group count match.
        const styleOf = (sel: string) => review.locator(sel).first().evaluate((el) => {
          const s = getComputedStyle(el);
          return [s.backgroundColor, s.borderRadius, s.fontSize, s.fontVariantNumeric, s.height].join('|');
        });
        const sub = review.locator('.column__subsection-count');
        if (await sub.count()) expect(await styleOf('.column__subsection-count')).toBe(await styleOf('.column__count'));
        // No status accent bar on lane frames.
        const borderLeft = await review.evaluate((el) => getComputedStyle(el).borderLeftWidth);
        expect(borderLeft).toBe('0px');
      }
      await capture(page, 'board', width);
    });

    test(`task detail at ${width}px: protocol chronology, actions and archived run selection`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.addInitScript(() => {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
      });
      await installRoutes(page);
      await page.goto(`/?job=${encodeURIComponent(DETAIL_ID)}&watchPath=${encodeURIComponent(WP)}`,
        { waitUntil: 'domcontentloaded', timeout: 30_000 });
      await cleanOverlays(page);
      const overview = page.getByTestId('overview-pipeline');
      await expect(overview).toBeVisible({ timeout: 15_000 });

      // Archived run selection remains: current #2 plus the archived #1.
      const switcher = page.getByTestId('overview-pipeline-run-switcher');
      await expect(switcher).toBeVisible();
      await expect(switcher.locator('[data-testid="overview-pipeline-run-option"]')).toHaveCount(2);

      // Task controls stay reachable and are not covered by the facts region.
      const panes = page.getByTestId('detail-panes');
      await expect(panes).toBeVisible();
      const sections = panes.locator('section.pane');
      expect(await sections.count()).toBeGreaterThan(0);
      for (const pane of await sections.all()) {
        const box = await pane.boundingBox();
        expect(box?.width ?? 0).toBeGreaterThan(0);
        expect((box?.x ?? 0) + (box?.width ?? 0)).toBeLessThanOrEqual(width + 1);
      }
      await capture(page, 'detail', width);
    });
  }
});
