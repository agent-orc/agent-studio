import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Pipeline "Agent execution" row: run count + hover/focus details popover.
 *
 * Acceptance: the CORE Agent-execution row must not look empty when runs
 * happened. When run data exists it shows an explicit count ("34 runs"),
 * derived from the same RunTimeline.runCount that drives the Overview "Runs"
 * value. Hover OR keyboard focus opens a popover with run count, recovered
 * count, CLI / model / session, first-run + last-activity stamps, and a
 * pointer to the Timeline tab. An empty/no-run task keeps its dash state
 * (no badge).
 *
 * Fully mocked - no backend or git repository needed; the pipeline block and
 * the run-timeline both render from the joined API responses we stub here.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-agent-run-count';
const JOB_ID = 'pipeline-agent-run-count-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Run-count fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-8',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
      sessionName: '0c1e3817-91c2-43a1-a1aa-9f73d161d4a2',
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

function step(id: string, displayName: string, kind: string) {
  return { id, displayName, kind, runMode: 'sequential', dependsOn: [], idempotent: true, stub: false };
}

const allSteps = [
  step('pre-loop-guard', 'Loop check', 'module'),
  step('core-agent-run', 'Agent execution', 'core'),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect'),
];

function pipelineBody() {
  return {
    pipeline: {
      id: 'standard-task-pipeline',
      displayName: 'Standard task pipeline',
      version: 1,
      pre: [allSteps[0]],
      core: [allSteps[1]],
      post: [allSteps[2]],
      allSteps,
    },
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: null,
      steps: [
        { stepId: 'pre-loop-guard', kind: 'module', status: 'passed', durationMs: 12, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, startedAt: '2026-06-02T08:00:00Z', completedAt: '2026-06-02T08:00:00Z' },
        { stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'running', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, startedAt: '2026-06-02T08:00:01Z', completedAt: null },
        { stepId: 'aspect-requirement-fit', kind: 'aspect', status: 'pending', durationMs: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0, startedAt: null, completedAt: null },
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

function rowAnatomyPipelineBody() {
  const body = pipelineBody();
  const longNames = new Map([
    ['pre-loop-guard', 'Validate workspace boundaries before agent pickup'],
    ['core-agent-run', 'Resolve operator input and continue the core agent execution'],
    ['aspect-requirement-fit', 'Review every acceptance criterion and preserve complete evidence'],
  ]);
  return {
    ...body,
    pipeline: {
      ...body.pipeline,
      pre: body.pipeline.pre.map(item => ({ ...item, displayName: longNames.get(item.id) ?? item.displayName })),
      core: body.pipeline.core.map(item => ({ ...item, displayName: longNames.get(item.id) ?? item.displayName })),
      post: body.pipeline.post.map(item => ({ ...item, displayName: longNames.get(item.id) ?? item.displayName })),
      allSteps: body.pipeline.allSteps.map(item => ({ ...item, displayName: longNames.get(item.id) ?? item.displayName })),
    },
    execution: {
      ...body.execution,
      completedAt: '2026-06-02T08:06:19Z',
      steps: body.execution.steps.map(item => item.stepId === 'pre-loop-guard'
        ? {
            ...item,
            status: 'failed',
            verdict: 'ERROR',
            reason: 'The workspace validation failed.',
          }
        : item.stepId === 'core-agent-run'
          ? {
              ...item,
              status: 'failed',
              verdict: 'needsinput',
              reason: 'The operator must choose the primary column.',
              completedAt: '2026-06-02T08:06:19Z',
              durationMs: 378_000,
            }
          : item.stepId === 'aspect-requirement-fit'
            ? {
                ...item,
                status: 'skipped',
                verdict: 'skipped',
                reason: 'The core run needs operator input before review can start.',
              }
            : item),
    },
    config: {
      'aspect-requirement-fit': {
        enabled: true,
        canDisable: true,
        activation: {
          state: 'active',
          source: 'global',
          reason: 'Enabled by the global catalogue default.',
        },
      },
    },
  };
}

function runRecord(index: number, intent: string, startedAt: string) {
  return {
    index, intent, startedAt, endedAt: null, status: 'completed', cli: 'claude',
    exitCode: 0, durationSeconds: 30, inputSessionId: null, capturedSessionId: null,
    resumed: intent !== 'start', reason: null, userFollowup: null, lineStart: null,
    lineEnd: null, headShaBefore: null, headShaAfter: null, contextRef: null,
  };
}

// A long-running task with many CLI invocations. runCount is the canonical
// count (mirrors the Overview "Runs" value); the runs list is a representative
// sample the popover folds into recovered/first/last detail.
function multiRunTimeline() {
  return {
    runCount: 34,
    firstStartedAt: '2026-06-02T08:00:01Z',
    lastActivityAt: '2026-06-03T09:30:00Z',
    hasActiveRun: true,
    runs: [
      runRecord(0, 'start', '2026-06-02T08:00:01Z'),
      runRecord(1, 'continue', '2026-06-02T08:40:00Z'),
      runRecord(2, 'recovery', '2026-06-02T09:10:00Z'),
      runRecord(3, 'continue', '2026-06-03T09:30:00Z'),
    ],
  };
}

const EMPTY_TIMELINE = { runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [] };

async function installRoutes(
  page: Page,
  state: string,
  timelineBody: unknown,
  pipelineResponse: unknown = pipelineBody(),
) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
      .catch(() => { /* a more specific route already handled the request */ });
  });
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }),
  );
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [], ready: [],
        progress: [], failedPickup: [], autoReview: [], humanReview: [],
        completed: [], archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
      ]),
    }),
  );
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    }),
  );
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-06-02T00:00:00Z', snapshots: [] }),
    }),
  );
  await page.route('**/api/projects/*/workbenches**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projectName: PROJECT, includesHistory: true, count: 0, items: [] }),
    }),
  );
  await page.route('**/api/tasks/*/agent-work**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  );
  await page.route('**/api/tasks/*/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/tasks/*/claude-session**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  );
  await page.route('**/api/tasks/*/screenshots**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT, mode: 'auto', activeJobId: JOB_ID,
            activeExecution: null, queuedJobIds: [],
          },
        },
      }),
    }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(timelineBody) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/agent-work-summary(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        calls: 34, recovered: true, toolCalls: 210,
        toolCounts: [{ tool: 'Edit', count: 80 }, { tool: 'Read', count: 70 }],
        startedAt: '2026-06-02T08:00:01Z', lastTouchAt: '2026-06-03T09:30:00Z',
        currentSessionId: '0c1e3817-91c2-43a1-a1aa-9f73d161d4a2',
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(pipelineResponse) }),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 })
      .catch(() => { /* the overlay may already be detached */ });
  }
}

