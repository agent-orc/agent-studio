import { type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const JOB_ID = 'agt-2427-remote-lifecycle';
const WATCH_PATH = 'C:/fixtures/agt-2427';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? 'test-results';
const REMOTE_SKIP_REASON = 'Executed remotely; this local pipeline step is not applicable.';

test.use({
  serviceWorkers: 'block',
  viewport: { width: 1600, height: 1200 },
});

function json(body: unknown) {
  return {
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  };
}

function step(id: string, displayName: string, kind: string, runMode = 'sequential') {
  return {
    id,
    displayName,
    kind,
    runMode,
    dependsOn: [],
    idempotent: kind !== 'core',
    stub: false,
  };
}

const pre = [
  step('pre-loop-guard', 'Loop guard', 'pre'),
  step('pre-model-qualification', 'Model qualification', 'pre'),
];
const core = [step('core-agent-run', 'Remote agent execution', 'core')];
const aspects = [
  step('aspect-requirement-fit', 'Requirement fit', 'aspect', 'parallel'),
  step('aspect-code-quality', 'Code quality', 'aspect', 'parallel'),
  step('aspect-documentation-impact', 'Documentation impact', 'aspect', 'parallel'),
  step('aspect-tests-and-evidence', 'Tests and evidence', 'aspect', 'parallel'),
];
const gate = step('post-build-test-gate', 'Transactional integration gate', 'tool');
const decision = step('post-orchestrator-decision', 'Remote Review Plane grade', 'orchestrator');
const post = [...aspects, gate, decision];
const allSteps = [...pre, ...core, ...post];

function executionStep(
  definition: ReturnType<typeof step>,
  status: string,
  extra: Record<string, unknown> = {},
) {
  return {
    stepId: definition.id,
    kind: definition.kind,
    status,
    durationMs: 0,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    ...extra,
  };
}

function costStep(
  definition: ReturnType<typeof step>,
  inputTokens: number,
  outputTokens: number,
  costUsd: number,
  calls: number,
) {
  return {
    stepId: definition.id,
    kind: definition.kind,
    model: 'gpt-5.4',
    tokenUsageSource: `Remote token ledger · ${calls} calls`,
    modelKnown: true,
    inputTokens,
    outputTokens,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    totalTokens: inputTokens + outputTokens,
    inputCostUsd: costUsd * 0.7,
    outputCostUsd: costUsd * 0.3,
    cacheReadCostUsd: 0,
    cacheCreationCostUsd: 0,
    costUsd,
  };
}

function pipelineResponse(projected: boolean) {
  const gateExecution = executionStep(gate, 'passed', {
    startedAt: '2026-07-30T08:20:00Z',
    completedAt: '2026-07-30T08:43:00Z',
    durationMs: 23 * 60 * 1000,
    reason: 'Transactional integration gate passed.',
  });
  const steps = projected
    ? [
        ...pre.map((item) => executionStep(item, 'skipped', { reason: REMOTE_SKIP_REASON })),
        executionStep(core[0], 'passed', {
          model: 'gpt-5.4',
          thinkingLevel: 'high',
          startedAt: '2026-07-30T08:00:00Z',
          completedAt: '2026-07-30T08:10:00Z',
          durationMs: 10 * 60 * 1000,
          inputTokens: 8_000,
          outputTokens: 800,
          tokenUsageSource: 'Remote token ledger · 3 calls',
          reason: 'Remote runner completed the fenced coding run.',
        }),
        ...aspects.map((item) => executionStep(item, 'skipped', { reason: REMOTE_SKIP_REASON })),
        gateExecution,
        executionStep(decision, 'passed', {
          model: 'gpt-5.4',
          startedAt: '2026-07-30T08:20:00Z',
          completedAt: '2026-07-30T08:20:00Z',
          inputTokens: 1_000,
          outputTokens: 200,
          tokenUsageSource: 'Remote token ledger · 1 call',
          verdict: 'pass',
          verdictSummary: 'Review Plane grade passed on the immutable result.',
          reason: 'Remote Review Plane verdict Pass (attempt rat-2427).',
        }),
      ]
    : [
        ...allSteps
          .filter((item) => item.id !== gate.id)
          .map((item) => executionStep(item, 'not-run')),
        gateExecution,
      ];
  const costs = projected
    ? [costStep(core[0], 8_000, 800, 0.28, 3), costStep(decision, 1_000, 200, 0.04, 1)]
    : [];

  return {
    pipeline: {
      id: 'standard-task-pipeline',
      displayName: 'Standard task pipeline',
      version: 1,
      pre,
      core,
      post,
      allSteps,
    },
    execution: {
      pipelineId: 'standard-task-pipeline',
      pipelineVersion: 1,
      jobId: JOB_ID,
      project: 'Agent Taskboard',
      startedAt: projected ? '2026-07-30T08:00:00Z' : '2026-07-30T08:20:00Z',
      completedAt: '2026-07-30T08:43:00Z',
      attempt: 1,
      previousAttempts: [],
      steps,
    },
    cost: {
      steps: costs,
      totalInputTokens: projected ? 9_000 : 0,
      totalOutputTokens: projected ? 1_000 : 0,
      totalCacheReadTokens: 0,
      totalCacheCreationTokens: 0,
      totalTokens: projected ? 10_000 : 0,
      totalInputCostUsd: projected ? 0.224 : 0,
      totalOutputCostUsd: projected ? 0.096 : 0,
      totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0,
      totalCostUsd: projected ? 0.32 : 0,
      anyModelUnknown: false,
    },
    config: {},
  };
}

function taskDetail() {
  const info = {
    id: JOB_ID,
    key: 'AGT-2427',
    taskKey: 'AGT-2427',
    jobKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'Pipeline step table reflects remote execution',
    state: '6-completed',
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.4',
    thinkingLevel: 'high',
    watchPath: WATCH_PATH,
    projectName: 'Agent Taskboard',
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/6-completed/${JOB_ID}`,
    sessionName: null,
    tokenSummary: null,
    lastUsage: null,
    execution: null,
    order: 1,
    commit: null,
    commits: [],
    codeActivityDetected: true,
    kind: 'task',
    mode: 'coding',
    allowWebAccess: false,
    summaryState: null,
    outcomeIssue: null,
    orchestratorVerdict: 'pass',
    ownerClientId: 'local-default',
  };
  return {
    info,
    promptMarkdown: '# AGT-2427 remote lifecycle fixture',
    statusMarkdown: '## Delivered\n\nRemote runner and Review Plane completed successfully.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function installRoutes(page: Page, projected: () => boolean) {
  await page.route('**/api/**', (route) => route.fulfill(json([])));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill(
      json({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    ),
  );
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill(
      json({
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        autoReview: [],
        humanReview: [],
        completed: [taskDetail().info],
        archive: [],
      }),
    ),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill(
      json([
        {
          name: 'Agent Taskboard',
          path: WATCH_PATH,
          rootPath: WATCH_PATH,
          repositoryPath: WATCH_PATH,
        },
      ]),
    ),
  );
  await page.route('**/api/v1/studio/runner/status**', (route) => route.fulfill(json({ projects: {} })));
  await page.route('**/api/environment**', (route) =>
    route.fulfill(
      json({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    ),
  );
  await page.route('**/api/clients', (route) => route.fulfill(json([])));
  await page.route('**/api/cli/usage**', (route) => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill(
      json({
        snapshots: [],
        ttlSeconds: 600,
      }),
    ),
  );

  const id = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), (route) =>
    route.fulfill(json(pipelineResponse(projected()))),
  );
  await page.route(new RegExp(`/api/tasks/${id}/agent-work-summary(\\?|$)`), (route) =>
    route.fulfill(
      json({
        calls: projected() ? 4 : 1,
        recovered: false,
        toolCalls: 0,
        toolCounts: [],
        startedAt: '2026-07-30T08:00:00Z',
        lastTouchAt: projected() ? '2026-07-30T08:20:00Z' : '2026-07-30T08:00:00Z',
        currentSessionId: null,
      }),
    ),
  );
  await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), (route) =>
    route.fulfill(
      json({
        runCount: 1,
        firstStartedAt: '2026-07-30T08:00:00Z',
        lastActivityAt: '2026-07-30T08:10:00Z',
        hasActiveRun: false,
        runs: [],
      }),
    ),
  );
  await page.route(new RegExp(`/api/tasks/${id}/output(\\?|$)`), (route) =>
    route.fulfill(json([])),
  );
  await page.route(new RegExp(`/api/tasks/${id}/session-events(\\?|$)`), (route) =>
    route.fulfill(json({ events: [], sessionChain: [] })),
  );
  await page.route(new RegExp(`/api/tasks/${id}/timeline(\\?|$)`), (route) =>
    route.fulfill(json([])),
  );
  await page.route(new RegExp(`/api/tasks/${id}/claude-session(\\?|$)`), (route) =>
    route.fulfill(json(null)),
  );
  await page.route(new RegExp(`/api/tasks/${id}/screenshots(\\?|$)`), (route) =>
    route.fulfill(json([])),
  );
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${id}(\\?|$)`), (route) =>
    route.fulfill(json(taskDetail())),
  );
}

