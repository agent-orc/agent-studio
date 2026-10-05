import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * Pipeline column headers (Time / Duration / Tokens / Cost) and phase headers.
 *
 * The per-step metric cells (start clock, duration, token count, cost) used to
 * render bare numbers with no captions. This polish introduces one header row
 * at the top of the steps so the columns are interpretable. The header cells
 * must line up over their value cells: each header cell's right edge sits on the
 * same x as the corresponding value cell's right edge (right-aligned columns).
 *
 * Fully mocked - no backend or git repository needed.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/pipeline-column-headers';
const JOB_ID = 'pipeline-column-headers-test';

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Column headers fixture',
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

const pre = [step('pre-loop-guard', 'Loop guard', 'module', 'sequential')];
const core = [step('core-agent-run', 'Agent execution', 'core', 'sequential')];
const post = [step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential')];
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

function execStep(stepId: string, kind: string, model: string, extra: Record<string, unknown> = {}) {
  return {
    stepId,
    kind,
    status: 'passed',
    model,
    durationMs: 92_000,
    inputTokens: 8_000,
    outputTokens: 2_000,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    startedAt: '2026-06-02T08:00:00Z',
    completedAt: '2026-06-02T08:01:32Z',
    ...extra,
  };
}

function costStep(stepId: string, model: string, totalTokens: number, costUsd: number) {
  return {
    stepId,
    model,
    modelKnown: true,
    tokenUsageSource: 'orchestrator',
    inputTokens: Math.round(totalTokens * 0.8),
    outputTokens: Math.round(totalTokens * 0.2),
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens,
    inputCostUsd: costUsd * 0.6,
    outputCostUsd: costUsd * 0.4,
    cacheReadCostUsd: 0,
    cacheCreationCostUsd: 0,
    costUsd,
  };
}

function pipelineWithMetrics() {
  return {
    pipeline: basePipeline(),
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: PROJECT,
      startedAt: '2026-06-02T08:00:00Z',
      completedAt: '2026-06-02T08:05:00Z',
      steps: [
        execStep('pre-loop-guard', 'module', 'claude-haiku-4-5'),
        execStep('core-agent-run', 'core', 'claude-opus-4-7'),
        execStep('post-orchestrator-decision', 'orchestrator', 'claude-haiku-4-5', { verdict: 'accept' }),
      ],
    },
    cost: {
      steps: [
        costStep('pre-loop-guard', 'claude-haiku-4-5', 1_200, 0.0021),
        costStep('core-agent-run', 'claude-opus-4-7', 248_000, 4.37),
        costStep('post-orchestrator-decision', 'claude-haiku-4-5', 5_400, 0.0089),
      ],
      totalInputTokens: 203_680,
      totalOutputTokens: 50_920,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      totalTokens: 254_600,
      totalInputCostUsd: 2.6286,
      totalOutputCostUsd: 1.7524,
      totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0,
      totalCostUsd: 4.3810,
      anyModelUnknown: false,
    },
    config: {},
  };
}

function pipelineWithAllPhases() {
  const phasePost = [
    step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
    step('post-git-commit-attribution', 'Git attribution', 'tool', 'sequential'),
    step('post-orchestrator-decision', 'Final verdict', 'orchestrator', 'sequential'),
    step('post-drift-adr-code', 'ADR drift', 'drift', 'sequential'),
  ];
  return {
    ...pipelineWithMetrics(),
    pipeline: {
      ...basePipeline(),
      post: phasePost,
      allSteps: [...pre, ...core, ...phasePost],
    },
    execution: {
      ...pipelineWithMetrics().execution,
      steps: [
        execStep('pre-loop-guard', 'module', 'claude-haiku-4-5'),
        execStep('core-agent-run', 'core', 'claude-opus-4-7'),
        execStep('aspect-requirement-fit', 'aspect', 'claude-haiku-4-5', { verdict: 'pass' }),
        execStep('post-git-commit-attribution', 'tool', 'claude-haiku-4-5'),
        execStep('post-orchestrator-decision', 'orchestrator', 'claude-haiku-4-5', { verdict: 'accept' }),
        execStep('post-drift-adr-code', 'drift', 'claude-haiku-4-5', { verdict: 'clean' }),
      ],
    },
    cost: {
      ...pipelineWithMetrics().cost,
      steps: [
        costStep('pre-loop-guard', 'claude-haiku-4-5', 1_200, 0.0021),
        costStep('core-agent-run', 'claude-opus-4-7', 248_000, 4.37),
        costStep('aspect-requirement-fit', 'claude-haiku-4-5', 8_000, 0.0120),
        costStep('post-git-commit-attribution', 'claude-haiku-4-5', 800, 0.0010),
        costStep('post-orchestrator-decision', 'claude-haiku-4-5', 5_400, 0.0089),
        costStep('post-drift-adr-code', 'claude-haiku-4-5', 12_000, 0.0180),
      ],
      totalTokens: 275_400,
    },
  };
}

