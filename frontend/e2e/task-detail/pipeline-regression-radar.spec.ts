import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Regression Radar as its own pipeline step.
 *
 * The radar runs today as a deterministic reporting-only Tool post-step
 * (it never reissues). This spec asserts the acceptance criterion:
 * "Regression Radar erscheint als eigener Step in der Pipeline-Liste mit
 * Status + Dauer." (Regression Radar appears as its own step in the
 * pipeline list with status + duration.)
 *
 * The Overview pipeline block is generic — it joins the static catalogue
 * (which now carries `post-regression-radar`) with the per-job execution
 * record and renders one row per step. So a fully-mocked
 * `/api/tasks/{id}/pipeline` response whose execution lists the radar step
 * is enough to render the row; no backend or git repository is needed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-radar';
const JOB_ID = 'pipeline-radar-test';

// A clean spec-change verdict from the radar: reporting-only, so the step
// records Passed with the worst category carried in the verdict token and a
// fast, sub-second tool duration.
const RADAR_DURATION_MS = 850;
const RADAR_VERDICT = 'drift';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Regression radar pipeline-step fixture',
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
  return {
    id,
    displayName,
    kind,
    runMode: 'sequential',
    dependsOn: [],
    idempotent: true,
    stub: false,
  };
}

// A lean but realistic Post bracket: lint-scss, then the radar, then the
// orchestrator decision. The radar sits between them just like the real
// catalogue, so the screenshot shows it as a first-class neighbour.
function makePipelineResponse() {
  const post = [
    step('post-lint-scss', 'Frontend stylelint', 'tool'),
    step('post-regression-radar', 'Regression radar', 'tool'),
    step('post-orchestrator-decision', 'Auto-review decision', 'orchestrator'),
  ];
  const core = [step('core-agent-run', 'Agent run', 'core')];
  const allSteps = [...core, ...post];

  return {
    pipeline: {
      id: 'standard-task-pipeline',
      displayName: 'Standard task pipeline',
      version: 1,
      pre: [],
      core,
      post,
      allSteps,
    },
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:01:00Z',
      steps: [
        {
          stepId: 'core-agent-run',
          kind: 'core',
          status: 'passed',
          durationMs: 42_000,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
        },
        {
          stepId: 'post-lint-scss',
          kind: 'tool',
          status: 'passed',
          durationMs: 1_200,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          verdict: 'clean',
        },
        {
          stepId: 'post-regression-radar',
          kind: 'tool',
          status: 'passed',
          startedAt: '2026-06-02T08:00:58Z',
          completedAt: '2026-06-02T08:00:59Z',
          durationMs: RADAR_DURATION_MS,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          verdict: RADAR_VERDICT,
          reason: '2 spec change(s): 1 intended, 0 at-risk, 1 drift',
        },
        {
          stepId: 'post-orchestrator-decision',
          kind: 'orchestrator',
          status: 'passed',
          durationMs: 300,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
        },
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {
      'post-regression-radar': { enabled: true, model: null, mode: null },
    },
  };
}

async function installRoutes(page: Page, state: string) {
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
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        autoReview: [],
        humanReview: [],
        completed: [],
        archive: [],
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
    }),
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
            mode: 'manual',
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ runs: [] }),
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(makePipelineResponse()),
    }),
  );
  // Exact-task detail (the `(\?|$)` guard keeps it from swallowing the
  // `/pipeline`, `/runs`, ... sub-routes registered above).
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(detail),
    }),
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
 * Expand every collapsible pipeline section (ASS-1914). Sections that hold no
 * running/failed work default-collapse, so a test that asserts on a specific
 * row must first open its section. Each header is a toggle button carrying
 * `aria-expanded`; clicking a collapsed one reduces the count.
 */
async function expandAllPipelineSections(page: Page): Promise<void> {
  const collapsed = page.locator('[data-testid="overview-pipeline-phase"][aria-expanded="false"]');
  for (let i = 0; i < 20; i++) {
    if ((await collapsed.count()) === 0) break;
    await collapsed.first().click();
  }
}

test.describe('Regression radar pipeline step', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        // Prompt pane visible so its default Overview tab (and the pipeline
        // block within it) renders.
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: false, git: false }),
        );
      } catch {
        /* private mode */
      }
    });
  });

  test('renders as its own pipeline-list row with status + duration', async ({ page }) => {
    await installRoutes(page, '5-human-review');
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);

    // The Overview pipeline block is up.
    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    // The radar is a first-class row, marked as a Tool step that Passed; its
    // compact kind marker reads TOOL (full name in the tooltip).
    const radarRow = page.locator('[data-step-id="post-regression-radar"]');
    await expect(radarRow).toBeVisible();
    await expect(radarRow).toHaveAttribute('data-status', 'passed');
    await expect(radarRow).toContainText('Regression radar');
    await expect(radarRow).toContainText('TOOL');

    // Status pill carries the worst spec-change category as its verdict.
    await expect(radarRow.getByTestId('overview-pipeline-step-verdict')).toHaveText(RADAR_VERDICT);

    // Duration cell renders the recorded wall-clock time (acceptance: "Dauer").
    await expect(radarRow.getByTestId('overview-pipeline-step-duration')).toHaveText('850ms');

    if (RESULTS_DIR) {
      await radarRow.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-regression-radar-step.png'),
        fullPage: true,
      });
    }
  });
});
