import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3565: Review results on a research card whose run finished without a
 * report review.
 *
 * The lightweight report pipeline stopped after a failed summary step, so the
 * requirement-fit row never ran and completion had no content verdict. The
 * backend lists that step in `onDemand.runnableStepIds`; the overview row
 * offers Review results, which runs only that review, and the row shows the
 * verdict afterwards. A row the backend does not list offers no run.
 *
 * Fully mocked: the pipeline response and the review-results call are stubbed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/research-review-results';
const JOB_ID = 'research-review-results-test';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function makeDetail() {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Research the lease renewal interval',
      state: '5-human-review',
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5.4',
      mode: 'research',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Report the lease renewal interval.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

function step(id: string, displayName: string, kind: string, dependsOn: string[] = []) {
  return { id, displayName, kind, runMode: 'sequential', dependsOn, idempotent: true, stub: false };
}

const pre = [step('pre-loop-guard', 'Loop check', 'module')];
const core = [step('core-agent-run', 'Agent execution', 'core')];
const post = [
  step('post-summary-assessment', 'Summary assessment', 'module', ['core-agent-run']),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', ['post-summary-assessment']),
  step('post-orchestrator-review', 'Orchestrator review', 'module', ['aspect-requirement-fit']),
  step('post-orchestrator-decision', 'Orchestrator decision', 'module', ['post-orchestrator-review']),
];

function record(stepId: string, kind: string, status: string, extra: Record<string, unknown> = {}) {
  const ran = status !== 'pending';
  return {
    stepId, kind, status, durationMs: ran ? 1_200 : 0,
    inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
    startedAt: ran ? '2026-11-08T16:00:00Z' : null,
    completedAt: ran ? '2026-11-08T16:00:01Z' : null,
    ...extra,
  };
}

function pipelineBody(reviewed: boolean) {
  return {
    pipeline: {
      id: 'read-only-task-pipeline',
      displayName: 'Lightweight report pipeline',
      version: 1,
      pre, core, post,
      allSteps: [...pre, ...core, ...post],
    },
    execution: {
      pipelineId: 'read-only-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-11-08T15:40:00Z',
      completedAt: '2026-11-08T16:00:01Z',
      steps: [
        record('pre-loop-guard', 'module', 'passed'),
        record('core-agent-run', 'core', 'passed'),
        record('post-summary-assessment', 'module', 'failed', {
          reason: 'The summary model returned no assessment.',
        }),
        reviewed
          ? record('aspect-requirement-fit', 'aspect', 'passed', {
              verdict: 'pass',
              reason: 'Requirement fit of the report: pass. The report names the renewal interval.',
            })
          : record('aspect-requirement-fit', 'aspect', 'pending'),
        record('post-orchestrator-review', 'module', 'pending'),
        record('post-orchestrator-decision', 'module', 'pending'),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
    onDemand: {
      plannedStepIds: [],
      attempts: [],
      runnableStepIds: reviewed ? [] : ['aspect-requirement-fit'],
    },
  };
}

async function installRoutes(page: Page, state: { reviewed: boolean; reviewCalls: number }) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
      .catch(() => { /* a more specific route already handled the request */ });
  });
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill(json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null })));
  await page.route('**/api/tasks', (route) => route.fulfill(json([])));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, (route) => route.fulfill(json([])));
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, (route) => route.fulfill(json([])));
  await page.route('**/api/git/summary**', (route) => route.fulfill(json([])));
  await page.route('**/api/agent-rules**', (route) => route.fulfill(json([])));
  await page.route('**/api/clients', (route) => route.fulfill(json([])));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill(json({
      preparation: [], orchestratorPrep: [], ready: [],
      progress: [], failedPickup: [], autoReview: [], humanReview: [],
      completed: [], archive: [],
    })));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill(json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }])));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) => route.fulfill(json({})));
  await page.route('**/api/environment**', (route) =>
    route.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route('**/api/usage/cockpit**', (route) =>
    route.fulfill(json({
      snapshotVersion: 1,
      workspaceId: PROJECT,
      timeZone: 'UTC',
      weekStart: 1,
      generatedAt: '2026-11-09T00:00:00Z',
      calendar: { timeZone: 'UTC', weekStart: 1 },
      clis: [],
      cost: { currency: 'USD', todayUsd: null, weekUsd: null, projects: [],
        coverage: { status: 'unavailable', observedAt: null, ttlSeconds: null } },
      runs: [],
      slots: [],
      sources: {},
    })));
  await page.route('**/api/cli/usage**', (route) => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill(json({ at: '2026-11-09T00:00:00Z', snapshots: [] })));
  await page.route('**/api/projects/*/workbenches**', (route) =>
    route.fulfill(json({ projectName: PROJECT, includesHistory: true, count: 0, items: [] })));
  await page.route('**/api/tasks/*/agent-work**', (route) => route.fulfill(json(null)));
  await page.route('**/api/tasks/*/claude-session**', (route) => route.fulfill(json(null)));
  await page.route('**/api/tasks/*/timeline**', (route) => route.fulfill(json([])));
  await page.route('**/api/tasks/*/screenshots**', (route) => route.fulfill(json([])));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill(json({
      projects: {
        [PROJECT]: { projectName: PROJECT, mode: 'auto', activeJobId: null, activeExecution: null, queuedJobIds: [] },
      },
    })));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill(json({ runCount: 1, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [] })));
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${idEsc}/agent-work-summary(\\?|$)`), (route) =>
    route.fulfill(json({
      calls: 1, recovered: false, toolCalls: 0, toolCounts: [],
      startedAt: '2026-11-08T15:40:00Z', lastTouchAt: '2026-11-08T16:00:01Z', currentSessionId: null,
    })));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill(json(pipelineBody(state.reviewed))));
  await page.route(new RegExp(`/api/tasks/${idEsc}/review-results(\\?|$)`), (route) => {
    state.reviewCalls += 1;
    state.reviewed = true;
    return route.fulfill(json({
      stepId: 'aspect-requirement-fit',
      ran: true,
      status: 'pass',
      summary: 'Requirement fit of the report: pass. The report names the renewal interval.',
    }));
  });
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill(json(makeDetail())));
}

async function openDetail(page: Page): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  // No runtime error may surface: an error dialog would cover the controls.
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  const phases = page.getByTestId('overview-pipeline-phase');
  for (let index = 0; index < await phases.count(); index++) {
    const phase = phases.nth(index);
    if (await phase.getAttribute('aria-expanded') === 'false') await phase.click();
  }
}

test.describe('Research card Review results', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
      } catch {
        /* private mode */
      }
    });
  });

  test('runs only the missing requirement-fit review and shows its verdict', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    const state = { reviewed: false, reviewCalls: 0 };
    await installRoutes(page, state);
    await openDetail(page);

    const fitRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    const button = fitRow.getByTestId('overview-post-step-review-results');
    await expect(button).toBeVisible();
    await expect(button).toHaveText('Review results');
    // Only the listed missing review is runnable; no other row offers a run.
    await expect(page.getByTestId('overview-post-step-review-results')).toHaveCount(1);
    await expect(page.getByTestId('overview-post-step-run')).toHaveCount(0);

    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      if (RESULTS_DIR) {
        await page.getByTestId('overview-pipeline').screenshot({
          path: path.join(RESULTS_DIR, `research-review-results-before-${theme}.png`),
        });
      }
    }

    await button.click();
    await expect.poll(() => state.reviewCalls).toBe(1);
    await expect(fitRow.getByTestId('overview-post-step-review-results')).toHaveCount(0);
    await expect(fitRow.getByTestId('overview-pipeline-step-status')).toHaveAttribute('aria-label', 'Passed');

    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      if (RESULTS_DIR) {
        await page.getByTestId('overview-pipeline').screenshot({
          path: path.join(RESULTS_DIR, `research-review-results-after-${theme}.png`),
        });
      }
    }
    expect(state.reviewCalls).toBe(1);
  });
});