function pipelineWithDisabledStep() {
  const body = pipelineWithAllPhases();
  return {
    ...body,
    config: {
      ...body.config,
      'post-drift-adr-code': { enabled: false, model: null, mode: null },
    },
  };
}

function pipelineWithCrowdedNarrowGroups() {
  const crowdedPost = [
    step('analysis-regression-radar', 'Regression radar analysis with extended provenance', 'analysis', 'sequential'),
    step('aspect-requirement-fit', 'Requirement fit across every acceptance criterion', 'aspect', 'parallel'),
    step('aspect-code-quality', 'Code quality and maintainability review', 'aspect', 'parallel'),
    step('aspect-tests-and-evidence', 'Tests and durable review evidence', 'aspect', 'parallel'),
    step('aspect-documentation-impact', 'Documentation and operator workflow impact', 'aspect', 'parallel'),
    step('post-git-commit-attribution', 'Git attribution and provenance tooling', 'tool', 'sequential'),
  ];
  const metricSteps = [
    costStep('analysis-regression-radar', 'claude-haiku-4-5', 6_200, 0.0093),
    costStep('aspect-requirement-fit', 'claude-haiku-4-5', 20_000, 0.0300),
    costStep('aspect-code-quality', 'claude-haiku-4-5', 24_000, 0.0360),
    costStep('aspect-tests-and-evidence', 'claude-haiku-4-5', 25_800, 0.0387),
    costStep('aspect-documentation-impact', 'claude-haiku-4-5', 26_000, 0.0390),
    costStep('post-git-commit-attribution', 'claude-haiku-4-5', 4_800, 0.0072),
  ];
  return {
    ...pipelineWithMetrics(),
    pipeline: {
      ...basePipeline(),
      post: crowdedPost,
      allSteps: [...pre, ...core, ...crowdedPost],
    },
    execution: {
      ...pipelineWithMetrics().execution,
      steps: [
        execStep('pre-loop-guard', 'module', 'claude-haiku-4-5'),
        execStep('core-agent-run', 'core', 'claude-opus-4-7'),
        execStep('analysis-regression-radar', 'analysis', 'claude-haiku-4-5'),
        execStep('aspect-requirement-fit', 'aspect', 'claude-haiku-4-5', { verdict: 'pass' }),
        execStep('aspect-code-quality', 'aspect', 'claude-haiku-4-5', {
          status: 'failed', verdict: 'block', error: 'A blocking maintainability concern remains.',
        }),
        execStep('aspect-tests-and-evidence', 'aspect', 'claude-haiku-4-5', {
          verdict: 'concerns', concernSummary: 'The narrow-width evidence was missing.',
        }),
        execStep('aspect-documentation-impact', 'aspect', 'claude-haiku-4-5', { verdict: 'pass' }),
        execStep('post-git-commit-attribution', 'tool', 'claude-haiku-4-5'),
      ],
    },
    cost: {
      ...pipelineWithMetrics().cost,
      steps: [
        costStep('pre-loop-guard', 'claude-haiku-4-5', 1_200, 0.0021),
        costStep('core-agent-run', 'claude-opus-4-7', 248_000, 4.37),
        ...metricSteps,
      ],
      totalTokens: 356_000,
    },
  };
}

