import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Pre-run model visibility + editing (hierarchical per-step model config).
 *
 * Before any run has recorded a model, each LLM-backed pipeline step shows the
 * effective model it WILL run on, resolved by the backend the same way the
 * runtime resolves it (step override -> project model -> global -> catalogue ->
 * runtime default). The resolved chip renders with `data-model-resolved="true"`
 * and a dashed "will run on" style so the operator can see, and reason about,
 * the model hierarchy ahead of the run.
 *
 * The aspect review rows additionally expose an inline model selector that
 * writes a project-level per-step override via PUT /pipeline-step and re-
 * resolves the pipeline, so the operator can change the model a step will run
 * on, in context, before starting — the second test exercises that loop.
 *
 * Fully mocked - no backend or git repository needed. `execution` is null
 * (nothing has run yet), so the only model source is the resolved config.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-prerun-model';
const JOB_ID = 'pipeline-prerun-model-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Pre-run model fixture',
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

function step(id: string, displayName: string, kind: string, runMode: string) {
  return { id, displayName, kind, runMode, dependsOn: [], idempotent: true, stub: false };
}

const pre = [
  step('pre-loop-guard', 'Loop guard', 'module', 'sequential'),
  step('pre-model-qualification', 'Model qualification', 'module', 'sequential'),
];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
];
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

/**
 * Config block exactly as the backend emits it pre-run: the two aspect steps
 * resolve a model (one from the project override, one with a per-step override),
 * deterministic / core steps resolve none.
 */
function prerunConfig(): Record<string, Record<string, unknown>> {
  return {
    'aspect-code-quality': {
      enabled: true,
      model: 'claude-opus-4-7',
      thinkingLevel: null,
      mode: null,
      resolvedModel: 'claude-opus-4-7',
      modelSource: 'step',
    },
    'aspect-requirement-fit': {
      enabled: true,
      model: null,
      thinkingLevel: null,
      mode: null,
      resolvedModel: 'claude-sonnet-4-6',
      modelSource: 'project',
    },
  };
}

function prerunPipeline(config: Record<string, unknown>) {
  return {
    pipeline: basePipeline(),
    execution: null,
    cost: {
      steps: [],
      totalInputTokens: 0,
      totalOutputTokens: 0,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      totalTokens: 0,
      totalInputCostUsd: 0,
      totalOutputCostUsd: 0,
      totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0,
      totalCostUsd: 0,
      anyModelUnknown: false,
    },
    config,
  };
}

function qualificationPipeline(
  selectedModel: string,
  thinkingLevel: string,
  verdict: 'selected' | 'override',
  summary: string,
) {
  const result = prerunPipeline({});
  return {
    ...result,
    execution: {
      pipelineId: 'standard-task-pipeline', pipelineVersion: 1,
      jobId: JOB_ID, project: PROJECT, startedAt: '2026-07-11T18:00:00Z', completedAt: null,
      steps: [{
        stepId: 'pre-model-qualification', kind: 'module', model: selectedModel, thinkingLevel,
        recommendedModel: verdict === 'override' ? 'catalogue-economy' : selectedModel,
        recommendedThinkingLevel: verdict === 'override' ? 'low' : thinkingLevel,
        selectionSource: verdict === 'override' ? 'task-override' : 'qualification',
        estimatedSavingsPercent: verdict === 'selected' ? 55 : 0,
        status: 'passed', durationMs: 3, inputTokens: 0, outputTokens: 0,
        cacheReadTokens: 0, cacheCreationTokens: 0, verdict, verdictSummary: summary,
      }],
    },
  };
}

/**
 * Mutable backend state captured by the routes so a per-step model write
 * (PUT /pipeline-step) is reflected by the next pipeline GET — exercising the
 * full edit -> persist -> re-resolve loop the Overview selector drives.
 */
interface MockState {
  config: Record<string, Record<string, unknown>>;
  pipelineStepPuts: Array<Record<string, unknown>>;
}

function makeMockState(): MockState {
  return { config: prerunConfig(), pipelineStepPuts: [] };
}

async function installRoutes(page: Page, state: string, mock: MockState): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const projEsc = PROJECT.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
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
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-06-02T00:00:00Z', snapshots: [] }),
    }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
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
            activeJobId: null,
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
      body: JSON.stringify(prerunPipeline(mock.config)),
    }),
  );
  // Per-step model write. Records the request, mutates the captured config so
  // the next pipeline GET re-resolves with the new override (source "step"),
  // mirroring the backend's replace-then-resolve behaviour.
  await page.route(new RegExp(`/api/projects/${projEsc}/pipeline-step(\\?|$)`), async (route) => {
    const body = route.request().postDataJSON() as Record<string, unknown>;
    mock.pipelineStepPuts.push(body);
    const stepId = String(body.stepId);
    const model = (body.model as string | null) ?? null;
    if (mock.config[stepId]) {
      mock.config[stepId].model = model;
      mock.config[stepId].resolvedModel = model ?? 'claude-sonnet-4-6';
      mock.config[stepId].modelSource = model ? 'step' : 'project';
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ stepId, pipelineSteps: {} }),
    });
  });
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

/**
 * Expand every collapsible pipeline section (ASS-1914). Before any run the
 * Empty scenario collapses every section, so a test that inspects per-step
 * cells must first open them. Each header is a toggle button carrying
 * `aria-expanded`; clicking a collapsed one reduces the count.
 */