async function expandAllPipelineSections(page: Page) {
  for (let index = 0; index < 3; index++) {
    await page
      .locator('[data-testid="overview-pipeline-phase"][aria-expanded="false"]')
      .evaluateAll((buttons) => buttons.forEach((button) => (button as HTMLButtonElement).click()));
    await page.waitForTimeout(100);
  }
}

test('AGT-2427 before and after: remote lifecycle, review, gate, ledger cost and calls', async ({
  page,
}, testInfo) => {
  let projected = false;
  await page.addInitScript(() => {
    try {
      localStorage.setItem(
        'taskboard.panesVisible',
        JSON.stringify({ prompt: true, protocol: false, git: false }),
      );
    } catch {
      // Private-mode storage can be unavailable.
    }
  });
  await installRoutes(page, () => projected);
  await page.goto(
    `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
  );

  const pipeline = page.getByTestId('overview-pipeline');
  await expect(pipeline).toBeVisible({ timeout: 10_000 });
  await expandAllPipelineSections(page);
  await expect(page.locator('[data-step-id="core-agent-run"]')).toHaveAttribute(
    'data-status',
    'not-run',
  );
  await expect(page.locator('[data-step-id="post-orchestrator-decision"]')).toHaveAttribute(
    'data-status',
    'not-run',
  );
  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('$0.00');
  await expect(page.getByTestId('agent-work-calls')).toContainText('1 calls');

  await mkdir(RESULTS_DIR, { recursive: true });
  const before = join(RESULTS_DIR, 'AGT-2427-remote-pipeline-before.png');
  await page.screenshot({ path: before, fullPage: true });
  await testInfo.attach('AGT-2427 remote pipeline before', {
    path: before,
    contentType: 'image/png',
  });

  projected = true;
  await page.goto(
    `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
  );
  await expect(pipeline).toBeVisible({ timeout: 10_000 });
  await expandAllPipelineSections(page);
  const coreRow = page.locator('[data-step-id="core-agent-run"]');
  const aspectRow = page.locator('[data-step-id="aspect-code-quality"]');
  const gateRow = page.locator('[data-step-id="post-build-test-gate"]');
  const decisionRow = page.locator('[data-step-id="post-orchestrator-decision"]');
  await expect(coreRow).toHaveAttribute('data-status', 'passed', { timeout: 15_000 });
  await expect(coreRow).toContainText('10m');
  await expect(aspectRow).toHaveAttribute('data-status', 'skipped');
  await expect(aspectRow).toContainText('executed remotely · not applicable');
  await expect(
    page.locator('[data-testid="overview-pipeline-group"][data-phase="aspect"]'),
  ).toContainText('Not applicable');
  await expect(gateRow).toHaveAttribute('data-status', 'passed');
  await expect(gateRow).toContainText('23m');
  await expect(decisionRow).toHaveAttribute('data-status', 'passed');
  await expect(decisionRow.getByTestId('overview-pipeline-step-decision')).toHaveAttribute(
    'data-verdict',
    'pass',
  );
  await expect(page.getByTestId('overview-pipeline-total-tokens')).toContainText('10k');
  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('$0.32');
  await expect(page.getByTestId('agent-work-calls')).toContainText('4 calls');

  const after = join(RESULTS_DIR, 'AGT-2427-remote-pipeline-after.png');
  await page.screenshot({ path: after, fullPage: true });
  await testInfo.attach('AGT-2427 remote pipeline after', {
    path: after,
    contentType: 'image/png',
  });
});
