import { type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const JOB_ID = 'pipeline-step-usage-fixture';
const WATCH_PATH = 'C:/fixtures/agent-taskboard';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

test.use({ serviceWorkers: 'block' });

function json(body: unknown) {
  return {
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  };
}

function jobDetail() {
  return {
    info: {
      id: JOB_ID,
      key: 'AGT-2253',
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Pipeline step usage fixture',
      state: '4-auto-review',
      agent: 'codex',
      cliType: 'codex',
      model: 'claude-opus-4-8',
      thinkingLevel: null,
      watchPath: WATCH_PATH,
      projectName: 'agent-taskboard',
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/4-auto-review/${JOB_ID}`,
      sessionName: 'fixture-session',
      tokenSummary: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      codeActivityDetected: false,
      useOwnSession: null,
      kind: 'task',
      mode: 'coding',
      allowWebAccess: false,
      summaryState: null,
      outcomeIssue: null,
      orchestratorVerdict: null,
      ownerClientId: 'local-default',
    },
    promptMarkdown: '# Pipeline step usage fixture',
    statusMarkdown: '## Done\n\nFixture status.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

// A run with the core agent on Opus plus the aspect reviewer on Haiku, so each
// pipeline step can surface its own usage where the step is rendered.
function runRecord(attempt: number, startedAt: string, completedAt: string) {
  return {
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: JOB_ID,
    project: 'agent-taskboard',
    startedAt,
    completedAt,
    attempt,
    previousAttempts: [],
    steps: [
      {
        stepId: 'core-agent-run', kind: 'core', model: 'claude-opus-4-8', status: 'passed',
        startedAt, completedAt, durationMs: 120000,
        inputTokens: 100000, outputTokens: 10000, cacheReadTokens: 0, cacheCreationTokens: 0,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported', reason: null, verdict: null, verdictSummary: null,
      },
      {
        stepId: 'aspect-code-quality', kind: 'aspect', model: 'claude-haiku-4-5', status: 'passed',
        startedAt, completedAt, durationMs: 9000,
        inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 0, cacheCreationTokens: 0,
        tokenUsageSource: 'AGENT (CLI FOOTER) / reported', reason: null, verdict: 'pass', verdictSummary: null,
      },
    ],
  };
}

function costStep(stepId: string, kind: string, model: string, totalTokens: number, costUsd: number) {
  return {
    stepId,
    kind,
    model,
    tokenUsageSource: 'AGENT (CLI FOOTER) / reported',
    modelKnown: true,
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

function pipeline() {
  const current = runRecord(2, '2026-06-09T10:00:00Z', '2026-06-09T10:02:00Z');
  const previous = runRecord(1, '2026-06-08T10:00:00Z', '2026-06-08T10:02:00Z');
  const coreStep = {
    id: 'core-agent-run', displayName: 'Agent execution', kind: 'core',
    runMode: 'sequential', dependsOn: [], idempotent: false, stub: false,
  };
  const aspectStep = {
    id: 'aspect-code-quality', displayName: 'Code quality', kind: 'aspect',
    runMode: 'parallel', dependsOn: [], idempotent: true, stub: false,
  };
  const wikiSteps = [
    { id: 'post-wiki-maintenance', displayName: 'Wiki maintenance', kind: 'tool', runMode: 'sequential', dependsOn: ['core-agent-run'], idempotent: true, stub: false },
    { id: 'post-wiki-learnings', displayName: 'Wiki learnings', kind: 'tool', runMode: 'sequential', dependsOn: ['aspect-code-quality'], idempotent: true, stub: false },
    { id: 'post-agents-wiki-sync', displayName: 'Agent skills / AGENTS wiki sync', kind: 'tool', runMode: 'sequential', dependsOn: ['core-agent-run'], idempotent: true, stub: false },
  ];
  return {
    pipeline: {
      id: 'standard-task-pipeline', displayName: 'Standard', version: 1,
      pre: [], core: [coreStep], post: [aspectStep, ...wikiSteps],
      allSteps: [coreStep, aspectStep, ...wikiSteps],
    },
    execution: { ...current, previousAttempts: [previous] },
    cost: {
      steps: [
        costStep('core-agent-run', 'core', 'claude-opus-4-8', 110000, 0.75),
        costStep('aspect-code-quality', 'aspect', 'claude-haiku-4-5', 1200000, 2.0),
      ],
      totalInputTokens: 1100000, totalOutputTokens: 210000,
      totalCacheReadTokens: 0, totalCacheCreationTokens: 0, totalTokens: 1310000,
      totalInputCostUsd: 0, totalOutputCostUsd: 0, totalCacheReadCostUsd: 0,
      totalCacheCreationCostUsd: 0, totalCostUsd: 2.75, anyModelUnknown: false,
    },
    tokensByModel: {
      runs: [
        {
          attempt: 1, current: false, startedAt: previous.startedAt, completedAt: previous.completedAt,
          models: [
            { model: 'claude-haiku-4-5', modelKnown: true, steps: 1, inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 1200000, costUsd: 2.0 },
            { model: 'claude-opus-4-8', modelKnown: true, steps: 1, inputTokens: 100000, outputTokens: 10000, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 110000, costUsd: 0.75 },
          ],
          totalTokens: 1310000, totalCostUsd: 2.75, anyModelUnknown: false,
        },
        {
          attempt: 2, current: true, startedAt: current.startedAt, completedAt: current.completedAt,
          models: [
            { model: 'claude-haiku-4-5', modelKnown: true, steps: 1, inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 1200000, costUsd: 2.0 },
            { model: 'claude-opus-4-8', modelKnown: true, steps: 1, inputTokens: 100000, outputTokens: 10000, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 110000, costUsd: 0.75 },
          ],
          totalTokens: 1310000, totalCostUsd: 2.75, anyModelUnknown: false,
        },
      ],
      totalByModel: [
        { model: 'claude-haiku-4-5', modelKnown: true, steps: 2, inputTokens: 2000000, outputTokens: 400000, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 2400000, costUsd: 4.0 },
        { model: 'claude-opus-4-8', modelKnown: true, steps: 2, inputTokens: 200000, outputTokens: 20000, cacheReadTokens: 0, cacheCreationTokens: 0, totalTokens: 220000, costUsd: 1.5 },
      ],
      totalTokens: 2620000, totalCostUsd: 5.5, anyModelUnknown: false,
    },
    config: {
      'core-agent-run': {
        enabled: true, canDisable: false, enabledSource: 'catalogue',
        activation: { state: 'active', source: 'global', reason: 'Enabled by the global catalogue default.' },
      },
      'aspect-code-quality': {
        enabled: true, canDisable: true, enabledSource: 'catalogue',
        activation: { state: 'active', source: 'global', reason: 'Enabled by the global catalogue default.' },
      },
      'post-wiki-maintenance': {
        enabled: true, canDisable: true, enabledSource: 'catalogue',
        activation: {
          state: 'active', source: 'condition',
          reason: 'Enabled by the global catalogue default; condition "task has tag \'wiki\'" matched this run.',
        },
      },
      'post-wiki-learnings': {
        enabled: false, canDisable: true, enabledSource: 'project',
        activation: { state: 'inactive', source: 'project', reason: 'Disabled by the project override.' },
      },
      'post-agents-wiki-sync': {
        enabled: true, canDisable: true, enabledSource: 'catalogue',
        activation: {
          state: 'skipped', source: 'condition',
          reason: 'Condition "an aspect failed" did not match this run.',
        },
      },
    },
    onDemand: {
      plannedStepIds: ['post-wiki-maintenance'],
      attempts: [
        {
          stepId: 'post-wiki-maintenance', attempt: 1, status: 'Warn',
          summary: 'created the first maintenance note',
          startedAt: '2026-07-11T00:15:00Z', finishedAt: '2026-07-11T00:15:01Z', durationMs: 700,
          artifactRef: 'results/post-steps/post-wiki-maintenance-attempt-001.md',
        },
        {
          stepId: 'post-wiki-maintenance', attempt: 2, status: 'Ok',
          summary: 'updated existing problem entry',
          startedAt: '2026-07-12T00:15:00Z', finishedAt: '2026-07-12T00:15:01Z', durationMs: 850,
          artifactRef: 'results/post-steps/post-wiki-maintenance-attempt-002.md',
        },
      ],
    },
  };
}

function pipelineWithMissingPrices(mixed: boolean) {
  const fixture = pipeline();
  const gap = { modelId: 'gpt-5.6-sol', reason: 'NoPriceForDate', affectedRuns: 1 };
  const executionSteps = fixture.execution.steps.map((step, index) =>
    !mixed || index === 0 ? { ...step, model: 'gpt-5.6-sol' } : step);
  const unavailable = (step: ReturnType<typeof costStep>) => ({
    ...step,
    model: 'gpt-5.6-sol',
    modelKnown: false,
    pricingGaps: [gap],
    inputCostUsd: 0,
    outputCostUsd: 0,
    cacheReadCostUsd: 0,
    cacheCreationCostUsd: 0,
    costUsd: 0,
  });
  const core = unavailable(fixture.cost.steps[0]);
  const aspect = mixed ? fixture.cost.steps[1] : unavailable(fixture.cost.steps[1]);
  return {
    ...fixture,
    execution: {
      ...fixture.execution,
      steps: executionSteps,
      previousAttempts: fixture.execution.previousAttempts.map(run => ({
        ...run,
        steps: run.steps.map((step, index) =>
          !mixed || index === 0 ? { ...step, model: 'gpt-5.6-sol' } : step),
      })),
    },
    cost: {
      ...fixture.cost,
      steps: [core, aspect],
      totalInputCostUsd: mixed ? 1.2 : 0,
      totalOutputCostUsd: mixed ? 0.8 : 0,
      totalCostUsd: mixed ? 2 : 0,
      anyModelUnknown: true,
      unpricedRuns: 1,
      pricingGaps: [gap],
    },
  };
}

function pipelineWithResolvedGpt56Price() {
  const fixture = pipeline();
  const priceRun = (run: ReturnType<typeof runRecord>) => ({
    ...run,
    steps: run.steps.map(step => ({ ...step, model: 'gpt-5.6-sol' })),
  });
  const pricedModels = [{
    model: 'gpt-5.6-sol', modelKnown: true, unpricedRuns: 0, pricingGaps: [], steps: 2,
    inputTokens: 1100000, outputTokens: 210000, cacheReadTokens: 0,
    cacheCreationTokens: 0, totalTokens: 1310000, costUsd: 11.8,
  }];
  return {
    ...fixture,
    execution: {
      ...priceRun(fixture.execution),
      previousAttempts: fixture.execution.previousAttempts.map(priceRun),
    },
    cost: {
      ...fixture.cost,
      steps: [
        { ...costStep('core-agent-run', 'core', 'gpt-5.6-sol', 110000, 0.8), pricingGaps: [] },
        { ...costStep('aspect-code-quality', 'aspect', 'gpt-5.6-sol', 1200000, 11), pricingGaps: [] },
      ],
      totalInputCostUsd: 5.5,
      totalOutputCostUsd: 6.3,
      totalCostUsd: 11.8,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
    },
    tokensByModel: {
      runs: fixture.tokensByModel.runs.map(run => ({
        ...run,
        models: pricedModels,
        totalCostUsd: 11.8,
        anyModelUnknown: false,
        pricingGaps: [],
      })),
      totalByModel: [{ ...pricedModels[0], steps: 4, inputTokens: 2200000,
        outputTokens: 420000, totalTokens: 2620000, costUsd: 23.6 }],
      totalTokens: 2620000,
      totalCostUsd: 23.6,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
    },
  };
}

function runTimeline() {
  return {
    runCount: 2,
    firstStartedAt: '2026-06-08T10:00:00Z',
    lastActivityAt: '2026-06-09T10:02:00Z',
    hasActiveRun: false,
    runs: [],
  };
}

function pipelineWithSixRunsAndPartialUsage() {
  const fixture = pipeline();
  const decision = {
    id: 'post-orchestrator-decision', displayName: 'Final verdict', kind: 'orchestrator',
    runMode: 'sequential', dependsOn: ['aspect-code-quality'], idempotent: true, stub: false,
  };
  const recordedRuns = Array.from({ length: 6 }, (_, index) => {
    const available = index !== 2;
    const model = {
      model: 'gpt-5.6-sol', modelKnown: true, unpricedRuns: 0, pricingGaps: [], steps: 1,
      inputTokens: available ? 80_000 : 0,
      outputTokens: available ? 10_000 : 0,
      cacheReadTokens: available ? 20_000 : 0,
      cacheCreationTokens: available ? 5_000 : 0,
      totalTokens: available ? 115_000 : 0,
      costUsd: available ? 1.1 : 0,
    };
    return {
      attempt: index + 1,
      current: index === 5,
      startedAt: `2026-08-11T${String(8 + index).padStart(2, '0')}:00:00Z`,
      completedAt: `2026-08-11T${String(8 + index).padStart(2, '0')}:20:00Z`,
      models: available ? [model] : [],
      totalTokens: model.totalTokens,
      totalCostUsd: model.costUsd,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
      tokenUsageAvailable: available,
    };
  });
  return {
    ...fixture,
    pipeline: {
      ...fixture.pipeline,
      post: [...fixture.pipeline.post, decision],
      allSteps: [...fixture.pipeline.allSteps, decision],
    },
    execution: {
      ...fixture.execution,
      steps: [
        ...fixture.execution.steps.map(step => step.stepId === 'core-agent-run'
          ? { ...step, model: 'gpt-5.6-sol', verdict: 'done' }
          : step),
        {
          stepId: decision.id, kind: decision.kind, model: 'gpt-5.6-sol', status: 'passed',
          startedAt: '2026-08-11T14:21:00Z', completedAt: '2026-08-11T14:21:02Z',
          durationMs: 2_000, inputTokens: 0, outputTokens: 0,
          cacheReadTokens: 0, cacheCreationTokens: 0,
          tokenUsageSource: null, reason: null, verdict: 'accept',
          verdictSummary: 'All checks passed; route the task to human review.',
        },
      ],
    },
    tokensByModel: {
      runs: recordedRuns,
      totalByModel: [{
        model: 'gpt-5.6-sol', modelKnown: true, unpricedRuns: 0, pricingGaps: [], steps: 5,
        inputTokens: 400_000, outputTokens: 50_000, cacheReadTokens: 100_000,
        cacheCreationTokens: 25_000, totalTokens: 575_000, costUsd: 5.5,
      }],
      totalTokens: 575_000,
      totalCostUsd: 5.5,
      anyModelUnknown: false,
      unpricedRuns: 0,
      pricingGaps: [],
      missingTokenRuns: 1,
    },
  };
}

function sixRunTimeline() {
  const runs = Array.from({ length: 6 }, (_, index) => ({
    index: index + 1,
    intent: index === 0 ? 'start' : 'continue',
    startedAt: `2026-08-11T${String(8 + index).padStart(2, '0')}:00:00Z`,
    endedAt: `2026-08-11T${String(8 + index).padStart(2, '0')}:20:00Z`,
    status: 'completed',
    model: 'gpt-5.6-sol',
    durationSeconds: 1_200,
    resumed: index > 0,
  }));
  return {
    runCount: 6,
    firstStartedAt: runs[0].startedAt,
    lastActivityAt: runs[5].endedAt,
    hasActiveRun: false,
    runs,
  };
}

async function installFixtureRoutes(page: Page) {
  await page.route('**/api/**', route => route.fulfill(json([])));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/tasks/grouped**', route => route.fulfill(json({
    preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    autoReview: [jobDetail().info], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([
    { name: 'agent-taskboard', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ])));
  await page.route('**/api/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route('**/api/environment**', route => route.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/clients', route => route.fulfill(json([])));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ snapshots: [], ttlSeconds: 600 })));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill(json({
    projectName: 'agent-taskboard', includesHistory: true, count: 0, items: [],
  })));
  await page.route('**/api/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  })));

  const id = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => route.fulfill(json(pipeline())));
  await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), route => route.fulfill(json(runTimeline())));
  await page.route(new RegExp(`/api/tasks/${id}/output(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}/session-events(\\?|$)`), route => route.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(new RegExp(`/api/tasks/${id}/agent-work(\\?|$)`), route => route.fulfill(json(null)));
  await page.route(new RegExp(`/api/tasks/${id}/timeline(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}/claude-session(\\?|$)`), route => route.fulfill(json(null)));
  await page.route(new RegExp(`/api/tasks/${id}/screenshots(\\?|$)`), route => route.fulfill(json([])));
  await page.route(new RegExp(`/api/tasks/${id}(\\?|$)`), route => route.fulfill(json(jobDetail())));
  await page.route(/\/api\/token-pricing\/calculate/, route => route.fulfill(json({
    provider: 'TokenEconomy',
    items: [{
      model: 'claude-opus-4-8', label: 'Agent execution',
      inputTokens: 88000, outputTokens: 22000, cacheReadTokens: 0, cacheWriteTokens: 0,
      calculatedAt: '2026-06-09T10:00:00Z',
      estimate: {
        inputUsd: 4.8, outputUsd: 1.2, cacheReadUsd: 0, cacheWriteUsd: 0, total: 6,
        modelId: 'claude-opus-4-8', modelKnown: true, status: 'resolved',
        priceBasis: {
          inputPerMillion: 5, outputPerMillion: 25, cacheReadPerMillion: 0.5,
          cacheWritePerMillion: 6.25, currency: 'USD', validFrom: '2026-01-01T00:00:00Z',
          source: 'Anthropic published pricing', note: null, unconfirmed: false,
        },
      },
    }],
  })));
}

async function saveShot(page: Page, name: string) {
  const buf = await page.screenshot({ fullPage: false });
  if (!RESULTS_DIR) return;
  await mkdir(RESULTS_DIR, { recursive: true });
  await writeFile(join(RESULTS_DIR, name), buf);
}

async function savePipelineShot(page: Page, name: string) {
  const target = RESULTS_DIR ? join(RESULTS_DIR, name) : join('test-results', name);
  if (RESULTS_DIR) await mkdir(RESULTS_DIR, { recursive: true });
  await page.getByTestId('overview-pipeline').screenshot({ path: target });
}

async function savePipelineAndUsageShot(page: Page, name: string) {
  const target = RESULTS_DIR ? join(RESULTS_DIR, name) : join('test-results', name);
  if (RESULTS_DIR) await mkdir(RESULTS_DIR, { recursive: true });
  await page.getByTestId('overview-consumption').screenshot({ path: target });
}

async function expandPipelineSections(page: Page) {
  for (let index = 0; index < 20; index++) {
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

test('missing historical prices never render as zero and mixed totals stay explicit', async ({ page }) => {
  await page.addInitScript(() => {
    try {
      localStorage.removeItem('atp.studio.tabs.v1');
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    }
    catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  const pipelineRoute = new RegExp(`/api/tasks/${JOB_ID}/pipeline(\\?|$)`);
  await page.route(pipelineRoute, route => route.fulfill(json(pipelineWithMissingPrices(false))));
  const url = `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`;
  await page.goto(url);

  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await page.getByText('CORE AGENT WORK', { exact: true }).click();
  const coreCost = page.locator('[data-step-id="core-agent-run"]').getByTestId('overview-pipeline-step-cost');
  const totalCost = page.getByTestId('overview-pipeline-total-cost');
  await expect(coreCost).toContainText('no price data');
  await expect(totalCost).toContainText('no price data');
  await expect(totalCost).not.toContainText('$0.00');
  await coreCost.hover();
  await expect(page.getByTestId('cac-tooltip')).toContainText('gpt-5.6-sol');
  await expect(page.getByTestId('cac-tooltip')).toContainText('NoPriceForDate');

  // Recreate the legacy silent-zero projection solely for before/after visual
  // evidence. Reload immediately afterwards to restore the real renderer.
  await coreCost.evaluate(element => { element.textContent = '$0.00'; });
  await totalCost.evaluate(element => { element.textContent = '$0.00'; });
  await savePipelineShot(page, 'pipeline-price-before-legacy-zero-light--mocked.png');

  await page.goto(url);
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await page.getByText('CORE AGENT WORK', { exact: true }).click();
  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('no price data');
  await savePipelineShot(page, 'pipeline-price-after-no-data-light--mocked.png');
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await savePipelineShot(page, 'pipeline-price-after-no-data-dark--mocked.png');

  await page.route(pipelineRoute, route => route.fulfill(json(pipelineWithMissingPrices(true))));
  await page.goto(url);
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await page.getByText('CORE AGENT WORK', { exact: true }).click();
  await page.getByText('ASPECT', { exact: true }).click();
  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('$2.00');
  await expect(page.getByTestId('overview-pipeline-total-cost'))
    .toContainText('incomplete (1 run without price)');
  await page.getByTestId('overview-pipeline-total-cost').hover();
  await expect(page.getByTestId('cac-tooltip')).toContainText('gpt-5.6-sol');
  await expect(page.getByTestId('cac-tooltip')).toContainText('NoPriceForDate');
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'light'; });
  await savePipelineShot(page, 'pipeline-price-mixed-light--mocked.png');
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await savePipelineShot(page, 'pipeline-price-mixed-dark--mocked.png');

  await page.route(pipelineRoute, route => route.fulfill(json(pipelineWithResolvedGpt56Price())));
  await page.goto(url);
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await page.getByText('CORE AGENT WORK', { exact: true }).click();
  const resolvedCoreCost = page.locator('[data-step-id="core-agent-run"]')
    .getByTestId('overview-pipeline-step-cost');
  const resolvedTotalCost = page.getByTestId('overview-pipeline-total-cost');
  const resolvedLifetimeCost = page.getByTestId('pipeline-token-usage-grand-total-cost');
  await expect(resolvedCoreCost).toContainText('$0.80');
  await expect(resolvedTotalCost).toContainText('$11.80');
  await expect(resolvedLifetimeCost).toContainText('$23.60');
  await expect(resolvedTotalCost).not.toContainText('incomplete');
  await expect(resolvedLifetimeCost).not.toContainText('incomplete');
  await expect(resolvedLifetimeCost).not.toContainText('no price data');
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'light'; });
  await savePipelineShot(page, 'pipeline-price-gpt56-resolved-light--mocked.png');
});

test('six visible runs show their priced partial total and keep pipeline rows aligned', async ({ page, devBackend }) => {
  void devBackend;
  await page.setViewportSize({ width: 1200, height: 1200 });
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  const id = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route =>
    route.fulfill(json(pipelineWithSixRunsAndPartialUsage())));
  await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), route =>
    route.fulfill(json(sixRunTimeline())));
  await page.route(new RegExp(`/api/tasks/${id}/agent-work-summary(\\?|$)`), route => route.fulfill(json({
    calls: 4,
    recovered: true,
    toolCalls: 0,
    toolCounts: [],
    startedAt: '2026-08-11T08:00:00Z',
    lastTouchAt: '2026-08-11T14:20:00Z',
    currentSessionId: 'pipeline-grid-fixture',
  })));

  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await expandPipelineSections(page);

  const totalTokens = page.getByTestId('pipeline-token-usage-grand-total-tokens');
  const totalCost = page.getByTestId('pipeline-token-usage-grand-total-cost');
  await expect(totalTokens).toHaveText('575.0k');
  await expect(totalCost).toContainText('$5.50');
  await expect(totalCost).toContainText('incomplete (1 run without usage)');
  await expect(totalCost).not.toContainText('$0.00');
  await expect(page.getByTestId('pipeline-token-usage-run')).toHaveCount(6);
  await expect(page.getByTestId('overview-agent-work')).toBeVisible();

  const core = page.locator('[data-step-id="core-agent-run"]');
  await expect(core.getByTestId('overview-pipeline-step-name')).toHaveText('Agent execution');
  await expect(core.getByTestId('overview-pipeline-agent-runs')).toContainText('6');
  await expect(core.getByTestId('overview-pipeline-step-verdict')).toHaveText('done');
  await expect(core.getByTestId('overview-pipeline-step-model')).toContainText('gpt-5.6-sol');
  expect(await core.getByTestId('overview-pipeline-step-name').evaluate(element =>
    element.scrollWidth <= element.clientWidth)).toBe(true);
  const metaCenters = await core.getByTestId('overview-pipeline-step-meta').evaluate(element =>
    Array.from(element.children)
      .map(child => child.getBoundingClientRect())
      .filter(rect => rect.width > 0 && rect.height > 0)
      .map(rect => rect.top + rect.height / 2));
  expect(Math.max(...metaCenters) - Math.min(...metaCenters)).toBeLessThan(3);

  const decision = page.locator('[data-step-id="post-orchestrator-decision"]');
  const verdict = decision.getByTestId('overview-pipeline-step-final-verdict');
  await expect(verdict).toContainText('Final verdict');
  expect(await verdict.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  expect(await verdict.locator('.ov-pl-step__final-decision-route').evaluate(element =>
    getComputedStyle(element).textOverflow)).toBe('ellipsis');

  const tool = page.locator('[data-step-id="post-wiki-maintenance"]');
  const iconCenters = await Promise.all([decision, tool].map(async row => {
    const box = await row.locator('.ov-pl-step__kind').boundingBox();
    if (!box) throw new Error('Pipeline kind icon is not visible');
    return box.x + box.width / 2;
  }));
  expect(Math.abs(iconCenters[0] - iconCenters[1])).toBeLessThan(1);

  const timingEdges = await Promise.all([core, decision, tool].map(async row => {
    const started = await row.getByTestId('overview-pipeline-step-started').boundingBox();
    const duration = await row.getByTestId('overview-pipeline-step-duration').boundingBox();
    if (!started || !duration) throw new Error('Pipeline timing cells are not visible');
    return [started.x + started.width, duration.x + duration.width];
  }));
  for (const edges of timingEdges.slice(1)) {
    expect(Math.abs(edges[0] - timingEdges[0][0])).toBeLessThan(1);
    expect(Math.abs(edges[1] - timingEdges[0][1])).toBeLessThan(1);
  }

  const alignedRightEdges = await Promise.all([
    core.getByTestId('overview-pipeline-step-tokens'),
    page.getByTestId('overview-pipeline-total-tokens'),
    totalTokens,
    page.getByTestId('pipeline-token-usage-run-total').first(),
  ].map(async locator => {
    const box = await locator.boundingBox();
    if (!box) throw new Error('A token cell is not visible');
    return box.x + box.width;
  }));
  for (const edge of alignedRightEdges.slice(1)) {
    expect(Math.abs(edge - alignedRightEdges[0])).toBeLessThan(1);
  }

  const agentTimingEdges = await Promise.all([
    page.getByTestId('agent-work-started'),
    page.getByTestId('agent-work-last-touch'),
  ].map(async locator => {
    const box = await locator.boundingBox();
    if (!box) throw new Error('An Agent Work timing cell is not visible');
    return box.x + box.width;
  }));
  expect(Math.abs(agentTimingEdges[0] - timingEdges[0][0])).toBeLessThan(1);
  expect(Math.abs(agentTimingEdges[1] - timingEdges[0][1])).toBeLessThan(1);

  const consumption = await page.getByTestId('overview-consumption').boundingBox();
  const pipelineGrid = await page.getByTestId('overview-pipeline-steps').boundingBox();
  const usageGrid = await page.getByTestId('pipeline-token-usage').boundingBox();
  if (!consumption || !pipelineGrid || !usageGrid) throw new Error('Consumption grid bounds are unavailable');
  expect(Math.abs(pipelineGrid.x + pipelineGrid.width - usageGrid.x - usageGrid.width)).toBeLessThan(1);
  expect(Math.abs(consumption.x + consumption.width - usageGrid.x - usageGrid.width)).toBeLessThan(1);

  const recordedShare = page.getByTestId('pipeline-token-usage-run-share').first();
  const shareTrack = await recordedShare.boundingBox();
  const shareFill = await recordedShare.getByTestId('pipeline-token-usage-run-share-fill').boundingBox();
  if (!shareTrack || !shareFill) throw new Error('Run-share bar bounds are unavailable');
  expect(shareFill.width / shareTrack.width).toBeCloseTo(0.2, 2);

  await page.getByTestId('pipeline-token-usage-total-toggle').click();
  await expect(page.getByTestId('pipeline-token-usage-total-model')).toContainText('gpt-5.6-sol');
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await savePipelineAndUsageShot(page, `pipeline-after-${theme}.png`);
  }

  await page.getByTestId('overview-consumption').evaluate(element => {
    element.setAttribute('style', 'width: 600px');
  });
  await expect(page.getByTestId('overview-pipeline-step-cost').first()).toBeHidden();
  await expect(page.getByTestId('pipeline-token-usage-run-cost').first()).toBeHidden();

  await page.getByTestId('overview-consumption').evaluate(element => {
    element.setAttribute('style', 'width: 440px');
  });
  await expect(page.getByTestId('overview-pipeline-step-tokens').first()).toBeHidden();
  await expect(page.getByTestId('pipeline-token-usage-run-total').first()).toBeHidden();
});

test('token usage: each pipeline step surfaces its own usage, without the aggregate model block', async ({ page, devBackend }) => {
  void devBackend;
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);

  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  const pipeline = page.getByTestId('overview-pipeline');
  await expect(pipeline).toBeVisible({ timeout: 10000 });
  await expect(page.getByTestId('overview-pipeline-tokens-by-model')).toHaveCount(0);
  await page.getByText('CORE AGENT WORK', { exact: true }).click();
  await page.getByText('ASPECT', { exact: true }).click();

  const coreRow = page.locator('[data-step-id="core-agent-run"]');
  const aspectRow = page.locator('[data-step-id="aspect-code-quality"]');
  await expect(coreRow.getByTestId('overview-pipeline-step-tokens')).toHaveText(/110(?:\.0)?k/i);
  await expect(coreRow.getByTestId('overview-pipeline-step-cost')).toContainText('$0.75');
  await expect(aspectRow.getByTestId('overview-pipeline-step-tokens')).toHaveText(/1\.2(?:0)?m/i);
  await expect(aspectRow.getByTestId('overview-pipeline-step-cost')).toContainText('$2.00');
  await expect(page.getByTestId('overview-pipeline-total-tokens')).toHaveText(/1\.3(?:1)?m/i);
  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('$2.75');

  await coreRow.getByTestId('overview-pipeline-step-tokens').hover();
  await expect(page.getByTestId('cac-tooltip')).toContainText('Estimated - historical list prices');
  await saveShot(page, 'pipeline-token-cost-tooltip--mocked.png');

  await pipeline.screenshot({
    path: RESULTS_DIR ? join(RESULTS_DIR, 'pipeline-step-usage--mocked.png') : 'test-results/pipeline-step-usage--mocked.png',
  });

  await aspectRow.getByTestId('overview-pipeline-step-tokens').click();
  const dialog = page.getByTestId('overview-step-token-modal');
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('Code quality');
  await expect(dialog).toContainText('Input');
  await expect(dialog).toContainText(/960(?:\.0)?k/i);
  await expect(dialog).toContainText('Output');
  await expect(dialog).toContainText(/240(?:\.0)?k/i);
  await expect(dialog).toContainText(/1\.2(?:0)?m/i);

  await dialog.getByRole('button', { name: 'Close' }).last().click();
  await coreRow.getByTestId('overview-pipeline-step-cost').click();
  const costDialog = page.getByTestId('cost-breakdown-dialog');
  await expect(costDialog).toBeVisible();
  await expect(costDialog).toContainText('claude-opus-4-8');
  await expect(costDialog).toContainText('Input / 1M');
  await expect(costDialog.getByTestId('cost-breakdown-formula')).toContainText('/ 1M × $5.00');
  await expect(costDialog).toContainText('Anthropic published pricing');
  await expect(costDialog).toContainText('Price effective date');
  await expect(costDialog).toContainText('Provider: TokenEconomy');
  await saveShot(page, 'cost-breakdown-dialog-light--mocked.png');
  await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await saveShot(page, 'cost-breakdown-dialog-dark--mocked.png');
});

test('task totals show no price data, never $0.00, when all recorded usage is unpriced', async ({ page, devBackend }) => {
  void devBackend;
  await page.addInitScript(() => {
    try {
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false }));
    } catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  const unpriced = pipeline();
  unpriced.cost = {
    ...unpriced.cost,
    steps: unpriced.cost.steps.map(step => ({
      ...step,
      model: 'future-unpriced-model',
      modelKnown: false,
      inputCostUsd: 0,
      outputCostUsd: 0,
      cacheReadCostUsd: 0,
      cacheCreationCostUsd: 0,
      costUsd: 0,
    })),
    totalInputCostUsd: 0,
    totalOutputCostUsd: 0,
    totalCacheReadCostUsd: 0,
    totalCacheCreationCostUsd: 0,
    totalCostUsd: 0,
    anyModelUnknown: true,
  };
  unpriced.tokensByModel = {
    runs: unpriced.tokensByModel.runs.map(run => ({
      ...run,
      models: [{
        model: 'future-unpriced-model', modelKnown: false, steps: 2,
        inputTokens: 1100000, outputTokens: 210000, cacheReadTokens: 0,
        cacheCreationTokens: 0, totalTokens: 1310000, costUsd: 0,
      }],
      totalCostUsd: 0,
      anyModelUnknown: true,
    })),
    totalByModel: [{
      model: 'future-unpriced-model', modelKnown: false, steps: 4,
      inputTokens: 2200000, outputTokens: 420000, cacheReadTokens: 0,
      cacheCreationTokens: 0, totalTokens: 2620000, costUsd: 0,
    }],
    totalTokens: 2620000,
    totalCostUsd: 0,
    anyModelUnknown: true,
  };

  const id = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => route.fulfill(json(unpriced)));
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  await expect(page.getByTestId('overview-pipeline-total-cost')).toContainText('no price data');
  await expect(page.getByTestId('pipeline-token-usage-grand-total-cost')).toContainText('no price data');

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    const path = RESULTS_DIR
      ? join(RESULTS_DIR, `pipeline-total-unpriced-${theme}--mocked.png`)
      : `test-results/pipeline-total-unpriced-${theme}--mocked.png`;
    await page.screenshot({ path, fullPage: true });
  }
});

test('post-step lifecycle shows backend activation, history, and the exact settings row in both themes', async ({ page, devBackend }) => {
  void devBackend;
  await page.addInitScript(() => {
    try { localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false })); }
    catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  await page.route('**/api/projects/pipeline-catalogue**', route => route.fulfill(json({ steps: [{
    id: 'post-wiki-learnings', displayName: 'Wiki learnings', kind: 'tool', phase: 'post',
    runMode: 'sequential', dependsOn: ['aspect-code-quality'], idempotent: true, stub: false,
    usesModel: false, usesPrompt: false, supportsMode: false, canDisable: true,
    defaultEnabled: false, supportsCondition: false,
  }] })));
  await page.route('**/api/projects/settings', route => route.fulfill(json({
    'agent-taskboard': { pipelineSteps: { 'post-wiki-learnings': { enabled: false } } },
  })));
  let activationBody: unknown = null;
  await page.route('**/api/projects/agent-taskboard/pipeline-step', async route => {
    activationBody = route.request().postDataJSON();
    await route.fulfill(json({
      stepId: 'post-wiki-learnings',
      pipelineSteps: { 'post-wiki-learnings': { enabled: true } },
    }));
  });
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

  await expect(page.getByTestId('overview-pipeline')).toBeVisible({ timeout: 10_000 });
  await page.getByText('TOOL', { exact: true }).click();
  await page.getByText('ASPECT', { exact: true }).click();
  const maintenance = page.locator('[data-step-id="post-wiki-maintenance"]');
  const learnings = page.locator('[data-step-id="post-wiki-learnings"]');
  const agentsSync = page.locator('[data-step-id="post-agents-wiki-sync"]');
  const aspect = page.locator('[data-step-id="aspect-code-quality"]');
  await expect(maintenance.getByTestId('overview-post-step-source')).toHaveText('C');
  await expect(maintenance.getByTestId('overview-post-step-source')).toHaveAttribute(
    'aria-label',
    'active from condition: Enabled by the global catalogue default; condition "task has tag \'wiki\'" matched this run. Open settings.',
  );
  await expect(maintenance.getByTestId('overview-post-step-run')).toHaveText('Run again');
  await expect(maintenance.getByTestId('overview-post-step-attempts')).toContainText('2 attempts');
  await maintenance.getByTestId('overview-post-step-attempts').click();
  await expect(maintenance.getByTestId('overview-post-step-attempt-row')).toHaveCount(2);
  await expect(maintenance.getByTestId('overview-post-step-attempt-row').first()).toContainText('#2');
  await expect(maintenance.getByTestId('overview-post-step-artifact').first()).toContainText('attempt-002.md');
  await expect(learnings.getByTestId('overview-post-step-source')).toHaveText('P');
  await expect(learnings.getByTestId('overview-post-step-run')).toHaveText('Add + run');
  await expect(agentsSync.getByTestId('overview-post-step-source')).toHaveText('C');
  await expect(agentsSync.getByTestId('overview-post-step-source')).toHaveAttribute(
    'aria-label',
    'skipped from condition: Condition "an aspect failed" did not match this run. Open settings.',
  );
  await expect(aspect.getByTestId('overview-post-step-source')).toHaveText('G');
  await expect(aspect.getByTestId('overview-post-step-run')).toHaveCount(0);

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(t => { document.documentElement.dataset['studioTheme'] = t; }, theme);
    const path = RESULTS_DIR
      ? join(RESULTS_DIR, `post-step-lifecycle-${theme}--mocked.png`)
      : `test-results/post-step-lifecycle-${theme}--mocked.png`;
    await page.getByTestId('overview-pipeline').screenshot({ path });
  }

  await maintenance.getByTestId('overview-post-step-attempts').click();
  await expect(maintenance.getByTestId('overview-post-step-attempt-row')).toHaveCount(0);
  await learnings.getByTestId('overview-post-step-source').click();
  const activationRow = page.getByTestId('pipeline-step-row-post-wiki-learnings');
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible();
  await expect(activationRow).toBeVisible();
  await expect(activationRow).toHaveAttribute('aria-current', 'location');
  await expect(activationRow).toHaveJSProperty('open', true);
  await page.getByTestId('pipeline-step-row-enabled-post-wiki-learnings').check();
  await expect.poll(() => activationBody).toMatchObject({ stepId: 'post-wiki-learnings', enabled: true });
});

test('council reaction links the targeted follow-up round and renders in both themes', async ({ page }, testInfo) => {
  await page.addInitScript(() => {
    try { localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: false, git: false })); }
    catch { /* ignore */ }
  });
  await installFixtureRoutes(page);
  await page.route('**/api/tasks/code-review/defaults', route => route.fulfill(json({ cliType: 'claude', model: 'claude-opus-4-8' })));
  await page.route(`**/api/tasks/${JOB_ID}/code-review/list**`, route => route.fulfill(json({ entries: [{
    fileName: 'code-review-grade-2026-07-11T22-15-00Z.md', verdict: 'pass', grade: 'B',
    summary: 'Solid result with one small evidence gap.', model: 'claude-opus-4-8', cliType: 'claude',
    commit: 'base..task/pipeline-step-usage-fixture', runAt: '2026-07-11T22:15:00Z',
    inputTokens: 125000, outputTokens: 18000, cacheReadTokens: 42000, cacheCreationTokens: 6000,
    totalTokens: 191000, estimatedApiCostUsd: 1.42, priceKnown: true,
    councilReaction: {
      createdAt: '2026-07-11T22:15:01Z', reviewFileName: 'code-review-grade-2026-07-11T22-15-00Z.md',
      grade: 'B', disposition: 'Reissue', summary: 'Fix 2 review finding(s) in the next round.',
      startsNewRound: true, targetJobId: JOB_ID, targetRunAttempt: 2,
      assessments: [
        {
          finding: 'Dark-theme colors are incorrect; fix them and provide both-theme screenshots.',
          action: 'FixNextRound', reason: 'Concrete review deficiency; provide focused evidence.',
        },
        {
          finding: 'Upload rejection lacks focused test evidence; add the missing regression test.',
          action: 'FixNextRound', reason: 'Concrete review deficiency; provide focused evidence.',
        },
      ],
    },
  }] })));
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await page.getByTestId('prompt-tab-code-review').click();

  await expect(page.getByTestId('code-review-grade-run')).toBeVisible();
  await expect(page.getByTestId('code-review-list')).toContainText('Grade B');
  const usage = page.getByTestId('code-review-token-usage');
  await expect(usage).toContainText('125k in / 18k out (191k) tokens');
  await usage.hover();
  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toContainText('Estimated cost: $1.42');
  await expect(tooltip).toContainText('Estimated - historical list prices');
  await saveShot(page, 'review-token-cost-tooltip--mocked.png');
  const reaction = page.getByTestId('code-review-council-reaction');
  await expect(reaction).toContainText('Orchestrator reaction');
  await expect(reaction).toContainText('Dark-theme colors are incorrect');
  await expect(reaction).toContainText('Upload rejection lacks focused test evidence');
  await expect(reaction).toHaveAttribute('data-disposition', 'reissue');
  await expect(page.getByTestId('code-review-council-round-link')).toHaveAttribute('href', /\/#\/tasks\/AGT-2253/);
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(t => { document.documentElement.dataset['studioTheme'] = t; }, theme);
    const fileName = `council-review-reaction-${theme}.png`;
    const path = RESULTS_DIR
      ? join(RESULTS_DIR, fileName)
      : join('test-results', fileName);
    await page.getByTestId('code-review-panel').screenshot({ path });
    await testInfo.attach(fileName, { path, contentType: 'image/png' });
  }
});
