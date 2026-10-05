import { test, expect, type Page } from '@playwright/test';
import * as path from 'path';

/**
 * Pipeline workbench state evidence (ASS-1914).
 *
 * Renders the Overview pipeline in each of the settled workbench states — Empty,
 * Running, Blocked, Done, and an all-collapsed Done — and captures a labelled
 * `--mocked` screenshot of each into the job results folder. Fully mocked (no
 * backend / git), so it doubles as a light smoke check that every state renders
 * without a runtime error.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-workbench-evidence';
const JOB_ID = 'pipeline-workbench-evidence';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Pipeline workbench state evidence',
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

function step(id: string, displayName: string, kind: string, runMode: string, extra: Record<string, unknown> = {}) {
  return { id, displayName, kind, runMode, dependsOn: [], idempotent: true, stub: false, ...extra };
}

// A realistic configured shape: pre / core / two aspects / tool / decision / drift.
const pre = [
  step('pre-loop-guard', 'Loop guard', 'module', 'sequential'),
  step('pre-model-qualification', 'Model qualification', 'module', 'sequential'),
  step('pre-reissue-open-items', 'Reissue open-items check', 'module', 'sequential'),
];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('post-git-commit-attribution', 'Git attribution', 'tool', 'sequential'),
  step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
  step('post-drift-adr-code', 'ADR drift', 'drift', 'sequential'),
];
const allSteps = [...pre, ...core, ...post];

function basePipeline() {
  return { id: 'standard-task-pipeline', displayName: 'Standard task pipeline', version: 1, pre, core, post, allSteps };
}

function execStep(stepId: string, kind: string, status: string, extra: Record<string, unknown> = {}) {
  const ran = status === 'passed' || status === 'failed';
  return {
    stepId,
    kind,
    status,
    model: kind === 'core' ? 'claude-opus-4-7' : 'claude-haiku-4-5',
    durationMs: status === 'running' ? 0 : ran ? 92_000 : 0,
    inputTokens: ran ? 8_000 : 0,
    outputTokens: ran ? 2_000 : 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: status === 'pending' ? null : '2026-06-02T08:00:00Z',
    completedAt: ran ? '2026-06-02T08:01:32Z' : null,
    ...extra,
  };
}

function costStep(stepId: string, totalTokens: number, costUsd: number) {
  return {
    stepId, model: 'claude-haiku-4-5', modelKnown: true, tokenUsageSource: 'orchestrator',
    inputTokens: Math.round(totalTokens * 0.8), outputTokens: Math.round(totalTokens * 0.2),
    cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens,
    inputCostUsd: costUsd * 0.6, outputCostUsd: costUsd * 0.4, cacheReadCostUsd: 0, cacheCreationCostUsd: 0, costUsd,
  };
}

const emptyCost = {
  steps: [], totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
  totalTokens: 0, totalInputCostUsd: 0, totalOutputCostUsd: 0, totalCacheReadCostUsd: 0, totalCacheCreationCostUsd: 0,
  totalCostUsd: 0, anyModelUnknown: false,
};

// EMPTY: configured pipeline, nothing has run.
function pipelineEmpty() {
  return {
    pipeline: basePipeline(), execution: null, cost: emptyCost,
    config: { 'pre-model-qualification': { enabled: true, canDisable: true } },
  };
}

// RUNNING: core in flight, everything after it still pending.
function pipelineRunning() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: JOB_ID, project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z', completedAt: null, attempt: 1, previousAttempts: [],
      steps: [
        execStep('pre-loop-guard', 'module', 'passed'),
        execStep('core-agent-run', 'core', 'running'),
        execStep('aspect-requirement-fit', 'aspect', 'pending'),
        execStep('aspect-code-quality', 'aspect', 'pending'),
        execStep('post-git-commit-attribution', 'tool', 'pending'),
        execStep('post-orchestrator-decision', 'orchestrator', 'pending'),
        execStep('post-drift-adr-code', 'drift', 'pending'),
      ],
    },
    cost: { ...emptyCost, steps: [costStep('core-agent-run', 12_400, 0.9)], totalTokens: 12_400, totalCostUsd: 0.9 },
    config: {},
  };
}

// BLOCKED: a re-issued run whose core failed and whose final ruling escalates,
// with an aspect concern flagged — the danger states the operator must act on.
function pipelineBlocked() {
  const priorRun = {
    pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: JOB_ID, project: PROJECT,
    startedAt: '2026-06-02T07:00:00Z', completedAt: '2026-06-02T07:20:00Z', attempt: 1, previousAttempts: [],
    steps: [execStep('core-agent-run', 'core', 'passed'), execStep('aspect-code-quality', 'aspect', 'failed')],
  };
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: JOB_ID, project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z', completedAt: null, attempt: 2, previousAttempts: [priorRun],
      steps: [
        execStep('pre-loop-guard', 'module', 'passed'),
        execStep('pre-reissue-open-items', 'module', 'failed', {
          verdict: 'escalate',
          verdictSummary: '2 open item(s): browser evidence is missing; integration test is still failing',
        }),
        execStep('core-agent-run', 'core', 'failed'),
        execStep('aspect-requirement-fit', 'aspect', 'passed', { verdict: 'concerns', verdictSummary: 'Integration test evidence is missing for the changed export path.' }),
        execStep('aspect-code-quality', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('post-git-commit-attribution', 'tool', 'passed'),
        execStep('post-orchestrator-decision', 'orchestrator', 'failed', { verdict: 'escalate' }),
        execStep('post-drift-adr-code', 'drift', 'pending'),
      ],
    },
    cost: {
      ...emptyCost,
      steps: [costStep('core-agent-run', 47_600, 3.1), costStep('aspect-requirement-fit', 8_000, 0.012)],
      totalTokens: 55_600, totalCostUsd: 3.11,
    },
    config: { 'pre-model-qualification': { enabled: true, canDisable: true } },
  };
}

// DONE: a multi-run task that finished; every executed section reads ok, the
// final ruling accepts, drift stays disabled.
function pipelineDone() {
  const prior = (attempt: number) => ({
    pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: JOB_ID, project: PROJECT,
    startedAt: '2026-06-02T06:00:00Z', completedAt: '2026-06-02T06:30:00Z', attempt, previousAttempts: [],
    steps: [execStep('core-agent-run', 'core', 'passed'), execStep('post-orchestrator-decision', 'orchestrator', 'passed', { verdict: 'reissue' })],
  });
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1, jobId: JOB_ID, project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z', completedAt: '2026-06-02T08:20:00Z', attempt: 3,
      previousAttempts: [prior(2), prior(1)],
      steps: [
        execStep('pre-loop-guard', 'module', 'passed'),
        execStep('core-agent-run', 'core', 'passed'),
        execStep('aspect-requirement-fit', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('aspect-code-quality', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('post-git-commit-attribution', 'tool', 'passed'),
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', { verdict: 'accept' }),
      ],
    },
    cost: {
      ...emptyCost,
      steps: [
        costStep('pre-loop-guard', 1_200, 0.0021),
        costStep('core-agent-run', 248_000, 4.37),
        costStep('aspect-requirement-fit', 8_000, 0.012),
        costStep('aspect-code-quality', 9_400, 0.014),
        costStep('post-git-commit-attribution', 800, 0.001),
        costStep('post-orchestrator-decision', 5_400, 0.0089),
      ],
      totalTokens: 272_800, totalCostUsd: 4.41,
    },
    // ADR drift stays switched off in project config -> a muted, collapsed section.
    config: {
      'pre-model-qualification': { enabled: true, canDisable: true },
      'post-drift-adr-code': { enabled: false, model: null, mode: null },
    },
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], autoReview: [], humanReview: [], completed: [], archive: [] }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]) }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }),
  );
  // The header quota chip reads `{ snapshots: [] }`; without a real mock the
  // generic `[]` catch-all makes it crash into a global error dialog.
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-02T00:00:00Z', snapshots: [] }) }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'auto', activeJobId: JOB_ID, activeExecution: null, queuedJobIds: [] } } }) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(pipelineBody()) }),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]')?.click());
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

async function expandAllPipelineSections(page: Page): Promise<void> {
  const collapsed = page.locator('[data-testid="overview-pipeline-phase"][aria-expanded="false"]');
  for (let i = 0; i < 20; i++) {
    if ((await collapsed.count()) === 0) break;
    await collapsed.first().click();
  }
}

async function collapseAllPipelineSections(page: Page): Promise<void> {
  const expanded = page.locator('[data-testid="overview-pipeline-phase"][aria-expanded="true"]');
  for (let i = 0; i < 20; i++) {
    if ((await expanded.count()) === 0) break;
    await expanded.first().click();
  }
}

async function load(page: Page, state: string, body: () => unknown) {
  await installRoutes(page, state, body);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissErrorDialog(page);
  const pipeline = page.getByTestId('overview-pipeline');
  await expect(pipeline).toBeVisible({ timeout: 10_000 });
  return pipeline;
}

async function shot(page: Page, pipeline: ReturnType<Page['getByTestId']>, name: string) {
  if (!RESULTS_DIR) return;
  await pipeline.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(RESULTS_DIR, name), fullPage: true });
}

test.describe('Pipeline workbench state evidence', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try { localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false })); } catch { /* private mode */ }
    });
  });

  test('empty: configured pipeline, nothing run, every section collapsed neutral', async ({ page }) => {
    const pipeline = await load(page, '2-ready', pipelineEmpty);
    const phases = page.getByTestId('overview-pipeline-phase');
    await expect(phases).toHaveCount(6);
    // Nothing has run -> every section defaults collapsed and shows no step rows.
    const expanded = page.locator('[data-testid="overview-pipeline-phase"][aria-expanded="true"]');
    await expect(expanded).toHaveCount(0);
    await expect(page.getByTestId('overview-pipeline-step')).toHaveCount(0);
    await expandAllPipelineSections(page);
    await expect(page.getByTestId('pipeline-step-toggle-pre-model-qualification')).toBeVisible();
    await shot(page, pipeline, 'pipeline-state-empty--mocked.png');
  });

  test('running: core in flight, live section open, quiet finished section collapsed', async ({ page }) => {
    const pipeline = await load(page, '3-progress', pipelineRunning);
    const coreHeader = page.locator('[data-testid="overview-pipeline-phase"][data-phase="core"]');
    await expect(coreHeader).toHaveAttribute('data-tone', 'warn');
    await expect(coreHeader).toHaveAttribute('aria-expanded', 'true');
    await expect(page.locator('[data-step-id="core-agent-run"]')).toHaveAttribute('data-status', 'running');
    await shot(page, pipeline, 'pipeline-state-running--mocked.png');
  });

  test('blocked: failed core, aspect concern, escalating final ruling, repeated run', async ({ page }) => {
    const pipeline = await load(page, '3-progress', pipelineBlocked);
    // The run band shows this is a second attempt.
    await expect(page.getByTestId('overview-pipeline-run-switcher')).toBeVisible();
    const coreHeader = page.locator('[data-testid="overview-pipeline-phase"][data-phase="core"]');
    await expect(coreHeader).toHaveAttribute('data-tone', 'danger');
    await expandAllPipelineSections(page);
    await expect(page.locator('[data-step-id="core-agent-run"]')).toHaveAttribute('data-status', 'failed');
    // The aspect concern is surfaced on its row and rolled up on its section header.
    await expect(page.locator('[data-testid="overview-pipeline-phase"][data-phase="aspect"] [data-testid="overview-pipeline-phase-concern"]')).toBeVisible();
    // The failed open-items guard explains exactly why it escalated in its
    // details dialog instead of leaving the operator with only a red cross.
    await page.locator('[data-step-id="pre-reissue-open-items"]')
      .getByTestId('overview-pipeline-step-details').click();
    const detail = page.getByTestId('overview-pipeline-step-concerns-detail');
    await expect(detail).toContainText('Escalation reason');
    await expect(detail).toContainText('browser evidence is missing');
    await page.getByTestId('overview-pipeline-step-details-dialog').getByRole('button', { name: 'Close' }).click();
    await shot(page, pipeline, 'pipeline-state-blocked--mocked.png');
  });

  test('done: multiple runs, all sections passed, drift disabled + collapsed', async ({ page }) => {
    const pipeline = await load(page, '5-human-review', pipelineDone);
    await expect(page.getByTestId('overview-pipeline-run-switcher')).toBeVisible();
    await expandAllPipelineSections(page);
    // Every executable section reads ok; the disabled drift section reads muted.
    await expect(page.locator('[data-testid="overview-pipeline-phase"][data-phase="core"]')).toHaveAttribute('data-tone', 'ok');
    await expect(page.locator('[data-testid="overview-pipeline-phase"][data-phase="drift"]')).toHaveAttribute('data-tone', 'muted');
    // A pending project-level switch is no longer actionable from a task that
    // has already reached human review.
    await expect(page.getByTestId('pipeline-step-toggle-pre-model-qualification')).toHaveCount(0);
    await shot(page, pipeline, 'pipeline-state-done--mocked.png');
  });

  test('collapsed groups: a finished run collapsed keeps its aggregate tone', async ({ page }) => {
    const pipeline = await load(page, '5-human-review', pipelineDone);
    await collapseAllPipelineSections(page);
    // No step rows are shown, yet every header still carries its tone + status word.
    await expect(page.getByTestId('overview-pipeline-step')).toHaveCount(0);
    await expect(page.locator('[data-testid="overview-pipeline-phase"][data-phase="core"] .ov-pl-phase__status')).toHaveText('Passed');
    await expect(page.locator('[data-testid="overview-pipeline-phase"][data-phase="drift"] .ov-pl-phase__status')).toHaveText('Disabled');
    await shot(page, pipeline, 'pipeline-state-collapsed-groups--mocked.png');
  });
});