async function installRoutes(page: Page, state: string, pipelineBody: () => unknown) {
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
  await page.route('**/api/projects/*/workbenches**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projectName: PROJECT, includesHistory: true, count: 0, items: [] }),
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
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 })
      .catch(() => { /* the overlay may already be detached */ });
  }
}

/**
 * Expand every collapsible pipeline section (ASS-1914). Sections that hold no
 * running/failed work default-collapse, so a test that asserts on the full
 * configured row set must first open every section. Each header is a toggle
 * button carrying `aria-expanded`; clicking a collapsed one reduces the count.
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

test.describe('Pipeline: per-step metric column headers', () => {
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

  test('a header row labels the Time / Duration / Tokens / Cost columns, right-aligned over their values', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithMetrics);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    // The header appears once and carries all four captions.
    const header = page.getByTestId('overview-pipeline-header');
    await expect(header).toHaveCount(1);
    await expect(header.locator('.ov-pl-header__time')).toHaveText('Time');
    await expect(header.locator('.ov-pl-header__duration')).toHaveText('Duration');
    await expect(header.locator('.ov-pl-header__tokens')).toHaveText('Tokens');
    await expect(header.locator('.ov-pl-header__cost')).toHaveText('Cost');

    // Right-edge alignment: each header cell sits over its value cell. Measured
    // against the core row, which has populated timing, tokens and cost. Wait
    // for the poll to populate the row content before measuring.
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    await expect(coreRow.locator('.ov-pl-step__tokens')).toHaveText('248k');

    const align = await page.evaluate(() => {
      const right = (sel: string, root: ParentNode = document): number => {
        const el = root.querySelector<HTMLElement>(sel);
        if (!el) throw new Error(`missing ${sel}`);
        return el.getBoundingClientRect().right;
      };
      const coreRow = document.querySelector<HTMLElement>('[data-step-id="core-agent-run"]')!;
      return {
        time: { h: right('.ov-pl-header__time'), v: right('.ov-pl-step__started', coreRow) },
        duration: { h: right('.ov-pl-header__duration'), v: right('.ov-pl-step__duration', coreRow) },
        tokens: { h: right('.ov-pl-header__tokens'), v: right('.ov-pl-step__tokens', coreRow) },
        cost: { h: right('.ov-pl-header__cost'), v: right('.ov-pl-step__cost', coreRow) },
      };
    });

    for (const [col, { h, v }] of Object.entries(align)) {
      expect(Math.abs(h - v), `${col}: header right ${h} vs value right ${v} (${JSON.stringify(align)})`).toBeLessThanOrEqual(1.5);
    }

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-column-headers--mocked.png'),
        fullPage: true,
      });
    }
  });

  test('phase headers visually group PRE / CORE / ASPECT / TOOL / DECISION / DRIFT without reordering steps', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithAllPhases);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });

    const phases = page.getByTestId('overview-pipeline-phase');
    await expect(phases).toHaveCount(6);
    await expect(phases.locator('.ov-pl-phase__label')).toHaveText([
      'PRE STEPS',
      'CORE AGENT WORK',
      'ASPECT',
      'TOOL',
      'DECISION',
      'DRIFT',
    ]);

    // Section headers carry an accessible name that folds in the phase label,
    // its aggregate tone/step count, and the description (ASS-1914).
    const phaseAriaLabels = await phases.evaluateAll(els =>
      els.map(el => el.getAttribute('aria-label') ?? ''),
    );
    expect(phaseAriaLabels).toEqual([
      expect.stringContaining('PRE STEPS phase,'),
      expect.stringContaining('CORE AGENT WORK phase,'),
      expect.stringContaining('ASPECT phase,'),
      expect.stringContaining('TOOL phase,'),
      expect.stringContaining('DECISION phase,'),
      expect.stringContaining('DRIFT phase,'),
    ]);

    // Open every section so the grouped DOM order below includes each step row.
    await expandAllPipelineSections(page);

    const domOrder = await page.evaluate(() => {
      return Array
        .from(document.querySelectorAll<HTMLElement>('[data-testid="overview-pipeline-phase"], [data-testid="overview-pipeline-step"]'))
        .map(el => {
          if (el.dataset['testid'] === 'overview-pipeline-phase') {
            return `phase:${el.dataset['phase']}`;
          }
          return `step:${el.dataset['stepId']}`;
        });
    });

    expect(domOrder).toEqual([
      'phase:pre',
      'step:pre-loop-guard',
      'phase:core',
      'step:core-agent-run',
      'phase:aspect',
      'step:aspect-requirement-fit',
      'phase:tool',
      'step:post-git-commit-attribution',
      'phase:decision',
      'step:post-orchestrator-decision',
      'phase:drift',
      'step:post-drift-adr-code',
    ]);

    const groupingStyle = await page.evaluate(() => {
      const phase = document.querySelector<HTMLElement>('[data-testid="overview-pipeline-phase"][data-phase="aspect"]')!;
      const row = document.querySelector<HTMLElement>('[data-testid="overview-pipeline-step"][data-phase="aspect"]')!;
      const phaseStyle = getComputedStyle(phase);
      const rowStyle = getComputedStyle(row);
      return {
        phaseBackground: phaseStyle.backgroundColor,
        phaseBorderLeftWidth: phaseStyle.borderLeftWidth,
        phaseBoxShadow: phaseStyle.boxShadow,
        rowBackground: rowStyle.backgroundColor,
        rowBorderLeftWidth: rowStyle.borderLeftWidth,
        rowBoxShadow: rowStyle.boxShadow,
      };
    });

    // Aggregate state uses a whole-surface tint. R1 forbids decorative left
    // borders and inset left-edge shadows on both phase headers and step rows.
    expect(groupingStyle.phaseBackground).not.toMatch(/^(?:transparent|rgba\(0, 0, 0, 0\))$/);
    expect(groupingStyle.phaseBorderLeftWidth).toBe('0px');
    expect(groupingStyle.phaseBoxShadow).toBe('none');
    expect(groupingStyle.rowBackground).not.toMatch(/^(?:transparent|rgba\(0, 0, 0, 0\))$/);
    expect(groupingStyle.rowBorderLeftWidth).toBe('0px');
    expect(groupingStyle.rowBoxShadow).toBe('none');

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-phase-groups--mocked.png'),
        fullPage: true,
      });
    }
  });

  test('phase headers stay wider than inset steps and the disabled-step filter hides disabled rows', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithDisabledStep);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);
    await expect(page.getByTestId('overview-pipeline-step')).toHaveCount(6);

    const geometry = await page.evaluate(() => {
      const rect = (selector: string): DOMRect => {
        const el = document.querySelector<HTMLElement>(selector);
        if (!el) throw new Error(`missing ${selector}`);
        return el.getBoundingClientRect();
      };
      const phase = rect('[data-testid="overview-pipeline-phase"][data-phase="aspect"]');
      const step = rect('[data-testid="overview-pipeline-step"][data-phase="aspect"]');
      const header = rect('[data-testid="overview-pipeline-header"]');
      return {
        stepInset: step.left - phase.left,
        stepRightOverhang: step.right - phase.right,
        headerLeftDelta: Math.abs(header.left - step.left),
        headerRightDelta: Math.abs(header.right - step.right),
      };
    });

    expect(geometry.stepInset, `step inset ${geometry.stepInset}px`).toBeGreaterThan(4);
    expect(geometry.stepRightOverhang, `step right overhang ${geometry.stepRightOverhang}px`).toBeLessThanOrEqual(1);
    expect(geometry.headerLeftDelta, `header/step left delta ${geometry.headerLeftDelta}px`).toBeLessThanOrEqual(1.5);
    expect(geometry.headerRightDelta, `header/step right delta ${geometry.headerRightDelta}px`).toBeLessThanOrEqual(1.5);

    const disabledRow = page.locator('[data-step-id="post-drift-adr-code"]');
    await expect(disabledRow).toHaveAttribute('data-status', 'disabled');

    const toggle = page.getByTestId('overview-pipeline-toggle-disabled');
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');

    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect(disabledRow).toHaveCount(0);
    await expect(page.getByTestId('overview-pipeline-phase')).toHaveCount(5);

    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await expect(disabledRow).toHaveCount(1);
  });

  test('metric columns share one right edge across every phase group (no per-row drift)', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithAllPhases);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    // All six phase rows must be present and populated before measuring.
    const steps = page.getByTestId('overview-pipeline-step');
    await expect(steps).toHaveCount(6);
    await expect(
      page.locator('[data-step-id="core-agent-run"] .ov-pl-step__cost'),
    ).not.toHaveText('—');

    // Every step is its own grid; the slack-absorbing flex track is what keeps
    // Duration / Tokens / Cost on a single shared right edge. Collect the right
    // edge of each metric cell across all rows and assert they coincide. This
    // is the guard against the "krumm und schief" misalignment where the metric
    // block landed at a different x on each row.
    const spread = await page.evaluate(() => {
      const rows = Array.from(
        document.querySelectorAll<HTMLElement>('[data-testid="overview-pipeline-step"]'),
      );
      const rightsOf = (sel: string): number[] =>
        rows
          .map(r => r.querySelector<HTMLElement>(sel))
          .filter((el): el is HTMLElement => !!el)
          .map(el => el.getBoundingClientRect().right);
      const span = (xs: number[]): number => Math.max(...xs) - Math.min(...xs);
      const duration = rightsOf('.ov-pl-step__duration');
      const tokens = rightsOf('.ov-pl-step__tokens');
      const cost = rightsOf('.ov-pl-step__cost');
      return {
        counts: { duration: duration.length, tokens: tokens.length, cost: cost.length },
        durationSpan: span(duration),
        tokensSpan: span(tokens),
        costSpan: span(cost),
      };
    });

    // Each metric cell renders once per row.
    expect(spread.counts).toEqual({ duration: 6, tokens: 6, cost: 6 });
    // Sub-pixel tolerance for rounding; a per-row grid would drift by tens of px.
    expect(spread.durationSpan, `duration right edges spread ${spread.durationSpan}px`).toBeLessThanOrEqual(1.5);
    expect(spread.tokensSpan, `tokens right edges spread ${spread.tokensSpan}px`).toBeLessThanOrEqual(1.5);
    expect(spread.costSpan, `cost right edges spread ${spread.costSpan}px`).toBeLessThanOrEqual(1.5);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-cross-row-alignment--mocked.png'),
        fullPage: true,
      });
    }
  });

  test('narrow pipeline drops Cost then Tokens, keeps Duration, and never overflows a row', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithAllPhases);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);
    await expect(page.getByTestId('overview-pipeline-step')).toHaveCount(6);

    // The metric columns degrade off the pipeline block's own inline-size, not
    // the viewport. Constrain the grid container directly so the container
    // queries fire deterministically regardless of pane layout.
    const setContainerWidth = async (px: number) => {
      await page.evaluate((w) => {
        let tag = document.getElementById('ov-pl-test-width') as HTMLStyleElement | null;
        if (!tag) {
          tag = document.createElement('style');
          tag.id = 'ov-pl-test-width';
          document.head.appendChild(tag);
        }
        tag.textContent = `.ov-pipeline { width: ${w}px !important; max-width: ${w}px !important; }`;
      }, px);
    };

    const cost = page.locator('[data-step-id="core-agent-run"] .ov-pl-step__cost');
    const tokens = page.locator('[data-step-id="core-agent-run"] .ov-pl-step__tokens');
    const duration = page.locator('[data-step-id="core-agent-run"] .ov-pl-step__duration');

    // Mid-narrow: Cost drops, Tokens + Duration survive.
    await setContainerWidth(500);
    await expect(cost).toBeHidden();
    await expect(tokens).toBeVisible();
    await expect(duration).toBeVisible();

    // Very narrow: Tokens drops too; Duration is never dropped.
    await setContainerWidth(430);
    await expect(cost).toBeHidden();
    await expect(tokens).toBeHidden();
    await expect(duration).toBeVisible();

    // No horizontal overflow / Schieflage inside any row at the narrowest width:
    // every row's content fits its own box (name ellipsizes, flex track shrinks).
    const overflow = await page.evaluate(() => {
      return Array
        .from(document.querySelectorAll<HTMLElement>('[data-testid="overview-pipeline-step"]'))
        .map(r => r.scrollWidth - r.clientWidth)
        .reduce((max, d) => Math.max(max, d), 0);
    });
    expect(overflow, `max intra-row overflow ${overflow}px`).toBeLessThanOrEqual(1);

    if (RESULTS_DIR) {
      await pipeline.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'pipeline-narrow-degraded--mocked.png'),
        fullPage: true,
      });
    }
  });

  test('crowded analysis, aspect, and tool groups stay collision-free at side-sheet width', async ({ page }) => {
    await installRoutes(page, '4-auto-review', pipelineWithCrowdedNarrowGroups);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const pipeline = page.getByTestId('overview-pipeline');
    await expect(pipeline).toBeVisible({ timeout: 10_000 });
    await expandAllPipelineSections(page);

    await page.evaluate(() => {
      const tag = document.createElement('style');
      tag.id = 'ov-pl-crowded-width';
      tag.textContent = '.ov-pipeline { width: 320px !important; max-width: 320px !important; }';
      document.head.appendChild(tag);
    });
    const pipelineSteps = page.getByTestId('overview-pipeline-steps');
    await expect(pipelineSteps).toBeVisible();

    const aspectPhase = page.locator('[data-testid="overview-pipeline-phase"][data-phase="aspect"]');
    const aspectSummary = aspectPhase.getByTestId('overview-pipeline-phase-summary');
    await expect(aspectSummary).toContainText('Attention');
    await expect(aspectSummary.getByTestId('overview-pipeline-phase-ran')).toHaveText('4/4');
    await expect(aspectSummary.getByTestId('overview-pipeline-phase-concern')).toHaveText('⚠ 2');
    await expect(aspectSummary).toContainText('95.8k');

    if (RESULTS_DIR) {
      await pipelineSteps.scrollIntoViewIfNeeded();
      await page.evaluate(() => {
        const tag = document.createElement('style');
        tag.id = 'ov-pl-before-layout';
        tag.textContent = `
          .ov-pl-phase { display: flex !important; }
          .ov-pl-phase__rule { flex: 1 1 24px !important; width: auto !important; }
          .ov-pl-phase__summary {
            flex: 0 0 auto !important;
            flex-wrap: nowrap !important;
            margin-left: auto !important;
          }
          .ov-pl-header, .ov-pl-step {
            --ov-pipeline-time-col: 96px !important;
            --ov-pl-grid-cols: 16px 26px minmax(12rem, 2fr) minmax(0, 1fr) 96px !important;
          }
          .ov-pl-header__timing, .ov-pl-step__timing { grid-template-columns: 38px 52px !important; }
          .ov-pl-header__time, .ov-pl-step__started, .ov-pl-step__model { display: inline !important; }
          .ov-pl-step__parallel-note { display: inline-flex !important; }
        `;
        document.head.appendChild(tag);
      });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await pipelineSteps.screenshot({
          path: path.join(RESULTS_DIR, `pipeline-narrow-before-${theme}--mocked.png`),
        });
      }
      await page.evaluate(() => document.getElementById('ov-pl-before-layout')?.remove());
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await pipelineSteps.screenshot({
          path: path.join(RESULTS_DIR, `pipeline-narrow-after-${theme}--mocked.png`),
        });
      }
    }

    const geometry = await page.evaluate(() => {
      const visibleRect = (root: ParentNode, selector: string): DOMRect => {
        const element = root.querySelector<HTMLElement>(selector);
        if (!element) throw new Error(`Missing ${selector}`);
        return element.getBoundingClientRect();
      };
      const rows = Array.from(document.querySelectorAll<HTMLElement>(
        '[data-testid="overview-pipeline-step"][data-phase="aspect"]',
      ));
      const rowMeasurements = rows.map((row) => {
        const cells = [
          visibleRect(row, '[data-testid="overview-pipeline-step-status"]'),
          visibleRect(row, '[role="img"][aria-label="Aspect step"]'),
          visibleRect(row, '[data-testid="overview-pipeline-step-name-cell"]'),
          visibleRect(row, '[data-testid="overview-pipeline-step-meta"]'),
          visibleRect(row, '[data-testid="overview-pipeline-step-timing"]'),
        ];
        const name = row.querySelector<HTMLElement>('[data-testid="overview-pipeline-step-name"]')!;
        const details = visibleRect(row, '[data-testid="overview-pipeline-step-details"]');
        return {
          overflow: row.scrollWidth - row.clientWidth,
          gaps: cells.slice(0, -1).map((cell, index) => cells[index + 1].left - cell.right),
          centreSpread: Math.max(...cells.map(cell => cell.top + cell.height / 2))
            - Math.min(...cells.map(cell => cell.top + cell.height / 2)),
          widths: {
            status: cells[0].width,
            kind: cells[1].width,
            details: details.width,
          },
          nameTruncated: name.scrollWidth > name.clientWidth,
          nameDetailsGap: details.left - name.getBoundingClientRect().right,
        };
      });
      const headers = Array.from(document.querySelectorAll<HTMLElement>(
        '[data-testid="overview-pipeline-phase"][data-phase="analysis"], '
          + '[data-testid="overview-pipeline-phase"][data-phase="aspect"], '
          + '[data-testid="overview-pipeline-phase"][data-phase="tool"]',
      ));
      const aspectHeader = headers.find(header => header.dataset['phase'] === 'aspect')!;
      const marker = visibleRect(aspectHeader, '[data-testid="overview-pipeline-phase-marker"]');
      const summary = visibleRect(aspectHeader, '[data-testid="overview-pipeline-phase-summary"]');
      return {
        rows: rowMeasurements,
        headerOverflow: Math.max(...headers.map(header => header.scrollWidth - header.clientWidth)),
        headerSummaryGap: summary.top - marker.bottom,
      };
    });

    expect(geometry.rows).toHaveLength(4);
    expect(geometry.headerOverflow, `header overflow ${geometry.headerOverflow}px`).toBeLessThanOrEqual(1);
    expect(geometry.headerSummaryGap, 'summary should wrap below the collapse control').toBeGreaterThanOrEqual(0);
    for (const [index, row] of geometry.rows.entries()) {
      expect(row.overflow, `aspect row ${index} overflow ${row.overflow}px`).toBeLessThanOrEqual(1);
      expect(Math.min(...row.gaps), `aspect row ${index} gaps ${JSON.stringify(row.gaps)}`).toBeGreaterThanOrEqual(-1);
      expect(row.centreSpread, `aspect row ${index} centre spread ${row.centreSpread}px`).toBeLessThanOrEqual(1);
      expect(row.widths.status, `aspect row ${index} status width`).toBeGreaterThan(0);
      expect(row.widths.kind, `aspect row ${index} kind width`).toBeGreaterThanOrEqual(20);
      expect(row.widths.details, `aspect row ${index} details width`).toBeGreaterThanOrEqual(20);
      expect(row.nameTruncated, `aspect row ${index} long name should ellipsize`).toBe(true);
      expect(row.nameDetailsGap, `aspect row ${index} name/details gap`).toBeGreaterThanOrEqual(-1);
    }

    const firstAspect = page.locator('[data-testid="overview-pipeline-step"][data-phase="aspect"]').first();
    await expect(firstAspect.getByTestId('overview-pipeline-step-started')).toBeHidden();
    await expect(firstAspect.getByTestId('overview-pipeline-step-duration')).toBeVisible();
  });

  test('overview content keeps left-aligned prose and tabular measures on ultrawide viewports', async ({ page }) => {
    await page.setViewportSize({ width: 1900, height: 1050 });
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: true, protocol: true, git: false }),
        );
      } catch {
        /* private mode */
      }
    });

    await installRoutes(page, '4-auto-review', pipelineWithMetrics);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissErrorDialog(page);

    const overview = page.getByTestId('overview-tab');
    const pipeline = page.getByTestId('overview-pipeline');
    const protocol = page.getByTestId('pane-protocol');

    await expect(overview).toBeVisible({ timeout: 10_000 });
    await expect(protocol).toBeVisible();
    await expect(pipeline).toBeVisible();
    await expandAllPipelineSections(page);

    const geometry = await page.evaluate(() => {
      const box = (selector: string): DOMRect => {
        const el = document.querySelector<HTMLElement>(selector);
        if (!el) throw new Error(`missing ${selector}`);
        return el.getBoundingClientRect();
      };

      const overview = box('[data-testid="overview-tab"]');
      const title = box('[data-testid="overview-title-block"]');
      const status = box('[data-testid="overview-status"]');
      const pipeline = box('[data-testid="overview-pipeline"]');
      const protocol = box('[data-testid="pane-protocol"]');
      const coreRow = box('[data-step-id="core-agent-run"]');
      const coreCost = box('[data-step-id="core-agent-run"] .ov-pl-step__cost');
      const titleStyle = getComputedStyle(document.querySelector<HTMLElement>('[data-testid="overview-title-block"]')!);
      const pipelineStyle = getComputedStyle(document.querySelector<HTMLElement>('[data-testid="overview-pipeline"]')!);

      return {
        overviewLeft: overview.left,
        titleLeft: title.left,
        titleWidth: title.width,
        statusLeft: status.left,
        statusWidth: status.width,
        pipelineLeft: pipeline.left,
        pipelineWidth: pipeline.width,
        protocolLeft: protocol.left,
        protocolWidth: protocol.width,
        coreRowRight: coreRow.right,
        coreCostRight: coreCost.right,
        proseMax: titleStyle.maxWidth,
        pipelineMax: pipelineStyle.maxWidth,
      };
    });

    // The central measure primitive is explicitly left-aligned, not centered
    // inside the prompt pane.
    expect(Math.abs(geometry.titleLeft - geometry.overviewLeft)).toBeLessThanOrEqual(20);
    expect(Math.abs(geometry.statusLeft - geometry.overviewLeft)).toBeLessThanOrEqual(20);
    expect(Math.abs(geometry.pipelineLeft - geometry.overviewLeft)).toBeLessThanOrEqual(20);

    // Prose stays on a character measure; tabular blocks get the wider but
    // still capped pixel measure from the shared tokens.
    expect(Number.parseFloat(geometry.proseMax)).toBeGreaterThan(500);
    expect(Number.parseFloat(geometry.proseMax)).toBeLessThan(900);
    expect(geometry.pipelineMax).toBe('900px');
    expect(geometry.titleWidth).toBeLessThan(900);
    expect(geometry.statusWidth).toBeLessThanOrEqual(1040);
    expect(geometry.pipelineWidth).toBeLessThanOrEqual(940);

    // Pipeline metrics remain near the row instead of drifting toward the
    // viewport edge, and the right protocol/activity pane keeps its own space.
    expect(geometry.coreCostRight).toBeLessThanOrEqual(geometry.coreRowRight + 1);
    expect(geometry.coreRowRight).toBeLessThan(geometry.protocolLeft);
    expect(geometry.protocolWidth).toBeGreaterThan(260);

    if (RESULTS_DIR) {
      await overview.scrollIntoViewIfNeeded();
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'overview-content-measures-wide--mocked.png'),
        fullPage: true,
      });
    }
  });
});