async function expandAllPipelineSections(page: Page): Promise<void> {
  const collapsed = page.locator('[data-testid="overview-pipeline-phase"][aria-expanded="false"]');
  for (let i = 0; i < 20; i++) {
    if ((await collapsed.count()) === 0) break;
    await collapsed.first().click();
  }
}

test.describe('Pipeline: pre-run resolved model', () => {
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

  test('each LLM step shows its effective model before the run, marked as resolved', async ({ page }) => {
    await installRoutes(page, '2-ready', makeMockState());
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    // Nothing has run yet (Empty scenario), so every section starts collapsed;
    // open them to inspect the pre-run per-step model chips.
    await expandAllPipelineSections(page);

    // The aspect rows render the resolved model even though nothing has run.
    const cqRow = page.locator('[data-step-id="aspect-code-quality"]');
    const cqModel = cqRow.getByTestId('overview-pipeline-step-model');
    await expect(cqModel).toHaveText('claude-opus-4-7');
    await expect(cqModel).toHaveAttribute('data-model-resolved', 'true');

    const rfRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    const rfModel = rfRow.getByTestId('overview-pipeline-step-model');
    await expect(rfModel).toHaveText('claude-sonnet-4-6');
    await expect(rfModel).toHaveAttribute('data-model-resolved', 'true');

    // The Overview is read-only for step configuration (handoff non-goal: no
    // pipeline configuration editor here — that lives in project settings), so
    // the resolved model is shown as a chip, not an inline editor.
    await expect(cqRow.getByTestId('overview-pipeline-step-model-select')).toHaveCount(0);
    await expect(rfRow.getByTestId('overview-pipeline-step-model-select')).toHaveCount(0);

    // Deterministic steps resolve no model, so no chip renders.
    const loopRow = page.locator('[data-step-id="pre-loop-guard"]');
    await expect(loopRow.getByTestId('overview-pipeline-step-model')).toHaveCount(0);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-prerun-model.png'),
        fullPage: true,
      });
    }
  });

  test('the Overview shows the resolved step model read-only, with no inline model editor', async ({ page }) => {
    // Regression guard for the handoff non-goal "Do not add a pipeline
    // configuration editor to Overview." Per-step model configuration lives in
    // project settings; the Overview only surfaces the resolved effective model
    // as a read-only chip. (An earlier iteration briefly rendered an inline
    // per-step model <select> on aspect rows; that affordance was retired.)
    const mock = makeMockState();
    await installRoutes(page, '2-ready', mock);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    const rfRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    // The resolved model is shown as a read-only chip (inherited project model).
    await expect(rfRow.getByTestId('overview-pipeline-step-model')).toHaveText('claude-sonnet-4-6');

    // No inline model editor is offered anywhere in the pipeline, and no
    // per-step configuration write can be triggered from the Overview.
    await expect(page.getByTestId('overview-pipeline-step-model-select')).toHaveCount(0);
    await expect(page.locator('[data-testid^="overview-pipeline-step-agent-"]')).toHaveCount(0);
    expect(mock.pipelineStepPuts.length).toBe(0);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-prerun-model-readonly.png'),
        fullPage: true,
      });
    }
  });

  test('qualification visibly distinguishes polish, architecture, and an explicit override', async ({ page }) => {
    await installRoutes(page, '3-progress', makeMockState());
    const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    let response = qualificationPipeline(
      'catalogue-economy', 'low', 'selected',
      'chore/small/frontend polish; selected recommendation; expected saving about 55% vs top rung',
    );
    await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), route =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(response) }),
    );
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);
    await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    const row = page.locator('[data-step-id="pre-model-qualification"]');
    await expect(row.getByTestId('overview-pipeline-step-model')).toContainText('catalogue-economy');
    await expect(row.getByTestId('overview-pipeline-step-model')).toContainText('low');
    await expect(row.getByTestId('overview-pipeline-step-verdict')).toHaveText('selected');
    await row.getByTestId('overview-pipeline-step-verdict').hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('frontend polish');
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'model-qualification-small--mocked.png'), fullPage: true });

    response = qualificationPipeline(
      'catalogue-top', 'max', 'selected',
      'feature/large/cross-cutting architecture; selected recommendation; expected saving about 0% vs top rung',
    );
    await page.reload();
    await dismissErrorDialog(page);
    await expandAllPipelineSections(page);
    await expect(row.getByTestId('overview-pipeline-step-model')).toContainText('catalogue-top');
    await expect(row.getByTestId('overview-pipeline-step-model')).toContainText('max');
    await row.getByTestId('overview-pipeline-step-verdict').hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('cross-cutting architecture');
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'model-qualification-architecture--mocked.png'), fullPage: true });

    response = qualificationPipeline(
      'operator-pinned', 'high', 'override',
      'chore/small/frontend polish; recommend catalogue-economy at low; card override wins, selected operator-pinned at high',
    );
    await page.reload();
    await dismissErrorDialog(page);
    await expandAllPipelineSections(page);
    await expect(row.getByTestId('overview-pipeline-step-model')).toContainText('operator-pinned');
    await expect(row.getByTestId('overview-pipeline-step-verdict')).toHaveText('override');
    await row.getByTestId('overview-pipeline-step-verdict').hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('card override wins');
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'model-qualification-override--mocked.png'), fullPage: true });
  });
});
