import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Ralph-loop early detection in the pipeline table.
 *
 * Acceptance ("Pipeline: Fruehe Loop-Erkennung (Ralph-Loop) + in der Tabelle
 * sichtbar"): the auto-mode loop guard (StuckLoopGuard) must be checked early
 * and a detected loop must be visible in the step table. The backend records a
 * `pre-loop-guard` step in `pipeline-execution.json`: Passed with no verdict
 * while healthy, Passed/`looping` while a loop builds under budget, and
 * Failed/`loop-detected` when the circuit-breaker fires. It is the first
 * (Pre) row, so a detected loop is marked early - before the core run and the
 * aspect verdicts.
 *
 * Fully mocked - no backend or git repository needed; the pipeline block is
 * generic and renders one row per step from the joined catalogue + execution.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-loop-guard';
const JOB_ID = 'pipeline-loop-guard-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Loop guard fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
      sessionName: null,
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

const pre = [step('pre-loop-guard', 'Loop check', 'module')];
const core = [step('core-agent-run', 'Agent execution', 'core')];
const post = [step('aspect-requirement-fit', 'Requirement fit', 'aspect')];
const allSteps = [...pre, ...core, ...post];

function basePipeline() {
  return {
    id: 'standard-task-pipeline',
    displayName: 'Standard task pipeline',
    version: 1,
    pre,
    core,
    post,
    allSteps,
  };
}

function execStep(stepId: string, kind: string, status: string, extra: Record<string, unknown> = {}) {
  return {
    stepId,
    kind,
    status,
    durationMs: 0,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: status === 'pending' ? null : '2026-06-02T08:00:00Z',
    completedAt: status === 'running' || status === 'pending' ? null : '2026-06-02T08:00:02Z',
    ...extra,
  };
}

// The auto-mode Ralph-loop tripped the circuit-breaker: the loop guard is the
// first row and reads as a hard stop with a `loop-detected` verdict.
function pipelineLoopDetected() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: null,
      steps: [
        execStep('pre-loop-guard', 'module', 'failed', {
          verdict: 'loop-detected',
          verdictSummary:
            'Auto-loop circuit-breaker fired after 5/5 iterations (orchestrator tokens 200000/200000). Loop stopped to preserve quota; awaiting user.',
        }),
        execStep('core-agent-run', 'core', 'passed'),
        execStep('aspect-requirement-fit', 'aspect', 'pending'),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
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
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'auto',
            activeJobId: JOB_ID,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(pipelineBody()),
    }),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

test.describe('Pipeline loop guard (Ralph-loop early detection)', () => {
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

  test('a detected loop is marked early as the first row and shown in the step table', async ({ page }) => {
    await installRoutes(page, '3-progress', pipelineLoopDetected);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    // The loop guard is the FIRST row ("frueh markiert") and is a Pre step;
    // its compact kind marker reads PRE (full name in the tooltip).
    const firstRow = page.getByTestId('overview-pipeline-step').first();
    await expect(firstRow).toHaveAttribute('data-step-id', 'pre-loop-guard');
    await expect(firstRow).toContainText('PRE');
    await expect(firstRow).toContainText('Loop check');

    // The detected loop reads as a hard stop with a `loop-detected` verdict pill.
    const guardRow = page.locator('[data-step-id="pre-loop-guard"]');
    await expect(guardRow).toHaveAttribute('data-status', 'failed');
    const verdict = guardRow.getByTestId('overview-pipeline-step-verdict');
    await expect(verdict).toHaveAttribute('data-verdict', 'loop-detected');
    await expect(verdict).toContainText('loop-detected');

    if (RESULTS_DIR) {
      await guardRow.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-loop-detected.png'),
        fullPage: true,
      });
    }
  });
});
