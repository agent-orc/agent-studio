import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * Job-Details pipeline: every step name carries a "what happens here"
 * explanation tooltip.
 *
 * Acceptance ("Hover-Erklaerungen fuer alle Pipeline-Steps"): hovering a step
 * name in the Overview pipeline opens the canonical cac-tooltip with the step
 * label as title and a per-step explanation as body, so a user understands what
 * PRE / CORE / ASPECT / TOOL / DECISION / DRIFT each do without leaving the
 * Overview.
 *
 * Fully mocked - no backend or git repository needed; the pipeline block is
 * generic and renders one row per step from the joined catalogue + execution.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-step-explanations';
const JOB_ID = 'pipeline-step-explanations-test';

function makeDetail(state: string, gateStatus: 'passed' | 'notApplicable' | 'skipped' = 'passed') {
  const evidenceState =
    gateStatus === 'notApplicable'
      ? 'not-applicable'
      : gateStatus === 'skipped'
        ? 'not-proven'
        : 'proven';
  const evidenceSummary =
    gateStatus === 'notApplicable'
      ? 'No build/test defined'
      : gateStatus === 'skipped'
        ? 'Build/test gate skipped at d1649ce9'
        : 'Build/test gate passed at d1649ce9';
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Step explanations fixture',
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
      testEvidence: {
        runId: null,
        runCommit: null,
        runState: null,
        runResult: null,
        matchQuality: 'perfect',
        direction: 'exact',
        distance: 0,
        diffContained: true,
        evidenceState,
        awaitingEvidence: false,
        summary: evidenceSummary,
        sources: [
          {
            kind: 'build-test-gate',
            id: 'gate-d1649ce9',
            commit: 'd1649ce9',
            result: evidenceState,
            observedAt: '2026-06-02T08:00:02Z',
            summary: evidenceSummary,
            reason: gateStatus === 'skipped'
              ? 'The build command did not run.'
              : gateStatus === 'notApplicable'
                ? 'No build/test commands are defined.'
                : 'All selected commands passed.',
            reportRef: 'post-steps/build-test-gate-1.log',
          },
        ],
      },
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: `# Status

- Result: Success

## Overview
- Problem: Build/test evidence must communicate whether the gate was applicable.
- Solution: The result uses the same neutral or conspicuous state as the board and timeline.
`,
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

function makeBlockedReviewDetail() {
  const detail = makeDetail('5-human-review');
  detail.info.testEvidence = {
    ...detail.info.testEvidence,
    evidenceState: 'proven',
    summary: 'Review build-tests Pass at 491ddd64 (verify-1, verify-2)',
    sources: [
      {
        kind: 'review-build-tests',
        id: 'review_ad5cca8e3178425fb9ba9cabe329d50e',
        commit: '491ddd64',
        result: 'passed',
        observedAt: '2026-08-31T12:00:00Z',
        summary: 'Review build-tests Pass at 491ddd64 (verify-1, verify-2)',
        reason: 'verify-1 and verify-2 passed.',
        reportRef: 'remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md',
      },
      {
        kind: 'review-aspects',
        id: 'review_ad5cca8e3178425fb9ba9cabe329d50e',
        commit: '491ddd64',
        result: 'blocked',
        observedAt: '2026-08-31T12:00:00Z',
        summary: 'Review blocked by documentation-impact',
        reason: 'documentation-impact blocked: Public API and state-file contract changed without corresponding load-bearing doc updates.',
        reportRef: 'remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md',
      },
    ],
  };
  return detail;
}

function step(id: string, displayName: string, kind: string, runMode: string) {
  return { id, displayName, kind, runMode, dependsOn: [], idempotent: true, stub: false };
}

// One step per kind so the explanation copy is exercised across PRE / CORE /
// ASPECT / TOOL / DECISION / DRIFT.
const pre = [step('pre-loop-guard', 'Loop check', 'module', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('post-git-commit-attribution', 'Commit attribution', 'tool', 'sequential'),
  step('post-build-test-gate', 'Build/test gate', 'tool', 'sequential'),
  step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
  step('post-drift-adr-code', 'ADR vs code drift', 'drift', 'sequential'),
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

function execStep(
  stepId: string,
  kind: string,
  status: string,
  extra: Record<string, unknown> = {},
) {
  return {
    stepId,
    kind,
    status,
    durationMs: 1_500,
    inputTokens: 800,
    outputTokens: 200,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: status === 'pending' ? null : '2026-06-02T08:00:00Z',
    completedAt: status === 'running' || status === 'pending' ? null : '2026-06-02T08:00:02Z',
    ...extra,
  };
}

function pipelineBody(gateStatus: 'passed' | 'notApplicable' | 'skipped' = 'passed') {
  const gateExtra =
    gateStatus === 'notApplicable'
      ? { verdict: 'not-applicable', reason: 'no verify commands derivable' }
      : gateStatus === 'skipped'
        ? { verdict: 'skipped', reason: 'pipeline interrupted before command execution' }
        : {
            verdict: 'ok',
            reason:
              'verify gate passed; test-level=work-package; selected=2; full-suite=not-run; omitted=11',
          };
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:00:08Z',
      steps: [
        execStep('pre-loop-guard', 'module', 'passed'),
        execStep('core-agent-run', 'core', 'passed'),
        execStep('aspect-requirement-fit', 'aspect', 'passed', { verdict: 'pass' }),
        execStep('post-git-commit-attribution', 'tool', 'passed'),
        execStep(
          'post-build-test-gate',
          'tool',
          gateStatus === 'notApplicable' ? 'skipped' : gateStatus,
          gateExtra,
        ),
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', { verdict: 'accept' }),
        execStep('post-drift-adr-code', 'drift', 'passed'),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
  };
}

async function installRoutes(
  page: Page,
  state: string,
  gateStatus: () => 'passed' | 'notApplicable' | 'skipped' = () => 'passed',
  detailFactory: (() => ReturnType<typeof makeDetail>) | null = null,
) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {
      /* the page may cancel a fallback request during navigation */
    });
  });
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }),
  );
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/tasks/grouped**', (route) =>
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
  await page.route('**/api/workspaces**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/projects**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(`**/api/projects/${PROJECT}/workbenches**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
    }),
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
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
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
      body: JSON.stringify(pipelineBody(gateStatus())),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(detailFactory ? detailFactory() : makeDetail(state, gateStatus())),
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
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {
      /* best-effort cleanup for a fixture-only dialog */
    });
  }
}