async function openDetail(page: Page): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissErrorDialog(page);
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

test.describe('Pipeline Agent-execution run count + details popover', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: false, git: false }),
        );
      } catch {
        /* private mode */
      }
    });
  });

  test('core row shows the run count and a focus/hover popover with run details', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 560 });
    await installRoutes(page, '3-progress', multiRunTimeline());
    await openDetail(page);

    // The count badge lives on the CORE row, not the Pre/aspect rows.
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    const badge = coreRow.getByTestId('overview-pipeline-agent-runs');
    await expect(badge).toBeVisible();
    await expect(badge).toHaveText('34 runs');
    await expect(badge).toHaveAttribute('data-run-count', '34');
    await expect(badge).toHaveAttribute('aria-haspopup', 'dialog');

    // Exactly one badge across the whole pipeline (core only).
    await expect(page.getByTestId('overview-pipeline-agent-runs')).toHaveCount(1);

    // Keyboard accessibility: focus (not mouse) must open the popover.
    await badge.focus();
    const tip = page.getByTestId('cac-tooltip');
    await expect(tip).toBeVisible();
    await expect(tip).toContainText('34 runs');
    await expect(tip).toContainText('Runs: 34');
    await expect(tip).toContainText('Recovered: 1');
    await expect(tip).toContainText('Model: claude-opus-4-8');
    await expect(tip).toContainText('See the Timeline tab');

    // The popover stays inside the viewport (does not clip out of the pane).
    const box = await tip.boundingBox();
    const vp = page.viewportSize();
    expect(box).not.toBeNull();
    expect(vp).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(vp!.width + 1);
    expect(box!.y + box!.height).toBeLessThanOrEqual(vp!.height + 1);

    // The Overview pane itself is scrollable at this height; the tooltip is
    // fixed to the viewport, so it stays readable while the pane scrolls.
    const promptBody = page.getByTestId('pane-prompt').locator('.pane__body');
    await expect.poll(async () =>
      promptBody.evaluate(el => el.scrollHeight > el.clientHeight),
    ).toBe(true);
    await promptBody.evaluate((el) => {
      el.scrollTop = Math.min(32, el.scrollHeight - el.clientHeight);
    });
    await expect(tip).toBeVisible();
    const scrolledBox = await tip.boundingBox();
    expect(scrolledBox).not.toBeNull();
    expect(scrolledBox!.x).toBeGreaterThanOrEqual(0);
    expect(scrolledBox!.y).toBeGreaterThanOrEqual(0);
    expect(scrolledBox!.x + scrolledBox!.width).toBeLessThanOrEqual(vp!.width + 1);
    expect(scrolledBox!.y + scrolledBox!.height).toBeLessThanOrEqual(vp!.height + 1);

    // Blur hides it; hover (mouse) re-opens it — both affordances work.
    await page.locator('body').click({ position: { x: 2, y: 2 } });
    await expect(tip).toBeHidden();
    await badge.hover();
    await expect(page.getByTestId('cac-tooltip')).toBeVisible();

    // Multiple invocations are actionable: clicking the count opens the full
    // chronological run history instead of leaving the history in a tooltip.
    await badge.click();
    await expect(badge).toHaveAttribute('aria-expanded', 'true');
    const history = page.getByTestId('overview-pipeline-agent-run-history');
    await expect(history).toBeVisible();
    await expect(history).toContainText('Chronological CLI invocation history');
    await expect(history.getByTestId('run-timeline-card')).toHaveCount(4);
    await expect(history).toContainText('Run #0');
    await expect(history).toContainText('recovery');
  });

  test('keeps names dominant and metadata inline in crowded pipeline rows', async ({ page }) => {
    const allRuns = multiRunTimeline();
    const timeline = { ...allRuns, runCount: 2, runs: allRuns.runs.slice(0, 2) };
    await page.setViewportSize({ width: 1280, height: 560 });
    await installRoutes(page, '3-progress', timeline, rowAnatomyPipelineBody());
    await openDetail(page);

    for (const label of ['PRE STEPS', 'CORE AGENT WORK', 'ASPECT']) {
      const phase = page.getByTestId('overview-pipeline-phase').filter({ hasText: label });
      if (await phase.getAttribute('aria-expanded') === 'false') await phase.click();
    }

    const preRow = page.locator('[data-step-id="pre-loop-guard"]');
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    const aspectRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    await expect(preRow.getByTestId('overview-pipeline-step-verdict')).toHaveCount(0);
    await expect(preRow.getByTestId('overview-pipeline-step-status')).toHaveAttribute('aria-label', 'Failed');
    await expect(coreRow.getByTestId('overview-pipeline-agent-runs')).toHaveText('2 runs');
    await expect(coreRow.getByTestId('overview-pipeline-step-verdict')).toHaveText('needsinput');
    await expect(aspectRow.getByTestId('overview-pipeline-step-verdict')).toHaveCount(0);
    await expect(aspectRow.getByTestId('overview-post-step-source')).toHaveText('G');
    await expect(aspectRow.getByTestId('overview-post-step-source')).toHaveAttribute(
      'aria-label',
      'active from global: Enabled by the global catalogue default. Open settings.',
    );

    const geometry = await coreRow.evaluate((row) => {
      const rect = (testId: string) => {
        const element = row.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
        if (!element) throw new Error(`Missing ${testId}`);
        const bounds = element.getBoundingClientRect();
        return { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom, width: bounds.width };
      };
      const rowBounds = row.getBoundingClientRect();
      const ordered = [
        rect('overview-pipeline-step-name-cell'),
        rect('overview-pipeline-step-verdict'),
        rect('overview-pipeline-agent-runs'),
        rect('overview-pipeline-step-model'),
        rect('overview-pipeline-step-timing'),
      ];
      const inline = ordered.every(part =>
        part.top >= rowBounds.top - 1 && part.bottom <= rowBounds.bottom + 1);
      const centers = ordered.map(part => (part.top + part.bottom) / 2);
      const noOverlap = ordered.slice(0, -1).every((part, index) => part.right <= ordered[index + 1].left);
      const started = rect('overview-pipeline-step-started');
      const duration = rect('overview-pipeline-step-duration');
      return {
        inline,
        verticallyAligned: Math.max(...centers) - Math.min(...centers) <= 2,
        noOverlap,
        timingSeparated: started.right <= duration.left,
        noHorizontalOverflow: row.scrollWidth <= row.clientWidth + 1,
        nameWidth: ordered[0].width,
      };
    });
    expect(geometry).toMatchObject({
      inline: true,
      verticallyAligned: true,
      noOverlap: true,
      timingSeparated: true,
      noHorizontalOverflow: true,
    });
    expect(geometry.nameWidth).toBeGreaterThan(192);

    const aspectName = 'Review every acceptance criterion and preserve complete evidence';
    await expect(aspectRow.getByTestId('overview-pipeline-step-name')).toHaveText(aspectName);
    const aspectOrder = await aspectRow.evaluate((row) => {
      const name = row.querySelector<HTMLElement>('[data-testid="overview-pipeline-step-name-cell"]')!.getBoundingClientRect();
      const scope = row.querySelector<HTMLElement>('[data-testid="overview-post-step-source"]')!.getBoundingClientRect();
      const timing = row.querySelector<HTMLElement>('[data-testid="overview-pipeline-step-timing"]')!.getBoundingClientRect();
      return { nameBeforeScope: name.right <= scope.left, scopeBeforeTiming: scope.right <= timing.left };
    });
    expect(aspectOrder).toEqual({ nameBeforeScope: true, scopeBeforeTiming: true });
    await aspectRow.getByTestId('overview-pipeline-step-name').hover();
    await expect(page.getByTestId('cac-tooltip').locator('.cac-tooltip__title')).toHaveText(aspectName);
    await page.mouse.move(0, 0);
    await expect(page.getByTestId('cac-tooltip')).toBeHidden();

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('overview-pipeline').screenshot({
        path: path.join(RESULTS_DIR, `pipeline-step-rows-after-${theme}--mocked.png`),
      });
    }
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`run-count badge + popover render in the ${theme} theme`, async ({ page }) => {
      await installRoutes(page, '3-progress', multiRunTimeline());
      await openDetail(page);
      await setTheme(page, theme);

      const badge = page.locator('[data-step-id="core-agent-run"]').getByTestId('overview-pipeline-agent-runs');
      await expect(badge).toBeVisible();
      await expect(badge).toHaveText('34 runs');

      await badge.focus();
      const tip = page.getByTestId('cac-tooltip');
      await expect(tip).toBeVisible();
      await expect(tip).toContainText('Runs: 34');

      if (RESULTS_DIR) {
        await page.screenshot({
          path: path.join(RESULTS_DIR, `pipeline-agent-run-count-${theme}.png`),
          fullPage: true,
        });
      }
    });
  }

  test('a task with no runs keeps the dash state — no run-count badge', async ({ page }) => {
    await installRoutes(page, '2-ready', EMPTY_TIMELINE);
    // No prior runs: the agent-work summary is empty too.
    await page.route(new RegExp(`/api/tasks/${JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/agent-work-summary(\\?|$)`), (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ calls: 0, recovered: false, toolCalls: 0, toolCounts: [], startedAt: null, lastTouchAt: null, currentSessionId: null }),
      }),
    );
    await openDetail(page);

    await expect(page.getByTestId('overview-pipeline-agent-runs')).toHaveCount(0);
    // The core row still renders (with its existing dash cells), it just
    // carries no run-count badge.
    await expect(page.locator('[data-step-id="core-agent-run"]')).toBeVisible();
  });
});
