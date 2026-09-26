import { test, expect, Locator, Page } from '@playwright/test';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * Visual + behavioural evidence for the agent-run-metrics bug fix
 * (bug-agent-run-metrics-missing-tokens-and-implausible-duration).
 *
 * Two symptoms, both surfaced in the task-detail Overview tab:
 *
 *   Symptom 1 - missing tokens: the CORE claude-opus-4-8 agent run reported
 *     no tokens in the Overview ("No token activity recorded ... agent did not
 *     report a CLI footer") even though it ran. The backend fix captures the
 *     Claude stream-json `result`-frame usage in ClaudeCliService and mirrors
 *     it onto the agent message bus, so the bus-backed per-job token summary
 *     (TaskInfo.tokenSummary) now includes the claude agent run. This spec
 *     feeds that post-fix summary shape and asserts the CORE row and latest
 *     run-history row render the claude token values.
 *
 *   Symptom 2 - implausible duration: the single persistent CORE pipeline step
 *     was overwritten with only the LAST run's duration (~55s for a task that
 *     ran 5 times across 3 attempts). The fix accumulates each run's duration
 *     onto the carried-forward total (CoreRunStepAccumulator), so the CORE row
 *     reflects all attempts and matches the Runs-section "total". This spec
 *     feeds a CORE step whose durationMs is the cumulative 125s and asserts the
 *     CORE row shows "2m 5s" (not the last run's 55s).
 *
 * Fully mocked - no backend or git repository needed. All /api responses are
 * stubbed to the shape the fixed backend now produces. When JOB_RESULTS_DIR is
 * set (orchestrator run) the rendered Overview is captured as PNG evidence.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/agent-run-metrics';
const JOB_ID = 'agent-run-metrics-fix-test';

// Post-fix per-job token summary: the claude agent run's usage now reaches the
// bus-backed summary. lastModel is the CORE agent model, proving the claude run
// (not just the Haiku aspects) contributes tokens to the Overview.
const CLAUDE_TOKEN_SUMMARY = {
  calls: 5,
  inputTokens: 12_840,
  outputTokens: 7_960,
  cacheReadTokens: 61_300,
  cacheCreationTokens: 4_120,
  totalTokens: 86_220,
  lastModel: 'claude-opus-4-8',
  lastUpdate: '2026-06-03T09:30:00Z',
  entries: [],
};

function makeDetail(
  state: string,
  key = 'FIXTURE-1',
  title = 'Agent-run metrics fixture',
  reviewEvidence: readonly Record<string, unknown>[] = [],
) {
  return {
    info: {
      id: JOB_ID,
      key,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title,
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-8',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
      sessionName: '0c1e3817-91c2-43a1-a1aa-9f73d161d4a2',
      lastUsage: null,
      tokenSummary: CLAUDE_TOKEN_SUMMARY,
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
    reviewEvidence,
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

const allSteps = [
  step('pre-loop-guard', 'Loop check', 'module'),
  step('core-agent-run', 'Agent execution', 'core'),
  step('aspect-requirement-fit', 'Requirement fit', 'aspect'),
];

// CORE step duration is the cumulative total across all 5 runs (125_000 ms),
// not the last run's 55s. status=passed: a completed multi-attempt run.
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
      completedAt: '2026-06-03T09:30:00Z',
      steps: [
        {
          stepId: 'pre-loop-guard',
          kind: 'module',
          status: 'passed',
          durationMs: 12,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          startedAt: '2026-06-02T08:00:00Z',
          completedAt: '2026-06-02T08:00:00Z',
        },
        {
          stepId: 'core-agent-run',
          kind: 'core',
          model: 'claude-opus-4-8',
          status: 'passed',
          durationMs: 125_000,
          inputTokens: 12_840,
          outputTokens: 7_960,
          cacheReadTokens: 61_300,
          cacheCreationTokens: 4_120,
          startedAt: '2026-06-02T08:00:01Z',
          completedAt: '2026-06-03T09:30:00Z',
        },
        {
          stepId: 'aspect-requirement-fit',
          kind: 'aspect',
          status: 'passed',
          durationMs: 4_200,
          inputTokens: 41_200,
          outputTokens: 39_900,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          startedAt: '2026-06-03T09:30:00Z',
          completedAt: '2026-06-03T09:30:04Z',
        },
      ],
    },
    // Full PipelineCostSummary shape (per-step + totals). The real backend
    // always populates every cost subfield; the per-step CORE entry is what
    // surfaces the claude agent run's tokens + cost on the CORE pipeline row —
    // the direct proof for symptom 1.
    cost: {
      steps: [
        {
          stepId: 'pre-loop-guard',
          kind: 'module',
          model: null,
          tokenUsageSource: null,
          modelKnown: true,
          inputTokens: 0,
          outputTokens: 0,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          totalTokens: 0,
          inputCostUsd: 0,
          outputCostUsd: 0,
          cacheReadCostUsd: 0,
          cacheCreationCostUsd: 0,
          costUsd: 0,
        },
        {
          stepId: 'core-agent-run',
          kind: 'core',
          model: 'claude-opus-4-8',
          tokenUsageSource: 'cli-footer',
          modelKnown: true,
          inputTokens: 12_840,
          outputTokens: 7_960,
          cacheReadTokens: 61_300,
          cacheCreationTokens: 4_120,
          totalTokens: 86_220,
          inputCostUsd: 0.1926,
          outputCostUsd: 0.597,
          cacheReadCostUsd: 0.0919,
          cacheCreationCostUsd: 0.0773,
          costUsd: 0.9588,
        },
        {
          stepId: 'aspect-requirement-fit',
          kind: 'aspect',
          model: 'claude-haiku-4-5',
          tokenUsageSource: 'orchestrator',
          modelKnown: true,
          inputTokens: 41_200,
          outputTokens: 39_900,
          cacheReadTokens: 0,
          cacheCreationTokens: 0,
          totalTokens: 81_100,
          inputCostUsd: 0.033,
          outputCostUsd: 0.1596,
          cacheReadCostUsd: 0,
          cacheCreationCostUsd: 0,
          costUsd: 0.1926,
        },
      ],
      totalInputTokens: 54_040,
      totalOutputTokens: 47_860,
      totalCacheReadTokens: 61_300,
      totalCacheCreationTokens: 4_120,
      totalTokens: 167_320,
      totalInputCostUsd: 0.2256,
      totalOutputCostUsd: 0.7566,
      totalCacheReadCostUsd: 0.0919,
      totalCacheCreationCostUsd: 0.0773,
      totalCostUsd: 1.1514,
      anyModelUnknown: false,
    },
    config: {},
  };
}

function runRecord(
  index: number,
  intent: string,
  startedAt: string,
  durationSeconds: number,
  options: { status?: string; userFollowup?: string | null; totalTokens?: number } = {},
) {
  return {
    index,
    intent,
    startedAt,
    endedAt: null,
    status: options.status ?? 'completed',
    cli: 'claude',
    model: 'claude-opus-4-8',
    thinkingLevel: 'high',
    exitCode: 0,
    durationSeconds,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: intent !== 'start',
    reason: null,
    userFollowup: options.userFollowup ?? null,
    lineStart: null,
    lineEnd: null,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    ...(options.totalTokens === undefined
      ? {}
      : {
          tokenSummary: {
            calls: 1,
            inputTokens: options.totalTokens,
            outputTokens: 0,
            cacheReadTokens: 0,
            cacheCreationTokens: 0,
            totalTokens: options.totalTokens,
            lastModel: 'claude-opus-4-8',
            lastUpdate: startedAt,
            entries: [],
          },
        }),
  };
}

// 3 attempts / 5 runs. Per-run durations sum to 125s. The last run is 55s -
// the value the buggy CORE row used to show on its own. totalDuration() sums
// all of them, so the Runs section reads "2m 5s total".
function multiRunTimeline() {
  return {
    runCount: 5,
    firstStartedAt: '2026-06-02T08:00:01Z',
    lastActivityAt: '2026-06-03T09:30:00Z',
    hasActiveRun: false,
    runs: [
      runRecord(1, 'start', '2026-06-02T08:00:01Z', 22),
      runRecord(2, 'continue', '2026-06-02T08:40:00Z', 18, {
        userFollowup: 'Address the browser evidence gap.',
      }),
      runRecord(3, 'recovery', '2026-06-02T09:10:00Z', 12),
      runRecord(4, 'continue', '2026-06-03T08:55:00Z', 18),
      runRecord(5, 'continue', '2026-06-03T09:30:00Z', 55, {
        totalTokens: 86_220,
      }),
    ],
  };
}

function overflowReviewEvidence(): readonly Record<string, unknown>[] {
  return Array.from({ length: 8 }, (_, index) => ({
    id: `overflow-evidence-${index + 1}`,
    source: index % 2 === 0 ? 'code-review' : 'task-check',
    severity: index % 3 === 0 ? 'warn' : 'info',
    title: `Responsive evidence finding ${index + 1}`,
    body: 'A deliberately long finding verifies that evidence content wraps inside its pane without creating horizontal or nested vertical scrolling.',
    createdAt: `2026-08-11T10:${String(index).padStart(2, '0')}:00Z`,
    runIndex: 5,
    artifacts: [],
    fileRefs: [`frontend/src/app/features/task-detail/very-long-evidence-reference-${index + 1}.ts`],
    acknowledged: false,
    followupJobId: null,
  }));
}

function legacyMissingCloseoutTimeline() {
  return {
    runCount: 1,
    firstStartedAt: '2026-08-08T16:08:43Z',
    lastActivityAt: '2026-08-08T16:08:43Z',
    hasActiveRun: false,
    runs: [
      {
        ...runRecord(1, 'start', '2026-08-08T16:08:43Z', 0, { status: 'unknown' }),
        endedAt: null,
        result: null,
        closeoutSource: 'legacy-missing',
        durationSeconds: null,
      },
    ],
  };
}

function mkt21HealedTimeline() {
  return {
    runCount: 3,
    firstStartedAt: '2026-08-08T16:00:55Z',
    lastActivityAt: '2026-08-08T16:27:47Z',
    hasActiveRun: false,
    runs: [
      {
        ...runRecord(1, 'start', '2026-08-08T16:00:55Z', 632.6),
        endedAt: '2026-08-08T16:11:29Z',
        result: 'completed',
        closeoutSource: 'timeline',
      },
      {
        ...runRecord(2, 'continue', '2026-08-08T16:27:09Z', 18.2),
        endedAt: '2026-08-08T16:27:28Z',
        result: 'completed',
        closeoutSource: 'timeline',
      },
      {
        ...runRecord(3, 'continue', '2026-08-08T16:27:36Z', 11.1),
        endedAt: '2026-08-08T16:27:47Z',
        result: 'completed',
        closeoutSource: 'timeline',
      },
    ],
  };
}

function triggerProvenanceTimeline() {
  return {
    runCount: 3,
    firstStartedAt: '2026-09-18T08:00:00Z',
    lastActivityAt: '2026-09-18T09:30:00Z',
    hasActiveRun: false,
    runs: [
      {
        ...runRecord(1, 'start', '2026-09-18T08:00:00Z', 30),
        trigger: 'initial',
        triggeredBy: 'pipeline',
        triggerReason: 'Initial task run.',
      },
      {
        ...runRecord(2, 'start', '2026-09-18T09:00:00Z', 20),
        trigger: 'review-concern',
        triggeredBy: 'pipeline',
        triggerReason: 'Review concern round 1 of 1.',
        triggerSource: 'review=review_01bb31c8;aspects=code-quality',
      },
      runRecord(3, 'start', '2026-09-18T09:30:00Z', 10),
    ],
  };
}

async function installRoutes(
  page: Page,
  state: string,
  timeline: ReturnType<typeof multiRunTimeline>
    | ReturnType<typeof legacyMissingCloseoutTimeline>
    | ReturnType<typeof mkt21HealedTimeline>
    | ReturnType<typeof triggerProvenanceTimeline>
    = multiRunTimeline(),
  detailIdentity?: { key: string; title: string },
  reviewEvidence: readonly Record<string, unknown>[] = [],
): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(
    state,
    detailIdentity?.key,
    detailIdentity?.title,
    reviewEvidence,
  );

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {
      // A more specific route may already have completed this request.
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
      body: JSON.stringify(timeline),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/agent-work-summary(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        calls: 5,
        recovered: true,
        toolCalls: 96,
        toolCounts: [
          { tool: 'Edit', count: 38 },
          { tool: 'Read', count: 41 },
          { tool: 'Bash', count: 17 },
        ],
        startedAt: '2026-06-02T08:00:01Z',
        lastTouchAt: '2026-06-03T09:30:00Z',
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(pipelineBody()),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/artifacts(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        jobId: JOB_ID,
        files: Array.from({ length: 4 }, (_, index) => ({
          name: index === 0 ? 'prompt.md' : `result-${index}.md`,
          sizeBytes: 128 + index,
          mtime: '2026-08-11T10:00:00Z',
          kind: index === 0 ? 'prompt' : 'other',
        })),
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {
      // Best effort: the mocked page remains usable if the dev-only dialog races away.
    });
  }
}

async function openDetail(page: Page): Promise<void> {
  await page.goto(
    `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
  );
  await dismissErrorDialog(page);
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
}

async function expectNoHorizontalOverflow(locator: Locator): Promise<void> {
  await expect.poll(async () => locator.evaluate(
    element => element.scrollWidth <= element.clientWidth + 1,
  )).toBe(true);
}

async function verticalScrollOwners(root: Locator): Promise<string[]> {
  return root.evaluate((element) => {
    const nodes = [element, ...Array.from(element.querySelectorAll<HTMLElement>('*'))];
    return nodes
      .filter((node) => {
        const overflowY = getComputedStyle(node).overflowY;
        return (overflowY === 'auto' || overflowY === 'scroll')
          && node.scrollHeight > node.clientHeight + 1;
      })
      .map((node) => node.getAttribute('data-testid') || node.tagName.toLowerCase());
  });
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

test.describe('Overview agent-run metrics fix (tokens + cumulative duration)', () => {
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

  test('claude agent tokens render and the CORE row shows the cumulative duration', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 1200 });
    await installRoutes(page, '3-progress');
    await openDetail(page);

    // The CORE Agent-execution row shows the cumulative 125s
    // ("2m 5s"), not the last run's 55s.
    await page
      .locator('[data-testid="overview-pipeline-phase"][data-phase="core"]')
      .click();
    const coreRow = page.locator('[data-step-id="core-agent-run"]');
    const coreDuration = coreRow.getByTestId('overview-pipeline-step-duration');
    await expect(coreDuration).toBeVisible();
    await expect(coreDuration).toHaveText('2m 5s');

    // The Runs section "total" matches the CORE row (the two surfaces that
    // used to disagree: 55s row vs the summed total).
    const runsDuration = page.getByTestId('overview-runs-duration');
    await expect(runsDuration).toBeVisible();
    await expect(runsDuration).toHaveText('2m 5s total');

    // The Overview owns a compact, card-scoped history rather than hiding
    // trigger/result/duration in icon tooltips.
    const runs = page.getByTestId('overview-runs');
    await expect(runs.getByTestId('overview-runs-count')).toHaveText('5 runs');
    const runRows = runs.getByTestId('overview-run-row');
    await expect(runRows).toHaveCount(5);
    await expect(runRows.first()).toHaveAttribute('data-run-index', '5');
    await expect(runRows.first().getByTestId('overview-run-trigger')).toHaveText('Not recorded');
    await expect(runRows.first().getByTestId('overview-run-result')).toContainText('Completed');
    await expect(runRows.first().getByTestId('overview-run-duration')).toHaveText('55s');
    await expect(runRows.first().getByTestId('overview-run-tokens')).toHaveText('86k tokens');
    await expect(runRows.nth(3).getByTestId('overview-run-trigger')).toHaveText('Not recorded');
    await expect(runs.getByTestId('overview-runs-agent')).toHaveText(
      'Claude Code · opus 4.8 · high',
    );
    await expect(runs.getByTestId('overview-run-engine')).toHaveCount(0);
    await expect(runRows.last().getByTestId('overview-run-id')).toHaveText('Run #1');
    expect(await runRows.last().getByTestId('overview-run-id').evaluate(
      (element) => element.scrollWidth <= element.clientWidth,
    )).toBe(true);

    // Symptom 1 (direct): the CORE Agent-execution row now carries the claude
    // run's own token + cost values on the pipeline row, not "—".
    const coreTokens = coreRow.getByTestId('overview-pipeline-step-tokens');
    await expect(coreTokens).toHaveText('86k');
    const coreCost = coreRow.getByTestId('overview-pipeline-step-cost');
    await expect(coreCost).toHaveText('$0.96');

    // The rendered Overview is clean: no runtime-error dialog over the pane
    // (the fixture supplies a complete cost summary, as the real backend does).
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

    if (RESULTS_DIR) {
      await setTheme(page, 'dark');
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'overview-agent-metrics-fix--mocked.png'),
        fullPage: true,
      });
    }
  });

  test('legacy run without terminal evidence is labelled honestly', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await installRoutes(page, '5-human-review', legacyMissingCloseoutTimeline());
    await openDetail(page);

    const runs = page.getByTestId('overview-runs');
    await expect(runs.getByTestId('overview-runs-duration')).toHaveText('2m 5s total');
    const row = runs.getByTestId('overview-run-row');
    await expect(row.getByTestId('overview-run-result')).toContainText(
      'Not recorded (legacy run)',
    );
    await expect(row.getByTestId('overview-run-duration')).toHaveText(
      'Not recorded (legacy run)',
    );

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);

      if (RESULTS_DIR) {
        await runs.screenshot({
          path: path.join(RESULTS_DIR, `runs-panel-legacy-closeout-${theme}--mocked.png`),
        });
      }
    }
  });

  test('run cards show recorded review provenance and do not guess legacy triggers', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await installRoutes(page, '5-human-review', triggerProvenanceTimeline());
    await openDetail(page);

    const runs = page.getByTestId('overview-runs');
    const rows = runs.getByTestId('overview-run-row');
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0).getByTestId('overview-run-trigger')).toHaveText('Not recorded');
    await expect(rows.nth(1).getByTestId('overview-run-trigger')).toHaveText(
      'Review concern: Code Quality (review_01bb31c8)',
    );
    await expect(rows.nth(1).getByTestId('overview-run-trigger')).toHaveAttribute(
      'href',
      /remote-review-grade-review_01bb31c8\.md/,
    );
    await expect(rows.nth(2).getByTestId('overview-run-trigger')).toHaveText('Initial start');

    if (RESULTS_DIR) {
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        await runs.screenshot({
          path: path.join(RESULTS_DIR, `run-trigger-provenance-${theme}--mocked.png`),
        });
      }
    }
  });

  test('MKT-21 legacy terminal events heal every row and the shared agent is not repeated', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await installRoutes(page, '6-completed', mkt21HealedTimeline(), {
      key: 'MKT-21',
      title: 'MKT-21 · Marketing Studio run-history close-out',
    });
    await openDetail(page);

    const runs = page.getByTestId('overview-runs');
    const rows = runs.getByTestId('overview-run-row');
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0).getByTestId('overview-run-result')).toContainText('Completed');
    await expect(rows.nth(0).getByTestId('overview-run-duration')).toHaveText('11s');
    await expect(rows.nth(1).getByTestId('overview-run-result')).toContainText('Completed');
    await expect(rows.nth(1).getByTestId('overview-run-duration')).toHaveText('18s');
    await expect(rows.nth(2).getByTestId('overview-run-result')).toContainText('Completed');
    await expect(rows.nth(2).getByTestId('overview-run-duration')).toHaveText('10m 33s');
    await expect(rows.getByText('Running', { exact: true })).toHaveCount(0);
    await expect(runs.getByTestId('overview-runs-agent')).toHaveText(
      'Claude Code · opus 4.8 · high',
    );
    await expect(runs.getByTestId('overview-run-engine')).toHaveCount(0);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      if (RESULTS_DIR) {
        await runs.screenshot({
          path: path.join(RESULTS_DIR, `mkt-21-runs-healed-${theme}--mocked.png`),
        });
      }
    }
  });

  test('tabs use actual overflow width, keep badges in the menu, and share one pane scroll surface', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem(
        'taskboard.panesVisible',
        JSON.stringify({ prompt: true, protocol: true, git: true }),
      );
      localStorage.setItem(
        'taskboard.paneWeights',
        JSON.stringify({ prompt: 1, protocol: 1, git: 1 }),
      );
    });
    await installRoutes(page, '6-completed', multiRunTimeline(), undefined, overflowReviewEvidence());

    for (const viewport of [
      { name: 'wide', width: 1440, height: 900 },
      { name: 'narrow', width: 980, height: 720 },
    ] as const) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      await openDetail(page);
      const promptPane = page.getByTestId('pane-prompt');
      const promptHeader = page.getByTestId('pane-prompt-header');
      const protocolHeader = page.getByTestId('pane-protocol-header');
      const promptBody = page.getByTestId('pane-prompt-body');
      const runs = page.getByTestId('overview-runs');
      const pipeline = page.getByTestId('overview-pipeline');

      const headerGeometry = await promptHeader.evaluate((header) => {
        const tabs = Array.from(header.querySelectorAll<HTMLElement>('[role="tab"]'));
        const more = header.querySelector<HTMLElement>('[data-testid="pane-tabs-overflow"]');
        const maximize = header.querySelector<HTMLElement>('[data-testid="pane-header-maximize"]');
        const close = header.querySelector<HTMLElement>('[data-testid="pane-header-hide"]');
        const headerBox = header.getBoundingClientRect();
        if (!maximize || !close) return null;
        return {
          headerRight: headerBox.right,
          lastTabRight: tabs.at(-1)?.getBoundingClientRect().right ?? 0,
          moreLeft: more?.getBoundingClientRect().left ?? null,
          moreRight: more?.getBoundingClientRect().right ?? null,
          maximizeLeft: maximize.getBoundingClientRect().left,
          maximizeRight: maximize.getBoundingClientRect().right,
          closeLeft: close.getBoundingClientRect().left,
          closeRight: close.getBoundingClientRect().right,
          tabCount: tabs.length,
        };
      });
      expect(headerGeometry).not.toBeNull();
      expect(headerGeometry!.tabCount).toBeGreaterThanOrEqual(1);
      if (headerGeometry!.moreLeft !== null && headerGeometry!.moreRight !== null) {
        expect(headerGeometry!.lastTabRight).toBeLessThanOrEqual(headerGeometry!.moreLeft + 1);
        expect(headerGeometry!.moreRight).toBeLessThanOrEqual(headerGeometry!.maximizeLeft - 4);
      } else {
        expect(headerGeometry!.lastTabRight).toBeLessThanOrEqual(headerGeometry!.maximizeLeft - 4);
      }
      expect(headerGeometry!.maximizeRight).toBeLessThanOrEqual(headerGeometry!.closeLeft - 4);
      expect(headerGeometry!.closeRight).toBeLessThanOrEqual(headerGeometry!.headerRight + 1);

      const evidenceLabel = await promptHeader.locator('.pane-tab__label').first().evaluate((label) => {
        const style = getComputedStyle(label);
        return {
          overflow: style.overflow,
          textOverflow: style.textOverflow,
          whiteSpace: style.whiteSpace,
        };
      });
      expect(evidenceLabel).toEqual({
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      });

      const promptOverflow = promptHeader.getByTestId('pane-tabs-overflow');
      await expect(promptOverflow).toHaveAttribute('aria-label', /More tabs: \d+ hidden tabs?/);
      await expect(promptOverflow).toHaveText('⋯');
      await promptOverflow.click();
      await expect(page.getByTestId('pane-tabs-overflow-panel')).toBeVisible();
      const docsOverflowItem = page.getByTestId('pane-tabs-overflow-item-description');
      await expect(docsOverflowItem).toContainText('Docs');
      await expect(docsOverflowItem.getByText('4', { exact: true })).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('pane-tabs-overflow-panel')).toBeHidden();

      const protocolOverflow = protocolHeader.getByTestId('pane-tabs-overflow');
      if (viewport.name === 'wide') {
        await expect(protocolHeader.getByRole('tab')).toHaveCount(3);
        await expect(protocolOverflow).toHaveCount(0);
      } else {
        await expect(protocolOverflow).toBeVisible();
        await protocolOverflow.click();
        await expect(page.getByTestId('pane-tabs-overflow-panel')).toBeVisible();
        await expect(page.getByTestId('pane-tabs-overflow-item-protocol')).toHaveText('Result');
        await page.keyboard.press('Escape');
      }

      await expectNoHorizontalOverflow(promptPane);
      await expectNoHorizontalOverflow(promptBody);
      // The 198 px three-pane minimum is the tab-strip stress case. Pipeline
      // metric columns retain their separate, wider content contract and are
      // covered at the regular three-pane width above.
      if (viewport.name === 'wide') {
        await expectNoHorizontalOverflow(pipeline);
        await expectNoHorizontalOverflow(runs);
        expect(await runs.evaluate((host) => {
          const boundary = host.getBoundingClientRect();
          const rows = Array.from(host.querySelectorAll<HTMLElement>('[data-testid="overview-run-row"]'));
          return rows.every((row) => {
            const box = row.getBoundingClientRect();
            return box.left >= boundary.left - 1 && box.right <= boundary.right + 1;
          });
        })).toBe(true);
      }
      expect(await verticalScrollOwners(promptBody)).toEqual(['pane-prompt-body']);

      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        await promptBody.evaluate((element) => { element.scrollTop = element.scrollHeight; });
        if (RESULTS_DIR) {
          await page.screenshot({
            path: path.join(
              RESULTS_DIR,
              `agt-2691-tab-overflow-after--${viewport.name}-${theme}--mocked.png`,
            ),
            fullPage: false,
          });
        }
      }

      const inlineEvidenceTab = page.getByTestId('prompt-tab-evidence');
      if (await inlineEvidenceTab.isVisible()) {
        await inlineEvidenceTab.click();
      } else {
        await promptOverflow.click();
        await page.getByTestId('pane-tabs-overflow-item-evidence').click();
      }
      const evidence = page.getByTestId('evidence-view');
      await expect(evidence).toBeVisible();
      await expect(page.getByTestId('review-evidence-count')).toHaveText('8 findings');
      await expectNoHorizontalOverflow(evidence);
      await expectNoHorizontalOverflow(page.getByTestId('review-evidence-panel'));
      expect(await verticalScrollOwners(promptBody)).toEqual(['pane-prompt-body']);
    }
  });
});