async function expandAllPipelineSections(page: Page): Promise<void> {
  const collapsed = page.locator('[data-testid="overview-pipeline-phase"][aria-expanded="false"]');
  await collapsed.evaluateAll((buttons) => {
    for (const button of buttons) {
      (button as HTMLButtonElement).click();
    }
  });
  await expect(collapsed).toHaveCount(0);
}

test.describe('Pipeline: per-step explanation tooltips', () => {
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

  test('every step name opens an explanation tooltip with the step label as title', async ({
    page,
  }, testInfo) => {
    await installRoutes(page, '4-auto-review');
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    // One explanation anchor per rendered pipeline step (7 here).
    const names = page.getByTestId('overview-pipeline-step-name');
    await expect(names).toHaveCount(7);

    const tooltip = page.getByTestId('cac-tooltip');

    // CORE: hovering the agent-run step explains the single coding seat.
    await page
      .locator('[data-step-id="core-agent-run"]')
      .getByTestId('overview-pipeline-step-name')
      .hover();
    await expect(tooltip).toBeVisible();
    await expect(tooltip.locator('.cac-tooltip__title')).toHaveText('Agent execution');
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('coding seat');

    // PRE: the loop guard explanation mentions the loop guard.
    await page
      .locator('[data-step-id="pre-loop-guard"]')
      .getByTestId('overview-pipeline-step-name')
      .hover();
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('loop guard');

    // ASPECT: the requirement-fit aspect explanation mentions acceptance criteria.
    await page
      .locator('[data-step-id="aspect-requirement-fit"]')
      .getByTestId('overview-pipeline-step-name')
      .hover();
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('acceptance criteria');

    // TOOL: the commit-attribution step explanation mentions git commits.
    await page
      .locator('[data-step-id="post-git-commit-attribution"]')
      .getByTestId('overview-pipeline-step-name')
      .hover();
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('git commits');

    // A passed subset gate exposes the exact coverage scope from its green
    // status icon instead of implying that the full suite ran.
    await page
      .locator('[data-step-id="post-build-test-gate"]')
      .getByTestId('overview-pipeline-step-status')
      .hover();
    await expect(tooltip.locator('.cac-tooltip__title')).toHaveText('Build/test gate: Passed');
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('test-level=work-package');
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('full-suite=not-run');

    // DECISION: the orchestrator decision explanation mentions the final ruling.
    await page
      .locator('[data-step-id="post-orchestrator-decision"]')
      .getByTestId('overview-pipeline-step-name')
      .hover();
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('final ruling');

    // DRIFT: the drift step explanation flags that it is off by default.
    await page
      .locator('[data-step-id="post-drift-adr-code"]')
      .getByTestId('overview-pipeline-step-name')
      .hover();
    await expect(tooltip.locator('.cac-tooltip__body')).toContainText('off by default');

    if (RESULTS_DIR) {
      for (const theme of ['dark', 'light'] as const) {
        await setTheme(page, theme);
        // Keep the subset-coverage proof open for each themed capture.
        await page
          .locator('[data-step-id="post-build-test-gate"]')
          .getByTestId('overview-pipeline-step-status')
          .hover();
        await expect(tooltip).toBeVisible();
        const pipelineBox = await pipeline.boundingBox();
        const tooltipBox = await tooltip.boundingBox();
        expect(pipelineBox).not.toBeNull();
        expect(tooltipBox).not.toBeNull();
        const x = Math.max(0, Math.min(pipelineBox!.x, tooltipBox!.x) - 16);
        const y = Math.max(0, Math.min(pipelineBox!.y, tooltipBox!.y) - 16);
        const right =
          Math.max(pipelineBox!.x + pipelineBox!.width, tooltipBox!.x + tooltipBox!.width) + 16;
        const bottom =
          Math.max(pipelineBox!.y + pipelineBox!.height, tooltipBox!.y + tooltipBox!.height) + 16;
        const screenshotPath = path.join(
          RESULTS_DIR,
          `pipeline-subset-coverage-tooltip--${theme}.png`,
        );
        await page.screenshot({
          path: screenshotPath,
          clip: { x, y, width: right - x, height: bottom - y },
        });
        await testInfo.attach(`pipeline-subset-coverage-tooltip--${theme}`, {
          path: screenshotPath,
          contentType: 'image/png',
        });
      }
    }
  });

  test('build gate not-applicable stays neutral and a true skip stays conspicuous in task detail', async ({
    page,
  }, testInfo) => {
    let status: 'notApplicable' | 'skipped' = 'notApplicable';
    await installRoutes(page, '5-human-review', () => status);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);
    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);
    const gate = page.locator('[data-step-id="post-build-test-gate"]');
    await expect(gate).toHaveAttribute('data-status', 'notApplicable');
    await expect(gate).toContainText('no build/test defined');
    await expect(gate).not.toHaveAttribute('data-attention-required', 'true');

    await page.getByTestId('studio-pane-toggle-protocol').click();
    await expect(page.getByTestId('pane-protocol')).toBeVisible();
    await page.getByTestId('inspector-tab-protocol').click();
    const resultEvidence = page.getByTestId('result-test-evidence');
    await expect(resultEvidence).toHaveAttribute('data-evidence-state', 'not-applicable');
    await expect(resultEvidence).toContainText('No build/test defined');

    if (RESULTS_DIR) {
      for (const theme of ['dark', 'light'] as const) {
        await setTheme(page, theme);
        const screenshotPath = path.join(
          RESULTS_DIR,
          `agt-2518--task-detail-build-test-gate--not-applicable--${theme}--mocked.png`,
        );
        await pipeline.screenshot({ path: screenshotPath });
        await testInfo.attach(`task-detail-not-applicable--${theme}`, {
          path: screenshotPath,
          contentType: 'image/png',
        });
        const resultScreenshotPath = path.join(
          RESULTS_DIR,
          `agt-2518--task-result-build-test-gate--not-applicable--${theme}--mocked.png`,
        );
        await resultEvidence.screenshot({ path: resultScreenshotPath });
        await testInfo.attach(`task-result-not-applicable--${theme}`, {
          path: resultScreenshotPath,
          contentType: 'image/png',
        });
      }
    }

    status = 'skipped';
    await page.reload();
    await dismissErrorDialog(page);
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);
    await expect(gate).toHaveAttribute('data-status', 'skipped');
    await expect(gate).toHaveAttribute('data-attention-required', 'true');
    await page.getByTestId('studio-pane-toggle-protocol').click();
    await expect(page.getByTestId('pane-protocol')).toBeVisible();
    await page.getByTestId('inspector-tab-protocol').click();
    await expect(resultEvidence).toHaveAttribute('data-evidence-state', 'not-proven');
    await expect(resultEvidence).toContainText('Build/test gate skipped at d1649ce9');

    if (RESULTS_DIR) {
      for (const theme of ['dark', 'light'] as const) {
        await setTheme(page, theme);
        const screenshotPath = path.join(
          RESULTS_DIR,
          `agt-2518--task-detail-build-test-gate--skipped--${theme}--mocked.png`,
        );
        await pipeline.screenshot({ path: screenshotPath });
        await testInfo.attach(`task-detail-skipped--${theme}`, {
          path: screenshotPath,
          contentType: 'image/png',
        });
        const resultScreenshotPath = path.join(
          RESULTS_DIR,
          `agt-2518--task-result-build-test-gate--skipped--${theme}--mocked.png`,
        );
        await resultEvidence.screenshot({ path: resultScreenshotPath });
        await testInfo.attach(`task-result-skipped--${theme}`, {
          path: resultScreenshotPath,
          contentType: 'image/png',
        });
      }
    }
  });

  test('Evidence tab keeps passing build proof separate from a blocked review aspect', async ({
    page,
  }, testInfo) => {
    await installRoutes(page, '5-human-review', () => 'passed', makeBlockedReviewDetail);
    await page.goto(`/#/tasks/${encodeURIComponent(JOB_ID)}?view=evidence%3Aprotocol&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);
    await expect(page.getByTestId('prompt-tab-evidence')).toHaveAttribute('aria-selected', 'true');
    await dismissErrorDialog(page);

    const evidence = page.getByTestId('evidence-tab-test-evidence');
    const build = page.getByTestId('test-evidence-source-review-build-tests');
    const aspects = page.getByTestId('test-evidence-source-review-aspects');
    await expect(evidence).toHaveAttribute('data-evidence-state', 'proven');
    await expect(build).toHaveAttribute('data-source-result', 'passed');
    await expect(build).toContainText('Review build-tests Pass at 491ddd64 (verify-1, verify-2)');
    await expect(build).toContainText('verify-1 and verify-2 passed.');
    await expect(aspects).toHaveAttribute('data-source-result', 'blocked');
    await expect(aspects).toContainText('Review blocked by documentation-impact');
    await expect(aspects).toContainText('Public API and state-file contract changed without corresponding load-bearing doc updates.');
    await expect(aspects).toHaveAttribute('aria-label', /documentation-impact blocked/);
    await expect(build.getByRole('link', { name: /Open report/ })).toHaveAttribute(
      'href',
      /remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e\.md/,
    );
    await expect(aspects.getByRole('link', { name: /Open report/ })).toHaveAttribute(
      'href',
      /remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e\.md/,
    );

    if (RESULTS_DIR) {
      for (const theme of ['dark', 'light'] as const) {
        await setTheme(page, theme);
        const screenshotPath = path.join(
          RESULTS_DIR,
          `agt-2714--review-test-evidence-sources--${theme}--mocked.png`,
        );
        await page.getByTestId('ev-section-test').screenshot({ path: screenshotPath });
        await testInfo.attach(`review-test-evidence-sources--${theme}`, {
          path: screenshotPath,
          contentType: 'image/png',
        });
      }
    }
  });
});
