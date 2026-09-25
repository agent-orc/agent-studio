import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Auto-review display: parallel aspects + the orchestrator final verdict as
 * separate pipeline steps.
 *
 * Acceptance ("die parallelen Aspekte UND das Orchestrator-Final-Verdict als
 * eigene, klar getrennte Schritte im Job-Details darstellen"): aspect reviews
 * run in a read-only parallel pool, so each aspect row carries a muted
 * parallel note. The orchestrator then makes ONE final ruling, recorded as its own
 * `post-orchestrator-decision` step (kind `orchestrator`) and rendered as a
 * visually separated final-verdict row with a "Final verdict" chip.
 *
 * Fully mocked - no backend or git repository needed; the pipeline block is
 * generic and renders one row per step from the joined catalogue + execution.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-final-verdict';
const JOB_ID = 'pipeline-final-verdict-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      key: 'AGT-2794',
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Final verdict fixture',
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

// Core run (sequential), then the read-only aspect pool (parallel), then the
// single orchestrator final verdict (sequential).
const pre = [step('pre-loop-guard', 'Loop guard', 'pre', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('aspect-tests-and-evidence', 'Tests and evidence', 'aspect', 'parallel'),
  step('post-lint-scss', 'Frontend stylelint', 'tool', 'sequential'),
  step('post-regression-radar', 'Regression radar', 'drift', 'sequential'),
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

function execStep(stepId: string, kind: string, status: string, extra: Record<string, unknown> = {}) {
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

// All aspects passed in the parallel pool, so the orchestrator's single final
// verdict is `accept`.
function pipelineAcceptedFinalVerdict() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:00:04Z',
      steps: [
        execStep('pre-loop-guard', 'pre', 'passed'),
        execStep('core-agent-run', 'core', 'passed'),
        execStep('aspect-requirement-fit', 'aspect', 'passed', {
          verdict: 'pass', verdictSummary: 'All acceptance criteria are covered by the implementation.',
        }),
        execStep('aspect-code-quality', 'aspect', 'passed', {
          verdict: 'concerns', verdictSummary: 'Clean, well-structured diff with one dead no-op assertion in a new spec file.',
        }),
        execStep('aspect-tests-and-evidence', 'aspect', 'passed', {
          verdict: 'pass', verdictSummary: 'Focused component and projection tests passed.',
        }),
        execStep('post-lint-scss', 'tool', 'passed'),
        execStep('post-regression-radar', 'drift', 'passed', { verdict: 'clean' }),
        execStep('post-orchestrator-decision', 'orchestrator', 'passed', {
          verdict: 'accept',
          verdictSummary:
            'All 3 aspect reviews passed in the read-only pool; accepting and moving to human review.',
        }),
      ],
    },
    cost: { steps: [], totalTokens: 0, totalCostUsd: 0, anyModelUnknown: false },
    config: {},
    resultFiles: {
      'aspect-requirement-fit': 'aspect-requirement-fit.md',
      'aspect-code-quality': 'aspect-code-quality.md',
      'aspect-tests-and-evidence': 'aspect-tests-and-evidence.md',
    },
    aspectEvidence: {
      'aspect-code-quality': [
        {
          attemptId: 'review_latest',
          reportFile: 'aspect-code-quality.md',
          rawLogFile: 'remote-review-review_latest-candidate_aspect_code_quality_stdout_log',
          reviewGradeFile: 'remote-review-grade-review_latest.md',
        },
        {
          attemptId: 'review_earlier',
          reportFile: 'aspect-code-quality.md',
          rawLogFile: 'remote-review-review_earlier-candidate_aspect_code_quality_stdout_log',
          reviewGradeFile: 'remote-review-grade-review_earlier.md',
        },
      ],
    },
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown, outputLines: unknown[] = []) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
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
        preparation: [], orchestratorPrep: [], ready: [],
        progress: [], failedPickup: [], autoReview: [detail.info], humanReview: [],
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
  await page.route('**/api/workspaces**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/projects**', (route) =>
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(outputLines) }),
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
  await page.route(new RegExp(`/api/tasks/${idEsc}/files/(status\\.md|aspect-[^?]+\\.md)(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/plain',
      body: [
        '---',
        'status: pass',
        '---',
        '',
        '## Model reply',
        '',
        '```',
        '## Requirement Fit Review',
        '',
        'The implementation matches the task prompt and no blocking gap remains.',
        '```',
        '[[ASPECT_VERDICT: status=pass]]',
      ].join('\n'),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
  await page.route(/\/api\/projects\/[^/]+\/workbenches(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projectName: PROJECT, includesHistory: true, count: 0, items: [] }),
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
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

/**
 * Expand every collapsible pipeline section (ASS-1914). Sections that hold no
 * running/failed work default-collapse, so a test that measures the full
 * configured row set (e.g. the shared stage gutter across PRE…DRIFT) must open
 * every section first. Each header is a toggle button carrying `aria-expanded`.
 */
async function expandAllPipelineSections(page: Page): Promise<void> {
  for (let i = 0; i < 20; i++) {
    const expanded = await page.evaluate(() => {
      const phase = document.querySelector<HTMLButtonElement>(
        '[data-testid="overview-pipeline-phase"][aria-expanded="false"]',
      );
      phase?.click();
      return phase !== null;
    });
    if (!expanded) break;
  }
}

test.describe('Pipeline: parallel aspects + orchestrator final verdict', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: false, git: false }),
        );
        localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
        sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '0');
      } catch {
        /* private mode */
      }
    });
  });

  test('aspect rows show muted parallel metadata and the orchestrator decision is a separate final-verdict step', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineAcceptedFinalVerdict);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    // Each aspect row carries quiet parallel metadata (read-only pool, Req 1 + 3).
    const parallelNotes = page.getByTestId('overview-pipeline-step-parallel');
    await expect(parallelNotes).toHaveCount(3);
    await expect(parallelNotes.first()).toHaveText('∥');
    await expect(parallelNotes.first()).toHaveAttribute('aria-label', 'Parallel review pool');

    // The orchestrator decision is its own, clearly separated final-verdict row.
    // Its compact icon marker keeps the full kind available to assistive tech.
    const decisionRow = page.locator('[data-step-id="post-orchestrator-decision"]');
    await expect(decisionRow).toBeVisible();
    await expect(decisionRow.locator('.ov-pl-step__kind')).toHaveAttribute('aria-label', 'Decision step');
    await expect(decisionRow).toHaveClass(/ov-pl-step--final-verdict/);
    await expect(decisionRow).toHaveAttribute('data-run-mode', 'sequential');

    const finalChip = decisionRow.getByTestId('overview-pipeline-step-final-verdict');
    await expect(finalChip).toBeVisible();
    await expect(finalChip).toContainText('Final verdict');

    // The one combined chip projects the authoritative current-run outcome.
    const verdict = decisionRow.getByTestId('overview-pipeline-step-final-verdict');
    await expect(verdict).toHaveAttribute('data-verdict', 'succeeded');
    await expect(verdict).toContainText('Final verdict → Pipeline completed');

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-parallel-aspects-and-final-verdict--mocked.png'),
        fullPage: true,
      });
    }

    const aspectRow = page.locator('[data-step-id="aspect-requirement-fit"]');
    await aspectRow.getByTestId('overview-pipeline-step-details').click();
    const detailsDialog = page.getByTestId('overview-pipeline-step-details-dialog');
    await expect(detailsDialog).toBeVisible();
    const resultTrigger = detailsDialog.getByTestId('pipeline-step-result-toggle');
    await expect(resultTrigger).toBeVisible();
    await resultTrigger.click();
    await expect(detailsDialog.getByTestId('pipeline-step-result-card')).toBeVisible();
    await expect(detailsDialog.getByTestId('pipeline-step-result-body')).toContainText('Requirement Fit Review');

    const popoverBackground = await detailsDialog
      .getByTestId('pipeline-step-result-card')
      .evaluate((el) => getComputedStyle(el).backgroundColor);
    const channels = popoverBackground.match(/rgba?\(([^)]+)\)/);
    const parts = channels?.[1].split(',').map((part) => part.trim()) ?? [];
    const popoverAlpha = parts.length === 4 ? Number(parts[3]) : 1;
    expect(
      popoverAlpha,
      `aspect popover background must be opaque, got "${popoverBackground}"`,
    ).toBe(1);

    if (RESULTS_DIR) {
      await detailsDialog.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-result-popover-open--mocked.png'),
      });
    }
  });

  test('stage labels use one fixed gutter and names start on one shared edge', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineAcceptedFinalVerdict);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    const metrics = await page.getByTestId('overview-pipeline-step').evaluateAll((rows) =>
      rows.map((row) => {
        const kind = row.querySelector<HTMLElement>('.ov-pl-step__kind');
        const name = row.querySelector<HTMLElement>('.ov-pl-step__name');
        if (!kind || !name) throw new Error('Pipeline row is missing kind or name cell.');
        const kindRect = kind.getBoundingClientRect();
        const nameRect = name.getBoundingClientRect();
        return {
          label: kind.getAttribute('aria-label') ?? '',
          kindLeft: Math.round(kindRect.left),
          kindWidth: Math.round(kindRect.width),
          nameLeft: Math.round(nameRect.left),
        };
      }),
    );

    expect(metrics.map(m => m.label)).toEqual([
      'pre step',
      'Core agent work step',
      'Aspect step',
      'Aspect step',
      'Aspect step',
      'Tool step',
      'Drift step',
      'Decision step',
    ]);

    const maxDelta = (values: number[]) => Math.max(...values) - Math.min(...values);
    expect(maxDelta(metrics.map(m => m.kindLeft)), JSON.stringify(metrics)).toBeLessThanOrEqual(1);
    expect(maxDelta(metrics.map(m => m.kindWidth)), JSON.stringify(metrics)).toBeLessThanOrEqual(1);
    expect(maxDelta(metrics.map(m => m.nameLeft)), JSON.stringify(metrics)).toBeLessThanOrEqual(1);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-fixed-stage-gutter-alignment--mocked.png'),
        fullPage: true,
      });
    }
  });

  test('captures aspect, sentinel, and tab-strip evidence at desktop widths', async ({ page }) => {
    test.setTimeout(120_000);
    test.skip(!RESULTS_DIR, 'JOB_RESULTS_DIR is required for review evidence');
    let legacyProjection = true;
    const marker = '[[ASPECT_VERDICT: status=concerns; summary=Clean diff with one dead no-op assertion; evidence_checked=overview-pane.component.spec.ts; missing=none]] [[TASK_DONE]]';
    const current = pipelineAcceptedFinalVerdict();
    const pipelineBody = () => {
      if (!legacyProjection) return current;
      const legacy = structuredClone(current);
      const codeQuality = legacy.execution.steps.find(item => item.stepId === 'aspect-code-quality')!;
      codeQuality.status = 'failed';
      delete codeQuality.verdict;
      delete codeQuality.verdictSummary;
      codeQuality.reason = 'Clean, well-structured diff with one dead no-op assertion in a new spec file.';
      delete legacy.aspectEvidence;
      return legacy;
    };
    await installRoutes(page, '4-auto-review', pipelineBody, [{
      timestamp: '2026-09-18T12:00:00Z', stream: 'stdout', text: marker,
    }]);
    await page.addInitScript(({ watchPath, jobId }) => {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
      localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
      sessionStorage.setItem('atp.studio.orchestratorOpen.v1', '0');
      const taskKey = `${watchPath}::${jobId}`;
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [
          { kind: 'task', taskKey },
          { kind: 'board', projectName: 'fixture' },
          {
            kind: 'hub', projectName: 'fixture', section: 'wiki',
            wikiTarget: {
              kind: 'page', relPath: 'operations/pre-develop-gate-a-run-budget-overrun.md',
              title: 'Pre-develop gate run budget overrun',
            },
          },
        ],
        activeKey: `task:${taskKey}`,
      }));
    }, { watchPath: WATCH_PATH, jobId: JOB_ID });

    for (const width of [1280, 1920]) {
      await page.setViewportSize({ width, height: 1080 });
      legacyProjection = true;
      await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      await dismissErrorDialog(page);
      await expandAllPipelineSections(page);
      const pipeline = page.getByTestId('overview-pipeline');
      await expect(pipeline).toBeVisible();
      await pipeline.screenshot({ path: path.join(RESULTS_DIR, `pipeline-before-${width}--mocked.png`) });

      const refreshedPipeline = page.waitForResponse(response =>
        response.url().includes(`/api/tasks/${JOB_ID}/pipeline`) && response.ok());
      legacyProjection = false;
      await refreshedPipeline;
      await expandAllPipelineSections(page);
      await expect(page.locator('[data-step-id="aspect-code-quality"]')).toContainText('concerns');
      await page.getByTestId('overview-pipeline').screenshot({
        path: path.join(RESULTS_DIR, `pipeline-after-${width}--mocked.png`),
      });

      const tabbar = page.getByTestId('studio-tabbar');
      const legacyTabStyle = await page.addStyleTag({ content: `
        .studio-tab { flex: 1 1 0 !important; min-width: 0 !important; }
        .studio-tab__prefix { padding: 0 !important; border: 0 !important; background: transparent !important; }
        .studio-tab--active { box-shadow: none !important; border-top: 1px solid var(--studio-accent) !important; }
      ` });
      const documentTabTitle = page.getByTestId(/studio-tab-hub:fixture/).locator('.studio-tab__title');
      await documentTabTitle
        .evaluate(element => { element.textContent = 'pre-develop-gate-a-run-budget-overrun-b...'; });
      await tabbar.screenshot({ path: path.join(RESULTS_DIR, `tab-strip-before-${width}--mocked.png`) });
      await legacyTabStyle.evaluate(element => element.remove());
      await documentTabTitle.evaluate(element => { element.textContent = 'Pre-develop gate run budget overrun'; });
      await page.getByTestId('studio-tabbar').screenshot({
        path: path.join(RESULTS_DIR, `tab-strip-after-${width}--mocked.png`),
      });

      await page.getByTestId('inspector-tab-activity').click();
      await page.getByTestId('protocol-maximize-log').click();
      const overlay = page.getByTestId('log-overlay');
      await expect(overlay).toBeVisible();
      await overlay.evaluate((root, rawMarker) => {
        root.querySelectorAll<HTMLElement>('app-runtime-sentinel-view').forEach(item => { item.style.display = 'none'; });
        const legacy = document.createElement('pre');
        legacy.dataset['testid'] = 'legacy-raw-sentinel';
        legacy.textContent = rawMarker;
        root.querySelector('.convo-turn--agent')?.append(legacy);
      }, marker);
      await expect(overlay.getByTestId('legacy-raw-sentinel')).toContainText('[[ASPECT_VERDICT:');
      await overlay.screenshot({ path: path.join(RESULTS_DIR, `log-before-${width}--mocked.png`) });
      await overlay.evaluate(root => {
        root.querySelector('[data-testid="legacy-raw-sentinel"]')?.remove();
        root.querySelectorAll<HTMLElement>('app-runtime-sentinel-view').forEach(item => { item.style.display = ''; });
      });
      await expect(overlay.getByTestId('activity-aspect-verdict')).toContainText('Clean diff');
      await overlay.screenshot({ path: path.join(RESULTS_DIR, `log-after-${width}--mocked.png`) });
      await overlay.getByTestId('log-overlay-close').click();
    }
  });
});
